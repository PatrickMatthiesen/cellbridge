using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public class PublicationTests
{
    [PostgreSqlFact]
    public async Task ReusingCorruptPostgreSqlContentCannotReturnAHandle()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        var content = new PostgreSqlContentStore(source);
        foreach (var mutation in new[] { "chunk", "missing", "metadata" })
        {
            var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(65536 + 17);
            using var input = new MemoryStream(bytes);
            var handle = await content.WriteAsync(input);
            // Healthy deduplication must still succeed before damage is injected.
            using var duplicate = new MemoryStream(bytes);
            Assert.Equal(handle, await content.WriteAsync(duplicate));
            var sql = mutation switch
            {
                "chunk" => "UPDATE cellbridge_chunks SET bytes=decode('00','hex') WHERE object_key=$1 AND ordinal=1",
                "missing" => "DELETE FROM cellbridge_chunks WHERE object_key=$1 AND ordinal=1",
                _ => "UPDATE cellbridge_objects SET length=length+1 WHERE object_key=$1",
            };
            await using var command = source.CreateCommand(sql);
            command.Parameters.Add(new NpgsqlParameter { Value = handle.Key });
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
            using var rewrite = new MemoryStream(bytes);
            await Assert.ThrowsAsync<StorageCorruptionException>(() => content.WriteAsync(rewrite).AsTask());
        }
    }

    [PostgreSqlFact]
    public async Task CorruptPostgreSqlChunkFailsBeforeFinalBytesAreReturned()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        var content = new PostgreSqlContentStore(source);
        using var input = new MemoryStream(Guid.NewGuid().ToByteArray());
        var handle = await content.WriteAsync(input);
        await using var command = source.CreateCommand("UPDATE cellbridge_chunks SET bytes=$2 WHERE object_key=$1");
        command.Parameters.Add(new NpgsqlParameter { Value = handle.Key });
        command.Parameters.Add(new NpgsqlParameter { Value = new byte[16] });
        await command.ExecuteNonQueryAsync();
        await using var read = await content.OpenReadAsync(handle);
        using var destination = new MemoryStream();
        await Assert.ThrowsAsync<StorageCorruptionException>(() => read.CopyToAsync(destination));
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task ConcurrentIdenticalRequestsPublishOnce()
    {
        var provider = StorageTestDocuments.Memory();
        await ConcurrentRetries(provider);
    }

    internal static async Task ConcurrentRetries(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var original = await StorageTestDocuments.SaveRequestAsync(service, state, MinimalDocx.Create("same operation"));
        var requests = Enumerable.Range(0, 8).Select(_ => FsshttpbCellRequest.Deserialize(new BinaryReaderEx(original.ToByteArray()))).ToArray();
        var results = await Task.WhenAll(requests.Select(request => new CellBridgeDocumentService(provider)
            .ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value).AsTask()));
        Assert.All(results, result => Assert.False(Assert.Single(result.Response.SubResponses).Status));
        var final = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.Equal(state.ContentVersion + 1, final.ContentVersion);
        Assert.Equal(1, final.Counters.SaveReceipts);
        Assert.All(results, result => Assert.Equal(final.StateVersion, result.State.StateVersion));
        var committed = Assert.Single(results, result => !Assert.Single(result.AcceptedSaves).IsReplay);
        Assert.All(results, result =>
        {
            Assert.Equal(committed.Response.ToByteArray(), result.Response.ToByteArray());
            var receipt = Assert.Single(result.AcceptedSaves);
            Assert.Equal(final.Package, receipt.Package);
            Assert.Equal(final.ContentVersion, receipt.ContentVersion);
            Assert.Equal(IndexedPartitionSavePreparer.Identify(state.LifecycleGeneration, 0, original.SubRequests[0], original.DataElementPackage!).Operation.OperationKey, receipt.OperationKey);
        });
    }

    [Fact]
    public async Task PutThenQueryAndInterruptedBatchKeepAcceptedRevision()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/batch.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = await StorageTestDocuments.SaveRequestAsync(service, state, MinimalDocx.Create("saved"));
        request.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 991, Data = new QueryChangesSubRequestData() });
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal(2, result.Response.SubResponses.Count);
        Assert.All(result.Response.SubResponses, r => Assert.False(r.Status));
        Assert.NotNull(result.Response.DataElementPackage);
        var latest = StorageIds.Restore((await provider.Documents.ReadPartitionAsync(state.ResourceId, new(0)))!.Partition.StorageIndex!);
        Assert.Equal(latest, ((QueryChangesSubResponseData)result.Response.SubResponses[1].Data!).StorageIndexExtendedGuid);
        // A failed later operation does not roll back the first publication.
        request.SubRequests[1] = new(RequestTypes.PutChanges) { RequestId = 992,
            Data = new PutChangesSubRequestData { StorageIndex = new ExGuid(1, Guid.NewGuid()) } };
        var repeated = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(repeated.Response.SubResponses[0].Status);
        Assert.True(repeated.Response.SubResponses[1].Status);
        Assert.Equal(result.State.ContentVersion, repeated.State.ContentVersion);
        Assert.Equal(1, repeated.State.Counters.SaveReceipts);
    }

    [Fact]
    public async Task EditorsQueryPersistsExpiryAndKnowledgeTogether()
    {
        var clock = new ManualClock(); var content = new InMemoryContentStore();
        var provider = new StorageProvider(new InMemoryDocumentRepository(content, clock: clock), content);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/editors.docx", MinimalDocx.Create(), TestActor.Value))!;
        var join = new FssHttpSubRequest { Type = SubRequestType.Coauth,
            SubRequestDataAttributes = { ["CoauthRequestType"] = "JoinCoauthoring", ["ClientID"] = Guid.NewGuid().ToString(),
                ["SchemaLockID"] = Guid.NewGuid().ToString(), ["Timeout"] = "3600" } };
        Assert.True((await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.Office(state.ResourceId, TestStorage.Admit(state), join))).Changed);
        var before = (await provider.Documents.ReadControlAsync(state.ResourceId))!.Partitions.Single(p => p.Kind == 2).Knowledge;
        clock.Advance(TimeSpan.FromHours(2));
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 1, Data = new QueryChangesSubRequestData() });
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.EditorsTable, request, new Dictionary<string, string>(), TestActor.Value);
        var after = (await provider.Documents.ReadControlAsync(state.ResourceId))!;
        Assert.Equal(2, after.Head.StateVersion);
        Assert.Equal(after.Head.StateVersion, result.State.StateVersion);
        Assert.Empty(after.Editors);
        Assert.True(after.Partitions.Single(p => p.Kind == 2).Knowledge > before);
    }

    [PostgreSqlFact]
    public async Task PostgreSqlConcurrentRetriesPublishOnce()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await ConcurrentRetries(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)));
    }

    [PostgreSqlFact]
    public async Task WaitingTransitionReadsNewPointerAndTimeAfterLockRelease()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!)
        { ApplicationName = "CellBridge.PublicationTests.WaitingTransition" }.ConnectionString;
        await using var source = NpgsqlDataSource.Create(connectionString);
        var provider = new StorageProvider(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var control = (await provider.Documents.ReadControlAsync(state.ResourceId))!;
        var expires = control.UtcNow.AddSeconds(1);
        Assert.True((await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetHostLease(state.ResourceId,
            TestStorage.Admit(state), new("lease", TestActor.Value.Identity.Subject, expires), null))).Changed);
        control = (await provider.Documents.ReadControlAsync(state.ResourceId))!;
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT \"ResourceId\" FROM cellbridge_repository_heads WHERE \"ResourceId\"=$1 FOR UPDATE", connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = state.ResourceId });
            await command.ExecuteScalarAsync();
        }
        var waiting = provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetHostLease(state.ResourceId,
            TestStorage.Admit(control.Head), new("after-expiry", TestActor.Value.Identity.Subject, expires.AddMinutes(1)), null)).AsTask();
        await WaitForDatabaseLockWait(source);
        await WaitUntilAsync(() => DateTime.UtcNow > expires);
        Assert.False(waiting.IsCompleted);
        await transaction.CommitAsync();
        var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(acquired.Changed);
        Assert.True(acquired.Current.UtcNow > expires);
        Assert.Equal("after-expiry", acquired.Current.Coordination.HostLock!.Token);
    }

    private static async Task WaitForDatabaseLockWait(NpgsqlDataSource source)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var command = source.CreateCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE application_name = $1 AND wait_event_type = 'Lock')
                """);
            command.Parameters.AddWithValue("CellBridge.PublicationTests.WaitingTransition");
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!) return;
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
