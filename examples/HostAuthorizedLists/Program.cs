using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Npgsql;
using NpgsqlTypes;

var cacheEnabled = args.Contains("--cache");
var positional = args.Where(argument => argument != "--cache").ToArray();
if (positional.Length != 3)
{
    Console.Error.WriteLine("Usage: HostAuthorizedLists <admin-connection> <runtime-connection> <runtime-role> [--cache]");
    return 2;
}

await using var admin = NpgsqlDataSource.Create(positional[0]);
await using var runtime = NpgsqlDataSource.Create(positional[1]);
await PostgreSqlListSetup.InitializeAsync(admin, positional[2]);
await using (var setup = admin.CreateCommand("""
    CREATE TABLE IF NOT EXISTS example_list_permissions (
      domain text NOT NULL, tenant text NOT NULL, workspace uuid NOT NULL, subject text NOT NULL,
      list_id uuid NOT NULL, action text NOT NULL,
      PRIMARY KEY(domain,tenant,workspace,subject,list_id,action));
    """)) await setup.ExecuteNonQueryAsync();

// The domain also isolates this run's permission rows, including concurrent example runs.
var domain = "example:lists:" + Guid.NewGuid().ToString("N");
var workspace = Guid.NewGuid();
var otherWorkspace = Guid.NewGuid();
var actor = new ListActor("example-owner", "example-tenant");
var reader = new ListActor("example-reader", actor.TenantId);
var authority = new PostgreSqlListAuthority(admin, domain);
var cache = cacheEnabled ? new CachedListAuthorizationProvider(authority,
    new() { Capacity = 64, DecisionTtl = TimeSpan.FromSeconds(10) }) : null;
var lists = new PostgreSqlListEngine(runtime, null, (IListAuthorizationProvider?)cache ?? authority);
var title = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text, Required: true, MaxLength: 200);
var visibleId = Guid.NewGuid();
var hiddenId = Guid.NewGuid();
var metrics = new List<object>();
try
{
    foreach (var id in new[] { workspace, otherWorkspace })
    {
        await PostgreSqlListSetup.RegisterHostWorkspaceAsync(admin, id, "Example workspace", domain, actor.TenantId!);
        await Grant(actor, id, null, ListAuthorizationAction.UseLists, ListAuthorizationAction.CreateList);
        await Grant(actor, id, visibleId, ListAuthorizationAction.Read, ListAuthorizationAction.Write);
        await lists.CreateListAsync(actor, id, new(visibleId, "Tasks", [title]));
    }
    await Grant(actor, workspace, hiddenId, ListAuthorizationAction.Read, ListAuthorizationAction.Write);
    await lists.CreateListAsync(actor, workspace, new(hiddenId, "Private tasks", [title]));
    await Grant(reader, workspace, null, ListAuthorizationAction.UseLists);
    await Grant(reader, workspace, visibleId, ListAuthorizationAction.Read);

    var record = await lists.CreateRecordAsync(actor, workspace, visibleId,
        new(Guid.NewGuid(), 1, new JsonObject { [title.Id.ToString("D")] = "Draft" }));
    var updated = await lists.UpdateRecordAsync(actor, workspace, visibleId, record.Id,
        new(record.Revision, 1, [new(title.Id, JsonValue.Create("Ready"))]));
    Require(updated.Revision == 2, "Update did not advance the revision.");
    Require((await lists.GetRecordAsync(reader, workspace, visibleId, record.Id))
        .Values[title.Id.ToString("D")]!.GetValue<string>() == "Ready", "Read did not return the update.");
    await ExpectDenied(() => lists.GetListAsync(reader, workspace, hiddenId));
    await ExpectDenied(() => lists.GetListAsync(reader, otherWorkspace, visibleId));
    await ExpectDenied(() => lists.GetListAsync(actor with { TenantId = "another-tenant" }, workspace, visibleId));
    await ExpectDenied(() => lists.CreateRecordAsync(reader, workspace, visibleId,
        new(Guid.NewGuid(), 1, new JsonObject { [title.Id.ToString("D")] = "denied" })));

    await CheckPage(lists, candidateLimit: 1); // Includes an empty denied page and a continuation.

    // New decorator so setup cannot warm the measured keys. An undecorated batch provider
    // executes one SQL query per batch; the decorator currently splits cold misses into point calls.
    IListAuthorizationProvider measured = cacheEnabled
        ? new CachedListAuthorizationProvider(authority, new() { Capacity = 64, DecisionTtl = TimeSpan.FromSeconds(10) })
        : authority;
    var measuredLists = new PostgreSqlListEngine(runtime, null, measured);
    await Measure("mixed-page-first", () => CheckPage(measuredLists));
    await Measure("mixed-page-repeat", () => CheckPage(measuredLists));

    // Commit the permission change before invalidating this process's cache.
    await using (var revoke = admin.CreateCommand("""
        DELETE FROM example_list_permissions WHERE domain=@domain AND subject=@subject
          AND workspace=@workspace AND list_id=@list
        """))
    {
        revoke.Parameters.AddWithValue("domain", domain);
        revoke.Parameters.AddWithValue("subject", reader.Subject);
        revoke.Parameters.AddWithValue("workspace", workspace);
        revoke.Parameters.AddWithValue("list", visibleId);
        await revoke.ExecuteNonQueryAsync();
    }
    cache?.InvalidateSubject(reader.Subject);
    if (measured is CachedListAuthorizationProvider measuredCache) measuredCache.InvalidateSubject(reader.Subject);
    await ExpectDenied(() => lists.GetRecordAsync(reader, workspace, visibleId, record.Id));
    Require((await measuredLists.GetListsPageAsync(reader, workspace)).Lists.Count == 0, "Revocation leaked a list.");
    Require((await lists.GetRecordAsync(actor, workspace, visibleId, record.Id)).Revision == 2, "Denied write changed the record.");

    Console.WriteLine(JsonSerializer.Serialize(new {
        cached = cacheEnabled, permissionStore = "PostgreSQL, host-owned indexed permission rows",
        listStore = "PostgreSQL, restricted runtime role", subjects = 2, workspaces = 2, lists = 3,
        checks = "create/read/update, mixed allow/deny paging, subject/workspace/tenant separation, revocation",
        metrics, totalProviderCalls = authority.PointCalls + authority.BatchCalls, totalAuthoritySqlCalls = authority.SqlCalls,
        caveat = "Small sequential consumer scenario, not a throughput or remote-latency benchmark. Cold cached batch misses use point queries."
    }, new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    // Remove only rows belonging to this invocation, even after a failed check.
    foreach (var id in new[] { workspace, otherWorkspace })
    {
        await using var connection = await admin.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var cleanup = connection.CreateCommand();
        cleanup.Transaction = transaction;
        cleanup.CommandText = """
            SELECT set_config('cellbridge.workspace_id',@workspace::text,true);
            DELETE FROM cellbridge_lists.records WHERE workspace_id=@workspace;
            DELETE FROM cellbridge_lists.workspaces WHERE workspace_id=@workspace;
            DELETE FROM example_list_permissions WHERE domain=@domain AND workspace=@workspace;
            """;
        cleanup.Parameters.AddWithValue("workspace", id);
        cleanup.Parameters.AddWithValue("domain", domain);
        await cleanup.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }
}
return 0;

async Task Grant(ListActor subject, Guid id, Guid? listId, params ListAuthorizationAction[] actions)
{
    foreach (var action in actions)
    {
        await using var command = admin.CreateCommand("""
            INSERT INTO example_list_permissions VALUES(@domain,@tenant,@workspace,@subject,@list,@action)
            ON CONFLICT DO NOTHING
            """);
        command.Parameters.AddWithValue("domain", domain);
        command.Parameters.AddWithValue("tenant", subject.TenantId!);
        command.Parameters.AddWithValue("workspace", id);
        command.Parameters.AddWithValue("subject", subject.Subject);
        command.Parameters.AddWithValue("list", listId ?? Guid.Empty);
        command.Parameters.AddWithValue("action", action.ToString());
        await command.ExecuteNonQueryAsync();
    }
}
async Task CheckPage(PostgreSqlListEngine engine, int candidateLimit = 2)
{
    var visible = new List<Guid>();
    var emptyPages = 0;
    string? continuation = null;
    do
    {
        var page = await engine.GetListsPageAsync(reader, workspace, continuation, candidateLimit);
        visible.AddRange(page.Lists.Select(list => list.Id));
        if (page.Lists.Count == 0) emptyPages++;
        continuation = page.Continuation;
    } while (continuation is not null);
    Require(visible.Count == 1 && visible[0] == visibleId, "Pages did not filter denied candidates.");
    if (candidateLimit == 1) Require(emptyPages == 1, "Expected one denied candidate page.");
}
async Task Measure(string workload, Func<Task> execute)
{
    var before = (authority.PointCalls, authority.BatchCalls, authority.SqlCalls);
    var timer = Stopwatch.StartNew();
    await execute();
    timer.Stop();
    metrics.Add(new { workload,
        providerCalls = authority.PointCalls + authority.BatchCalls - before.PointCalls - before.BatchCalls,
        pointCalls = authority.PointCalls - before.PointCalls, batchCalls = authority.BatchCalls - before.BatchCalls,
        authoritySqlCalls = authority.SqlCalls - before.SqlCalls, elapsedMs = timer.Elapsed.TotalMilliseconds });
}
static async Task ExpectDenied(Func<Task> execute)
{
    try { await execute(); }
    catch (ListException exception) when (exception.Failure is ListFailure.NotFound or ListFailure.Forbidden) { return; }
    throw new InvalidOperationException("Unauthorized operation succeeded.");
}
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

// One SQL statement evaluates each bounded batch. Identity comes from the host's
// trusted actor; workspace/tenant/domain are additionally checked against stored anchors by CellBridge.
file sealed class PostgreSqlListAuthority(NpgsqlDataSource database, string domain) : IListBatchAuthorizationProvider
{
    private int _sqlCalls, _pointCalls, _batchCalls;
    public string PolicyDomain => domain;
    public int SqlCalls => Volatile.Read(ref _sqlCalls);
    public int PointCalls => Volatile.Read(ref _pointCalls);
    public int BatchCalls => Volatile.Read(ref _batchCalls);

    public async ValueTask<ListAuthorizationDecision> AuthorizeAsync(ListAuthorizationRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _pointCalls);
        return (await EvaluateAsync([request], ct))[0];
    }

    public async ValueTask<IReadOnlyList<ListAuthorizationDecision>> AuthorizeBatchAsync(
        IReadOnlyList<ListAuthorizationRequest> requests, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _batchCalls);
        return await EvaluateAsync(requests, ct);
    }

    private async Task<IReadOnlyList<ListAuthorizationDecision>> EvaluateAsync(
        IReadOnlyList<ListAuthorizationRequest> requests, CancellationToken ct)
    {
        Interlocked.Increment(ref _sqlCalls);
        await using var command = database.CreateCommand("""
            SELECT EXISTS (SELECT 1 FROM example_list_permissions p
              WHERE p.domain=@domain AND p.domain=r.domain AND p.tenant=r.tenant
                AND p.workspace=r.workspace AND p.subject=r.subject
                AND p.list_id=r.list_id AND p.action=r.action)
            FROM jsonb_to_recordset(@requests) AS r(ordinal integer, domain text, tenant text,
              workspace uuid, subject text, list_id uuid, action text)
            ORDER BY r.ordinal
            """);
        command.Parameters.AddWithValue("domain", domain);
        command.Parameters.AddWithValue("requests", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(requests.Select((request, ordinal) => new {
            ordinal, domain = request.PolicyDomain, tenant = request.TenantId, workspace = request.WorkspaceId,
            subject = request.Subject, list_id = request.ListId ?? Guid.Empty, action = request.Action.ToString()
        })));
        var results = new List<ListAuthorizationDecision>(requests.Count);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) results.Add(new(reader.GetBoolean(0)));
        return results;
    }
}
