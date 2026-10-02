using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

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
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        await ConcurrentRetries(provider);
    }

    internal static async Task ConcurrentRetries(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create()))!;
        var expected = (await StoredDocument.RestoreAsync(state, provider.Content)).FilePartition.FileGraph.StorageIndex;
        var requests = Enumerable.Range(0, 8).Select(_ => StorageTests.Fixture("save-first")).ToArray();
        foreach (var request in requests)
            ((PutChangesSubRequestData)request.SubRequests.Single().Data!).ExpectedStorageIndex = expected;
        var results = await Task.WhenAll(requests.Select(request => new CellBridgeDocumentService(provider)
            .ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>()).AsTask()));
        Assert.All(results, result => Assert.False(Assert.Single(result.Response.SubResponses).Status));
        var final = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal(state.ContentVersion + 1, final.ContentVersion);
        Assert.Single(final.Receipts);
        Assert.All(results, result => Assert.Equal(final.StateVersion, result.State.StateVersion));
    }

    [Fact]
    public async Task PutThenQueryAndInterruptedBatchKeepAcceptedRevision()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/batch.docx", MinimalDocx.Create()))!;
        var request = StorageTests.Fixture("save-first");
        ((PutChangesSubRequestData)request.SubRequests[0].Data!).ExpectedStorageIndex =
            (await StoredDocument.RestoreAsync(state, provider.Content)).FilePartition.FileGraph.StorageIndex;
        request.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 991, Data = new QueryChangesSubRequestData() });
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>());
        Assert.Equal(2, result.Response.SubResponses.Count);
        Assert.All(result.Response.SubResponses, r => Assert.False(r.Status));
        Assert.NotNull(result.Response.DataElementPackage);
        var latest = (await StoredDocument.RestoreAsync(result.State, provider.Content)).FilePartition.FileGraph.StorageIndex;
        Assert.Equal(latest, ((QueryChangesSubResponseData)result.Response.SubResponses[1].Data!).StorageIndexExtendedGuid);
        // A failed later operation does not roll back the first publication.
        request.SubRequests[1] = new(RequestTypes.PutChanges) { RequestId = 992,
            Data = new PutChangesSubRequestData { StorageIndex = new ExGuid(1, Guid.NewGuid()) } };
        var repeated = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>());
        Assert.False(repeated.Response.SubResponses[0].Status);
        Assert.True(repeated.Response.SubResponses[1].Status);
        Assert.Equal(result.State.ContentVersion, repeated.State.ContentVersion);
        Assert.Single(repeated.State.Receipts);
    }

    [Fact]
    public async Task EditorsQueryPersistsExpiryAndKnowledgeTogether()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/editors.docx", MinimalDocx.Create()))!;
        var document = await StoredDocument.RestoreAsync(state, provider.Content);
        document.JoinSession(Guid.NewGuid(), "test-editor");
        var withEditor = document.CaptureCoordination(state, state.Coordination);
        withEditor = withEditor with { Editors = withEditor.Editors.Select(e => e with { ExpiresUtc = DateTime.UtcNow.AddMinutes(-10) }).ToImmutableArray() };
        await provider.State.TransitionAsync(state.ResourceId, (_, _) => new StateTransition<bool>(withEditor, true));
        var before = withEditor.Partitions.Single(p => p.Kind == 2).Knowledge;
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 1, Data = new QueryChangesSubRequestData() });
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.EditorsTable, request, new Dictionary<string, string>());
        var after = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal(2, after.StateVersion);
        Assert.Equal(after.StateVersion, result.State.StateVersion);
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
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        var provider = new StorageProvider(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create()))!;
        var now = DateTime.UtcNow;
        var expired = state with { Coordination = new(null, [], new("lease", "client", now.AddMilliseconds(250), 0, null), 1) };
        await provider.State.TransitionAsync(state.ResourceId, (_, _) => new StateTransition<bool>(expired, true));
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT state_version FROM cellbridge_documents WHERE resource_id=$1 FOR UPDATE", connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = state.ResourceId });
            await command.ExecuteScalarAsync();
        }
        var waiting = provider.State.TransitionAsync(state.ResourceId, (current, clock) =>
        {
            var document = StoredDocument.RestoreMetadata(current, clock);
            var coordinator = FssHttpLockCoordinator.Restore(document, current.Coordination, clock);
            Assert.True(coordinator.ExecuteCellWrite(new Dictionary<string, string>(), () => true, out _, out _, clock));
            Assert.True(clock > now.AddMilliseconds(250));
            return new StateTransition<DateTime>(current with { Coordination = coordinator.Capture() }, clock);
        }).AsTask();
        await Task.Delay(400);
        Assert.False(waiting.IsCompleted);
        await transaction.CommitAsync();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null((await provider.State.FindByResourceIdAsync(state.ResourceId))!.Coordination.Exclusive);
    }
}
