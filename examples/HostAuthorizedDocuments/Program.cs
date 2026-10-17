using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
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
var id = Guid.NewGuid();
var workspace = Guid.NewGuid().ToString("D");
var actor = new CellBridgeActor(new("example-user", "example-user", "Example user"), CanCreate: true);
await SetPermissions(true);
var hostAuthority = new PostgreSqlHostAuthority(database);
IDocumentAuthorizationProvider authority = hostAuthority;
// Optional and separate. Remove this block to evaluate every operation against the host database.
if (args.Contains("--cache")) authority = new CachedDocumentAuthorizationProvider(hostAuthority,
    new() { Capacity = 512, DecisionTtl = TimeSpan.FromSeconds(10) });
var storage = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
var service = new CellBridgeDocumentService(storage, documentAuthorizationProvider: authority);
var state = await service.ImportExternalAsync(id, "/shared/example.docx", Package("original"),
    new("example-tenant", workspace), actor.Identity, actor) ?? throw new InvalidOperationException("Import failed.");
var processor = new CellBridgeRequestProcessor(service);
var request = new CellStorageRequest { Requests = { new FssHttpRequest {
    Url = "https://example.test/shared/example.docx", UseResourceId = true, ResourceId = id.ToString("D"),
    RequestToken = 1, SubRequests = { new FssHttpSubRequest { Type = SubRequestType.GetDocMetaInfo, SubRequestToken = 1 } }
} } };
await new SharedDocumentLocks(service).ApplyAsync(id, HostLockOperation.Acquire, "example-lock", actor);
var metrics = new List<object>();
await Measure("history-read", 1000, async _ => { await service.ListRevisionsAsync(id, actor); });
await Measure("direct-processor-metadata", 200, async _ => {
    var result = await processor.ExecuteAsync(request, "https://example.test", actor);
    if (result.Response.Responses[0].SubResponses[0].ErrorCode != "Success") throw new InvalidOperationException("Metadata denied.");
});
var package = Package("replacement");
await Measure("whole-file-save-with-receipts", 100, async index => {
    using var bytes = new MemoryStream(package);
    await service.SaveWholeFileAsync(id, bytes, "save-" + index, actor, "example-lock");
});
await SetPermissions(false);
if (authority is CachedDocumentAuthorizationProvider cache) cache.InvalidateSubject(actor.Identity.Subject);
try { await service.ListRevisionsAsync(id, actor); throw new InvalidOperationException("Revoked access was accepted."); }
catch (UnauthorizedAccessException) { }
Console.WriteLine(JsonSerializer.Serialize(new {
    cached = authority is CachedDocumentAuthorizationProvider,
    permissionStore = "PostgreSQL, one indexed host-owned row per resource and subject",
    documentStore = "InMemory, one DOCX resource, sequential operations, concurrency 1",
    packageBytes = package.Length, metrics, totalProviderCalls = hostAuthority.Calls,
    caveat = "Measures host policy I/O and consumer API execution, not durable document write throughput or Office interoperability."
}, new JsonSerializerOptions { WriteIndented = true }));
await using (var cleanup = database.CreateCommand("DELETE FROM example_document_permissions WHERE resource_id=@id")) {
    cleanup.Parameters.AddWithValue("id", id); await cleanup.ExecuteNonQueryAsync();
}

async Task Measure(string operation, int count, Func<int, Task> execute) {
    var calls = hostAuthority.Calls;
    var elapsed = Stopwatch.StartNew();
    for (var index = 0; index < count; index++) await execute(index);
    elapsed.Stop();
    metrics.Add(new { operation, count, providerCalls = hostAuthority.Calls - calls,
        elapsedMs = elapsed.Elapsed.TotalMilliseconds, millisecondsPerOperation = elapsed.Elapsed.TotalMilliseconds / count });
}
async Task SetPermissions(bool allowed) {
    await using var command = database.CreateCommand("""
        INSERT INTO example_document_permissions VALUES(@id,@subject,@workspace,@allow,@allow)
        ON CONFLICT(resource_id,subject) DO UPDATE SET can_read=excluded.can_read,can_write=excluded.can_write
        """);
    command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("subject", actor.Identity.Subject);
    command.Parameters.AddWithValue("workspace", workspace); command.Parameters.AddWithValue("allow", allowed);
    await command.ExecuteNonQueryAsync();
}
static byte[] Package(string text) {
    using var bytes = new MemoryStream();
    using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true)) {
        void Add(string path, string xml) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), Encoding.UTF8); writer.Write(xml); }
        Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Add("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Add("word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>" + text + "</w:t></w:r></w:p></w:body></w:document>");
    }
    return bytes.ToArray();
}

sealed class PostgreSqlHostAuthority(NpgsqlDataSource database) : IDocumentAuthorizationProvider {
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public string PolicyDomain => "example:host-permissions";
    public async ValueTask<DocumentAuthorizationDecision> AuthorizeAsync(DocumentAuthorizationRequest request,
        CancellationToken cancellationToken = default) {
        Interlocked.Increment(ref _calls);
        var evaluatedAt = DateTimeOffset.UtcNow;
        if (request.Scope.TenantId != "example-tenant") return new(false, evaluatedAt);
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
