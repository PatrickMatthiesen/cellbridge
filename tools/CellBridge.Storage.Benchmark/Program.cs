using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.FileSystem;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Npgsql;

// Offline full-graph uploads, not a desktop Office benchmark. Use a disposable
// database/content root: all accepted benchmark revisions remain retained.
string Option(string name, string fallback) => args.SkipWhile(x => x != name).Skip(1).FirstOrDefault() ?? fallback;
var sizes = Option("--sizes", "1,10").Split(',').Select(int.Parse).ToArray();
var clients = Option("--clients", "1,8").Split(',').Select(int.Parse).ToArray();
var iterations = int.Parse(Option("--iterations", "3"));
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);
await using var source = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge") is { } cs ? NpgsqlDataSource.Create(cs) : null;
var backend = Option("--content", source is null ? "memory" : "postgresql");
IContentStore content = backend switch
{
    "memory" => new InMemoryContentStore(),
    "postgresql" when source is not null => new PostgreSqlContentStore(source),
    "filesystem" => new FileSystemContentStore(Option("--root", Path.Combine(Path.GetTempPath(), "cellbridge-benchmark-" + Guid.NewGuid().ToString("N")))),
    _ => throw new ArgumentException("Content must be memory, postgresql or filesystem; PostgreSQL requires its connection string."),
};
IDocumentStateStore stateStore = source is null ? new InMemoryStateStore() : new PostgreSqlStateStore(source);
var provider = new StorageProvider(stateStore, content);
await stateStore.CheckHealthAsync();
var report = new List<object>();
foreach (int size in sizes)
foreach (int concurrency in clients)
{
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
    var payload = Package(size);
    var samples = new ConcurrentBag<double>();
    var reads = new ConcurrentBag<double>();
    var elementCounts = new ConcurrentBag<int>();
    var allocationBefore = GC.GetTotalAllocatedBytes();
    var total = Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
    {
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/benchmark-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create()))!;
        for (int i = 0; i < iterations; i++)
        {
            var document = await StoredDocument.RestoreAsync(state, content);
            var graph = new DocumentStore().Put("/proposal.docx", payload).FilePartition.FileGraph;
            var package = new DataElementPackage(); package.DataElements.AddRange(graph.Elements);
            var request = new FsshttpbCellRequest { DataElementPackage = package };
            request.SubRequests.Add(new(RequestTypes.PutChanges) { RequestId = (ulong)i + 1, Data = new PutChangesSubRequestData
            { StorageIndex = graph.StorageIndex, ExpectedStorageIndex = document.FilePartition.FileGraph.StorageIndex } });
            var timer = Stopwatch.StartNew();
            var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>());
            if (result.Response.SubResponses.Any(r => r.Status)) throw new InvalidOperationException("Benchmark save failed.");
            samples.Add(timer.Elapsed.TotalMilliseconds);
            state = result.State;
            timer.Restart();
            await using var read = await content.OpenReadAsync(state.Content);
            await read.CopyToAsync(Stream.Null);
            reads.Add(timer.Elapsed.TotalMilliseconds);
        }
        elementCounts.Add(state.Partitions.Single(p => p.Kind == 0).Elements.Length);
    }));
    total.Stop();
    var result = new { backend, sizeMiB = size, actualBytes = payload.Length, concurrency, iterations,
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
    note = "Offline synthetic complete DOCX graphs; includes graph restoration, materialization, validation and durable publication. Not live Office or delta latency.",
    results = report }, new JsonSerializerOptions { WriteIndented = true }));

static object Percentiles(IEnumerable<double> values)
{
    var ordered = values.Order().ToArray();
    double P(double q) => ordered[Math.Max(0, (int)Math.Ceiling(q * ordered.Length) - 1)];
    return new { p50 = P(.50), p95 = P(.95), p99 = P(.99) };
}
static byte[] Package(int sizeMiB)
{
    // Fill the document text with random hex and store the XML without ZIP
    // compression. This keeps the file large and the main document part valid.
    using var initial = new MemoryStream(MinimalDocx.Create());
    using var existing = new ZipArchive(initial, ZipArchiveMode.Read);
    using var output = new MemoryStream();
    using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        foreach (var entry in existing.Entries)
        {
            var copy = zip.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
            using var target = copy.Open();
            if (entry.FullName == "word/document.xml")
            {
                using var writer = new StreamWriter(target);
                writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>");
                var buffer = new byte[4096];
                for (long count = 0; count < sizeMiB * 1024L * 1024; count += buffer.Length * 2)
                { RandomNumberGenerator.Fill(buffer); writer.Write(Convert.ToHexString(buffer)); }
                writer.Write("</w:t></w:r></w:p></w:body></w:document>");
            }
            else { using var input = entry.Open(); input.CopyTo(target); }
        }
    return output.ToArray();
}
