using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Npgsql;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class HostPreflightReserveTests(ListDatabaseFixture database)
{
    [PostgreSqlTheory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("resolve")]
    [InlineData("schema")]
    [InlineData("template")]
    public async Task Exhausted_reserve_stops_definition_preflight_before_target_authority(string operation)
    {
        await CheckAsync(operation, exhaustDuringTargetCheck: false);
    }

    [PostgreSqlTheory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("resolve")]
    [InlineData("schema")]
    [InlineData("template")]
    public async Task Final_transaction_rechecks_reserve_after_target_authority(string operation)
    {
        await CheckAsync(operation, exhaustDuringTargetCheck: true);
    }

    private async Task CheckAsync(string operation, bool exhaustDuringTargetCheck)
    {
        var workspace = Guid.NewGuid();
        var template = new ListTemplate(Guid.NewGuid(), "Reference template", 1, []);
        var actor = new ListActor("host:preflight", "tenant:" + Guid.NewGuid().ToString("N"));
        var authority = new RecordingAuthority("preflight:" + Guid.NewGuid().ToString("N"));
        try
        {
            await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, workspace,
                "Preflight reserve", authority.PolicyDomain, actor.TenantId!);
            var engine = new PostgreSqlListEngine(database.RuntimeSource!, new() { MaxSecurityAuditEvents = 2 }, authority);
            var title = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
            var target = await engine.CreateListAsync(actor, workspace, new(Guid.NewGuid(), "Targets", [title]));
            var targetRecords = new[] { Guid.NewGuid(), Guid.NewGuid() };
            foreach (var id in targetRecords)
                await engine.CreateRecordAsync(actor, workspace, target.Id,
                    new(id, 1, new JsonObject { [title.Id.ToString("D")] = "Target" }));
            var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference,
                ReferenceListId: target.Id, ReferenceDisplayFieldId: title.Id);
            var source = await engine.CreateListAsync(actor, workspace, new(Guid.NewGuid(), "Sources", [reference]));
            var recordId = Guid.NewGuid();
            await engine.CreateRecordAsync(actor, workspace, source.Id,
                new(recordId, 1, new JsonObject { [reference.Id.ToString("D")] = targetRecords[0].ToString("D") }));
            template = template with { Fields = [reference] };
            await PostgreSqlListSetup.RegisterTemplateAsync(database.AdminSource!, template);
            authority.Requests.Clear();
            if (exhaustDuringTargetCheck)
                authority.OnTargetRead = request => request.ListId == target.Id ? SetReserveAsync(workspace) : Task.CompletedTask;
            else await SetReserveAsync(workspace);

            var newReference = reference with { Id = Guid.NewGuid(), Name = "Another target" };
            Func<Task> attempt = operation switch
            {
                "create" => () => engine.CreateRecordAsync(actor, workspace, source.Id,
                    new(Guid.NewGuid(), 1, new JsonObject { [reference.Id.ToString("D")] = targetRecords[1].ToString("D") })),
                "update" => () => engine.UpdateRecordAsync(actor, workspace, source.Id, recordId,
                    new(1, 1, [new(reference.Id, JsonValue.Create(targetRecords[1].ToString("D")))])),
                "resolve" => () => engine.ResolveReferenceAsync(actor, workspace, source.Id, recordId, reference.Id),
                "schema" => () => engine.PublishSchemaAsync(actor, workspace, source.Id,
                    new(1, source.Name, [reference, newReference])),
                "template" => () => engine.CreateListAsync(actor, workspace,
                    new(Guid.NewGuid(), "From template", [], template.Id, template.Version)),
                _ => throw new ArgumentException("Unknown operation.")
            };
            Assert.Equal(ListFailure.Unavailable, (await Assert.ThrowsAsync<ListException>(attempt)).Failure);
            var targetRequests = authority.Requests.Where(request => request.ListId == target.Id).ToArray();
            if (exhaustDuringTargetCheck) Assert.Single(targetRequests);
            else Assert.Empty(targetRequests);
            await database.SetWorkspaceContextAsync(workspace, async (connection, transaction) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT (SELECT count(*) FROM cellbridge_lists.lists WHERE workspace_id=@workspace),
                      (SELECT count(*) FROM cellbridge_lists.records WHERE workspace_id=@workspace),
                      (SELECT schema_version FROM cellbridge_lists.lists WHERE workspace_id=@workspace AND list_id=@source),
                      (SELECT revision FROM cellbridge_lists.records WHERE workspace_id=@workspace AND list_id=@source AND record_id=@record)
                    """, connection, transaction);
                command.Parameters.AddWithValue("workspace", workspace);
                command.Parameters.AddWithValue("source", source.Id);
                command.Parameters.AddWithValue("record", recordId);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(2, reader.GetInt64(0));
                Assert.Equal(3, reader.GetInt64(1));
                Assert.Equal(1, reader.GetInt64(2));
                Assert.Equal(1, reader.GetInt64(3));
            });
        }
        finally
        {
            await using var connection = await database.AdminSource!.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var cleanup = new NpgsqlCommand("""
                SELECT set_config('cellbridge.workspace_id',@workspace::text,true);
                DELETE FROM cellbridge_lists.record_references WHERE workspace_id=@workspace;
                DELETE FROM cellbridge_lists.records WHERE workspace_id=@workspace;
                DELETE FROM cellbridge_lists.workspaces WHERE workspace_id=@workspace;
                DELETE FROM cellbridge_lists.templates WHERE template_id=@template;
                """, connection, transaction);
            cleanup.Parameters.AddWithValue("workspace", workspace);
            cleanup.Parameters.AddWithValue("template", template.Id);
            await cleanup.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
    }

    private Task SetReserveAsync(Guid workspace) => database.SetWorkspaceContextAsync(workspace, async (connection, transaction) =>
    {
        await using var command = new NpgsqlCommand(
            "UPDATE cellbridge_lists.workspaces SET security_audit_count=2 WHERE workspace_id=$1", connection, transaction);
        command.Parameters.AddWithValue(workspace);
        await command.ExecuteNonQueryAsync();
    });

    private sealed class RecordingAuthority(string domain) : IListAuthorizationProvider
    {
        public string PolicyDomain => domain;
        public List<ListAuthorizationRequest> Requests { get; } = [];
        public Func<ListAuthorizationRequest, Task>? OnTargetRead { get; set; }
        public async ValueTask<ListAuthorizationDecision> AuthorizeAsync(ListAuthorizationRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (request.Action == ListAuthorizationAction.Read && OnTargetRead is { } callback) await callback(request);
            return new(true);
        }
    }
}
