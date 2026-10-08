using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.FileSystem;
using CellBridge.Storage.InMemory;

string Option(string name, string fallback) => args.SkipWhile(x => x != name).Skip(1).FirstOrDefault() ?? fallback;
var root = Path.GetFullPath(Option("--repo", "."));
var output = Path.GetFullPath(Option("--output", "artifacts/allocations/run.json"));
var stage = Option("--stage", "all");
if (stage is not ("all" or "inbound" or "binary" or "restore" or "nested" or "file-build"))
    throw new ArgumentException("Stage must be all, inbound, binary, restore, nested or file-build.");
var workload = Option("--workload", "synthetic");
var backend = Option("--content", "memory");
if (backend is not ("memory" or "filesystem")) throw new ArgumentException("Content must be memory or filesystem.");
int size = int.Parse(Option("--bytes", "1048576"));
int iterations = int.Parse(Option("--iterations", "30"));
ArgumentOutOfRangeException.ThrowIfNegative(size);
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);
var temporaryRoot = Path.Combine(Path.GetTempPath(), "cellbridge-allocations-" + Guid.NewGuid().ToString("N"));
try
{
    IContentStore provider = backend == "memory" ? new InMemoryContentStore() : new FileSystemContentStore(temporaryRoot);
    var content = new CountingContentStore(provider);
    var guid = new Guid("00112233-4455-6677-8899-aabbccddeeff");
    var identity = new StorageManifestBuilder.StableIdentity(new(1, guid), new(2, guid), new(3, guid),
        new(4, guid), new(5, guid), new(6, guid), new(7, guid), new(new(8, guid), new(9, guid)), guid);
    byte[] payload;
    byte[] inbound;
    string contentType;
    ReadOnlyMemory<byte> binary;
    PartitionGraphSnapshot graph;
    string provenance;
    if (workload == "synthetic")
    {
        payload = new byte[size];
        new Random(42).NextBytes(payload);
        var response = FileContentPartitionBuilder.BuildQueryChangesResponse(1, payload, identity, 1);
        graph = PartitionGraphSnapshot.Create(response.DataElementPackage!.DataElements, identity.ObjectDataBlobGuid);
        var request = new FsshttpbCellRequest { DataElementPackage = response.DataElementPackage };
        request.SubRequests.Add(new(RequestTypes.PutChanges) { RequestId = 1,
            Data = new PutChangesSubRequestData { StorageIndex = identity.ObjectDataBlobGuid } });
        binary = request.ToByteArray();
        string soap = "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><RequestVersion xmlns=\"http://schemas.microsoft.com/sharepoint/soap/\" Version=\"2\" MinorVersion=\"2\"/><RequestCollection xmlns=\"http://schemas.microsoft.com/sharepoint/soap/\" CorrelationId=\"00112233-4455-6677-8899-aabbccddeeff\"><Request Url=\"http://example.invalid/shared/test.docx\" RequestToken=\"1\"><SubRequest Type=\"Cell\" SubRequestToken=\"1\"><SubRequestData><x:Include xmlns:x=\"http://www.w3.org/2004/08/xop/include\" href=\"cid:binary\"/></SubRequestData></SubRequest></Request></RequestCollection></s:Body></s:Envelope>";
        soap = soap.Replace("<SubRequestData>", $"<SubRequestData BinaryDataSize=\"{binary.Length}\">");
        inbound = [.. Encoding.UTF8.GetBytes("--bench\r\nContent-Type: application/xop+xml\r\nContent-ID: <soap>\r\n\r\n" + soap + "\r\n--bench\r\nContent-Type: application/octet-stream\r\nContent-ID: <binary>\r\n\r\n"), .. binary.Span, .. "\r\n--bench--\r\n"u8];
        contentType = "multipart/related; boundary=bench";
        provenance = "seed=42; deterministic opaque bytes in complete synthetic file graph; no Office client";
    }
    else if (workload is "save-first" or "save-second")
    {
        var fixturePath = Path.Combine(root, "testdata/sharepoint/fixtures/save_reopen", workload + ".json");
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
        var side = fixture.RootElement.GetProperty("request");
        inbound = Convert.FromBase64String(side.GetProperty("bodyBase64").GetString()!);
        contentType = side.GetProperty("contentType").GetString()!;
        if (Convert.ToHexStringLower(SHA256.HashData(inbound)) != side.GetProperty("sha256").GetString())
            throw new InvalidDataException("Reviewed request hash mismatch.");
        binary = MtomMessageParser.ParseViews(inbound, contentType).Single(p => p.ContentType.Contains("application/octet-stream")).ContentMemory;
        var request = ParseBinary(binary);
        var put = (PutChangesSubRequestData)request.SubRequests.Single().Data!;
        if (workload == "save-second")
        {
            using var previousFixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,
                "testdata/sharepoint/fixtures/save_reopen/save-first.json")));
            var previousSide = previousFixture.RootElement.GetProperty("request");
            var previousBody = Convert.FromBase64String(previousSide.GetProperty("bodyBase64").GetString()!);
            if (Convert.ToHexStringLower(SHA256.HashData(previousBody)) != previousSide.GetProperty("sha256").GetString())
                throw new InvalidDataException("Reviewed preceding request hash mismatch.");
            var previousBinary = MtomMessageParser.ParseViews(previousBody, previousSide.GetProperty("contentType").GetString()!)
                .Single(p => p.ContentType.Contains("application/octet-stream")).ContentMemory;
            var previous = ParseBinary(previousBinary);
            graph = PartitionGraphSnapshot.Create(previous.DataElementPackage!.DataElements,
                ((PutChangesSubRequestData)previous.SubRequests.Single().Data!).StorageIndex)
                .Merge(request.DataElementPackage!.DataElements, put.StorageIndex);
        }
        else graph = PartitionGraphSnapshot.Create(request.DataElementPackage!.DataElements, put.StorageIndex);
        payload = graph.Materialize();
        provenance = "sanitized SharePoint selected exchange 20260925T084814Z-session-f088f59e; " + workload +
            "; client/backend builds not recorded in reviewed fixture; no new desktop or complete-session evidence";
    }
    else throw new ArgumentException("Workload must be synthetic, save-first or save-second.");

    // Persist setup outside every measured interval. Restore retains the reviewed graph structure.
    var document = new DocumentStore().Put("/shared/benchmark.docx", payload);
    var state = await document.CaptureAsync(content);
    var filePartition = await DocumentPartition.CaptureFileGraphAsync(state.Partitions.Single(p => p.Kind == 0),
        graph, 1, state.Content, content);
    state = state with { Partitions = state.Partitions.Select(p => p.Kind == 0 ? filePartition : p).ToImmutableArray() };
    var rows = new List<object>();
    if (stage is "all" or "inbound") await Measure("inbound-mtom-soap-binary", () =>
    {
        var parts = MtomMessageParser.ParseViews(inbound, contentType);
        var soap = parts.First(p => p.ContentType.Contains("xop+xml"));
        _ = CellStorageRequestParser.Parse(Encoding.UTF8.GetString(soap.ContentMemory.Span));
        return ValueTask.FromResult<object>(ParseBinary(parts.Single(p => p.ContentType.Contains("application/octet-stream")).ContentMemory));
    });
    if (stage is "all" or "binary") await Measure("binary-parse", () => ValueTask.FromResult<object>(ParseBinary(binary)));
    if (stage is "all" or "restore") await Measure("eager-document-restore", async () => await StoredDocument.RestoreAsync(state, content));
    if (stage is "all" or "nested") await Measure("nested-object-group-build", () => ValueTask.FromResult<object>(
        StorageManifestBuilder.BuildObjectGroupDataElement(identity.ObjectGroupGuid, new(guid, 1), identity.ObjectGuid,
            (ulong)payload.Length, payload)));
    if (stage is "all" or "file-build") await Measure("file-response-build", () => ValueTask.FromResult<object>(
        FileContentPartitionBuilder.BuildQueryChangesResponse(1, payload, identity, 1)));
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
    {
        source = Option("--source", "unspecified"), runtime = RuntimeInformation.FrameworkDescription,
        os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        processorCount = Environment.ProcessorCount, serverGc = System.Runtime.GCSettings.IsServerGC,
        workload, provenance, backend, iterations, payloadBytes = payload.Length, requestBytes = inbound.Length,
        binaryBytes = binary.Length, graphElements = filePartition.Elements.Length,
        graphBytes = filePartition.Elements.Sum(e => e.Payload.Length),
        payloadSha256 = Convert.ToHexStringLower(SHA256.HashData(payload)),
        nestedSha256 = Convert.ToHexStringLower(SHA256.HashData(StorageManifestBuilder.BuildObjectGroupDataElement(
            identity.ObjectGroupGuid, new(guid, 1), identity.ObjectGuid, (ulong)payload.Length, payload).Data!)),
        fileCurrentSha256 = Convert.ToHexStringLower(SHA256.HashData(FileContentPartitionBuilder.BuildQueryChangesResponse(
            1, payload, identity, 1).ToByteArray(FsshttpbSerializationProfile.Current))),
        fileLegacySha256 = Convert.ToHexStringLower(SHA256.HashData(FileContentPartitionBuilder.BuildQueryChangesResponse(
            1, payload, identity, 1).ToByteArray(FsshttpbSerializationProfile.SharePoint13_11))), measurements = rows,
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(output);

    async Task Measure(string name, Func<ValueTask<object>> operation)
    {
        // Warm JIT/provider paths. Keep setup, output serialization and probes outside the batch.
        for (int i = 0; i < 5; i++) _ = await operation();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        content.Reset();
        var samples = new double[iterations];
        var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        long before = GC.GetTotalAllocatedBytes(precise: true);
        long start = Stopwatch.GetTimestamp();
        object? result = null;
        for (int i = 0; i < iterations; i++)
        {
            long tick = Stopwatch.GetTimestamp();
            result = await operation();
            samples[i] = Stopwatch.GetElapsedTime(tick).TotalMilliseconds;
        }
        double wallMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        var reads = content.Reads;
        var ioBytes = content.Bytes;
        // Retained result is not transient peak memory. Process peak includes setup/JIT and all stages.
        long retainedManagedBytes = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(result);
        Array.Sort(samples);
        rows.Add(new { stage = name, allocatedBytesPerOperation = (double)allocated / iterations,
            allocatedBytesPerPayloadByte = payload.Length == 0 ? (double?)null : (double)allocated / iterations / payload.Length,
            wallMsPerOperation = wallMs / iterations, cpuMsPerOperation = cpuMs / iterations,
            p50Ms = samples[(iterations - 1) / 2], p95Ms = samples[(int)Math.Ceiling(iterations * .95) - 1],
            contentReadsPerOperation = (double)reads / iterations, logicalContentBytesPerOperation = (double)ioBytes / iterations,
            retainedManagedBytes, processPeakWorkingSetBytes = process.PeakWorkingSet64 });
    }
}
finally
{
    if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
}

static FsshttpbCellRequest ParseBinary(ReadOnlyMemory<byte> binary)
{
    var reader = new BinaryReaderEx(binary);
    var request = FsshttpbCellRequest.Deserialize(reader);
    if (reader.Remaining != 0) throw new InvalidDataException("Binary parser did not consume the complete request.");
    return request;
}

sealed class CountingContentStore(IContentStore inner) : IContentStore
{
    public bool Durable => inner.Durable;
    public bool Shared => inner.Shared;
    public long Reads { get; private set; }
    public long Bytes { get; private set; }
    public void Reset() { Reads = 0; Bytes = 0; }
    public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) => inner.WriteAsync(source, cancellationToken);
    public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
    {
        Reads++; Bytes += handle.Length;
        return inner.OpenReadAsync(handle, cancellationToken);
    }
}
