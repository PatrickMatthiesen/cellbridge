using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.FileSystem;
using CellBridge.Storage.InMemory;

// BenchmarkDotNet owns process isolation, warm-up, measurement and GC diagnostics.
// The companion --diagnostics mode retains logical provider I/O and reviewed
// capture reports that BenchmarkDotNet's microbenchmark columns do not describe.
[MemoryDiagnoser]
public class AllocationBenchmarks
{
    [Params(1024, 1048576, 8388608)]
    public int PayloadBytes { get; set; }

    [Params("memory", "filesystem")]
    public string Backend { get; set; } = "memory";

    private readonly Guid guid = new("00112233-4455-6677-8899-aabbccddeeff");
    private StorageManifestBuilder.StableIdentity identity = null!;
    private string temporaryRoot = null!;
    private IContentStore content = null!;
    private byte[] payload = null!;
    private byte[] binary = null!;
    private byte[] inbound = null!;
    private DocumentState state = null!;
    private const string ContentType = "multipart/related; boundary=bench";

    [GlobalSetup]
    public async Task SetupAsync()
    {
        temporaryRoot = Path.Combine(Path.GetTempPath(), "cellbridge-bdn-" + Guid.NewGuid().ToString("N"));
        content = Backend == "memory" ? new InMemoryContentStore() : new FileSystemContentStore(temporaryRoot);
        identity = new(new(1, guid), new(2, guid), new(3, guid), new(4, guid), new(5, guid), new(6, guid),
            new(7, guid), new(new(8, guid), new(9, guid)), guid);
        payload = new byte[PayloadBytes];
        new Random(42).NextBytes(payload);
        var response = FileContentPartitionBuilder.BuildQueryChangesResponse(1, payload, identity, 1);
        var graph = PartitionGraphSnapshot.Create(response.DataElementPackage!.DataElements, identity.ObjectDataBlobGuid);
        var request = new FsshttpbCellRequest { DataElementPackage = response.DataElementPackage };
        request.SubRequests.Add(new(RequestTypes.PutChanges) { RequestId = 1,
            Data = new PutChangesSubRequestData { StorageIndex = identity.ObjectDataBlobGuid } });
        binary = request.ToByteArray();
        var soap = "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><RequestVersion xmlns=\"http://schemas.microsoft.com/sharepoint/soap/\" Version=\"2\" MinorVersion=\"2\"/><RequestCollection xmlns=\"http://schemas.microsoft.com/sharepoint/soap/\" CorrelationId=\"00112233-4455-6677-8899-aabbccddeeff\"><Request Url=\"http://example.invalid/shared/test.docx\" RequestToken=\"1\"><SubRequest Type=\"Cell\" SubRequestToken=\"1\"><SubRequestData BinaryDataSize=\"" + binary.Length + "\"><x:Include xmlns:x=\"http://www.w3.org/2004/08/xop/include\" href=\"cid:binary\"/></SubRequestData></SubRequest></Request></RequestCollection></s:Body></s:Envelope>";
        inbound = [.. Encoding.UTF8.GetBytes("--bench\r\nContent-Type: application/xop+xml\r\nContent-ID: <soap>\r\n\r\n" + soap + "\r\n--bench\r\nContent-Type: application/octet-stream\r\nContent-ID: <binary>\r\n\r\n"),
            .. binary, .. "\r\n--bench--\r\n"u8];
        state = await new DocumentStore().Put("/shared/benchmark.docx", payload).CaptureAsync(content);
        var partition = await DocumentPartition.CaptureFileGraphAsync(state.Partitions.Single(p => p.Kind == 0), graph, 1, state.Content, content);
        state = state with { Partitions = state.Partitions.Select(p => p.Kind == 0 ? partition : p).ToImmutableArray() };
        // Write metadata beside the BDN results. This happens once per benchmark
        // case before its warm-up, never in a measured invocation.
        var directory = Path.GetFullPath(Environment.GetEnvironmentVariable("CELLBRIDGE_BENCHMARK_METADATA")
            ?? Path.Combine(FindRepository(), "artifacts", "allocations", "metadata"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{PayloadBytes}-{Backend}-{Environment.ProcessId}.json"), JsonSerializer.Serialize(new
        {
            workload = "Deterministic complete synthetic file graph, seed 42; opaque bytes, no Office client.", PayloadBytes, Backend,
            payloadSha256 = Convert.ToHexStringLower(SHA256.HashData(payload)), requestBytes = inbound.Length, binaryBytes = binary.Length,
            setup = "Payload, MTOM request and stored document prepared in GlobalSetup; BDN warm-up excluded from measured intervals.",
            interval = "One parse, restore or build invocation, selected by the benchmark method.",
            source = BenchmarkMetadata.Capture(typeof(AllocationBenchmarks).Assembly, typeof(FsshttpbCellRequest).Assembly, typeof(StoredDocument).Assembly)
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Benchmark]
    public FsshttpbCellRequest InboundMtomSoapBinary()
    {
        var parts = MtomMessageParser.ParseViews(inbound, ContentType);
        _ = CellStorageRequestParser.Parse(Encoding.UTF8.GetString(parts.First(p => p.ContentType.Contains("xop+xml")).ContentMemory.Span));
        return Parse(parts.Single(p => p.ContentType.Contains("application/octet-stream")).ContentMemory);
    }

    [Benchmark]
    public FsshttpbCellRequest BinaryParse() => Parse(binary);

    [Benchmark]
    public async Task<StoredDocument> Restore() => await StoredDocument.RestoreAsync(state, content);

    [Benchmark]
    public DataElement NestedObjectGroupBuild() => StorageManifestBuilder.BuildObjectGroupDataElement(
        identity.ObjectGroupGuid, new(guid, 1), identity.ObjectGuid, (ulong)payload.Length, payload);

    [Benchmark]
    public FsshttpbResponse FileResponseBuild() => FileContentPartitionBuilder.BuildQueryChangesResponse(1, payload, identity, 1);

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
    }

    private static FsshttpbCellRequest Parse(ReadOnlyMemory<byte> bytes)
    {
        var reader = new BinaryReaderEx(bytes);
        var result = FsshttpbCellRequest.Deserialize(reader);
        if (reader.Remaining != 0) throw new InvalidDataException("Binary parser did not consume the complete request.");
        return result;
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CellBridge.slnx"))) return directory.FullName;
        return Directory.GetCurrentDirectory();
    }
}
