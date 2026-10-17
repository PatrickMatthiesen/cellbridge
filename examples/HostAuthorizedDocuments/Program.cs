using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using Npgsql;

// This host owns permissions. CellBridge owns protocol/state/lock enforcement.
var connection = Environment.GetEnvironmentVariable("ConnectionStrings__authority")
    ?? throw new InvalidOperationException("Set ConnectionStrings__authority to a disposable PostgreSQL database.");
await using var database = NpgsqlDataSource.Create(connection);
await using (var command = database.CreateCommand("""
    CREATE TABLE IF NOT EXISTS example_document_permissions (
      resource_id uuid NOT NULL, subject text NOT NULL, workspace text NOT NULL,
      can_read boolean NOT NULL, can_write boolean NOT NULL, PRIMARY KEY(resource_id,subject));
    """)) await command.ExecuteNonQueryAsync();
if (args.Any(argument => argument is not ("--cache" or "--durable")))
    throw new ArgumentException("Supported switches: --cache, --durable.");
const int cacheCapacity = 32;
const int resourceCount = 64;
const int concurrency = 8;
var id = Guid.NewGuid();
var workspace = Guid.NewGuid().ToString("D");
var actor = new CellBridgeActor(new("example-user", "example-user", "Example user"), CanCreate: true);
var reader = new CellBridgeActor(new("example-reader", "example-reader", "Example reader"), CanCreate: true);
var hostAuthority = new PostgreSqlHostAuthority(database);
IDocumentAuthorizationProvider authority = hostAuthority;
// Optional and separate. Remove this block to evaluate every operation against the host database.
if (args.Contains("--cache")) authority = new CachedDocumentAuthorizationProvider(hostAuthority,
    new() { Capacity = cacheCapacity, DecisionTtl = TimeSpan.FromSeconds(10) });
var durable = args.Contains("--durable");
StorageProvider storage;
if (durable)
{
    var stateStore = new PostgreSqlStateStore(database);
    await stateStore.InitializeAsync();
    storage = new(stateStore, new PostgreSqlContentStore(database));
}
else storage = new(new InMemoryStateStore(), new InMemoryContentStore());
var service = new CellBridgeDocumentService(storage, documentAuthorizationProvider: authority);
var metrics = new List<object>();
var checks = new List<string>();
try
{
    await SetPermissions(id, actor, read: true, write: true);
    await SetPermissions(id, reader, read: true, write: false);
    var state = await service.CreateExternalAsync(id, $"/shared/{workspace}/example.docx", Package("original"),
        new("example-tenant", workspace), actor) ?? throw new InvalidOperationException("Create failed.");
    var processor = new CellBridgeRequestProcessor(service);
    var request = new CellStorageRequest
    {
        Requests = { new FssHttpRequest {
    Url = "https://example.test" + state.Path, UseResourceId = true, ResourceId = id.ToString("D"),
    RequestToken = 1, SubRequests = { new FssHttpSubRequest { Type = SubRequestType.GetDocMetaInfo, SubRequestToken = 1 } }
} }
    };
    var locks = new SharedDocumentLocks(service);
    if (!(await locks.ApplyAsync(id, HostLockOperation.Acquire, "example-lock", actor)).Success)
        throw new InvalidOperationException("Initial lock was not acquired.");
    await service.ListRevisionsAsync(id, reader);
    await ExpectDenied("reader-cannot-save", async () =>
        await service.SaveWholeFileAsync(id, new MemoryStream(Package("reader must not write")), "reader-save", reader));
    var deniedCreate = Guid.NewGuid();
    await SetPermissions(deniedCreate, reader, read: true, write: false);
    await ExpectDenied("create-check-despite-CanCreate", async () =>
        await service.CreateExternalAsync(deniedCreate, $"/shared/{workspace}/denied.docx", Package("denied"),
            new("example-tenant", workspace), reader));
    if (await storage.State.FindByResourceIdAsync(deniedCreate) is not null)
        throw new InvalidOperationException("Denied creation left document state behind.");
    await Measure("history-read", 1000, async _ => { await service.ListRevisionsAsync(id, actor); });
    await Measure("direct-processor-metadata", 200, async _ =>
    {
        var result = await processor.ExecuteAsync(request, "https://example.test", actor);
        if (result.Response.Responses[0].SubResponses[0].ErrorCode != "Success") throw new InvalidOperationException("Metadata denied.");
    });
    var packages = Enumerable.Range(0, 100).Select(index => Package("replacement " + index)).ToArray();
    await Measure("changed-whole-file-save-and-readback", packages.Length, async index =>
    {
        using var bytes = new MemoryStream(packages[index]);
        var saved = await service.SaveWholeFileAsync(id, bytes, "save-" + index, actor, "example-lock");
        if (saved.IsReplay || !(await storage.Content.ReadVerifiedAsync(saved.Revision.Content)).SequenceEqual(packages[index]))
            throw new InvalidOperationException("Saved content did not match the submitted revision.");
    });
    var currentAfterSaves = (await storage.State.FindByResourceIdAsync(id))!;
    if (currentAfterSaves.ContentVersion != state.ContentVersion + packages.Length ||
        !(await storage.Content.ReadVerifiedAsync(currentAfterSaves.Content)).SequenceEqual(packages[^1]))
        throw new InvalidOperationException("Authoritative content did not advance through every save.");
    checks.Add("100-changing-saves-with-verified-revision-and-current-bytes");

    // Exceed cache capacity and interleave hot and cold resources with actual concurrent operations.
    var resources = new List<Guid> { id };
    for (var index = 1; index < resourceCount; index++)
    {
        var resource = Guid.NewGuid();
        await SetPermissions(resource, actor, read: true, write: true);
        await service.ImportExternalAsync(resource, $"/shared/{workspace}/file-{index}.docx", Package("resource " + index),
            new("example-tenant", workspace), actor.Identity, actor);
        resources.Add(resource);
    }
    await MeasureConcurrent("mixed-hot-cold-history", 512, async index =>
        await service.ListRevisionsAsync(index % 2 == 0 ? id : resources[(index / 2) % resources.Count], actor));

    var scopedId = Guid.NewGuid();
    await SetPermissions(scopedId, actor, read: true, write: true);
    await service.ImportExternalAsync(scopedId, $"/shared/{workspace}/other-workspace.docx", Package("private"),
        new("example-tenant", "other-" + workspace), actor.Identity, actor);
    await ExpectDenied("workspace-scope-isolation", async () => await service.ListRevisionsAsync(scopedId, actor));

    // Re-warm both actions immediately before revocation; test Write as well as Read invalidation.
    if (await service.GetAccessAsync(actor, (await storage.State.FindByResourceIdAsync(id))!) !=
        (DocumentAccess.Read | DocumentAccess.Write))
        throw new InvalidOperationException("Writer lost access before the revocation check.");
    await SetPermissions(id, actor, read: false, write: false);
    if (authority is CachedDocumentAuthorizationProvider cache) cache.InvalidateSubject(actor.Identity.Subject);
    var beforeDenials = JsonSerializer.Serialize(await storage.State.FindByResourceIdAsync(id));
    await ExpectDenied("revoked-history-read", async () => await service.ListRevisionsAsync(id, actor));
    await ExpectDenied("revoked-new-save", async () =>
        await service.SaveWholeFileAsync(id, new MemoryStream(Package("revoked")), "revoked-save", actor, "example-lock"));
    await ExpectDenied("revoked-receipt-replay", async () =>
        await service.SaveWholeFileAsync(id, new MemoryStream(packages[^1]), "save-99", actor, "example-lock"));
    await ExpectDenied("revoked-lock-refresh", async () =>
        await locks.ApplyAsync(id, HostLockOperation.Refresh, "example-lock", actor));
    var deniedProtocol = await processor.ExecuteAsync(request, "https://example.test", actor);
    if (deniedProtocol.Response.Responses.Single().ErrorCode != "FileUnauthorizedAccess")
        throw new InvalidOperationException("Revoked protocol request was accepted.");
    if (beforeDenials != JsonSerializer.Serialize(await storage.State.FindByResourceIdAsync(id)))
        throw new InvalidOperationException("Denied operations changed document state or receipts.");
    checks.Add("revoked-protocol-denied-with-unchanged-document-state");

    // A grant must invalidate a cached deny too. A revoked user's old lock is a separate lifetime.
    if ((await service.GetAccessAsync(reader, (await storage.State.FindByResourceIdAsync(id))!)).HasFlag(DocumentAccess.Write))
        throw new InvalidOperationException("Reader unexpectedly had write permission before the grant.");
    await SetPermissions(id, reader, read: true, write: true);
    if (authority is CachedDocumentAuthorizationProvider grantCache) grantCache.InvalidateResource(id);
    if (!(await service.GetAccessAsync(reader, (await storage.State.FindByResourceIdAsync(id))!)).HasFlag(DocumentAccess.Write))
        throw new InvalidOperationException("New grant was not visible after invalidation.");
    if ((await locks.ApplyAsync(id, HostLockOperation.Acquire, "reader-lock", reader)).Success)
        throw new InvalidOperationException("Revocation unexpectedly removed the previous editor's lock.");
    checks.Add("new-grant-visible-but-revoked-editor-lease-retained");

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        cached = authority is CachedDocumentAuthorizationProvider,
        permissionStore = "PostgreSQL, one indexed host-owned row per resource and subject",
        documentStore = durable ? "PostgreSQL" : "InMemory",
        cacheCapacity,
        cacheTtlSeconds = 10,
        resourceCount,
        mixedReadConcurrency = concurrency,
        packageBytes = packages[^1].Length,
        revisionCount = currentAfterSaves.Revisions.Length,
        documentStateJsonBytes = Encoding.UTF8.GetByteCount(beforeDenials),
        metrics,
        checks,
        totalProviderCalls = hostAuthority.Calls,
        caveat = "Synthetic consumer workload. No HTTP transport, external destination, fault recovery or Office interoperability qualification."
    }, new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    await using var cleanup = database.CreateCommand("DELETE FROM example_document_permissions WHERE workspace=@workspace");
    cleanup.Parameters.AddWithValue("workspace", workspace);
    await cleanup.ExecuteNonQueryAsync();
}

async Task ExpectDenied(string name, Func<Task> execute)
{
    Console.Error.WriteLine("Checking " + name);
    try { await execute(); }
    catch (UnauthorizedAccessException) { checks.Add(name); return; }
    throw new InvalidOperationException(name + " was unexpectedly permitted.");
}

async Task MeasureConcurrent(string operation, int count, Func<int, Task> execute)
{
    Console.Error.WriteLine("Running " + operation);
    var calls = hostAuthority.Calls;
    var elapsed = Stopwatch.StartNew();
    await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = concurrency },
        async (index, _) => await execute(index));
    elapsed.Stop();
    metrics.Add(new
    {
        operation,
        count,
        concurrency,
        providerCalls = hostAuthority.Calls - calls,
        elapsedMs = elapsed.Elapsed.TotalMilliseconds
    });
}

async Task Measure(string operation, int count, Func<int, Task> execute)
{
    Console.Error.WriteLine("Running " + operation);
    var calls = hostAuthority.Calls;
    var elapsed = Stopwatch.StartNew();
    for (var index = 0; index < count; index++) await execute(index);
    elapsed.Stop();
    metrics.Add(new
    {
        operation,
        count,
        providerCalls = hostAuthority.Calls - calls,
        elapsedMs = elapsed.Elapsed.TotalMilliseconds,
        millisecondsPerOperation = elapsed.Elapsed.TotalMilliseconds / count
    });
}
async Task SetPermissions(Guid resource, CellBridgeActor subject, bool read, bool write)
{
    await using var command = database.CreateCommand("""
        INSERT INTO example_document_permissions(resource_id,subject,workspace,can_read,can_write)
        VALUES(@id,@subject,@workspace,@read,@write)
        ON CONFLICT(resource_id,subject) DO UPDATE SET can_read=excluded.can_read,can_write=excluded.can_write
        """);
    command.Parameters.AddWithValue("id", resource); command.Parameters.AddWithValue("subject", subject.Identity.Subject);
    command.Parameters.AddWithValue("workspace", workspace); command.Parameters.AddWithValue("read", read);
    command.Parameters.AddWithValue("write", write);
    await command.ExecuteNonQueryAsync();
}
static byte[] Package(string text)
{
    using var bytes = new MemoryStream();
    using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
    {
        void Add(string path, string xml) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), Encoding.UTF8); writer.Write(xml); }
        Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Add("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Add("word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>" + text + "</w:t></w:r></w:p></w:body></w:document>");
    }
    return bytes.ToArray();
}

sealed class PostgreSqlHostAuthority(NpgsqlDataSource database) : IDocumentAuthorizationProvider
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public string PolicyDomain => "example:host-permissions";
    public async ValueTask<DocumentAuthorizationDecision> AuthorizeAsync(DocumentAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        var evaluatedAt = DateTimeOffset.UtcNow;
        if (request.Scope.TenantId != "example-tenant" || request.Scope.WorkspaceId is null ||
            !Enum.IsDefined(request.Action)) return new(false, evaluatedAt);
        await using var command = database.CreateCommand("""
            SELECT CASE WHEN @write THEN can_write ELSE can_read END
            FROM example_document_permissions WHERE resource_id=@id AND subject=@subject AND workspace=@workspace
            """);
        command.Parameters.AddWithValue("write", request.Action != DocumentAction.Read);
        command.Parameters.AddWithValue("id", request.ResourceId); command.Parameters.AddWithValue("subject", request.Subject);
        command.Parameters.AddWithValue("workspace", request.Scope.WorkspaceId!);
        return new(await command.ExecuteScalarAsync(cancellationToken) is true, evaluatedAt);
    }
}
