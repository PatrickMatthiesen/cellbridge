using BenchmarkDotNet.Attributes;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestInfrastructure;

[MemoryDiagnoser, Config(typeof(BoundedBatchConfig))]
public class ServiceSaveBenchmarks
{
    public const int BatchSize = 8;
    public const int PriorSaves = 4;
    [ParamsSource(nameof(Variants))] public string Variant { get; set; } = "";
    public IEnumerable<string> Variants => Settings.Variants;
    [Params("changed-full", "delta")] public string Mode { get; set; } = "";
    private PostgreSqlEnvironment environment = null!;
    private Database? database;
    private StorageProvider provider = null!;
    private CellBridgeDocumentService service = null!;
    private Evidence evidence = null!;
    private readonly CellBridgeActor actor = new(new SubjectIdentity("tests:writer", "test-writer", "Test writer"), CanCreate: true);
    private readonly Dictionary<string, string> attributes = new();
    private readonly DocumentState[] starting = new DocumentState[BatchSize];
    private readonly FsshttpbCellRequest[] requests = new FsshttpbCellRequest[BatchSize];
    private readonly CellExecution[] results = new CellExecution[BatchSize];
    private readonly byte[][] payloads = new byte[5 + BatchSize][];
    private int iteration;
    [GlobalSetup]
    public async Task Setup()
    {
        for (var i = 0; i < payloads.Length; i++) payloads[i] = Inputs.VerifiedPackage(42 + i);
        evidence = new(nameof(ServiceSaveBenchmarks), Variant, new { Mode, BatchSize, PriorSaves, clients = 1,
            startingHistoryRevisions = PriorSaves + 1, startingSaveReceipts = PriorSaves,
            endingHistoryRevisions = PriorSaves + 2, endingSaveReceipts = PriorSaves + 1,
            initialPayloadSeed = 42, preparationPayloadSeeds = Mode == "changed-full" ? "43 through 46" : "42",
            measuredPayloadSeeds = Mode == "changed-full" ? "47 through 54, one distinct new package per document" : "42, existing file objects reused",
            payloadSha256 = payloads.Select(Settings.Hash).ToArray(), limits = new StorageLimits(),
            timing = "ExecuteAsync only, one save on each of eight prepared documents. Restore, request construction and readback excluded.",
            exclusions = "No HTTP, binary query workload, retry timing, retention analysis or manual forced-GC heap probes." });
        environment = await PostgreSqlEnvironment.StartAsync();
    }
    [IterationSetup] public void Prepare() => PrepareAsync().GetAwaiter().GetResult();
    private async Task PrepareAsync()
    {
        if (database is not null) { await database.DisposeAsync(); database = null; }
        database = await Database.OpenAsync(environment.ConnectionString, Variant, lists: false);
        var limits = new StorageLimits();
        provider = new(Providers.State(Variant, database.Runtime, limits), Providers.Content(Variant, database.Runtime, limits), limits);
        provider.Require(durable: true, multipleInstances: false);
        await provider.State.CheckHealthAsync();
        service = new(provider);
        for (var doc = 0; doc < BatchSize; doc++)
        {
            var state = await service.CreateAsync($"/matched-{doc}.docx", payloads[0], actor)
                ?? throw new InvalidDataException("Document was not created.");
            var random = new Random(42 + doc);
            for (var save = 0; save < PriorSaves; save++)
            {
                var restored = await StoredDocument.RestoreAsync(state, provider.Content);
                var bytes = Mode == "changed-full" ? payloads[save + 1] : payloads[0];
                var request = SyntheticWorkload.Save(restored, bytes, Mode, save, random);
                var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, attributes, actor);
                VerifyExecution(result, state);
                state = result.State;
            }
            Settings.Require(state.Receipts.Length == PriorSaves && state.Revisions.Length == PriorSaves + 1,
                "Preparation did not establish the fixed save history.");
            starting[doc] = state;
            var current = await StoredDocument.RestoreAsync(state, provider.Content);
            requests[doc] = SyntheticWorkload.Save(current, Mode == "changed-full" ? payloads[5 + doc] : payloads[0], Mode, PriorSaves, random);
        }
        if (iteration == 0) evidence.Write("database", await database.MetadataAsync());
        evidence.Write("iteration-prepared", new { iteration, counts = await database.CountsAsync(),
            histories = starting.Select(History).ToArray(),
            // Protocol IDs created by DocumentStore vary. These are provenance hashes, not cross-provider equality assertions.
            requestSha256 = requests.Select(r => Settings.Hash(r.ToByteArray())).ToArray() });
    }
    [Benchmark(OperationsPerInvoke = BatchSize)]
    public async Task Batch()
    {
        for (var doc = 0; doc < BatchSize; doc++)
            results[doc] = await service.ExecuteAsync(starting[doc].ResourceId, DocumentPartitionKind.FileContents, requests[doc], attributes, actor);
    }
    [IterationCleanup] public void Verify() => VerifyAsync().GetAwaiter().GetResult();
    private async Task VerifyAsync()
    {
        var states = new DocumentState[BatchSize];
        for (var doc = 0; doc < BatchSize; doc++)
        {
            var result = results[doc]; VerifyExecution(result, starting[doc]);
            var state = await provider.State.FindByResourceIdAsync(result.State.ResourceId)
                ?? throw new InvalidDataException("Saved document disappeared.");
            Settings.Require(state.StateVersion == result.State.StateVersion && state.Content == result.State.Content &&
                state.Receipts.Length == PriorSaves + 1 && state.Revisions.Length == PriorSaves + 2,
                "Saved history/receipt readback mismatch.");
            await ContentChecks.VerifyAsync(provider.Content, state.Content, Mode == "changed-full" ? payloads[5 + doc] : payloads[0]);
            var restored = await StoredDocument.RestoreAsync(state, provider.Content);
            Settings.Require(Settings.Hash(restored.Content) == state.Content.Sha256, "Graph restore differs from saved content.");
            states[doc] = state;
        }
        evidence.Write("iteration-validated", new { iteration = iteration++, counts = await database!.CountsAsync(),
            histories = states.Select(History).ToArray(), normalizedResultsSha256 = Settings.JsonHash(states.Select(History)),
            stateSha256 = states.Select(s => Settings.Hash(System.Text.Encoding.UTF8.GetBytes(DocumentStateJson.Serialize(s)))).ToArray(),
            responseSha256 = results.Select(r => Settings.Hash(r.Response.ToByteArray())).ToArray() });
    }
    private static object History(DocumentState s) => new { s.StateVersion, s.ContentVersion, contentSha256 = s.Content.Sha256,
        revisions = s.Revisions.Length, receipts = s.Receipts.Length,
        graphElements = s.Partitions.Single(p => p.Kind == 0).Elements.Length,
        revisionContents = s.Revisions.Select(r => r.Content.Sha256).ToArray() };
    private static void VerifyExecution(CellExecution result, DocumentState previous)
    {
        Settings.Require(!result.IsCancelled && result.LockError is null && !result.Response.Status &&
            result.Response.SubResponses.Count == 1 && !result.Response.SubResponses[0].Status &&
            result.State.StateVersion == previous.StateVersion + 1 &&
            result.State.ContentVersion == previous.ContentVersion + 1 &&
            result.AcceptedSaves.Length == 1 && !result.AcceptedSaves[0].IsReplay, "Save failed, replayed, or did not publish a new revision.");
    }
    [GlobalCleanup]
    public async Task Cleanup()
    {
        try { if (database is not null) await database.DisposeAsync(); }
        finally { if (environment is not null) await environment.DisposeAsync(); }
    }
}
