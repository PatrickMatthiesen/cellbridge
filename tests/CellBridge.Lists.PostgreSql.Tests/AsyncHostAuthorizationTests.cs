using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Npgsql;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class AsyncHostAuthorizationTests(ListDatabaseFixture database)
{
    [PostgreSqlFact]
    public async Task Host_mode_ignores_local_acl_and_disables_native_grant_mutators()
    {
        var host = await CreateHostAsync();
        var field = TextField();
        var list = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Host list", [field]));

        await SeedLocalAclAsync(host, list.Id);
        host.Provider.Deny(list.Id, ListAuthorizationAction.Read);
        var denied = await Assert.ThrowsAsync<ListException>(() =>
            host.Engine.GetListAsync(host.Actor, host.Workspace, list.Id));
        Assert.Equal(ListFailure.NotFound, denied.Failure);

        Assert.Equal(ListFailure.NotSupported, (await Assert.ThrowsAsync<ListException>(() =>
            host.Engine.SetListGrantAsync(host.Actor, host.Workspace, list.Id, host.Actor.Subject,
                ListAccess.Read))).Failure);
        Assert.Equal(ListFailure.NotSupported, (await Assert.ThrowsAsync<ListException>(() =>
            host.Engine.SetMembershipAsync(host.Actor, host.Workspace, host.Actor.Subject,
                true, true, true))).Failure);
    }

    [PostgreSqlFact]
    public async Task Denied_reference_target_returns_before_target_record_lock()
    {
        var host = await CreateHostAsync();
        var label = TextField();
        var target = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Targets", [label]));
        var targetRecord = Guid.NewGuid();
        await host.Engine.CreateRecordAsync(host.Actor, host.Workspace, target.Id,
            new CreateRecordRequest(targetRecord, 1, new JsonObject { [label.Id.ToString("D")] = "target" }));
        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference,
            ReferenceListId: target.Id, ReferenceDisplayFieldId: label.Id);
        var source = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Sources", [reference]));

        await using var blocker = await database.AdminSource!.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await SetContextAsync(blocker, transaction, host.Workspace);
        await using (var command = new NpgsqlCommand("""
            SELECT record_id FROM cellbridge_lists.records
            WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3 FOR UPDATE
            """, blocker, transaction))
        {
            command.Parameters.AddWithValue(host.Workspace);
            command.Parameters.AddWithValue(target.Id);
            command.Parameters.AddWithValue(targetRecord);
            await command.ExecuteNonQueryAsync();
        }

        host.Provider.Deny(target.Id, ListAuthorizationAction.Read);
        var attempt = host.Engine.CreateRecordAsync(host.Actor, host.Workspace, source.Id,
            new CreateRecordRequest(Guid.NewGuid(), 1,
                new JsonObject { [reference.Id.ToString("D")] = targetRecord.ToString("D") }));
        var denied = await Assert.ThrowsAsync<ListException>(async () =>
            await attempt.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(ListFailure.NotFound, denied.Failure);
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task Query_scopes_support_all_none_empty_pages_and_reauthorize_continuations()
    {
        var host = await CreateHostAsync();
        for (var index = 0; index < 3; index++)
            await host.Engine.CreateListAsync(host.Actor, host.Workspace,
                new CreateListRequest(Guid.NewGuid(), "List " + index, [TextField()]));

        host.Provider.QueryScope = ListQueryAuthorizationScope.All;
        var first = await host.Engine.GetListsPageAsync(host.Actor, host.Workspace, candidateLimit: 1);
        Assert.Single(first.Lists);
        Assert.NotNull(first.Continuation);

        host.Provider.QueryScope = ListQueryAuthorizationScope.None;
        var revoked = await host.Engine.GetListsPageAsync(host.Actor, host.Workspace,
            first.Continuation, candidateLimit: 1);
        Assert.Empty(revoked.Lists);
        Assert.Null(revoked.Continuation);

        host.Provider.QueryScope = ListQueryAuthorizationScope.CheckCandidates;
        host.Provider.DenyAllReads = true;
        var empty = await host.Engine.GetListsPageAsync(host.Actor, host.Workspace, candidateLimit: 1);
        Assert.Empty(empty.Lists);
        Assert.NotNull(empty.Continuation);
    }

    [PostgreSqlFact]
    public async Task Provider_failure_and_invalid_freshness_fail_closed_before_data_access()
    {
        var host = await CreateHostAsync();
        var list = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Closed", [TextField()]));

        host.Provider.InvalidFreshness = true;
        Assert.Equal(ListFailure.Unavailable, (await Assert.ThrowsAsync<ListException>(() =>
            host.Engine.GetListAsync(host.Actor, host.Workspace, list.Id))).Failure);
        host.Provider.InvalidFreshness = false;
        host.Provider.Throw = true;
        Assert.Equal(ListFailure.Unavailable, (await Assert.ThrowsAsync<ListException>(() =>
            host.Engine.GetListAsync(host.Actor, host.Workspace, list.Id))).Failure);
    }

    [PostgreSqlFact]
    public async Task Reference_operation_reuses_source_admission_when_authorizing_targets()
    {
        var host = await CreateHostAsync();
        var label = TextField();
        var target = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Targets", [label]));
        var targetRecord = Guid.NewGuid();
        await host.Engine.CreateRecordAsync(host.Actor, host.Workspace, target.Id,
            new CreateRecordRequest(targetRecord, 1,
                new JsonObject { [label.Id.ToString("D")] = "target" }));
        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference,
            ReferenceListId: target.Id, ReferenceDisplayFieldId: label.Id);
        var source = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Sources", [reference]));

        host.Provider.ClearRequests();
        await host.Engine.CreateRecordAsync(host.Actor, host.Workspace, source.Id,
            new CreateRecordRequest(Guid.NewGuid(), 1,
                new JsonObject { [reference.Id.ToString("D")] = targetRecord.ToString("D") }));

        Assert.Equal(3, host.Provider.Requests.Count);
        Assert.Single(host.Provider.Requests, x => x.Action == ListAuthorizationAction.UseLists);
        Assert.Single(host.Provider.Requests, x =>
            x.ListId == source.Id && x.Action == ListAuthorizationAction.Write);
        Assert.Single(host.Provider.Requests, x =>
            x.ListId == target.Id && x.Action == ListAuthorizationAction.Read);
    }

    [PostgreSqlFact]
    public async Task Template_reference_targets_are_authorized_before_create_transaction()
    {
        var host = await CreateHostAsync();
        var target = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Targets", [TextField()]));
        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference,
            ReferenceListId: target.Id);
        var template = new ListTemplate(Guid.NewGuid(), "References", 1, [reference]);
        await PostgreSqlListSetup.RegisterTemplateAsync(database.AdminSource!, template);

        host.Provider.ClearRequests();
        var created = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "From template", [], template.Id, template.Version));

        Assert.Contains(created.Fields, field => field.Id == reference.Id);
        Assert.Equal(3, host.Provider.Requests.Count);
        Assert.Single(host.Provider.Requests, request => request.Action == ListAuthorizationAction.UseLists);
        Assert.Single(host.Provider.Requests, request => request.Action == ListAuthorizationAction.CreateList);
        Assert.Single(host.Provider.Requests, request =>
            request.ListId == target.Id && request.Action == ListAuthorizationAction.Read);
    }

    [PostgreSqlFact]
    public async Task Malformed_record_field_id_is_a_validation_failure()
    {
        var host = await CreateHostAsync();
        var list = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Records", [TextField()]));

        var failure = await Assert.ThrowsAsync<ListException>(() => host.Engine.CreateRecordAsync(
            host.Actor, host.Workspace, list.Id,
            new CreateRecordRequest(Guid.NewGuid(), 1, new JsonObject { ["not-a-guid"] = "value" })));

        Assert.Equal(ListFailure.Validation, failure.Failure);
    }

    [PostgreSqlFact]
    public async Task Clearing_optional_reference_does_not_authorize_its_target()
    {
        var host = await CreateHostAsync();
        var target = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Targets", [TextField()]));
        var targetRecord = Guid.NewGuid();
        await host.Engine.CreateRecordAsync(host.Actor, host.Workspace, target.Id,
            new CreateRecordRequest(targetRecord, 1, new JsonObject()));
        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference,
            ReferenceListId: target.Id);
        var source = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Sources", [reference]));
        var sourceRecord = Guid.NewGuid();
        await host.Engine.CreateRecordAsync(host.Actor, host.Workspace, source.Id,
            new CreateRecordRequest(sourceRecord, 1,
                new JsonObject { [reference.Id.ToString("D")] = targetRecord.ToString("D") }));

        host.Provider.Deny(target.Id, ListAuthorizationAction.Read);
        host.Provider.ClearRequests();
        var updated = await host.Engine.UpdateRecordAsync(host.Actor, host.Workspace, source.Id, sourceRecord,
            new UpdateRecordRequest(1, 1, [new FieldChange(reference.Id, null)]));

        Assert.True(updated.Values.ContainsKey(reference.Id.ToString("D")));
        Assert.Null(updated.Values[reference.Id.ToString("D")]);
        Assert.DoesNotContain(host.Provider.Requests, request => request.ListId == target.Id);
    }

    [PostgreSqlFact]
    public async Task Reference_resolution_checks_the_security_audit_reserve()
    {
        var host = await CreateHostAsync();
        var label = TextField();
        var target = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Targets", [label]));
        var targetRecord = Guid.NewGuid();
        await host.Engine.CreateRecordAsync(host.Actor, host.Workspace, target.Id,
            new CreateRecordRequest(targetRecord, 1,
                new JsonObject { [label.Id.ToString("D")] = "hidden" }));
        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference,
            ReferenceListId: target.Id, ReferenceDisplayFieldId: label.Id);
        var source = await host.Engine.CreateListAsync(host.Actor, host.Workspace,
            new CreateListRequest(Guid.NewGuid(), "Sources", [reference]));
        var sourceRecord = Guid.NewGuid();
        await host.Engine.CreateRecordAsync(host.Actor, host.Workspace, source.Id,
            new CreateRecordRequest(sourceRecord, 1,
                new JsonObject { [reference.Id.ToString("D")] = targetRecord.ToString("D") }));
        await database.SetWorkspaceContextAsync(host.Workspace, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                UPDATE cellbridge_lists.workspaces SET security_audit_count=10000 WHERE workspace_id=$1
                """, connection, transaction);
            command.Parameters.AddWithValue(host.Workspace);
            await command.ExecuteNonQueryAsync();
        });

        var failure = await Assert.ThrowsAsync<ListException>(() => host.Engine.ResolveReferenceAsync(
            host.Actor, host.Workspace, source.Id, sourceRecord, reference.Id));

        Assert.Equal(ListFailure.Unavailable, failure.Failure);
    }

    private async Task<Host> CreateHostAsync()
    {
        var workspace = Guid.NewGuid();
        var domain = "host-tests:" + Guid.NewGuid().ToString("N");
        var tenant = "tenant:" + Guid.NewGuid().ToString("N");
        var actor = new ListActor("host:actor:" + Guid.NewGuid().ToString("N"), tenant);
        await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, workspace,
            "Host authorization", domain, tenant);
        var provider = new MutableProvider(domain);
        var engine = new PostgreSqlListEngine(database.RuntimeSource!, new ListLimits(), provider);
        return new(workspace, actor, provider, engine);
    }

    private async Task SeedLocalAclAsync(Host host, Guid listId)
    {
        await using var connection = await database.AdminSource!.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContextAsync(connection, transaction, host.Workspace);
        await using (var member = new NpgsqlCommand("""
            INSERT INTO cellbridge_lists.memberships(workspace_id,subject,can_create_lists,can_manage_grants)
            VALUES ($1,$2,true,true)
            """, connection, transaction))
        {
            member.Parameters.AddWithValue(host.Workspace);
            member.Parameters.AddWithValue(host.Actor.Subject);
            await member.ExecuteNonQueryAsync();
        }
        await using (var grant = new NpgsqlCommand("""
            INSERT INTO cellbridge_lists.list_grants(workspace_id,list_id,subject,access)
            VALUES ($1,$2,$3,7)
            """, connection, transaction))
        {
            grant.Parameters.AddWithValue(host.Workspace);
            grant.Parameters.AddWithValue(listId);
            grant.Parameters.AddWithValue(host.Actor.Subject);
            await grant.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static async Task SetContextAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspace)
    {
        await using var context = new NpgsqlCommand(
            "SELECT set_config('cellbridge.workspace_id',$1,true)", connection, transaction);
        context.Parameters.AddWithValue(workspace.ToString("D"));
        await context.ExecuteNonQueryAsync();
    }

    private static ListField TextField() => new(Guid.NewGuid(), "Text", ListFieldType.Text, MaxLength: 100);

    private sealed record Host(Guid Workspace, ListActor Actor, MutableProvider Provider,
        PostgreSqlListEngine Engine);

    private sealed class MutableProvider(string policyDomain) : IListBatchAuthorizationProvider,
        IListQueryAuthorizationProvider
    {
        private readonly ConcurrentDictionary<(Guid, ListAuthorizationAction), bool> _denials = new();
        public string PolicyDomain { get; } = policyDomain;
        public ListQueryAuthorizationScope QueryScope { get; set; } = ListQueryAuthorizationScope.CheckCandidates;
        public bool DenyAllReads { get; set; }
        public bool InvalidFreshness { get; set; }
        public bool Throw { get; set; }
        public ConcurrentQueue<ListAuthorizationRequest> Requests { get; } = new();

        public void Deny(Guid listId, ListAuthorizationAction action) => _denials[(listId, action)] = true;
        public void ClearRequests() => Requests.Clear();

        public ValueTask<ListAuthorizationDecision> AuthorizeAsync(ListAuthorizationRequest request,
            CancellationToken ct = default)
        {
            if (Throw) throw new InvalidOperationException("provider unavailable");
            Requests.Enqueue(request);
            var now = DateTimeOffset.UtcNow;
            var allowed = !(request.ListId is { } listId &&
                (_denials.ContainsKey((listId, request.Action)) ||
                 DenyAllReads && request.Action == ListAuthorizationAction.Read));
            return ValueTask.FromResult(InvalidFreshness
                ? new ListAuthorizationDecision(allowed, now, now.AddMinutes(-1))
                : new ListAuthorizationDecision(allowed, now, now.AddMinutes(5)));
        }

        public async ValueTask<IReadOnlyList<ListAuthorizationDecision>> AuthorizeBatchAsync(
            IReadOnlyList<ListAuthorizationRequest> requests, CancellationToken ct = default)
        {
            var decisions = new List<ListAuthorizationDecision>(requests.Count);
            foreach (var request in requests) decisions.Add(await AuthorizeAsync(request, ct));
            return decisions;
        }

        public ValueTask<ListQueryAuthorizationDecision> AuthorizeQueryAsync(
            ListAuthorizationRequest request, CancellationToken ct = default)
        {
            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(new ListQueryAuthorizationDecision(QueryScope, request.PolicyDomain,
                request.Subject, request.TenantId, request.WorkspaceId, now, now.AddMinutes(5)));
        }
    }
}
