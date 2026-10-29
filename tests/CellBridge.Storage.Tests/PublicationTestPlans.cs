using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.Tests;

internal static class PublicationTestPlans
{
    internal static async Task<PreparedDocumentPublication> WholeFileAsync(StorageProvider provider, Guid id, byte[] bytes, string key = "prepared", string token = "host")
    {
        var capture = new Capture(provider.Documents);
        using var input = new MemoryStream(bytes);
        await Assert.ThrowsAsync<Captured>(() => new CellBridgeDocumentService(new(capture, provider.Content, provider.Limits))
            .SaveWholeFileAsync(id, input, key, TestActor.Value, token).AsTask());
        return capture.Publication ?? throw new InvalidOperationException("No typed publication was prepared.");
    }
    internal static async Task<PreparedDocumentPublication> NoopAsync(StorageProvider provider, Guid id, string key)
    {
        var current = (await provider.Documents.ReadControlAsync(id))!;
        var before = current.Partitions.Single(partition => partition.Kind == 0);
        var scope = new IndexedGraphScope(id, current.Head.LifecycleGeneration, 0);
        var sequence = before.PublicationSequence + 1;
        var index = new ExtendedId(1, Guid.NewGuid());
        var part = new PreparedPartitionPublication(before, before with { PublicationSequence = sequence, StorageIndex = index,
            StorageIndexSerial = new(Guid.NewGuid(), 1), Knowledge = before.Knowledge + 1 }, [], [], [], [], [],
            IndexedDocumentGraph.Difference(scope, before.PublicationSequence, sequence, [], []));
        return new(id, StorageTestDocuments.Admission(current.Head),
            new(current.Head.LifecycleGeneration, current.Head.AuthorityGeneration, current.Coordination.Generation, current.Head.StateVersion, current.Head.ContentVersion, [])
                { PreparedCoordination = DocumentControlReducer.ExpireForPublication(current).Current.Coordination },
            new(new(current.Head.LifecycleGeneration, SaveOperationFamily.Protocol, 0, key), new string('d', 64), TestActor.Value.Identity.Subject,
                current.Head.ContentVersion, current.Head.LatestRevisionNumber, null, null), [part]);
    }
    private sealed class Captured : Exception;
    private sealed class Capture(IDocumentRepository inner) : RepositoryTestProxy(inner)
    {
        internal PreparedDocumentPublication? Publication;
        public override ValueTask<PublicationResult> PublishAsync(PreparedDocumentPublication publication, CancellationToken ct = default)
        { Publication = publication; throw new Captured(); }
    }
}
