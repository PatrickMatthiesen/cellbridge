using System.Net.Http;
using System.Text.Json.Nodes;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Lists;
using CellBridge.Lists.AspNetCore;
using CellBridge.Lists.PostgreSql;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using CellBridge.TestInfrastructure;
using Npgsql;

namespace CellBridge.Benchmarks.Tests;

public sealed class SyntheticWorkloadTests
{
    [Theory]
    [InlineData("full")]
    [InlineData("changed-full")]
    [InlineData("delta")]
    [InlineData("delta-chain")]
    public async Task RepeatedSavesRetainContentAndRetryIdentity(string workload)
    {
        var content = new InMemoryContentStore();
        var provider = new StorageProvider(new InMemoryStateStore(), content);
        await CheckRepeatedSavesAsync(provider, workload);
    }

    internal static async Task CheckRepeatedSavesAsync(StorageProvider provider, string workload)
    {
        var actor = new CellBridgeActor(new SubjectIdentity("benchmark:test", "test", "Test"), CanCreate: true);
        var service = new CellBridgeDocumentService(provider);
        var payload = MinimalDocx.Create();
        var state = (await service.CreateAsync("/benchmark-test-" + Guid.NewGuid().ToString("N") + ".docx", payload, actor))!;
        var random = new Random(42);
        long firstRetainedBytes = 0;
        for (var i = 0; i < 16; i++)
        {
            var previous = state;
            var current = await StoredDocument.RestoreAsync(state, provider.Content);
            var bytes = workload == "changed-full" ? MinimalDocx.Create("Revision " + i) : payload;
            var request = SyntheticWorkload.Save(current, bytes, workload, i, random);
            var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), actor);
            Assert.All(result.Response.SubResponses, r => Assert.False(r.Status));
            state = result.State;
            Assert.True(state.StateVersion > previous.StateVersion);
            var restored = await StoredDocument.RestoreAsync(state, provider.Content);
            Assert.Equal(bytes, restored.FilePartition.FileGraph.Materialize());
            Assert.Equal(state.Content.Length, bytes.Length);
            var retention = restored.FilePartition.FileGraph.AnalyzeRetention();
            Assert.True(retention.Complete);
            if (i == 0) firstRetainedBytes = retention.RetainedBytes;
            if (workload == "delta-chain" && i > 0) Assert.True(retention.RetainedBytes > firstRetainedBytes);
            var retry = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), actor);
            Assert.All(retry.Response.SubResponses, r => Assert.False(r.Status));
            Assert.Equal(state.StateVersion, retry.State.StateVersion);
            Assert.Equal(state.Content, retry.State.Content);
        }
        Assert.Equal(16, state.Receipts.Length);
    }
}

[CollectionDefinition("benchmark-database", DisableParallelization = true)]
public sealed class BenchmarkDatabaseCollection : ICollectionFixture<BenchmarkEnvironment>;

public sealed class BenchmarkEnvironment : IAsyncLifetime
{
    public PostgreSqlEnvironment Environment { get; private set; } = null!;
    public async Task InitializeAsync() => Environment = await PostgreSqlEnvironment.StartAsync();
    public async Task DisposeAsync() { if (Environment is not null) await Environment.DisposeAsync(); }
}

[Collection("benchmark-database")]
public sealed class DatabaseWorkloadTests(BenchmarkEnvironment fixture)
{
    [Theory]
    [InlineData("full")]
    [InlineData("changed-full")]
    [InlineData("delta")]
    [InlineData("delta-chain")]
    public async Task RepeatedSavesAlsoRoundTripThroughRealPostgreSql(string workload)
    {
        await using var source = NpgsqlDataSource.Create(fixture.Environment.ConnectionString);
        var provider = new StorageProvider(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));
        await SyntheticWorkloadTests.CheckRepeatedSavesAsync(provider, workload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListMutationsAndPagesPreserveRevisionsValuesAndOrdering(bool useHttp)
    {
        using var listener = SqlMeasurements.Listen();
        await using var database = await BenchmarkDatabase.OpenAsync(fixture.Environment.ListsConnectionString);
        var actor = new ListActor("benchmark:test");
        var engine = new PostgreSqlListEngine(database.Runtime);
        await engine.CheckHealthAsync();
        await using var http = useHttp ? await BenchmarkHttpHost.StartAsync(engine, actor) : null;
        var counter = Guid.NewGuid();
        var payload = Guid.NewGuid();
        var workspace = Guid.NewGuid();
        await PostgreSqlListSetup.RegisterWorkspaceAsync(database.Admin, workspace, "Workload correctness", actor.Subject);
        var list = await engine.CreateListAsync(actor, workspace, new(Guid.NewGuid(), "Records",
            [new(counter, "Counter", ListFieldType.Integer, Required: true), new(payload, "Payload", ListFieldType.Text, Required: true)]));
        var path = $"/api/workspaces/{workspace:D}/lists/{list.Id:D}/records";
        const string text = "fixed-payload";
        var expected = new List<(Guid Id, long Revision)>();
        for (var i = 0; i < 12; i++)
        {
            var request = new CreateRecordRequest(Guid.NewGuid(), list.SchemaVersion, new JsonObject
            { [counter.ToString("D")] = i, [payload.ToString("D")] = text });
            var created = useHttp ? await http!.SendAsync<CreateRecordResult>(path, HttpMethod.Post, request, new())
                : await engine.CreateRecordAsync(actor, workspace, list.Id, request);
            Assert.Equal(request.Id, created.Id);
            Assert.Equal(1, created.Revision);
            Assert.True(created.Number > 0);
            Assert.False(created.Replayed);
            var patch = new UpdateRecordRequest(created.Revision, list.SchemaVersion, [new(counter, JsonValue.Create(i + 20))]);
            var updated = useHttp ? await http!.SendAsync<ListRecord>($"{path}/{created.Id:D}", HttpMethod.Patch, patch, new())
                : await engine.UpdateRecordAsync(actor, workspace, list.Id, created.Id, patch);
            Assert.Equal(workspace, updated.WorkspaceId);
            Assert.Equal(list.Id, updated.ListId);
            Assert.Equal(2, updated.Revision);
            Assert.Equal(i + 20, updated.Values[counter.ToString("D")]!.GetValue<long>());
            Assert.Equal(text, updated.Values[payload.ToString("D")]!.GetValue<string>());
            var persisted = await engine.GetRecordAsync(actor, workspace, list.Id, created.Id);
            Assert.Equal(updated.Revision, persisted.Revision);
            Assert.Equal(updated.Values.ToJsonString(), persisted.Values.ToJsonString());
            expected.Add((created.Id, updated.Revision));
        }
        var query = new ListQuery([new(counter, FilterOperator.GreaterOrEqual, JsonValue.Create(23))], counter, Limit: 4);
        IReadOnlyList<ListRecord> first;
        IReadOnlyList<ListRecord> second;
        if (useHttp)
        {
            var page = await http!.SendAsync<ListQueryResponse>($"{path}/query", HttpMethod.Post, new ListQueryRequest(query), new());
            Assert.NotNull(page.NextCursor);
            first = page.Records;
            second = (await http.SendAsync<ListQueryResponse>($"{path}/query", HttpMethod.Post,
                new ListQueryRequest(query, page.NextCursor), new())).Records;
        }
        else
        {
            var page = await engine.QueryAsync(actor, workspace, list.Id, query);
            Assert.NotNull(page.NextRecordId);
            first = page.Records;
            second = (await engine.QueryAsync(actor, workspace, list.Id, query with { AfterRecordId = page.NextRecordId })).Records;
        }
        Assert.Equal(expected.Skip(3).Take(8).Select(r => r.Id), first.Concat(second).Select(r => r.Id));
        Assert.Equal(Enumerable.Range(23, 8).Select(i => (long)i), first.Concat(second).Select(r => r.Values[counter.ToString("D")]!.GetValue<long>()));
        Assert.All(first.Concat(second), r =>
        {
            Assert.Equal(workspace, r.WorkspaceId);
            Assert.Equal(list.Id, r.ListId);
            Assert.Equal(2, r.Revision);
            Assert.Equal(text, r.Values[payload.ToString("D")]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task BenchmarkDatabaseDisposalDropsOnlyItsGeneratedDatabaseAndRole()
    {
        string databaseName;
        string username;
        await using (var database = await BenchmarkDatabase.OpenAsync(fixture.Environment.ListsConnectionString))
        {
            var settings = new NpgsqlConnectionStringBuilder(database.Runtime.ConnectionString);
            databaseName = settings.Database!;
            username = settings.Username!;
        }
        await using var cluster = NpgsqlDataSource.Create(fixture.Environment.ListsConnectionString);
        await using var command = cluster.CreateCommand("SELECT (SELECT count(*) FROM pg_database WHERE datname=$1), (SELECT count(*) FROM pg_roles WHERE rolname=$2)");
        command.Parameters.AddWithValue(databaseName);
        command.Parameters.AddWithValue(username);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
    }
}
