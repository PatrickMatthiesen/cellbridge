using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.FileSystem;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Npgsql;
using Microsoft.Extensions.Configuration;

// Offline workloads; use a disposable database/content root. No live Office claim.
string Option(string name, string fallback) => args.SkipWhile(x => x != name).Skip(1).FirstOrDefault() ?? fallback;
var sizes = Option("--sizes", "1,10").Split(',').Select(int.Parse).ToArray();
var clients = Option("--clients", "1,8").Split(',').Select(int.Parse).ToArray();
var iterations = int.Parse(Option("--iterations", "3"));
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);
var workload = Option("--workload", "full");
if (workload is not ("full" or "changed-full" or "delta" or "delta-chain" or "captured"))
    throw new ArgumentException("Workload must be full, changed-full, delta, delta-chain or captured.");
if (workload == "captured" && iterations != 2) throw new ArgumentException("Captured replay has exactly two saves; use --iterations 2.");
int seed = int.Parse(Option("--seed", "42"));
int queryEvery = int.Parse(Option("--query-every", "32"));
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queryEvery);
bool allowQuota = args.Contains("--allow-quota", StringComparer.Ordinal);
var limits = new ConfigurationBuilder().AddEnvironmentVariables().Build().GetSection("Storage").Get<StorageLimits>() ?? new StorageLimits();
limits.Validate();
var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge");
var backend = Option("--content", connectionString is null ? "memory" : "postgresql");
if (backend is not ("memory" or "postgresql" or "filesystem") || (backend != "memory" && connectionString is null))
    throw new ArgumentException("Durable benchmarks require ConnectionStrings__cellbridge; content must be memory, postgresql or filesystem.");
await using var database = await BenchmarkDatabase.OpenAsync(backend == "memory" ? null : connectionString, limits);
var source = database?.Source;
IContentStore content = backend switch
{
    "memory" => new InMemoryContentStore(),
    "postgresql" when source is not null => new PostgreSqlContentStore(source, limits.MaxObjectBytes),
    "filesystem" when source is not null => new PostgreSqlFileSystemContentStore(source, Option("--root", Path.Combine(Path.GetTempPath(), "cellbridge-benchmark-" + Guid.NewGuid().ToString("N"))), limits.MaxObjectBytes),
    _ => throw new ArgumentException("Content must be memory, postgresql or filesystem; PostgreSQL requires its connection string."),
};
IDocumentStateStore stateStore = source is null ? new InMemoryStateStore() : new PostgreSqlStateStore(source, limits);
var provider = new StorageProvider(stateStore, content, limits);
provider.Require(durable: source is not null, multipleInstances: false);
await stateStore.CheckHealthAsync();
var report = new List<object>();
foreach (int size in sizes)
foreach (int concurrency in clients)
{
    ArgumentOutOfRangeException.ThrowIfNegative(size);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
    var payload = Package(size, seed);
    var samples = new ConcurrentBag<double>();
    var reads = new ConcurrentBag<double>();
    var elementCounts = new ConcurrentBag<int>();
    var traces = new ConcurrentBag<(int Client, int Iteration, object Sample)>();
    int rejected = 0;
    var allocationBefore = GC.GetTotalAllocatedBytes();
    var total = Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async client =>
    {
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/benchmark-" + Guid.NewGuid().ToString("N") + ".docx", payload))!;
        var random = new Random(seed + client);
        ClientKnowledge? priorKnowledge = null;
        for (int i = 0; i < iterations; i++)
        {
            var document = await StoredDocument.RestoreAsync(state, content);
            var changed = workload == "changed-full" ? Package(size, seed + i + 1) : payload;
            var request = workload == "captured"
                ? SyntheticWorkload.Capture(Option("--fixtures", "testdata/sharepoint/fixtures/save_reopen"), i == 0 ? "save-first" : "save-second")
                : SyntheticWorkload.Save(document, changed, workload, i, random);
            if (workload == "captured" && i == 0)
                ((PutChangesSubRequestData)request.SubRequests.Single().Data!).ExpectedStorageIndex = document.FilePartition.FileGraph.StorageIndex;
            var timer = Stopwatch.StartNew();
            var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>());
            if (result.Response.SubResponses.Any(r => r.Status))
            {
                var error = result.Response.SubResponses.First(r => r.Status).Error;
                if (!allowQuota || error?.Type != ErrorType.Win32 || error.ErrorCode != 112)
                    throw new InvalidOperationException("Benchmark save failed: " + error?.ErrorMessage);
                if (result.State.StateVersion != state.StateVersion || result.State.ContentVersion != state.ContentVersion ||
                    result.State.Content != state.Content)
                    throw new InvalidOperationException("Rejected save changed the published revision.");
                Interlocked.Increment(ref rejected);
                traces.Add((client, i + 1, new { client, iteration = i + 1, rejected = true, reason = error.ErrorMessage }));
                break;
            }
            samples.Add(timer.Elapsed.TotalMilliseconds);
            state = result.State;
            timer.Restart();
            await using var read = await content.OpenReadAsync(state.Content);
            await read.CopyToAsync(Stream.Null);
            reads.Add(timer.Elapsed.TotalMilliseconds);
            var restored = await StoredDocument.RestoreAsync(state, content);
            var retained = restored.FilePartition.FileGraph;
            var retention = retained.AnalyzeRetention();
            var currentKnowledge = ClientKnowledge.Deserialize(new(BinaryKnowledgeBuilder.FromElements(
                retained.ElementMetadata, restored.FilePartition.ProtocolIdentity.CellId.ShortId,
                restored.FilePartition.KnowledgeSequence, mappingSerials: retained.MappingSerials.Values.SelectMany(s => s))));
            long? emptyQueryBytes = null;
            long? deltaQueryBytes = null;
            long? knownQueryBytes = null;
            if (i % queryEvery == 0 || i == iterations - 1)
            {
                async Task<long> Query(ClientKnowledge? knowledge)
                {
                    var query = new FsshttpbCellRequest();
                    query.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 1, Data = new QueryChangesSubRequestData
                    { IncludeStorageManifest = true, IncludeCellChanges = true, Knowledge = knowledge } });
                    var queried = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, query, new Dictionary<string, string>());
                    if (queried.Response.SubResponses.Any(r => r.Status)) throw new InvalidOperationException("Benchmark query failed.");
                    return queried.Response.ToByteArray().LongLength;
                }
                emptyQueryBytes = await Query(null);
                deltaQueryBytes = await Query(priorKnowledge);
                knownQueryBytes = await Query(currentKnowledge);
            }
            priorKnowledge = currentKnowledge;
            traces.Add((client, i + 1, new { client, iteration = i + 1, contentBytes = state.Content.Length,
                retainedElements = retention.RetainedElements, retainedBytes = retention.RetainedBytes,
                requiredElements = retention.RequiredElements, requiredBytes = retention.RequiredBytes,
                retentionComplete = retention.Complete, retentionBlockers = retention.Blockers,
                receiptCount = state.Receipts.Length,
                requestBytes = request.ToByteArray().Length,
                saveResponseBytes = result.Response.ToByteArray().Length,
                emptyQueryBytes, deltaQueryBytes, knownQueryBytes,
                allocatedBytes = GC.GetTotalAllocatedBytes() - allocationBefore,
                managedLiveBytes = i % queryEvery == 0 || i == iterations - 1 ? GC.GetTotalMemory(true) : (long?)null }));
            if (args.Contains("--retry", StringComparer.Ordinal))
            {
                var retry = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>());
                if (retry.State.StateVersion != state.StateVersion || retry.Response.SubResponses.Any(r => r.Status))
                    throw new InvalidOperationException("Identical retry changed state or failed.");
            }
        }
        elementCounts.Add(state.Partitions.Single(p => p.Kind == 0).Elements.Length);
    }));
    total.Stop();
    object usage;
    if (source is not null)
    {
        await using var query = source.CreateCommand("SELECT stored_bytes,document_count,(SELECT COUNT(*) FROM cellbridge_states),(SELECT COUNT(*) FROM cellbridge_objects) FROM cellbridge_usage");
        await using var reader = await query.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Missing usage ledger.");
        usage = new { storedBytes = reader.GetInt64(0), documents = reader.GetInt64(1), snapshots = reader.GetInt64(2), objects = reader.GetInt64(3) };
    }
    else usage = new { storedBytes = (content as InMemoryContentStore)?.Budget.StoredBytes };
    var result = new { backend, workload, seed, sizeMiB = size, actualBytes = payload.Length, concurrency, iterations, rejected,
        queryEvery, usage, growth = traces.OrderBy(t => t.Client).ThenBy(t => t.Iteration).Select(t => t.Sample).ToArray(),
        saveMs = Percentiles(samples), downloadMs = Percentiles(reads), elapsedSeconds = total.Elapsed.TotalSeconds,
        savesPerSecond = samples.Count / total.Elapsed.TotalSeconds,
        allocatedBytes = GC.GetTotalAllocatedBytes() - allocationBefore,
        peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
        retainedGraphElementsPerDocument = elementCounts.Order().ToArray() };
    report.Add(result);
    Console.WriteLine(JsonSerializer.Serialize(result));
}
var destination = Option("--output", "artifacts/storage-benchmark.json");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(new { measuredUtc = DateTime.UtcNow,
    runtime = Environment.Version.ToString(), machine = Environment.MachineName, processors = Environment.ProcessorCount,
    workload, seed, limits,
    note = "Offline service saves and binary queries, not HTTP or live Office. Delta reuses file objects; delta-chain retains explicit base-revision links. Allocations include setup, query and retention diagnostics; sampled forced collections affect throughput. RSS peak and usage are cumulative across scenarios in this invocation. Durable backends own an isolated disposable database. Raw growth samples are not memory-plateau proof.",
    results = report }, new JsonSerializerOptions { WriteIndented = true }));

static object Percentiles(IEnumerable<double> values)
{
    var ordered = values.Order().ToArray();
    if (ordered.Length == 0) return new { p50 = (double?)null, p95 = (double?)null, p99 = (double?)null };
    double P(double q) => ordered[Math.Max(0, (int)Math.Ceiling(q * ordered.Length) - 1)];
    return new { p50 = P(.50), p95 = P(.95), p99 = P(.99) };
}
static byte[] Package(int sizeMiB, int seed)
{
    // Fill the document text with random hex and store the XML without ZIP
    // compression. This keeps the file large and the main document part valid.
    var random = new Random(seed);
    using var initial = new MemoryStream(MinimalDocx.Create());
    using var existing = new ZipArchive(initial, ZipArchiveMode.Read);
    using var output = new MemoryStream();
    using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        foreach (var entry in existing.Entries)
        {
            var copy = zip.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
            copy.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var target = copy.Open();
            if (entry.FullName == "word/document.xml")
            {
                using var writer = new StreamWriter(target);
                writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>");
                writer.Write(seed.ToString("X8"));
                var buffer = new byte[4096];
                for (long count = 0; count < sizeMiB * 1024L * 1024; count += buffer.Length * 2)
                { random.NextBytes(buffer); writer.Write(Convert.ToHexString(buffer)); }
                writer.Write("</w:t></w:r></w:p></w:body></w:document>");
            }
            else { using var input = entry.Open(); input.CopyTo(target); }
        }
    return output.ToArray();
}
