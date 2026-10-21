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
        var proposals = files.Select(bytes => Proposal(state, bytes)).ToArray();
        var content = new RendezvousContent(provider.Content);
        var competing = new StorageProvider(provider.State, content);
        var results = await Task.WhenAll(proposals.Select(request => new CellBridgeDocumentService(competing)
            .ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request,
                new Dictionary<string, string>(), TestActor.Value).AsTask())).WaitAsync(TimeSpan.FromSeconds(30));
        var winner = Assert.Single(results, result => !Assert.Single(result.Response.SubResponses).Status);
        var loser = Assert.Single(results, result => Assert.Single(result.Response.SubResponses).Status);
        Assert.Equal((ulong)CellErrorCode.CoherencyFailure, loser.Response.SubResponses[0].Error!.ErrorCode);
        Assert.Empty(loser.AcceptedSaves);
        Assert.False(Assert.Single(winner.AcceptedSaves).IsReplay);
        var current = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal(state.ContentVersion + 1, current.ContentVersion);
        Assert.Single(current.Receipts);
        var actual = await provider.Content.ReadVerifiedAsync(current.Content);
        Assert.Equal(files[Array.IndexOf(results, winner)], actual);
        var graph = await DocumentPartition.RestoreFileGraphAsync(current, provider.Content, provider.Limits);
        Assert.Equal(actual, graph.Materialize());
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
        var request = Proposal(state, bytes);
        request.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 2,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } });
        using var cancellation = new CancellationTokenSource();
        var interrupted = new AfterSaveReadStore(provider.State, () =>
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
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        else
        {
            var error = await Assert.ThrowsAsync<AcceptedSaveException>(() => execution);
            Assert.IsType<IOException>(error.InnerException);
            var accepted = Assert.Single(error.AcceptedSaves);
            Assert.False(accepted.IsReplay);
            Assert.Equal(state.ContentVersion + 1, accepted.ContentVersion);
        }
        var current = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal(state.ContentVersion + 1, current.ContentVersion);
        Assert.Single(current.Receipts);
        Assert.Equal(bytes, await provider.Content.ReadVerifiedAsync(current.Content));
        var retry = await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.All(retry.Response.SubResponses, operation => Assert.False(operation.Status));
        Assert.True(Assert.Single(retry.AcceptedSaves).IsReplay);
        Assert.Equal(current.StateVersion, retry.State.StateVersion);
        var decoded = FsshttpbResponse.Deserialize(new(retry.Response.ToByteArray()));
        var query = Assert.IsType<QueryChangesSubResponseData>(decoded.SubResponses[1].Data);
        Assert.Equal(bytes, PartitionGraphSnapshot.Create(decoded.DataElementPackage!.DataElements, query.StorageIndexExtendedGuid).Materialize());
    }

    private static FsshttpbCellRequest Proposal(DocumentState state, byte[] bytes)
    {
        var id = DocumentStorageIdentity.Create();
        var identity = new StorageManifestBuilder.StableIdentity(id.StorageManifestGuid, id.CellManifestGuid,
            id.RevisionManifestGuid, id.ObjectGroupGuid, id.ObjectDataBlobGuid, id.ObjectGuid,
            id.RevisionId, id.CellId, id.SerialGuid);
        var generated = FileContentPartitionBuilder.BuildQueryChangesResponse(1, bytes, identity, 1);
        var index = Assert.IsType<QueryChangesSubResponseData>(generated.SubResponses[0].Data).StorageIndexExtendedGuid;
        return new() { DataElementPackage = generated.DataElementPackage, SubRequests =
        { new(RequestTypes.PutChanges) { RequestId = 1, Data = new PutChangesSubRequestData
            { StorageIndex = index, ExpectedStorageIndex = StorageIds.Restore(state.Partitions[0].StorageIndex!), Flags = 1 } } } };
    }

    private sealed class RendezvousContent(IContentStore inner) : IContentStore
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
        {
            if (source is FileStream && Interlocked.Increment(ref arrivals) <= 2)
            {
                if (Volatile.Read(ref arrivals) == 2) ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return await inner.WriteAsync(source, cancellationToken);
        }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(handle, cancellationToken);
    }

    private sealed class AfterSaveReadStore(IDocumentStateStore inner, Action interrupt) : IDocumentStateStore
    {
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public async ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var state = await inner.FindByResourceIdAsync(id, cancellationToken);
            if (state is { Receipts.IsEmpty: false }) interrupt();
            return state;
        }
        public ValueTask<DocumentState?> FindByPathKeyAsync(string key, CancellationToken ct = default) => inner.FindByPathKeyAsync(key, ct);
        public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken ct = default) => inner.ListAsync(offset, limit, ct);
        public ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken ct = default) => inner.TryCreateAsync(state, ct);
        public ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition, CancellationToken ct = default) => inner.TransitionAsync(id, transition, ct);
        public ValueTask CheckHealthAsync(CancellationToken ct = default) => inner.CheckHealthAsync(ct);
    }
}
