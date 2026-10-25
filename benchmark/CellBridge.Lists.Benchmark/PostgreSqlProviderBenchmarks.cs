using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using CellBridge.TestInfrastructure;

[MemoryDiagnoser]
public class PostgreSqlPagingBenchmarks
{
    [Params(false, true)] public bool Analyzed { get; set; }
    private PostgreSqlEnvironment environment = null!;
    private BenchmarkDatabase database = null!;
    private PostgreSqlListEngine engine = null!;
    private ListQuery first = null!, second = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        environment = await PostgreSqlEnvironment.StartAsync();
        database = await BenchmarkDatabase.OpenAsync(environment.ListsConnectionString);
        engine = new(database.Runtime);
        await using (var control = database.Admin.CreateCommand(
            "ALTER TABLE cellbridge_lists.records SET (autovacuum_analyze_threshold=2147483647, autovacuum_analyze_scale_factor=0)"))
            await control.ExecuteNonQueryAsync();
        await ListBenchmarkInputs.PrepareAsync(database, engine, 1000);
        if (Analyzed)
        {
            await using var analyze = database.Admin.CreateCommand("ANALYZE cellbridge_lists.records");
            await analyze.ExecuteNonQueryAsync();
        }
        await using (var stats = database.Admin.CreateCommand(
            "SELECT count(*) FROM pg_stats WHERE schemaname='cellbridge_lists' AND tablename='records'"))
        {
            var hasStatistics = (long)(await stats.ExecuteScalarAsync())! > 0;
            if (hasStatistics != Analyzed)
                throw new InvalidDataException("PostgreSQL statistics state differs from the requested case.");
        }
        first = new([new(ListBenchmarkInputs.Counter, FilterOperator.GreaterOrEqual, JsonValue.Create(500))],
            ListBenchmarkInputs.Counter, Limit: 100);
        var page = await engine.QueryAsync(ListBenchmarkInputs.Actor, ListBenchmarkInputs.Workspace,
            ListBenchmarkInputs.List, first);
        ListBenchmarkInputs.VerifyPage(page, 500);
        second = first with { AfterRecordId = page.NextRecordId };
        ListBenchmarkInputs.VerifyPage(await engine.QueryAsync(ListBenchmarkInputs.Actor,
            ListBenchmarkInputs.Workspace, ListBenchmarkInputs.List, second), 600);
    }

    [Benchmark] public Task<RecordPage> FirstPage() => engine.QueryAsync(ListBenchmarkInputs.Actor,
        ListBenchmarkInputs.Workspace, ListBenchmarkInputs.List, first);
    [Benchmark] public Task<RecordPage> SecondPage() => engine.QueryAsync(ListBenchmarkInputs.Actor,
        ListBenchmarkInputs.Workspace, ListBenchmarkInputs.List, second);

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (database is not null)
        {
            ListBenchmarkInputs.VerifyPage(await engine.QueryAsync(ListBenchmarkInputs.Actor,
                ListBenchmarkInputs.Workspace, ListBenchmarkInputs.List, first), 500);
            ListBenchmarkInputs.VerifyPage(await engine.QueryAsync(ListBenchmarkInputs.Actor,
                ListBenchmarkInputs.Workspace, ListBenchmarkInputs.List, second), 600);
            await database.DisposeAsync();
        }
        if (environment is not null) await environment.DisposeAsync();
    }
}

[MemoryDiagnoser]
public class PostgreSqlListMutationBenchmarks
{
    private const int BatchSize = 16;
    [Params("create", "patch")] public string Operation { get; set; } = "";
    private PostgreSqlEnvironment environment = null!;
    private BenchmarkDatabase? database;
    private PostgreSqlListEngine engine = null!;
    private readonly CreateRecordRequest[] creates = new CreateRecordRequest[BatchSize];
    private readonly UpdateRecordRequest[] patches = new UpdateRecordRequest[BatchSize];
    private readonly CreateRecordResult[] created = new CreateRecordResult[BatchSize];
    private readonly ListRecord[] patched = new ListRecord[BatchSize];
    private Dictionary<string, long> before = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        environment = await PostgreSqlEnvironment.StartAsync();
        database = await BenchmarkDatabase.OpenAsync(environment.ListsConnectionString);
        engine = new(database.Runtime);
        await PrepareFixtureAsync();
        before = await database.CountsAsync();
        await Batch();
        await VerifyAsync();
        await database.ResetListsAsync();
    }

    [IterationSetup]
    public async Task Prepare()
    {
        await database!.ResetListsAsync();
        await PrepareFixtureAsync();
        before = await database.CountsAsync();
    }

    private async Task PrepareFixtureAsync()
    {
        await ListBenchmarkInputs.PrepareAsync(database!, engine, Operation == "patch" ? BatchSize : 0);
        for (var i = 0; i < BatchSize; i++)
        {
            creates[i] = new(ListBenchmarkInputs.RecordId(i), 1, ListBenchmarkInputs.Values(i));
            patches[i] = new(1, 1, [new(ListBenchmarkInputs.Counter, JsonValue.Create(i + BatchSize))]);
        }
    }

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public async Task Batch()
    {
        for (var i = 0; i < BatchSize; i++)
        {
            if (Operation == "create")
                created[i] = await engine.CreateRecordAsync(ListBenchmarkInputs.Actor,
                    ListBenchmarkInputs.Workspace, ListBenchmarkInputs.List, creates[i]);
            else
                patched[i] = await engine.UpdateRecordAsync(ListBenchmarkInputs.Actor,
                    ListBenchmarkInputs.Workspace, ListBenchmarkInputs.List,
                    ListBenchmarkInputs.RecordId(i), patches[i]);
        }
    }

    [IterationCleanup]
    public Task Verify() => VerifyAsync();

    private async Task VerifyAsync()
    {
        for (var i = 0; i < BatchSize; i++)
        {
            var record = await engine.GetRecordAsync(ListBenchmarkInputs.Actor,
                ListBenchmarkInputs.Workspace, ListBenchmarkInputs.List, ListBenchmarkInputs.RecordId(i));
            var counter = i + (Operation == "patch" ? BatchSize : 0);
            if (record.Revision != (Operation == "patch" ? 2 : 1) || record.SchemaVersion != 1 ||
                record.Number != i + 1 || record.Values[ListBenchmarkInputs.Counter.ToString("D")]!.GetValue<long>() != counter ||
                record.Values[ListBenchmarkInputs.PayloadField.ToString("D")]!.GetValue<string>() != ListBenchmarkInputs.Payload)
                throw new InvalidDataException("List mutation readback differs from the expected record.");
            if (Operation == "create" && (created[i].Replayed || created[i].Id != record.Id || created[i].Revision != 1))
                throw new InvalidDataException("List create replayed or returned an invalid result.");
            if (Operation == "patch" && patched[i].Revision != record.Revision)
                throw new InvalidDataException("List patch result differs from the stored record.");
        }
        var counts = await database!.CountsAsync();
        if (counts["cellbridge_lists.records"] != BatchSize ||
            counts["cellbridge_lists.create_receipts"] != BatchSize ||
            counts["cellbridge_lists.audit_events"] - before["cellbridge_lists.audit_events"] != BatchSize)
            throw new InvalidDataException("Unexpected list mutation row or receipt count.");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (database is not null) await database.DisposeAsync();
        if (environment is not null) await environment.DisposeAsync();
    }
}

internal static class ListBenchmarkInputs
{
    public static readonly Guid Workspace = Guid.Parse("bb000000-0000-0000-0000-000000000001");
    public static readonly Guid List = Guid.Parse("bb000000-0000-0000-0000-000000000002");
    public static readonly Guid Title = Guid.Parse("ba000000-0000-0000-0000-000000000001");
    public static readonly Guid Counter = Guid.Parse("ba000000-0000-0000-0000-000000000002");
    public static readonly Guid PayloadField = Guid.Parse("ba000000-0000-0000-0000-000000000003");
    public static readonly ListActor Actor = new("benchmark:integration");
    public static readonly string Payload = new('x', 1024);

    public static JsonObject Values(int counter) => new()
    {
        [Title.ToString("D")] = "Prepared record", [Counter.ToString("D")] = counter,
        [PayloadField.ToString("D")] = Payload
    };

    public static Guid RecordId(int index) => Guid.Parse($"bc000000-0000-0000-0000-{index + 1:000000000000}");

    public static async Task PrepareAsync(BenchmarkDatabase database, PostgreSqlListEngine engine, int records)
    {
        await PostgreSqlListSetup.RegisterWorkspaceAsync(database.Admin, Workspace, "Provider benchmark", Actor.Subject);
        await engine.CreateListAsync(Actor, Workspace, new(List, "Provider benchmark", [
            new(Title, "Title", ListFieldType.Text), new(Counter, "Counter", ListFieldType.Integer),
            new(PayloadField, "Payload", ListFieldType.Text)]));
        for (var i = 0; i < records; i++)
        {
            var result = await engine.CreateRecordAsync(Actor, Workspace, List,
                new(RecordId(i), 1, Values(i)));
            if (result.Replayed || result.Revision != 1)
                throw new InvalidDataException("List preparation failed to create a record.");
        }
    }

    public static void VerifyPage(RecordPage page, int start)
    {
        if (page.SchemaVersion != 1 || page.Records.Count != 100 || page.NextRecordId != RecordId(start + 99))
            throw new InvalidDataException("Page shape or cursor differs from the expected result.");
        for (var i = 0; i < page.Records.Count; i++)
        {
            var record = page.Records[i];
            if (record.Id != RecordId(start + i) || record.Revision != 1 ||
                record.Values[Counter.ToString("D")]!.GetValue<long>() != start + i ||
                record.Values[PayloadField.ToString("D")]!.GetValue<string>() != Payload)
                throw new InvalidDataException("Page contains an unexpected record.");
        }
    }
}
