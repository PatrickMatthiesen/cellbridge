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
        var provider = new StorageProvider(new InMemoryStateStore(), content);
        var service = new CellBridgeDocumentService(provider);
        var payload = CreateDocx(1);
        var state = await service.CreateAsync(new Guid("9898f6fe-df33-4a39-8509-b65c36c4b3c4"),
            "/history-scaling.docx", payload, Actor) ?? throw new InvalidOperationException("Create failed.");
        var rows = new List<object>();
        Console.WriteLine("Synthetic tiny DOCX, full-package FSSHTTPB saves, one document, in-memory stores. Library components only; no HTTP, pacing or capacity claim.");

        for (var revisionCount = 1; revisionCount <= checkpoints[^1]; revisionCount++)
        {
            if (state.Revisions.Length != revisionCount || state.ContentVersion != revisionCount)
                throw new InvalidDataException($"Expected exactly {revisionCount} committed revisions and content version.");
            if (checkpoints.Contains(revisionCount))
            {
                await VerifyContentAsync(state, payload, content);
                var json = DocumentStateJson.Serialize(state);
                var decoded = DocumentStateJson.Deserialize(json);
                await VerifyContentAsync(decoded, payload, content);
                if (decoded.Revisions.Length != revisionCount) throw new InvalidDataException("Codec lost revision history.");
                var graph = await DocumentPartition.RestoreFileGraphAsync(state, content, provider.Limits);
                if (!graph.Materialize().AsSpan().SequenceEqual(payload)) throw new InvalidDataException("Latest graph content differs.");
                if (snapshots is not null) await PersistSnapshotAsync(snapshots, revisionCount, json, state, content);
                var stages = new List<object>();
                await MeasureAsync("codec-encode", () => ValueTask.FromResult<object>(state with { }),
                    input => ValueTask.FromResult<object>(DocumentStateJson.Serialize((DocumentState)input)));
                await MeasureAsync("codec-decode", () => ValueTask.FromResult<object>(json),
                    input => ValueTask.FromResult<object>(DocumentStateJson.Deserialize((string)input)));
                await MeasureAsync("admission-fresh-state", () => ValueTask.FromResult<object>(state with { }), input =>
                {
                    provider.Limits.CheckDocument((DocumentState)input);
                    return ValueTask.FromResult(input);
                });
                await MeasureAsync("latest-file-graph-restore-with-admission", () => ValueTask.FromResult<object>(state with { }),
                    async input => await DocumentPartition.RestoreFileGraphAsync((DocumentState)input, content, provider.Limits),
                    result =>
                    {
                        if (!((PartitionGraphSnapshot)result).Materialize().AsSpan().SequenceEqual(payload))
                            throw new InvalidDataException("Measured restore returned incorrect content.");
                        return ValueTask.CompletedTask;
                    });
                await MeasureAsync("latest-file-graph-capture-unchanged", () => ValueTask.FromResult<object>(state.Partitions.Single(p => p.Kind == 0)),
                    async input => await DocumentPartition.CaptureFileGraphAsync((PartitionState)input, graph,
                        ((PartitionState)input).Knowledge, state.Content, content), result =>
                    {
                        if (((PartitionState)result).Elements.Length != state.Partitions.Single(p => p.Kind == 0).Elements.Length)
                            throw new InvalidDataException("Capture lost graph elements.");
                        return ValueTask.CompletedTask;
                    });
                // Each measured save starts from the same aged state in a separate state store.
                // Request construction, seeding, and all content checks are outside its interval.
                var nextPayload = CreateDocx(revisionCount + 1);
                await MeasureAsync("full-package-save", async () =>
                {
                    var isolatedProvider = new StorageProvider(new InMemoryStateStore(content.Budget), content);
                    if (!await isolatedProvider.State.TryCreateAsync(state with { })) throw new InvalidOperationException("Seed failed.");
                    return new SaveInput(new CellBridgeDocumentService(isolatedProvider), BuildSave(state, nextPayload, revisionCount + 1));
                }, async input =>
                {
                    var save = (SaveInput)input;
                    return await save.Service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, save.Request, Attributes, Actor);
                }, async result =>
                {
                    var saved = (CellExecution)result;
                    EnsureSaved(saved, revisionCount + 1);
                    await VerifyContentAsync(saved.State, nextPayload, content);
                });
                rows.Add(new
                {
                    revisions = revisionCount, state.ContentVersion, state.StateVersion,
                    stateUtf8Bytes = Encoding.UTF8.GetByteCount(json),
                    currentGraphElements = state.Partitions.Sum(p => p.Elements.Length),
                    logicalGraphMembershipSlots = StorageLimits.GetDecodedGraphMembershipBytes(state) / IntPtr.Size,
                    historicalGraphMembershipSlots = state.Revisions.Sum(r => r.Partitions.Sum(p => (long)p.Elements.Length)),
                    uniqueContentHandles = StorageReferences.Handles(state).DistinctBy(h => h.Key).Count(),
                    payloadBytes = payload.Length, payloadSha256 = Hash(payload),
                    verification = "Committed content handle, streamed bytes, latest graph, codec roundtrip and exact revision/content-version counts checked; each measured save checked outside timing.",
                    measurements = stages
                });
                Console.WriteLine($"Verified {revisionCount} revisions; {Encoding.UTF8.GetByteCount(json)} state bytes.");

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
                BuildSave(state, payload, revisionCount + 1), Attributes, Actor);
            EnsureSaved(execution, revisionCount + 1);
            state = execution.State;
            await VerifyContentAsync(state, payload, content);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, source = Option("--source", "unspecified"),
            sourceMetadata = BenchmarkMetadata.Capture(typeof(HistoryScalingDiagnostics).Assembly,
                typeof(DocumentStateJson).Assembly, typeof(FsshttpbCellRequest).Assembly, typeof(InMemoryStateStore).Assembly),
            workload = "Synthetic fixed small valid DOCX packages with revision-specific text and fixed ZIP timestamps; fresh full-package FSSHTTPB PutChanges graphs; one document; in-memory state and content.",
            interpretation = "Library component timings only. No HTTP, Office desktop, concurrency, offered pacing or sustainable capacity evidence.",
            measurement = new
            {
                iterations, warmupOperationsPerStage = Warmups, checkpoints,
                setup = "Aging, request/package construction, per-save isolated state seeding, fresh immutable state clones, verification and JSON output excluded.",
                content = "Measured saves use separate state stores but a shared warm content store and the same deterministic next payload; this does not measure novel-content persistence.",
                admission = "Fresh DocumentState record per sample; no reused serialized-size cache entry. Restore includes admission and retained-handle validation; capture measures unchanged latest graph only.",
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
            execution.State.Revisions.Length != revisions || execution.State.ContentVersion != revisions ||
            execution.AcceptedSaves.Length != 1 || execution.AcceptedSaves[0].IsReplay)
            throw new InvalidDataException($"Full save did not commit exactly revision {revisions}: {JsonSerializer.Serialize(execution.Response.SubResponses.FirstOrDefault()?.Error)}");
    }

    private static async ValueTask VerifyContentAsync(DocumentState state, byte[] expected, IContentStore content)
    {
        if (state.Content.Length != expected.Length || state.Content.Sha256 != Hash(expected) ||
            state.Revisions[^1].Content != state.Content)
            throw new InvalidDataException("Committed content handle/revision mismatch.");
        await using var stream = await content.OpenReadAsync(state.Content);
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        if (!bytes.ToArray().AsSpan().SequenceEqual(expected)) throw new InvalidDataException("Committed bytes differ.");
    }

    private static FsshttpbCellRequest BuildSave(DocumentState state, byte[] payload, int revision)
    {
        var partition = state.Partitions.Single(p => p.Kind == 0);
        uint sequence = checked((uint)revision * 16);
        var serialBytes = new Guid("251fb6a8-9f45-411b-84eb-0ff9a15f19cc").ToByteArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(serialBytes.AsSpan(12), revision);
        ExGuid Fresh() => new(++sequence, new Guid("4580bb18-683a-4cfa-a701-2c6a6b3c477d"));
        var identity = new StorageManifestBuilder.StableIdentity(Fresh(), Fresh(), Fresh(), Fresh(), Fresh(), Fresh(), Fresh(),
            new CellId(StorageIds.Restore(partition.Identity.CellLong), StorageIds.Restore(partition.Identity.CellShort)),
            new Guid(serialBytes));
        var graph = FileContentPartitionBuilder.BuildQueryChangesResponse(1, payload, identity, partition.Knowledge + 100);
        var index = ((QueryChangesSubResponseData)graph.SubResponses[0].Data!).StorageIndexExtendedGuid;
        return new FsshttpbCellRequest
        {
            DataElementPackage = graph.DataElementPackage,
            SubRequests = { new(RequestTypes.PutChanges) { RequestId = 1, Data = new PutChangesSubRequestData
                { StorageIndex = index, ExpectedStorageIndex = StorageIds.Restore(partition.StorageIndex!) } } }
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
        DocumentState state, IContentStore content)
    {
        Directory.CreateDirectory(directory);
        var contentDirectory = Path.Combine(directory, "content");
        Directory.CreateDirectory(contentDirectory);
        foreach (var handle in StorageReferences.Handles(state).DistinctBy(h => h.Key))
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
    private sealed record SaveInput(CellBridgeDocumentService Service, FsshttpbCellRequest Request);
    private sealed record Sample(long AllocatedBytes, double WallMs, double CpuMs);
}
