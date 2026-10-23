using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using CellBridge.Lists;
using CellBridge.TestInfrastructure;

public sealed class BoundedBatchConfig : ManualConfig
{
    public BoundedBatchConfig() => AddJob(Job.Default.WithInvocationCount(1).WithUnrollFactor(1)
        .WithMinWarmupCount(3).WithMaxWarmupCount(8).WithMinIterationCount(8).WithMaxIterationCount(16).AsMutator());
}

[MemoryDiagnoser]
public class PagingBenchmarks
{
    [ParamsSource(nameof(Variants))] public string Variant { get; set; } = "";
    public IEnumerable<string> Variants => Settings.Variants;
    [Params(false, true)] public bool Analyzed { get; set; }
    private PostgreSqlEnvironment environment = null!;
    private Database database = null!;
    private IListEngine engine = null!;
    private Evidence evidence = null!;
    private ListQuery first = null!, second = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        evidence = new(nameof(PagingBenchmarks), Variant, new { Analyzed, records = 1000, payloadBytes = 1024,
            seed = 42, filter = "Counter >= 500", ascending = true, pageSize = 100, clients = 1,
            statisticsControl = "Table auto-analyze threshold INT_MAX, scale factor 0 for both cases; explicit ANALYZE only for analyzed case.",
            timing = "QueryAsync only; BDN adaptive pilot, warmup and target iterations." });
        environment = await PostgreSqlEnvironment.StartAsync();
        try
        {
            database = await Database.OpenAsync(environment.ListsConnectionString, Variant, lists: true);
            engine = Providers.Lists(Variant, database.Runtime);
            await using (var control = database.Admin.CreateCommand("ALTER TABLE cellbridge_lists.records SET (autovacuum_analyze_threshold=2147483647, autovacuum_analyze_scale_factor=0)"))
                await control.ExecuteNonQueryAsync();
            await Inputs.PrepareListAsync(database, engine, 1000);
            if (Analyzed)
            {
                await using var analyze = database.Admin.CreateCommand("ANALYZE cellbridge_lists.records");
                await analyze.ExecuteNonQueryAsync();
            }
            await using (var stats = database.Admin.CreateCommand("SELECT count(*) FROM pg_stats WHERE schemaname='cellbridge_lists' AND tablename='records'"))
                Settings.Require(((long)(await stats.ExecuteScalarAsync())! > 0) == Analyzed, "Unexpected fresh/analyzed statistics state.");
            first = new([new(Inputs.Counter, FilterOperator.GreaterOrEqual, System.Text.Json.Nodes.JsonValue.Create(500))], Inputs.Counter, Limit: 100);
            var page = await engine.QueryAsync(Inputs.Actor, Inputs.Workspace, Inputs.List, first);
            Inputs.VerifyPage(page, 500);
            second = first with { AfterRecordId = page.NextRecordId };
            Inputs.VerifyPage(await engine.QueryAsync(Inputs.Actor, Inputs.Workspace, Inputs.List, second), 600);
            evidence.Write("setup-complete", new { database = await database.MetadataAsync(), statistics = await database.StatisticsAsync(),
                counts = await database.CountsAsync(), payloadSha256 = Settings.Hash(System.Text.Encoding.UTF8.GetBytes(Inputs.Payload)) });
        }
        catch { await Cleanup(); throw; }
    }
    [Benchmark] public Task<RecordPage> FirstPage() => engine.QueryAsync(Inputs.Actor, Inputs.Workspace, Inputs.List, first);
    [Benchmark] public Task<RecordPage> SecondPage() => engine.QueryAsync(Inputs.Actor, Inputs.Workspace, Inputs.List, second);
    internal Task<RecordPage> QueryThreshold(int value) => engine.QueryAsync(Inputs.Actor, Inputs.Workspace, Inputs.List,
        first with { Filters = [new(Inputs.Counter, FilterOperator.GreaterOrEqual, System.Text.Json.Nodes.JsonValue.Create(value))] });
    [GlobalCleanup]
    public async Task Cleanup()
    {
        try
        {
            if (database is not null && second is not null)
            {
                var a = await FirstPage(); var b = await SecondPage();
                Inputs.VerifyPage(a, 500); Inputs.VerifyPage(b, 600);
                evidence.Write("validated", new { firstPageSha256 = Settings.JsonHash(a.Records.Select(Inputs.Normalize)),
                    secondPageSha256 = Settings.JsonHash(b.Records.Select(Inputs.Normalize)), statistics = await database.StatisticsAsync() });
            }
        }
        finally
        {
            try { if (database is not null) { await database.DisposeAsync(); database = null!; } }
            finally { if (environment is not null) { await environment.DisposeAsync(); environment = null!; } }
        }
    }
}

[MemoryDiagnoser, Config(typeof(BoundedBatchConfig))]
public class ListMutationBenchmarks
{
    public const int BatchSize = 16;
    [ParamsSource(nameof(Variants))] public string Variant { get; set; } = "";
    public IEnumerable<string> Variants => Settings.Variants;
    [Params("create", "patch")] public string Operation { get; set; } = "";
    private PostgreSqlEnvironment environment = null!;
    private Database? database;
    private IListEngine engine = null!;
    private Evidence evidence = null!;
    private readonly CreateRecordRequest[] creates = new CreateRecordRequest[BatchSize];
    private readonly UpdateRecordRequest[] patches = new UpdateRecordRequest[BatchSize];
    private readonly CreateRecordResult[] created = new CreateRecordResult[BatchSize];
    private readonly ListRecord[] patched = new ListRecord[BatchSize];
    private Dictionary<string, long> before = null!;
    private int iteration;
    [GlobalSetup]
    public async Task Setup()
    {
        evidence = new(nameof(ListMutationBenchmarks), Variant, new { Operation, BatchSize, clients = 1, workspaces = 1,
            startingRecords = Operation == "patch" ? BatchSize : 0, startingRevision = 1,
            timing = "One batch per BDN iteration. Only mutation setup changed: one database/runtime source/engine per case, one successful validated untimed batch on that engine in GlobalSetup, then schema reset; IterationSetup resets the Lists schema and repeats public preparation; validation in IterationCleanup.",
            limits = new ListLimits(), payloadSha256 = Settings.Hash(System.Text.Encoding.UTF8.GetBytes(Inputs.Payload)) });
        environment = await PostgreSqlEnvironment.StartAsync();
        try
        {
            database = await Database.OpenAsync(environment.ListsConnectionString, Variant, lists: true);
            engine = Providers.Lists(Variant, database.Runtime);
            evidence.Write("database", await database.MetadataAsync());
            await PrepareFixtureAsync(engineWarmup: true);
            await Batch();
            await VerifyAsync(engineWarmup: true);
            await database.ResetListsAsync();
        }
        catch { await Cleanup(); throw; }
    }
    [IterationSetup] public void Prepare() => PrepareAsync().GetAwaiter().GetResult();
    private async Task PrepareAsync()
    {
        await database!.ResetListsAsync();
        await PrepareFixtureAsync(engineWarmup: false);
    }
    private async Task PrepareFixtureAsync(bool engineWarmup)
    {
        await Inputs.PrepareListAsync(database!, engine, Operation == "patch" ? BatchSize : 0);
        for (var i = 0; i < BatchSize; i++)
        {
            creates[i] = new(Inputs.RecordId(i), 1, Inputs.Values(i));
            patches[i] = new(1, 1, [new(Inputs.Counter, System.Text.Json.Nodes.JsonValue.Create(i + BatchSize))]);
        }
        before = await database!.CountsAsync();
        if (engineWarmup) evidence.Write("engine-warmup-prepared", new { before, requestsSha256 = Operation == "create"
            ? Settings.JsonHash(creates) : Settings.JsonHash(patches) });
        else evidence.Write("iteration-prepared", new { iteration, before, requestsSha256 = Operation == "create"
            ? Settings.JsonHash(creates) : Settings.JsonHash(patches) });
    }
    [Benchmark(OperationsPerInvoke = BatchSize)]
    public async Task Batch()
    {
        for (var i = 0; i < BatchSize; i++)
            if (Operation == "create") created[i] = await engine.CreateRecordAsync(Inputs.Actor, Inputs.Workspace, Inputs.List, creates[i]);
            else patched[i] = await engine.UpdateRecordAsync(Inputs.Actor, Inputs.Workspace, Inputs.List, Inputs.RecordId(i), patches[i]);
    }
    [IterationCleanup] public void Verify() => VerifyAsync().GetAwaiter().GetResult();
    private async Task VerifyAsync(bool engineWarmup = false)
    {
        var records = new ListRecord[BatchSize];
        for (var i = 0; i < BatchSize; i++)
        {
            records[i] = await engine.GetRecordAsync(Inputs.Actor, Inputs.Workspace, Inputs.List, Inputs.RecordId(i));
            var r = records[i];
            Settings.Require(r.Revision == (Operation == "patch" ? 2 : 1) && r.SchemaVersion == 1 && r.Number == i + 1 &&
                r.Values[Inputs.Counter.ToString("D")]!.GetValue<long>() == i + (Operation == "patch" ? BatchSize : 0) &&
                r.Values[Inputs.PayloadField.ToString("D")]!.GetValue<string>() == Inputs.Payload, "Mutation readback mismatch.");
            if (Operation == "create") Settings.Require(!created[i].Replayed && created[i].Id == r.Id && created[i].Revision == 1, "Create replayed/failed.");
            else Settings.Require(Settings.JsonHash(Inputs.Normalize(patched[i])) == Settings.JsonHash(Inputs.Normalize(r)), "Patch result/readback mismatch.");
        }
        var after = await database!.CountsAsync();
        Settings.Require(after["cellbridge_lists.records"] == BatchSize &&
            after["cellbridge_lists.create_receipts"] == BatchSize &&
            after["cellbridge_lists.audit_events"] - before["cellbridge_lists.audit_events"] == BatchSize, "Unexpected mutation/audit/receipt counts.");
        if (engineWarmup) evidence.Write("engine-warmup-validated", new { before, after,
            normalizedResultsSha256 = Settings.JsonHash(records.Select(Inputs.Normalize)) });
        else evidence.Write("iteration-validated", new { iteration = iteration++, before, after,
            normalizedResultsSha256 = Settings.JsonHash(records.Select(Inputs.Normalize)) });
    }
    [GlobalCleanup]
    public async Task Cleanup()
    {
        try { if (database is not null) { await database.DisposeAsync(); database = null; } }
        finally { if (environment is not null) { await environment.DisposeAsync(); environment = null!; } }
    }
}
