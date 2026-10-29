using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class ExecutionPublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingSavesFromOneIndexPublishOneFileAndReturnOneCoherencyError(bool postgreSql)
    {
        await using var test = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = test.Provider;
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/competing-" + Guid.NewGuid() + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var files = new[] { MinimalDocx.Create("first contender"), MinimalDocx.Create("second contender") };
        var graph = await StorageTestDocuments.GraphAsync(service, state);
        var proposals = files.Select(bytes => SyntheticWorkload.Save(graph, bytes, "changed-full", 1, new Random(Random.Shared.Next()))).ToArray();
        var competing = new StorageProvider(new RendezvousRepository(provider.Documents), provider.Content);
        var results = await Task.WhenAll(proposals.Select(request => new CellBridgeDocumentService(competing)
            .ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request,
                new Dictionary<string, string>(), TestActor.Value).AsTask())).WaitAsync(TimeSpan.FromSeconds(30));
        var winner = Assert.Single(results, result => !Assert.Single(result.Response.SubResponses).Status);
        var loser = Assert.Single(results, result => Assert.Single(result.Response.SubResponses).Status);
        Assert.Equal((ulong)CellErrorCode.CoherencyFailure, loser.Response.SubResponses[0].Error!.ErrorCode);
        Assert.Empty(loser.AcceptedSaves);
        Assert.False(Assert.Single(winner.AcceptedSaves).IsReplay);
        var current = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.Equal(state.ContentVersion + 1, current.ContentVersion);
        Assert.Equal(1, current.Counters.SaveReceipts);
        var actual = await StorageTestDocuments.PackageAsync(provider, current.Package);
        Assert.Equal(files[Array.IndexOf(results, winner)], actual);
        var currentGraph = await StorageTestDocuments.GraphAsync(service, current);
        Assert.Equal(actual, currentGraph.Materialize());
        var winnerIndex = Array.IndexOf(results, winner);
        var replay = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, proposals[winnerIndex],
            new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(replay.AcceptedSaves).IsReplay);
        Assert.Equal(winner.Response.ToByteArray(), replay.Response.ToByteArray());
        Assert.Equal(current.StateVersion, replay.State.StateVersion);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InterruptionBetweenOperationsKeepsAcceptedSaveAndRetryDoesNotPublishAgain(bool postgreSql, bool cancel)
    {
        await using var test = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = test.Provider;
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/interrupted-" + Guid.NewGuid() + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var bytes = MinimalDocx.Create("accepted before interruption");
        var request = await StorageTestDocuments.SaveRequestAsync(service, state, bytes);
        request.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 2,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } });
        using var cancellation = new CancellationTokenSource();
        var interrupted = new AfterSaveReadStore(provider.Documents, () =>
        {
            if (cancel)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            throw new IOException("Injected failure between operations.");
        });
        var execution = new CellBridgeDocumentService(new(interrupted, provider.Content)).ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value, cancellation.Token).AsTask();
        if (cancel)
        {
            var cancelled = await execution;
            Assert.True(cancelled.IsCancelled);
            Assert.True(cancelled.Response.Status);
            Assert.Equal(ErrorType.Win32, cancelled.Response.Error!.Type);
            Assert.Equal(995UL, cancelled.Response.Error.ErrorCode);
            Assert.Single(cancelled.Response.SubResponses);
            var accepted = Assert.Single(cancelled.AcceptedSaves);
            Assert.False(accepted.IsReplay);
            Assert.Equal(state.ContentVersion + 1, accepted.ContentVersion);
        }
        else
        {
            var error = await Assert.ThrowsAsync<AcceptedSaveException>(() => execution);
            Assert.IsType<IOException>(error.InnerException);
            var accepted = Assert.Single(error.AcceptedSaves);
            Assert.False(accepted.IsReplay);
            Assert.Equal(state.ContentVersion + 1, accepted.ContentVersion);
        }
        var current = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.Equal(state.ContentVersion + 1, current.ContentVersion);
        Assert.Equal(1, current.Counters.SaveReceipts);
        Assert.Equal(bytes, await StorageTestDocuments.PackageAsync(provider, current.Package));
        var retry = await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.All(retry.Response.SubResponses, operation => Assert.False(operation.Status));
        Assert.True(Assert.Single(retry.AcceptedSaves).IsReplay);
        Assert.Equal(current.StateVersion, retry.State.StateVersion);
        var decoded = FsshttpbResponse.Deserialize(new(retry.Response.ToByteArray()));
        var query = Assert.IsType<QueryChangesSubResponseData>(decoded.SubResponses[1].Data);
        Assert.Equal(bytes, PartitionGraphSnapshot.Create(decoded.DataElementPackage!.DataElements, query.StorageIndexExtendedGuid).Materialize());
    }

    private sealed class RendezvousRepository(IDocumentRepository inner) : RepositoryTestProxy(inner)
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        public override async ValueTask<SavePreparationRead?> PrepareSaveAsync(Guid id, SavePreparationRequest request, CancellationToken ct = default)
        {
            var read = await Inner.PrepareSaveAsync(id, request, ct);
            if (Interlocked.Increment(ref arrivals) <= 2)
            {
                if (Volatile.Read(ref arrivals) == 2) ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            }
            return read;
        }
    }

    private sealed class AfterSaveReadStore(IDocumentRepository inner, Action interrupt) : RepositoryTestProxy(inner)
    {
        public override async ValueTask<DocumentHead?> FindAsync(DocumentIdentity identity, CancellationToken ct = default)
        {
            var head = await Inner.FindAsync(identity, ct);
            if (head is { Counters.SaveReceipts: > 0 }) interrupt();
            return head;
        }
    }
}
