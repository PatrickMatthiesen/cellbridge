using System.Text;
using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.SqlServer.Tests;

public sealed partial class SqlServerListEngineTests
{
    [Fact]
    public async Task HostAuthorityUsesImmutableDomainScopeAndCurrentOperationDecisions()
    {
        var env = await SetupAsync();
        var workspace = Guid.NewGuid();
        var actor = new ListActor("host actor", "scope ");
        var policy = new HostPolicy("domain");
        await SqlServerListSetup.RegisterHostWorkspaceAsync(env.AdminOptions, workspace, "Host", policy.PolicyDomain, actor.TenantId!);
        var engine = new SqlServerListEngine(env.RuntimeOptions, null, policy);
        var list = await engine.CreateListAsync(actor, workspace, NewListRequest());
        var request = Request(list);
        await engine.CreateRecordAsync(actor, workspace, list.Id, request);
        Assert.Equal(list.Id, (await engine.GetListAsync(actor, workspace, list.Id)).Id);
        var extra = new ListField(Guid.NewGuid(), "Optional", ListFieldType.Boolean);
        var schema = await engine.PublishSchemaAsync(actor, workspace, list.Id, new(1, list.Name, [.. list.Fields, extra]));
        var updated = await engine.UpdateRecordAsync(actor, workspace, list.Id, request.Id, new(1, 2, [new(extra.Id, JsonValue.Create(true))]));
        Assert.Equal(2, updated.Revision);
        await Failure(ListFailure.NotFound, () => engine.GetRecordAsync(actor with { TenantId = "scope" }, workspace, list.Id, request.Id));
        await Failure(ListFailure.NotFound, () => new SqlServerListEngine(env.RuntimeOptions, null, new HostPolicy("DOMAIN"))
            .GetListAsync(actor, workspace, list.Id));
        await Failure(ListFailure.NotFound, () => engine.GetListAsync(actor, env.Workspace, list.Id));
        await Failure(ListFailure.NotFound, () => env.Engine.GetListAsync(Admin, workspace, list.Id));
        await Failure(ListFailure.NotSupported, () => engine.SetMembershipAsync(actor, workspace, "new", true, true, true));
        Assert.Single(await engine.GetListsAsync(actor, workspace));
        policy.Allow = request => request.Action != ListAuthorizationAction.Write;
        await Failure(ListFailure.NotFound, () => engine.CreateRecordAsync(actor, workspace, list.Id, request));
        await Failure(ListFailure.NotFound, () => engine.UpdateRecordAsync(actor, workspace, list.Id, request.Id, new(2, 2, [])));
        policy.Allow = _ => false;
        await Failure(ListFailure.Forbidden, () => engine.GetListAsync(actor, workspace, list.Id));
        policy.Allow = _ => true;
        policy.Fail = true;
        await Failure(ListFailure.Unavailable, () => engine.GetListAsync(actor, workspace, list.Id));
        policy.Fail = false;
        policy.InvalidFreshness = true;
        await Failure(ListFailure.Unavailable, () => engine.GetListAsync(actor, workspace, list.Id));
        policy.InvalidFreshness = false;
        await engine.DeleteRecordAsync(actor, workspace, list.Id, request.Id, 2);
        await using var scope = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, workspace);
        Assert.Empty(await scope.Context.Memberships.ToListAsync());
        Assert.Empty(await scope.Context.ListGrants.ToListAsync());
        Assert.Equal(schema.SchemaVersion, (await scope.Context.Lists.SingleAsync()).SchemaVersion);
        await Failure(ListFailure.Conflict, () => SqlServerListSetup.RegisterWorkspaceAsync(env.AdminOptions, workspace, "Builtin", Admin.Subject));
        await Failure(ListFailure.Conflict, () => SqlServerListSetup.RegisterHostWorkspaceAsync(env.AdminOptions, workspace, "Rebound", "domain", "scope"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostReferencePreflightAndFinalAdmissionCheckSecurityReserve(bool exhaustDuringTargetCheck)
    {
        var env = await SetupAsync();
        var workspace = Guid.NewGuid();
        var actor = new ListActor("host", "tenant");
        var policy = new HostPolicy("policy");
        await SqlServerListSetup.RegisterHostWorkspaceAsync(env.AdminOptions, workspace, "Host", "policy", "tenant");
        var engine = new SqlServerListEngine(env.RuntimeOptions, new ListLimits { MaxSecurityAuditEvents = 2 }, policy);
        var target = await engine.CreateListAsync(actor, workspace, NewListRequest());
        var targetRequest = Request(target);
        await engine.CreateRecordAsync(actor, workspace, target.Id, targetRequest);
        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference, ReferenceListId: target.Id);
        var source = await engine.CreateListAsync(actor, workspace, new(Guid.NewGuid(), "Source", [reference]));
        policy.Requests.Clear();
        async Task ExhaustAsync()
        {
            await using var scope = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, workspace);
            (await scope.Context.Workspaces.SingleAsync()).SecurityAuditCount = 2;
            await scope.Context.SaveChangesAsync();
            await scope.CommitAsync();
        }
        if (exhaustDuringTargetCheck) policy.OnRequest = request => request.ListId == target.Id ? ExhaustAsync() : Task.CompletedTask;
        else await ExhaustAsync();
        await Failure(ListFailure.Unavailable, () => engine.CreateRecordAsync(actor, workspace, source.Id,
            new(Guid.NewGuid(), 1, new JsonObject { [reference.Id.ToString("D")] = targetRequest.Id.ToString("D") })));
        Assert.Equal(exhaustDuringTargetCheck ? 1 : 0, policy.Requests.Count(request => request.ListId == target.Id));
        await using var check = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, workspace);
        Assert.False(await check.Context.Records.AnyAsync(row => row.ListId == source.Id));
        Assert.False(await check.Context.RecordTombstones.AnyAsync(row => row.ListId == source.Id));
    }

    [Fact]
    public async Task HostReferenceAssignmentsRequireTargetAuthorityAndDoNotReuseStaleAllows()
    {
        var env = await SetupAsync();
        var workspace = Guid.NewGuid();
        var actor = new ListActor("host", "tenant");
        var policy = new HostPolicy("policy");
        await SqlServerListSetup.RegisterHostWorkspaceAsync(env.AdminOptions, workspace, "Host", "policy", "tenant");
        var engine = new SqlServerListEngine(env.RuntimeOptions, null, policy);
        var target = await engine.CreateListAsync(actor, workspace, NewListRequest());
        var a = Request(target);
        var b = Request(target);
        await engine.CreateRecordAsync(actor, workspace, target.Id, a);
        await engine.CreateRecordAsync(actor, workspace, target.Id, b);
        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference,
            ReferenceListId: target.Id, ReferenceDisplayFieldId: target.Fields[0].Id);
        var source = await engine.CreateListAsync(actor, workspace, new(Guid.NewGuid(), "Source", [reference]));
        var request = new CreateRecordRequest(Guid.NewGuid(), 1, new JsonObject { [reference.Id.ToString("D")] = a.Id.ToString("D") });
        await engine.CreateRecordAsync(actor, workspace, source.Id, request);
        Assert.Equal(a.Id, (await engine.ResolveReferenceAsync(actor, workspace, source.Id, request.Id, reference.Id)).RecordId);
        var template = new ListTemplate(Guid.NewGuid(), "Host references", 1, [reference]);
        await SqlServerListSetup.RegisterTemplateAsync(env.AdminOptions, template);
        var pinned = await engine.CreateListAsync(actor, workspace, new(Guid.NewGuid(), "Host pinned", [], template.Id, 1));
        Assert.True(pinned.Fields.Single().Protected);
        policy.Allow = request => request.ListId != target.Id;
        await Failure(ListFailure.NotFound, () => engine.UpdateRecordAsync(actor, workspace, source.Id, request.Id,
            new(1, 1, [new(reference.Id, JsonValue.Create(b.Id.ToString("D")))])));
        await Failure(ListFailure.NotFound, () => engine.CreateListAsync(actor, workspace, new(Guid.NewGuid(), "Denied", [reference])));
        policy.Allow = _ => true;
        var changed = await engine.UpdateRecordAsync(actor, workspace, source.Id, request.Id,
            new(1, 1, [new(reference.Id, JsonValue.Create(b.Id.ToString("D")))]));
        Assert.Equal(2, changed.Revision);
        Assert.Equal(b.Id, (await engine.ResolveReferenceAsync(actor, workspace, source.Id, request.Id, reference.Id)).RecordId);
    }

    [Fact]
    public async Task HostPatchPreservesUnchangedReferenceAfterTargetReadRevocation()
    {
        var env = await SetupAsync();
        var workspace = Guid.NewGuid();
        var actor = new ListActor("host", "tenant");
        var policy = new HostPolicy("policy");
        await SqlServerListSetup.RegisterHostWorkspaceAsync(env.AdminOptions, workspace, "Host", "policy", "tenant");
        var engine = new SqlServerListEngine(env.RuntimeOptions, null, policy);
        var target = await engine.CreateListAsync(actor, workspace, NewListRequest());
        var targetRecord = Request(target);
        await engine.CreateRecordAsync(actor, workspace, target.Id, targetRecord);
        var text = new ListField(Guid.NewGuid(), "Text", ListFieldType.Text);
        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference,
            ReferenceListId: target.Id);
        var source = await engine.CreateListAsync(actor, workspace,
            new(Guid.NewGuid(), "Source", [text, reference]));
        var sourceRecord = Guid.NewGuid();
        await engine.CreateRecordAsync(actor, workspace, source.Id, new(sourceRecord, 1,
            new JsonObject
            {
                [text.Id.ToString("D")] = "before",
                [reference.Id.ToString("D")] = targetRecord.Id.ToString("D")
            }));

        policy.Allow = request => request.ListId != target.Id;
        policy.Requests.Clear();
        var updated = await engine.UpdateRecordAsync(actor, workspace, source.Id, sourceRecord,
            new(1, 1, [new(text.Id, JsonValue.Create("after"))]));

        Assert.Equal(2, updated.Revision);
        Assert.Equal("after", updated.Values[text.Id.ToString("D")]!.GetValue<string>());
        Assert.Equal(targetRecord.Id.ToString("D"),
            updated.Values[reference.Id.ToString("D")]!.GetValue<string>());
        Assert.DoesNotContain(policy.Requests, request => request.ListId == target.Id);
        await using var check = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, workspace);
        var edge = await check.Context.RecordReferences.SingleAsync(row =>
            row.SourceListId == source.Id && row.SourceRecordId == sourceRecord);
        Assert.Equal(target.Id, edge.TargetListId);
        Assert.Equal(targetRecord.Id, edge.TargetRecordId);
    }

    [Fact]
    public async Task ConcurrentPatchesConflictAndPersistedInterceptorJsonControlsResponseAndCharge()
    {
        var env = await SetupAsync();
        var list = await env.CreateListAsync();
        var request = Request(list);
        await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 5).Select(async index =>
        {
            try
            {
                await env.Engine.UpdateRecordAsync(Admin, env.Workspace, list.Id, request.Id,
                    new(1, 1, [new(list.Fields[0].Id, JsonValue.Create(index.ToString()))]));
                return true;
            }
            catch (ListException ex) when (ex.Failure == ListFailure.Conflict) { return false; }
        }));
        Assert.Single(attempts, result => result);
        var current = await env.Engine.GetRecordAsync(Admin, env.Workspace, list.Id, request.Id);
        var before = await env.SnapshotAsync(list.Id);
        var replace = new ReplacePatchJson(new JsonObject { [list.Fields[0].Id.ToString("D")] = "\u00e9 persisted \U0001F680" }.ToJsonString());
        var engine = new SqlServerListEngine(env.OptionsWith(replace));
        var updated = await engine.UpdateRecordAsync(Admin, env.Workspace, list.Id, request.Id,
            new(2, 1, [new(list.Fields[0].Id, JsonValue.Create("caller"))]));
        Assert.Equal("\u00e9 persisted \U0001F680", updated.Values[list.Fields[0].Id.ToString("D")]!.GetValue<string>());
        var after = await env.SnapshotAsync(list.Id);
        var expectedDelta = Encoding.UTF8.GetByteCount(updated.Values.ToJsonString()) - Encoding.UTF8.GetByteCount(current.Values.ToJsonString()) +
            96 + Encoding.UTF8.GetByteCount(Admin.Subject) + Encoding.UTF8.GetByteCount("record.update") + 16;
        Assert.Equal(expectedDelta, after.Bytes - before.Bytes);
        var quota = new SqlServerListEngine(env.RuntimeOptions, new ListLimits { MaxAuditEvents = after.Audits });
        await Failure(ListFailure.Limit, () => quota.UpdateRecordAsync(Admin, env.Workspace, list.Id, request.Id, new(3, 1, [])));
        await Failure(ListFailure.Limit, () => quota.PublishSchemaAsync(Admin, env.Workspace, list.Id, new(1, "No quota", list.Fields)));
        await Failure(ListFailure.Limit, () => quota.DeleteRecordAsync(Admin, env.Workspace, list.Id, request.Id, 3));
        Assert.Equal(after, await env.SnapshotAsync(list.Id));
        Assert.Equal(3, (await env.Engine.GetRecordAsync(Admin, env.Workspace, list.Id, request.Id)).Revision);
    }

    private sealed class HostPolicy(string domain) : IListAuthorizationProvider
    {
        public string PolicyDomain => domain;
        public Func<ListAuthorizationRequest, bool> Allow { get; set; } = _ => true;
        public Func<ListAuthorizationRequest, Task>? OnRequest;
        public bool Fail;
        public bool InvalidFreshness;
        public List<ListAuthorizationRequest> Requests { get; } = [];
        public async ValueTask<ListAuthorizationDecision> AuthorizeAsync(ListAuthorizationRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Fail) throw new InvalidOperationException("Injected authority failure.");
            if (OnRequest is not null) await OnRequest(request);
            return InvalidFreshness ? new(true, DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddMinutes(2)) : new(Allow(request));
        }
    }

    private sealed class ReplacePatchJson(string json) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            foreach (var entry in eventData.Context!.ChangeTracker.Entries<RecordRow>().Where(entry => entry.State == EntityState.Modified))
                entry.Entity.ValuesJson = json;
            return ValueTask.FromResult(result);
        }
    }
}
