using System.Collections;
using System.Diagnostics;
using System.Reflection;
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
var saveCount = 100;
var payloadBytes = 0;
foreach (var argument in args)
{
    if (argument.StartsWith("--saves=", StringComparison.Ordinal) &&
        int.TryParse(argument[8..], out var saves) && saves is >= 1 and <= 100) saveCount = saves;
    else if (argument.StartsWith("--payload-bytes=", StringComparison.Ordinal) &&
        int.TryParse(argument[16..], out var payload) && payload is >= 0 and <= 4 * 1024 * 1024) payloadBytes = payload;
    else if (argument is not ("--cache" or "--durable" or "--measure-gc" or "--read-metrics"))
        throw new ArgumentException("Supported switches: --cache, --durable, --saves=1..100, --payload-bytes=0..4194304, --measure-gc, --read-metrics.");
}
var payloadRandom = new Random(42);
var extraText = new string(Enumerable.Range(0, payloadBytes).Select(_ => (char)payloadRandom.Next('a', 'z' + 1)).ToArray());
var allocationStart = GC.GetTotalAllocatedBytes(precise: true);
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
var readMetrics = args.Contains("--read-metrics") ? new ReadBenchmarkAdapter(service) : null;
var metrics = new List<object>();
var checks = new List<string>();
try
{
    await SetPermissions(id, actor, read: true, write: true);
    await SetPermissions(id, reader, read: true, write: false);
    var initialPackage = Package("original");
    var state = await service.CreateExternalAsync(id, $"/shared/{workspace}/example.docx", initialPackage,
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
    var packages = Enumerable.Range(0, saveCount).Select(index => Package("replacement " + index)).ToArray();
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
    checks.Add(saveCount + "-changing-saves-with-verified-revision-and-current-bytes");
    foreach (var index in new[] { -1, 0, packages.Length / 2, packages.Length - 1 }.Distinct())
    {
        var revision = await service.GetRevisionAsync(id, state.Revisions[0].RevisionNumber + (ulong)(index + 1), actor);
        var expected = index < 0 ? initialPackage : packages[index];
        if (!(await storage.Content.ReadVerifiedAsync(revision.Content)).SequenceEqual(expected))
            throw new InvalidOperationException("Historical revision bytes changed after subsequent saves.");
    }
    checks.Add("original-first-middle-latest-historical-bytes");

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

    if (readMetrics is not null)
    {
        const int pageSize = 8;
        var expectedHistory = new Dictionary<Guid, BenchmarkRevision[]>();
        foreach (var resource in resources)
            expectedHistory.Add(resource, (await service.ListRevisionsAsync(resource, actor))
                .Select(BenchmarkRevision.Capture).ToArray());
        var expectedCatalog = await AuthorizedDocumentCatalog.ReadAsync(service, actor, 0, resourceCount);
        if (expectedCatalog.NextOffset is not null || expectedCatalog.Documents.Count != resourceCount ||
            !expectedCatalog.Documents.Select(document => document.ResourceId).ToHashSet().SetEquals(resources))
            throw new InvalidOperationException("The catalog fixture did not contain every authorized resource.");
        await Measure("authorized-catalog-pages", 200, async index =>
        {
            var offset = index % (resourceCount / 16) * 16;
            var page = await AuthorizedDocumentCatalog.ReadAsync(service, actor, offset, 16);
            var expected = expectedCatalog.Documents.Skip(offset).Take(16).ToArray();
            if (page.NextOffset != (offset + 16 < resourceCount ? offset + 16 : null) ||
                !page.Documents.Select(document => (document.ResourceId, document.Path, document.Length,
                    document.ContentVersion, document.ModifiedUtc, document.ActiveEditors))
                    .SequenceEqual(expected.Select(document => (document.ResourceId, document.Path, document.Length,
                        document.ContentVersion, document.ModifiedUtc, document.ActiveEditors))))
                throw new InvalidOperationException("A catalog page changed authorized identities or metadata.");
        });
        await MeasureConcurrent("mixed-paged-revision-summaries", 512, async index =>
        {
            var resource = index % 2 == 0 ? id : resources[(index / 2) % resources.Count];
            var revisions = expectedHistory[resource];
            var offset = resource == id ? (index / 2) % ((revisions.Length + pageSize - 1) / pageSize) * pageSize : 0;
            var page = await readMetrics.ListAsync(resource, offset, pageSize, actor);
            if (page.NextOffset != (offset + pageSize < revisions.Length ? offset + pageSize : null) ||
                !page.Revisions.SequenceEqual(revisions.Skip(offset).Take(pageSize)))
                throw new InvalidOperationException("A revision-summary page changed historical metadata.");
        });
        var historical = new[] { -1, 0, packages.Length / 2, packages.Length - 1 }.Distinct()
            .Select(index => (Revision: state.Revisions[0].RevisionNumber + (ulong)(index + 1),
                Bytes: index < 0 ? initialPackage : packages[index])).ToArray();
        await MeasureConcurrent("selected-historical-download-metadata", 512, async index =>
        {
            var selected = historical[index % historical.Length];
            var summary = await readMetrics.GetAsync(id, selected.Revision, actor);
            if (summary != expectedHistory[id].Single(revision => revision.RevisionNumber == selected.Revision))
                throw new InvalidOperationException("Selected historical download metadata changed.");
        });
        foreach (var selected in historical)
        {
            var summary = await readMetrics.GetAsync(id, selected.Revision, actor);
            if (!(await storage.Content.ReadVerifiedAsync(summary.Content)).SequenceEqual(selected.Bytes))
                throw new InvalidOperationException("Selected metadata did not resolve to the historical file bytes.");
        }
        checks.Add("authorized-catalog-page-identities-and-metadata");
        checks.Add("paged-revision-summary-order-content-and-continuations");
        checks.Add("selected-historical-download-handles-with-verified-bytes");
    }

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
    if (readMetrics is not null)
    {
        await ExpectDenied("revoked-revision-summary-page", async () => await readMetrics.ListAsync(id, 0, 8, actor));
        await ExpectDenied("revoked-selected-historical-metadata", async () =>
            await readMetrics.GetAsync(id, state.Revisions[0].RevisionNumber, actor));
        if ((await AuthorizedDocumentCatalog.ReadAsync(service, actor, 0, resourceCount))
            .Documents.Any(document => document.ResourceId == id))
            throw new InvalidOperationException("The catalog exposed a revoked document.");
        checks.Add("revoked-document-absent-from-authorized-catalog");
    }
    await ExpectDenied("revoked-new-save", async () =>
        await service.SaveWholeFileAsync(id, new MemoryStream(Package("revoked")), "revoked-save", actor, "example-lock"));
    await ExpectDenied("revoked-receipt-replay", async () =>
        await service.SaveWholeFileAsync(id, new MemoryStream(packages[^1]), "save-" + (saveCount - 1), actor, "example-lock"));
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

    long? persistedCurrentStateBytes = null;
    long? retainedStateJsonBytes = null;
    long? retainedStateVersions = null;
    long? retainedDatabaseBytes = null;
    if (durable)
    {
        await using var stateBytes = database.CreateCommand("""
            SELECT octet_length(s.state_json)::bigint,
                   (SELECT sum(octet_length(state_json))::bigint FROM cellbridge_states WHERE resource_id=@id),
                   (SELECT count(*) FROM cellbridge_states WHERE resource_id=@id),
                   (SELECT sum(pg_total_relation_size(quote_ident(schemaname)||'.'||quote_ident(tablename)))::bigint
                    FROM pg_tables WHERE schemaname='public')
            FROM cellbridge_documents d JOIN cellbridge_states s USING(resource_id,state_version)
            WHERE d.resource_id=@id
            """);
        stateBytes.Parameters.AddWithValue("id", id);
        await using var stateBytesReader = await stateBytes.ExecuteReaderAsync();
        if (!await stateBytesReader.ReadAsync()) throw new InvalidOperationException("Current durable document disappeared.");
        persistedCurrentStateBytes = stateBytesReader.GetInt64(0);
        retainedStateJsonBytes = stateBytesReader.GetInt64(1);
        retainedStateVersions = stateBytesReader.GetInt64(2);
        retainedDatabaseBytes = stateBytesReader.GetInt64(3);
    }
    var totalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocationStart;
    var managedHeapBeforeGcBytes = GC.GetTotalMemory(forceFullCollection: false);
    long? postGcLiveManagedBytes = null;
    if (args.Contains("--measure-gc"))
    {
        Console.Error.WriteLine("Measurement-only full GC after all workload operations");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        postGcLiveManagedBytes = GC.GetTotalMemory(forceFullCollection: false);
    }
    // Keep this optional codec probe outside allocation and live-heap measurements so
    // the same executable can also measure older CellBridge dependency builds.
    var storageSizeMethod = typeof(DocumentState).Assembly
        .GetType("CellBridge.Storage.Abstractions.DocumentStateJson")?.GetMethod("GetSerializedSize");
    var encodedStateBytes = storageSizeMethod?.Invoke(null, [currentAfterSaves]) as long?
        ?? persistedCurrentStateBytes ?? Encoding.UTF8.GetByteCount(beforeDenials);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        cached = authority is CachedDocumentAuthorizationProvider,
        permissionStore = "PostgreSQL, one indexed host-owned row per resource and subject",
        documentStore = durable ? "PostgreSQL" : "InMemory",
        cacheCapacity,
        cacheTtlSeconds = 10,
        resourceCount,
        mixedReadConcurrency = concurrency,
        selectiveReadBenchmark = readMetrics?.Implementation,
        changedSaveCount = saveCount,
        extraTextBytes = payloadBytes,
        packageBytes = packages[^1].Length,
        totalAllocatedBytes,
        managedHeapBeforeGcBytes,
        postGcLiveManagedBytes,
        postGcOwners = "The service, stores, current state, packages and expanded denial-check JSON remain owned by this workload.",
        encodedStateBytes,
        persistedCurrentStateBytes,
        retainedStateJsonBytes,
        retainedStateVersions,
        retainedDatabaseBytes,
        revisionCount = currentAfterSaves.Revisions.Length,
        documentStateJsonBytes = Encoding.UTF8.GetByteCount(beforeDenials),
        metrics,
        checks,
        totalProviderCalls = hostAuthority.Calls,
        caveat = "Synthetic consumer workload. No HTTP transport, external destination, fault recovery or Office interoperability qualification."
    }, new JsonSerializerOptions { WriteIndented = true }));
    GC.KeepAlive(service);
    GC.KeepAlive(storage);
    GC.KeepAlive(currentAfterSaves);
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
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
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
        allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
        elapsedMs = elapsed.Elapsed.TotalMilliseconds
    });
}

async Task Measure(string operation, int count, Func<int, Task> execute)
{
    Console.Error.WriteLine("Running " + operation);
    var calls = hostAuthority.Calls;
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    var elapsed = Stopwatch.StartNew();
    for (var index = 0; index < count; index++) await execute(index);
    elapsed.Stop();
    metrics.Add(new
    {
        operation,
        count,
        providerCalls = hostAuthority.Calls - calls,
        allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
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
byte[] Package(string text)
{
    using var bytes = new MemoryStream();
    using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
    {
        void Add(string path, string xml) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), Encoding.UTF8); writer.Write(xml); }
        Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Add("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Add("word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>" + text + extraText + "</w:t></w:r></w:p></w:body></w:document>");
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


// The optional adapter keeps one measurement executable usable with older dependency
// builds. Baseline reads project the existing full-history APIs to the same fields.
sealed record BenchmarkRevision(Guid ResourceId, long LifecycleGeneration, ulong RevisionNumber,
    uint ContentVersion, DateTime CreatedUtc, SubjectIdentity Author, ContentHandle Content)
{
    public static BenchmarkRevision Capture(DocumentRevision value) => new(value.ResourceId,
        value.LifecycleGeneration, value.RevisionNumber, value.ContentVersion, value.CreatedUtc, value.Author, value.Content);
    public static BenchmarkRevision Capture(object value) => new(Read<Guid>(value, "ResourceId"),
        Read<long>(value, "LifecycleGeneration"), Read<ulong>(value, "RevisionNumber"),
        Read<uint>(value, "ContentVersion"), Read<DateTime>(value, "CreatedUtc"),
        Read<SubjectIdentity>(value, "Author"), Read<ContentHandle>(value, "Content"));
    public static T Read<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
}
sealed record BenchmarkRevisionPage(IReadOnlyList<BenchmarkRevision> Revisions, int? NextOffset);
sealed class ReadBenchmarkAdapter(CellBridgeDocumentService service)
{
    private readonly MethodInfo? _list = typeof(CellBridgeDocumentService).GetMethod("ListRevisionSummariesAsync",
        [typeof(Guid), typeof(int), typeof(int), typeof(CellBridgeActor), typeof(CancellationToken)]);
    private readonly MethodInfo? _get = typeof(CellBridgeDocumentService).GetMethod("GetRevisionSummaryAsync",
        [typeof(Guid), typeof(ulong), typeof(CellBridgeActor), typeof(CancellationToken)]);
    public string Implementation => _list is not null && _get is not null
        ? "Selective metadata APIs with a reflection measurement adapter"
        : "Compatibility projection of full-history APIs";
    public async ValueTask<BenchmarkRevisionPage> ListAsync(Guid resource, int offset, int limit, CellBridgeActor actor)
    {
        if (_list is null)
        {
            var revisions = await service.ListRevisionsAsync(resource, actor);
            return new(revisions.Skip(offset).Take(limit).Select(BenchmarkRevision.Capture).ToArray(),
                offset + limit < revisions.Count ? offset + limit : null);
        }
        var page = await Invoke(_list, [resource, offset, limit, actor, CancellationToken.None]);
        return new(BenchmarkRevision.Read<IEnumerable>(page, "Revisions").Cast<object>()
            .Select(BenchmarkRevision.Capture).ToArray(), BenchmarkRevision.Read<int?>(page, "NextOffset"));
    }
    public async ValueTask<BenchmarkRevision> GetAsync(Guid resource, ulong revision, CellBridgeActor actor)
    {
        if (_get is null) return BenchmarkRevision.Capture(await service.GetRevisionAsync(resource, revision, actor));
        return BenchmarkRevision.Capture(await Invoke(_get, [resource, revision, actor, CancellationToken.None]));
    }
    private async ValueTask<object> Invoke(MethodInfo method, object[] arguments)
    {
        object pending;
        try { pending = method.Invoke(service, arguments)!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        var task = (Task)pending.GetType().GetMethod("AsTask")!.Invoke(pending, null)!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }
}
