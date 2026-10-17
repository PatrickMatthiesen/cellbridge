using System.Text.Json.Nodes;
using CellBridge.Examples.HostAuthorizedLists;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Npgsql;

const string policyDomain = "example:host-authorized-lists:v1";
const string trustedConsoleSubject = "example:trusted-console-operator";

var adminConnectionString = RequiredEnvironment("CELLBRIDGE_LISTS_ADMIN_CONNECTION_STRING");
var runtimeConnectionString = RequiredEnvironment("CELLBRIDGE_LISTS_RUNTIME_CONNECTION_STRING");
var sqlitePath = Environment.GetEnvironmentVariable("CELLBRIDGE_LISTS_AUTHORITY_PATH")
    ?? Path.Combine(Path.GetTempPath(), $"cellbridge-list-authority-{Guid.NewGuid():N}.db");

await using var adminSource = NpgsqlDataSource.Create(adminConnectionString);
await using var runtimeSource = NpgsqlDataSource.Create(runtimeConnectionString);
await using var authority = new SqliteListAuthority(sqlitePath, policyDomain);
await authority.InitializeAsync();

var workspaceId = Guid.NewGuid();
await PostgreSqlListSetup.RegisterHostWorkspaceAsync(
    adminSource, workspaceId, "Host-authorized list example", policyDomain);

var provider = new SingleProcessListAuthorizationProvider(policyDomain, authority.LoadSnapshotAsync);
var engine = new PostgreSqlListEngine(runtimeSource, new ListLimits(), provider);

// A console process has no HTTP authentication boundary. This actor is a fixed, trusted process identity.
var actor = new ListActor(trustedConsoleSubject);
var listId = Guid.NewGuid();
await engine.CoordinateGlobalAuthorizationChangeAsync(ct => authority.InTransactionAsync(async (change, token) =>
{
    await change.SetAccountEnabledAsync(actor.Subject, true, token);
    await change.SetWorkspacePermissionAsync(actor.Subject, workspaceId,
        ListWorkspaceAccess.UseLists | ListWorkspaceAccess.CreateLists,
        ListAccess.None,
        new Dictionary<Guid, ListAccess>
        {
            [listId] = ListAccess.Read | ListAccess.Write | ListAccess.Design
        }, token);
    return true;
}, ct));

var field = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text, Required: true, MaxLength: 200);
var list = await engine.CreateListAsync(actor, workspaceId,
    new CreateListRequest(listId, "Requests", [field]));
var record = await engine.CreateRecordAsync(actor, workspaceId, list.Id,
    new CreateRecordRequest(Guid.NewGuid(), list.SchemaVersion,
        new JsonObject { [field.Id.ToString("D")] = "Created through host authorization" }));
Console.WriteLine($"Created list {list.Id:D} and record {record.Id:D}.");

await engine.CoordinateAuthorizationChangeAsync(workspaceId, ct => authority.InTransactionAsync(async (change, token) =>
{
    await change.SetWorkspacePermissionAsync(actor.Subject, workspaceId,
        ListWorkspaceAccess.UseLists, ListAccess.None, ct: token);
    return true;
}, ct));

await ExpectDeniedAsync("read", () => engine.GetRecordAsync(actor, workspaceId, list.Id, record.Id));
await ExpectDeniedAsync("write", () => engine.CreateRecordAsync(actor, workspaceId, list.Id,
    new CreateRecordRequest(Guid.NewGuid(), list.SchemaVersion, new JsonObject
    {
        [field.Id.ToString("D")] = "This write must be denied"
    })));

static string RequiredEnvironment(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} is required.");

static async Task ExpectDeniedAsync(string operation, Func<Task> action)
{
    try
    {
        await action();
        throw new InvalidOperationException($"The revoked {operation} unexpectedly succeeded.");
    }
    catch (ListException ex) when (ex.Failure is ListFailure.Forbidden or ListFailure.NotFound)
    {
        Console.WriteLine($"Revoked {operation} was denied.");
    }
}
