using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;

[MemoryDiagnoser]
public class PostgreSqlContentReadBenchmarks
{
    private string connectionString = null!;
    private BenchmarkDatabase database = null!;
    private PostgreSqlContentStore content = null!;
    private ContentHandle handle = null!;
    private byte[] payload = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        payload = ProviderBenchmarkInputs.Content(0);
        connectionString = BenchmarkConnections.Require(BenchmarkConnections.Documents);
        database = await BenchmarkDatabase.OpenAsync(connectionString, new StorageLimits());
        content = new(database.Source, new StorageLimits().MaxObjectBytes);
        using var input = new MemoryStream(payload, writable: false);
        handle = await content.WriteAsync(input);
        await ProviderBenchmarkInputs.VerifyContentAsync(content, handle, payload);
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
        if (database is not null)
        {
            await ProviderBenchmarkInputs.VerifyContentAsync(content, handle, payload);
            await database.DisposeAsync();
        }
    }
}

[MemoryDiagnoser]
public class PostgreSqlContentWriteBenchmarks
{
    private const int BatchSize = 8;
    private string connectionString = null!;
    private BenchmarkDatabase? database;
    private PostgreSqlContentStore content = null!;
    private readonly byte[][] payloads = Enumerable.Range(0, BatchSize).Select(ProviderBenchmarkInputs.Content).ToArray();
    private readonly MemoryStream[] inputs = new MemoryStream[BatchSize];
    private readonly ContentHandle[] handles = new ContentHandle[BatchSize];

    [GlobalSetup]
    public void Setup() => connectionString = BenchmarkConnections.Require(BenchmarkConnections.Documents);

    [IterationSetup]
    public void Prepare() => PrepareAsync().GetAwaiter().GetResult();

    private async Task PrepareAsync()
    {
        if (database is not null) await database.DisposeAsync();
        database = await BenchmarkDatabase.OpenAsync(connectionString, new StorageLimits());
        content = new(database.Source, new StorageLimits().MaxObjectBytes);
        for (var i = 0; i < BatchSize; i++)
        {
            inputs[i]?.Dispose();
            inputs[i] = new MemoryStream(payloads[i], writable: false);
        }
    }

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public async Task Batch()
    {
        for (var i = 0; i < BatchSize; i++) handles[i] = await content.WriteAsync(inputs[i]);
    }

    [IterationCleanup]
    public void Verify() => VerifyAsync().GetAwaiter().GetResult();

    private async Task VerifyAsync()
    {
        for (var i = 0; i < BatchSize; i++)
            await ProviderBenchmarkInputs.VerifyContentAsync(content, handles[i], payloads[i]);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        foreach (var input in inputs) input?.Dispose();
        if (database is not null) await database.DisposeAsync();
    }
}

[MemoryDiagnoser]
public class PostgreSqlServiceSaveBenchmarks
{
    private const int BatchSize = 8;
    private const int PriorSaves = 4;
    [Params("changed-full", "delta")] public string Mode { get; set; } = "";
    private string connectionString = null!;
    private BenchmarkDatabase? database;
    private StorageProvider provider = null!;
    private CellBridgeDocumentService service = null!;
    private readonly CellBridgeActor actor = new(new SubjectIdentity("benchmark:writer", "benchmark-writer", "Benchmark writer"), CanCreate: true);
    private readonly Dictionary<string, string> attributes = new();
    private readonly DocumentState[] starting = new DocumentState[BatchSize];
    private readonly FsshttpbCellRequest[] requests = new FsshttpbCellRequest[BatchSize];
    private readonly CellExecution[] results = new CellExecution[BatchSize];
    private readonly byte[][] payloads = Enumerable.Range(0, 5 + BatchSize)
        .Select(seed => StorageBenchmarkInputs.Package(1, seed + 42)).ToArray();

    [GlobalSetup]
    public void Setup() => connectionString = BenchmarkConnections.Require(BenchmarkConnections.Documents);

    [IterationSetup]
    public void Prepare() => PrepareAsync().GetAwaiter().GetResult();

    private async Task PrepareAsync()
    {
        if (database is not null) await database.DisposeAsync();
        database = await BenchmarkDatabase.OpenAsync(connectionString, new StorageLimits());
        var limits = new StorageLimits();
        provider = new(new PostgreSqlStateStore(database.Source, limits),
            new PostgreSqlContentStore(database.Source, limits.MaxObjectBytes), limits);
        provider.Require(durable: true, multipleInstances: false);
        await provider.State.CheckHealthAsync();
        service = new(provider);
        for (var doc = 0; doc < BatchSize; doc++)
        {
            var state = await service.CreateAsync($"/benchmark-{doc}.docx", payloads[0], actor)
                ?? throw new InvalidDataException("Document was not created.");
            var random = new Random(42 + doc);
            for (var save = 0; save < PriorSaves; save++)
            {
                var restored = await StoredDocument.RestoreAsync(state, provider.Content);
                var bytes = Mode == "changed-full" ? payloads[save + 1] : payloads[0];
                var request = SyntheticWorkload.Save(restored, bytes, Mode, save, random);
                var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
                    request, attributes, actor);
                VerifyExecution(result, state);
                state = result.State;
            }
            if (state.Receipts.Length != PriorSaves || state.Revisions.Length != PriorSaves + 1)
                throw new InvalidDataException("Preparation did not establish the expected save history.");
            starting[doc] = state;
            var current = await StoredDocument.RestoreAsync(state, provider.Content);
            var measured = Mode == "changed-full" ? payloads[5 + doc] : payloads[0];
            requests[doc] = SyntheticWorkload.Save(current, measured, Mode, PriorSaves, random);
        }
    }

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public async Task Batch()
    {
        for (var doc = 0; doc < BatchSize; doc++)
            results[doc] = await service.ExecuteAsync(starting[doc].ResourceId,
                DocumentPartitionKind.FileContents, requests[doc], attributes, actor);
    }

    [IterationCleanup]
    public void Verify() => VerifyAsync().GetAwaiter().GetResult();

    private async Task VerifyAsync()
    {
        for (var doc = 0; doc < BatchSize; doc++)
        {
            VerifyExecution(results[doc], starting[doc]);
            var state = await provider.State.FindByResourceIdAsync(results[doc].State.ResourceId)
                ?? throw new InvalidDataException("Saved document disappeared.");
            if (state.Receipts.Length != PriorSaves + 1 || state.Revisions.Length != PriorSaves + 2)
                throw new InvalidDataException("Saved history or receipt count differs.");
            var expected = Mode == "changed-full" ? payloads[5 + doc] : payloads[0];
            await ProviderBenchmarkInputs.VerifyContentAsync(provider.Content, state.Content, expected);
            var restored = await StoredDocument.RestoreAsync(state, provider.Content);
            if (!restored.Content.AsSpan().SequenceEqual(expected))
                throw new InvalidDataException("Restored document differs from the expected content.");
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (database is not null) await database.DisposeAsync();
    }

    private static void VerifyExecution(CellExecution result, DocumentState previous)
    {
        if (result.IsCancelled || result.LockError is not null || result.Response.Status ||
            result.Response.SubResponses.Count != 1 || result.Response.SubResponses[0].Status ||
            result.State.StateVersion != previous.StateVersion + 1 ||
            result.State.ContentVersion != previous.ContentVersion + 1 ||
            result.AcceptedSaves.Length != 1 || result.AcceptedSaves[0].IsReplay)
            throw new InvalidDataException("Save failed, replayed, or did not publish a new revision.");
    }
}

internal static class ProviderBenchmarkInputs
{
    public static byte[] Content(int seed)
    {
        var bytes = new byte[1024 * 1024];
        new Random(seed + 42).NextBytes(bytes);
        return bytes;
    }

    public static async Task VerifyContentAsync(IContentStore store, ContentHandle handle, byte[] expected)
    {
        await using var stream = await store.OpenReadAsync(handle);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        var actual = copy.ToArray();
        hash.AppendData(actual);
        var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!actual.AsSpan().SequenceEqual(expected) || handle.Length != expected.Length || handle.Sha256 != digest)
            throw new InvalidDataException("Stored content differs from the workload input.");
    }
}
