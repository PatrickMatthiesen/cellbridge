using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using Npgsql;

var files = args.Where(argument => !argument.StartsWith("--", StringComparison.Ordinal)).ToArray();
if (files.Length != 2 || args.Any(argument => argument.StartsWith("--", StringComparison.Ordinal)
    && argument is not ("--cache" or "--durable")))
{
    Console.Error.WriteLine("Usage: HostAuthorizedDocuments <original.docx> <edited.docx> [--cache] [--durable]");
    return 1;
}

var connection = Environment.GetEnvironmentVariable("ConnectionStrings__authority")
    ?? throw new InvalidOperationException("Set ConnectionStrings__authority to a disposable PostgreSQL database.");
await using var database = NpgsqlDataSource.Create(connection);
await using (var setup = database.CreateCommand("""
    CREATE TABLE IF NOT EXISTS example_document_permissions (
        resource_id uuid NOT NULL,
        subject text NOT NULL,
        workspace text NOT NULL,
        can_read boolean NOT NULL,
        can_write boolean NOT NULL,
        PRIMARY KEY (resource_id, subject));
    """))
{
    await setup.ExecuteNonQueryAsync();
}

var hostAuthority = new PostgreSqlHostAuthority(database);
var cache = args.Contains("--cache")
    ? new CachedDocumentAuthorizationProvider(hostAuthority, new() { DecisionTtl = TimeSpan.FromSeconds(10) })
    : null;
IDocumentAuthorizationProvider authority = cache is null ? hostAuthority : cache;

StorageProvider storage;
if (args.Contains("--durable"))
{
    var state = new PostgreSqlStateStore(database);
    await state.InitializeAsync();
    storage = new(state, new PostgreSqlContentStore(database));
}
else
{
    storage = new(new InMemoryStateStore(), new InMemoryContentStore());
}
var documents = new CellBridgeDocumentService(storage, documentAuthorizationProvider: authority);
var actor = new CellBridgeActor(new("example-user", "example-user", "Example user"), CanCreate: true);
var resourceId = Guid.NewGuid();
var workspace = Guid.NewGuid().ToString("D");
var scope = new DocumentAuthorizationScope("example-tenant", workspace);

try
{
    await using (var grant = database.CreateCommand("""
        INSERT INTO example_document_permissions (resource_id, subject, workspace, can_read, can_write)
        VALUES (@resource, @subject, @workspace, true, true)
        """))
    {
        grant.Parameters.AddWithValue("resource", resourceId);
        grant.Parameters.AddWithValue("subject", actor.Identity.Subject);
        grant.Parameters.AddWithValue("workspace", workspace);
        await grant.ExecuteNonQueryAsync();
    }

    var original = await File.ReadAllBytesAsync(files[0]);
    var path = $"/examples/{resourceId}/document{Path.GetExtension(files[0])}";
    var created = await documents.CreateExternalAsync(resourceId, path, original, scope, actor)
        ?? throw new InvalidOperationException("The document already exists.");
    Console.WriteLine($"Created document {resourceId}.");

    var locks = new SharedDocumentLocks(documents);
    const string lockToken = "example-edit-session";
    if (!(await locks.ApplyAsync(resourceId, HostLockOperation.Acquire, lockToken, actor)).Success)
        throw new InvalidOperationException("The document is locked by another editor.");
    try
    {
        await using var edited = File.OpenRead(files[1]);
        // Reuse this operation key when retrying the same save.
        var saved = await documents.SaveWholeFileAsync(resourceId, edited, "example-save-1", actor, lockToken);
        Console.WriteLine($"Saved revision {saved.Revision.RevisionNumber}.");
    }
    finally
    {
        await locks.ApplyAsync(resourceId, HostLockOperation.Release, lockToken, actor);
    }

    var history = await documents.ListRevisionSummariesAsync(resourceId, offset: 0, limit: 10, actor);
    foreach (var revision in history.Revisions)
        Console.WriteLine($"Revision {revision.RevisionNumber}: {revision.Content.Length} bytes, {revision.CreatedUtc:u}");

    var selected = await documents.GetRevisionSummaryAsync(resourceId, created.Revisions[0].RevisionNumber, actor);
    // Content storage is trusted host infrastructure. Authorize the selection before reading its handle.
    var historicalBytes = await storage.Content.ReadVerifiedAsync(selected.Content);
    Console.WriteLine($"Read {historicalBytes.Length} bytes from original revision {selected.RevisionNumber}.");

    // Commit the host permission change, then invalidate every affected local cache.
    await RemovePermissionAsync();
    cache?.InvalidateResource(resourceId);
    try
    {
        await documents.ListRevisionSummariesAsync(resourceId, offset: 0, limit: 10, actor);
        throw new InvalidOperationException("Access remained allowed after revocation.");
    }
    catch (UnauthorizedAccessException)
    {
        Console.WriteLine("Access was revoked by the host.");
    }
}
finally
{
    await RemovePermissionAsync();
}
return 0;

async Task RemovePermissionAsync()
{
    await using var remove = database.CreateCommand("""
        DELETE FROM example_document_permissions WHERE resource_id = @resource AND subject = @subject
        """);
    remove.Parameters.AddWithValue("resource", resourceId);
    remove.Parameters.AddWithValue("subject", actor.Identity.Subject);
    await remove.ExecuteNonQueryAsync();
}

sealed class PostgreSqlHostAuthority(NpgsqlDataSource database) : IDocumentAuthorizationProvider
{
    public string PolicyDomain => "example:host-permissions";

    public async ValueTask<DocumentAuthorizationDecision> AuthorizeAsync(DocumentAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Scope.TenantId != "example-tenant" || request.Scope.WorkspaceId is null
            || request.Action is not (DocumentAction.Read or DocumentAction.Write or DocumentAction.Create))
            return new(false);

        await using var command = database.CreateCommand("""
            SELECT CASE WHEN @write THEN can_write ELSE can_read END
            FROM example_document_permissions
            WHERE resource_id = @resource AND subject = @subject AND workspace = @workspace
            """);
        command.Parameters.AddWithValue("write", request.Action != DocumentAction.Read);
        command.Parameters.AddWithValue("resource", request.ResourceId);
        command.Parameters.AddWithValue("subject", request.Subject);
        command.Parameters.AddWithValue("workspace", request.Scope.WorkspaceId);
        return new(await command.ExecuteScalarAsync(cancellationToken) is true);
    }
}
