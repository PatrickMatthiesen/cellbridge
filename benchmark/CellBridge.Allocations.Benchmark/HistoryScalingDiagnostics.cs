using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;

internal static class HistoryScalingDiagnostics
{
    private static readonly CellBridgeActor Actor = new(new("history:writer", "writer", "Writer"), CanCreate: true);
    private static readonly Dictionary<string, string> Attributes = [];
    private const int Warmups = 10;

    public static async Task RunAsync(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length || args[i] is not ("--output" or "--source" or "--checkpoints" or "--iterations" or "--snapshots") ||
                !options.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Use --output PATH --source TAG --checkpoints 1,10,50,100,250 --iterations 15 [--snapshots DIRECTORY].");
        }
        string Option(string name, string fallback) => options.GetValueOrDefault(name, fallback);
        var output = Path.GetFullPath(Option("--output", Path.Combine(BenchmarkMetadata.RepositoryRoot(),
            "artifacts/office-performance/history-scaling.json")));
        var iterations = int.Parse(Option("--iterations", "15"));
        if (iterations is < 1 or > 100) throw new ArgumentOutOfRangeException("--iterations", "Use 1 through 100.");
        var checkpoints = Option("--checkpoints", "1,10,50,100,250").Split(',').Select(int.Parse).Distinct().Order().ToArray();
        if (checkpoints.Length is < 1 or > 20 || checkpoints.Any(n => n is < 1 or > 999))
            throw new ArgumentOutOfRangeException("--checkpoints", "Use at most 20 distinct revision counts between 1 and 999; the measured save adds one revision.");
        var snapshots = options.TryGetValue("--snapshots", out var directory) ? Path.GetFullPath(directory) : null;
        var content = new InMemoryContentStore();
        var provider = new StorageProvider(new InMemoryDocumentRepository(content), content);
        var service = new CellBridgeDocumentService(provider);
        var payload = CreateDocx(1);
        var state = await service.CreateAsync(new Guid("9898f6fe-df33-4a39-8509-b65c36c4b3c4"),
            "/history-scaling.docx", payload, Actor) ?? throw new InvalidOperationException("Create failed.");
        var rows = new List<object>();
        Console.WriteLine("Synthetic tiny DOCX, full-package FSSHTTPB saves, one document, in-memory stores. Library components only; no HTTP, pacing or capacity claim.");

        for (var revisionCount = 1; revisionCount <= checkpoints[^1]; revisionCount++)
        {
            if (state.Counters.Revisions != revisionCount || state.ContentVersion != revisionCount)
                throw new InvalidDataException($"Expected exactly {revisionCount} committed revisions and content version.");
            if (checkpoints.Contains(revisionCount))
            {
                await VerifyContentAsync(state, payload, provider);
                // Export is an explicit cold operation. Ordinary reads below never deserialize this snapshot.
                var snapshot = await provider.Documents.ExportDocumentAsync(state.ResourceId)
                    ?? throw new InvalidDataException("Missing diagnostic snapshot.");
                var json = JsonSerializer.Serialize(snapshot, RecoveryJson.Options);
                var decoded = JsonSerializer.Deserialize<NormalizedDocumentSnapshot>(json, RecoveryJson.Options)!;
                NormalizedDocumentRecovery.Validate(decoded, provider.Limits);
                var graph = await ReadGraphAsync(service, state);
                if (!graph.Materialize().AsSpan().SequenceEqual(payload)) throw new InvalidDataException("Latest graph content differs.");
                if (snapshots is not null) await PersistSnapshotAsync(snapshots, revisionCount, json, snapshot, content);
                var stages = new List<object>();
                await MeasureAsync("current-head-read", () => ValueTask.FromResult<object>(state.ResourceId),
                    async input => await provider.Documents.FindAsync(new DocumentIdentity.Resource((Guid)input))
                        ?? throw new InvalidDataException("Missing head."));
                await MeasureAsync("current-partition-metadata-read", () => ValueTask.FromResult<object>(state.ResourceId),
                    async input => await provider.Documents.ReadPartitionAsync((Guid)input, new(0))
                        ?? throw new InvalidDataException("Missing partition."));
                await MeasureAsync("current-bindings-read", () => ValueTask.FromResult<object>(state.ResourceId),
                    async input => await provider.Documents.ReadPartitionAsync((Guid)input, new(0) { IncludeBindings = true })
                        ?? throw new InvalidDataException("Missing bindings."));
                await MeasureAsync("current-package-stream", () => ValueTask.FromResult<object>(state.Package), async input =>
                {
                    await using var stream = await provider.Packages.OpenReadAsync((DocumentPackage)input);
                    await stream.CopyToAsync(Stream.Null);
                    return stream.Length;
                });
                await MeasureAsync("whole-query-client-graph", () => ValueTask.FromResult<object>(state),
                    async input => await ReadGraphAsync(service, (DocumentHead)input), result =>
                    {
                        if (!((PartitionGraphSnapshot)result).Materialize().AsSpan().SequenceEqual(payload))
                            throw new InvalidDataException("Measured query returned incorrect content.");
                        return ValueTask.CompletedTask;
                    });
                await MeasureAsync("offline-normalized-snapshot-encode", () => ValueTask.FromResult<object>(snapshot),
                    input => ValueTask.FromResult<object>(JsonSerializer.Serialize((NormalizedDocumentSnapshot)input, RecoveryJson.Options)));
                await MeasureAsync("offline-normalized-snapshot-decode", () => ValueTask.FromResult<object>(json),
                    input => ValueTask.FromResult<object>(JsonSerializer.Deserialize<NormalizedDocumentSnapshot>((string)input, RecoveryJson.Options)!));
                // Each measured save starts from the same aged state in a separate state store.
                // Request construction, seeding, and all content checks are outside its interval.
                var nextPayload = CreateDocx(revisionCount + 1);
                await MeasureAsync("full-package-save", async () =>
                {
                    var isolatedProvider = new StorageProvider(new InMemoryDocumentRepository(content), content);
                    var recovery = new ProviderSnapshot([snapshot], []);
                    await isolatedProvider.Documents.ImportRecoveryAsync(recovery,
                        new(Guid.NewGuid(), Hash(Encoding.UTF8.GetBytes(json)), 1, 1));
                    var isolatedService = new CellBridgeDocumentService(isolatedProvider);
                    return new SaveInput(isolatedProvider, isolatedService, BuildSave(graph, nextPayload, revisionCount + 1));
                }, async input =>
                {
                    var save = (SaveInput)input;
                    return new SavedResult(save.Provider, await save.Service.ExecuteAsync(state.ResourceId,
                        DocumentPartitionKind.FileContents, save.Request, Attributes, Actor));
                }, async result =>
                {
                    var saved = (SavedResult)result;
                    EnsureSaved(saved.Execution, revisionCount + 1);
                    await VerifyContentAsync(saved.Execution.State, nextPayload, saved.Provider);
                });
                rows.Add(new
                {
                    revisions = revisionCount, state.ContentVersion, state.StateVersion,
                    offlineSnapshotUtf8Bytes = Encoding.UTF8.GetByteCount(json),
                    currentGraphElements = snapshot.Partitions.Sum(partition => partition.Head.ElementCount),
                    currentBindingRows = snapshot.CurrentBindings.Length,
                    immutableGraphElements = snapshot.Elements.Length,
                    bindingDeltaRows = snapshot.BindingDeltas.Length,
                    checkpointCount = snapshot.Checkpoints.Length,
                    checkpointBindingRows = snapshot.Checkpoints.Sum(checkpoint => checkpoint.Bindings.Length),
                    mappingIntervalRows = snapshot.MappingIntervals.Length,
                    uniqueContentHandles = snapshot.ContentReferences.Length,
                    payloadBytes = payload.Length, payloadSha256 = Hash(payload),
                    verification = "Derived package bytes, latest wire graph, normalized snapshot structure and exact revision/content-version counts checked; each measured save checked outside timing.",
                    measurements = stages
                });
                Console.WriteLine($"Verified {revisionCount} revisions; {Encoding.UTF8.GetByteCount(json)} offline snapshot bytes.");

                async Task MeasureAsync(string stage, Func<ValueTask<object>> setup, Func<object, ValueTask<object>> operation,
                    Func<object, ValueTask>? verify = null)
                {
                    for (var i = 0; i < Warmups; i++)
                    {
                        var result = await operation(await setup());
                        if (verify is not null) await verify(result);
                    }
                    var samples = new List<Sample>(iterations);
                    using var process = Process.GetCurrentProcess();
                    for (var i = 0; i < iterations; i++)
                    {
                        var input = await setup();
                        var cpuBefore = CpuMilliseconds(process);
                        var before = GC.GetTotalAllocatedBytes(precise: true);
                        var start = Stopwatch.GetTimestamp();
                        var result = await operation(input);
                        var wall = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
                        var cpu = CpuMilliseconds(process) - cpuBefore;
                        if (verify is not null) await verify(result);
                        GC.KeepAlive(result);
                        samples.Add(new(allocated, wall, cpu));
                    }
                    stages.Add(new
                    {
                        stage,
                        medianAllocatedBytes = Median(samples.Select(s => (double)s.AllocatedBytes)),
                        medianWallMs = Median(samples.Select(s => s.WallMs)),
                        medianCpuMs = Median(samples.Select(s => s.CpuMs)),
                        meanCpuMs = samples.Average(s => s.CpuMs),
                        totalAllocatedBytes = samples.Sum(s => s.AllocatedBytes), samples
                    });
                }
            }
            if (revisionCount == checkpoints[^1]) break;
            payload = CreateDocx(revisionCount + 1);
            var execution = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
                BuildSave(await ReadGraphAsync(service, state), payload, revisionCount + 1), Attributes, Actor);
            EnsureSaved(execution, revisionCount + 1);
            state = execution.State;
            await VerifyContentAsync(state, payload, provider);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            schemaVersion = 2, source = Option("--source", "unspecified"),
            sourceMetadata = BenchmarkMetadata.Capture(typeof(HistoryScalingDiagnostics).Assembly,
                typeof(DocumentHead).Assembly, typeof(FsshttpbCellRequest).Assembly, typeof(InMemoryDocumentRepository).Assembly),
            workload = "Synthetic fixed small valid DOCX packages with revision-specific text and fixed ZIP timestamps; fresh full-package FSSHTTPB PutChanges graphs; one document; in-memory state and content.",
            interpretation = "Library component timings only. No HTTP, Office desktop, concurrency, offered pacing or sustainable capacity evidence.",
            measurement = new
            {
                iterations, warmupOperationsPerStage = Warmups, checkpoints,
                setup = "Aging, request/package construction, per-save isolated state seeding, normalized recovery import, verification and JSON output excluded.",
                content = "Measured saves use separate repositories but a shared warm content store and the same deterministic next payload; this does not measure novel-content persistence.",
                scope = "Head and partition metadata reads are bounded current operations. Package reconstruction reads current bindings and required physical elements. Whole-query graph and offline snapshot codec explicitly include their selected catalog/history scope.",
                allocation = "Exact GC.GetTotalAllocatedBytes(precise:true) process counter deltas per operation, including all executing threads; no allocation sampling or per-thread estimate.",
                cpu = OperatingSystem.IsLinux() ? "Linux CLOCK_PROCESS_CPUTIME_ID per operation; includes CPU counter/allocation probes and all process threads." :
                    "Process.TotalProcessorTime per operation; short samples can be below the operating system CPU counter resolution.",
                gc = "No forced collections in measured intervals; warm JIT paths with configured runtime GC/tiering. CPU includes runtime background activity.",
                tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime default",
                readyToRun = Environment.GetEnvironmentVariable("DOTNET_ReadyToRun") ?? "runtime default",
                stopwatchFrequency = Stopwatch.Frequency,
                snapshots
            }, checkpoints = rows
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(output);
    }

    private static void EnsureSaved(CellExecution execution, int revisions)
    {
        if (execution.IsCancelled || execution.LockError is not null || execution.Response.Status ||
            execution.Response.SubResponses.Count != 1 || execution.Response.SubResponses[0].Status ||
            execution.State.Counters.Revisions != revisions || execution.State.ContentVersion != revisions ||
            execution.AcceptedSaves.Length != 1 || execution.AcceptedSaves[0].IsReplay)
            throw new InvalidDataException($"Full save did not commit exactly revision {revisions}: {JsonSerializer.Serialize(execution.Response.SubResponses.FirstOrDefault()?.Error)}");
    }

    private static async ValueTask VerifyContentAsync(DocumentHead state, byte[] expected, StorageProvider provider)
    {
        var revision = await provider.Documents.ReadRevisionAsync(state.ResourceId, state.LatestRevisionNumber);
        if (state.Package.Length != expected.Length || state.Package.Sha256 != Hash(expected) || revision?.Revision?.Package != state.Package)
            throw new InvalidDataException("Committed package/revision mismatch.");
        await using var stream = await provider.Packages.OpenReadAsync(state.Package);
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        if (!bytes.ToArray().AsSpan().SequenceEqual(expected)) throw new InvalidDataException("Committed bytes differ.");
    }

    private static async Task<PartitionGraphSnapshot> ReadGraphAsync(CellBridgeDocumentService service, DocumentHead head)
    {
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 1,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } });
        var response = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents, request, Attributes, Actor);
        if (response.IsCancelled || response.Response.Status || response.Response.SubResponses.Single().Status ||
            response.Response.SubResponses.Single().Data is not QueryChangesSubResponseData query)
            throw new InvalidDataException("Graph query failed.");
        return PartitionGraphSnapshot.Create(response.Response.DataElementPackage!.DataElements, query.StorageIndexExtendedGuid);
    }

    private static FsshttpbCellRequest BuildSave(PartitionGraphSnapshot current, byte[] payload, int revision)
    {
        uint sequence = checked((uint)revision * 16);
        var serialBytes = new Guid("251fb6a8-9f45-411b-84eb-0ff9a15f19cc").ToByteArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(serialBytes.AsSpan(12), revision);
        ExGuid Fresh() => new(++sequence, new Guid("4580bb18-683a-4cfa-a701-2c6a6b3c477d"));
        var identity = new StorageManifestBuilder.StableIdentity(Fresh(), Fresh(), Fresh(), Fresh(), Fresh(), Fresh(), Fresh(),
            current.FileCell, new Guid(serialBytes));
        var graph = FileContentPartitionBuilder.BuildQueryChangesResponse(1, payload, identity, (ulong)revision + 100);
        var index = ((QueryChangesSubResponseData)graph.SubResponses[0].Data!).StorageIndexExtendedGuid;
        graph.DataElementPackage!.DataElements.Add(current.StorageIndexes.Single(element => element.DataElementExtendedGuid.Equals(current.StorageIndex)));
        return new FsshttpbCellRequest
        {
            DataElementPackage = graph.DataElementPackage,
            SubRequests = { new(RequestTypes.PutChanges) { RequestId = 1, Data = new PutChangesSubRequestData
                { StorageIndex = index, ExpectedStorageIndex = current.StorageIndex, HasAdditionalFlags = true, AdditionalFlagsBits = 1 } } }
        };
    }

    private static byte[] CreateDocx(int revision)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write("[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>
                """);
            Write("_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>
                """);
            Write("word/document.xml", $"""
                <?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>History scaling revision {revision:D4}</w:t></w:r></w:p></w:body></w:document>
                """);
            void Write(string path, string text)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        }
        return stream.ToArray();
    }

    private static async Task PersistSnapshotAsync(string directory, int revisions, string json,
        NormalizedDocumentSnapshot snapshot, IContentStore content)
    {
        Directory.CreateDirectory(directory);
        var contentDirectory = Path.Combine(directory, "content");
        Directory.CreateDirectory(contentDirectory);
        foreach (var handle in snapshot.ContentReferences.Select(reference => reference.Handle))
        {
            var path = Path.Combine(contentDirectory, handle.Sha256);
            if (File.Exists(path)) continue;
            await using var source = await content.OpenReadAsync(handle);
            await using var destination = File.Create(path);
            await source.CopyToAsync(destination);
        }
        await File.WriteAllTextAsync(Path.Combine(directory, $"revision-{revisions:D4}.json"), json);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
    }

    private static double CpuMilliseconds(Process process)
    {
        if (!OperatingSystem.IsLinux()) return process.TotalProcessorTime.TotalMilliseconds;
        if (ClockGetTime(2, out var time) != 0) throw new InvalidOperationException("Cannot read CLOCK_PROCESS_CPUTIME_ID.");
        return time.Seconds * 1000d + time.Nanoseconds / 1_000_000d;
    }
    [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = true)]
    private static extern int ClockGetTime(int clockId, out Timespec time);
    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec { public nint Seconds; public nint Nanoseconds; }
    private sealed record SaveInput(StorageProvider Provider, CellBridgeDocumentService Service, FsshttpbCellRequest Request);
    private sealed record SavedResult(StorageProvider Provider, CellExecution Execution);
    private sealed record Sample(long AllocatedBytes, double WallMs, double CpuMs);
}
