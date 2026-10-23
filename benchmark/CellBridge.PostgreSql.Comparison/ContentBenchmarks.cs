using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using CellBridge.Storage.Abstractions;
using CellBridge.TestInfrastructure;

[MemoryDiagnoser]
public class ContentReadBenchmarks
{
    [ParamsSource(nameof(Variants))] public string Variant { get; set; } = "";
    public IEnumerable<string> Variants => Settings.Variants;
    private PostgreSqlEnvironment environment = null!;
    private Database? database;
    private IContentStore content = null!;
    private ContentHandle handle = null!;
    private byte[] payload = null!;
    private Evidence evidence = null!;
    [GlobalSetup]
    public async Task Setup()
    {
        payload = Inputs.Content(0);
        evidence = new(nameof(ContentReadBenchmarks), Variant, new { bytes = payload.Length, seed = 42,
            payloadSha256 = Settings.Hash(payload), timing = "OpenReadAsync and complete CopyToAsync(Stream.Null); no caller hash in timing.", clients = 1 });
        environment = await PostgreSqlEnvironment.StartAsync();
        try
        {
            database = await Database.OpenAsync(environment.ConnectionString, Variant, lists: false);
            content = Providers.Content(Variant, database.Runtime, new());
            using var input = new MemoryStream(payload, writable: false);
            handle = await content.WriteAsync(input);
            await ContentChecks.VerifyAsync(content, handle, payload);
            evidence.Write("setup-complete", new { database = await database.MetadataAsync(), counts = await database.CountsAsync(), handle });
        }
        catch { await Cleanup(); throw; }
    }
    [Benchmark]
    public async Task Read()
    {
        await using var stream = await content.OpenReadAsync(handle);
        await stream.CopyToAsync(Stream.Null);
    }
    [GlobalCleanup]
    public async Task Cleanup()
    {
        try
        {
            if (database is not null && handle is not null)
            {
                await ContentChecks.VerifyAsync(content, handle, payload);
                evidence.Write("validated", new { handle, counts = await database!.CountsAsync() });
            }
        }
        finally
        {
            try { if (database is not null) { await database.DisposeAsync(); database = null; } }
            finally { if (environment is not null) { await environment.DisposeAsync(); environment = null!; } }
        }
    }
}

[MemoryDiagnoser, Config(typeof(BoundedBatchConfig))]
public class ContentWriteBenchmarks
{
    public const int BatchSize = 8;
    [ParamsSource(nameof(Variants))] public string Variant { get; set; } = "";
    public IEnumerable<string> Variants => Settings.Variants;
    private PostgreSqlEnvironment environment = null!;
    private Database? database;
    private IContentStore content = null!;
    private readonly byte[][] payloads = new byte[BatchSize][];
    private readonly MemoryStream[] inputs = new MemoryStream[BatchSize];
    private readonly ContentHandle[] handles = new ContentHandle[BatchSize];
    private Evidence evidence = null!;
    private int iteration;
    [GlobalSetup]
    public async Task Setup()
    {
        for (var i = 0; i < BatchSize; i++) payloads[i] = Inputs.Content(i);
        evidence = new(nameof(ContentWriteBenchmarks), Variant, new { BatchSize, bytesPerObject = 1024 * 1024,
            seeds = "42 through 49", payloadSha256 = payloads.Select(Settings.Hash).ToArray(), clients = 1,
            timing = "Eight new-object WriteAsync operations. Empty database recreated per iteration; inputs prepared outside timing." });
        environment = await PostgreSqlEnvironment.StartAsync();
    }
    [IterationSetup] public void Prepare() => PrepareAsync().GetAwaiter().GetResult();
    private async Task PrepareAsync()
    {
        if (database is not null) { await database.DisposeAsync(); database = null; }
        database = await Database.OpenAsync(environment.ConnectionString, Variant, lists: false);
        content = Providers.Content(Variant, database.Runtime, new());
        for (var i = 0; i < BatchSize; i++)
        {
            inputs[i]?.Dispose(); inputs[i] = new MemoryStream(payloads[i], writable: false);
        }
        if (iteration == 0) evidence.Write("database", await database.MetadataAsync());
        evidence.Write("iteration-prepared", new { iteration, counts = await database.CountsAsync() });
    }
    [Benchmark(OperationsPerInvoke = BatchSize)]
    public async Task Batch()
    {
        for (var i = 0; i < BatchSize; i++) handles[i] = await content.WriteAsync(inputs[i]);
    }
    [IterationCleanup] public void Verify() => VerifyAsync().GetAwaiter().GetResult();
    private async Task VerifyAsync()
    {
        for (var i = 0; i < BatchSize; i++) await ContentChecks.VerifyAsync(content, handles[i], payloads[i]);
        var counts = await database!.CountsAsync();
        Settings.Require(counts["cellbridge_objects"] == BatchSize && counts["cellbridge_chunks"] == BatchSize * 16,
            "Content write did not store eight distinct 16-chunk objects.");
        evidence.Write("iteration-validated", new { iteration = iteration++, counts, handlesSha256 = Settings.JsonHash(handles) });
    }
    [GlobalCleanup]
    public async Task Cleanup()
    {
        foreach (var input in inputs) input?.Dispose();
        try { if (database is not null) await database.DisposeAsync(); }
        finally { if (environment is not null) await environment.DisposeAsync(); }
    }
}

internal static class ContentChecks
{
    public static async Task VerifyAsync(IContentStore store, ContentHandle handle, byte[] expected)
    {
        await using var stream = await store.OpenReadAsync(handle);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long count = 0; int read;
        while ((read = await stream.ReadAsync(buffer)) > 0) { hash.AppendData(buffer.AsSpan(0, read)); count += read; }
        var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        Settings.Require(count == expected.Length && handle.Length == count && handle.Sha256 == digest &&
            digest == Settings.Hash(expected), "Stored content differs from input.");
    }
}
