using System.Collections.Immutable;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.AspNetCore;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class LinearHistoryTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(250)]
    [InlineData(1000)]
    public void CompactRoundTripSharesTableSlicesAndKeepsLogicalQuota(int count)
    {
        var state = History(count);
        var json = DocumentStateJson.Serialize(state);
        var decoded = DocumentStateJson.Deserialize(json, strict: true);
        var current = decoded.Partitions[0].Elements;
        Assert.Equal(count, current.Length);
        Assert.Equal(count, current.Root!.Table.Length);
        for (var index = 0; index < count; index++)
        {
            var historical = decoded.Revisions[index].Partitions[0].Elements;
            Assert.Equal(index + 1, historical.Length);
            Assert.True(current.Root.Table == historical.Root!.Table);
            Assert.Equal(0, historical.Root.Start);
            Assert.Same(current[index], historical[index]);
        }
        Assert.Equal(count, GraphElementCollection.UniqueSegments(decoded.Partitions
            .Concat(decoded.Revisions.SelectMany(r => r.Partitions)).Select(p => p.Elements)).Sum(s => s.Length));
        Assert.Equal(count, StorageReferences.Handles(decoded).Count(h => h.Key.StartsWith("graph-", StringComparison.Ordinal)));
        var logicalBytes = checked(((long)count + (long)count * (count + 1) / 2) * IntPtr.Size);
        Assert.Equal(logicalBytes, StorageLimits.GetDecodedGraphMembershipBytes(decoded));
        var limits = new StorageLimits { MaxDecodedGraphMembershipBytes = logicalBytes };
        limits.CheckDocument(decoded);
        Assert.Throws<StorageQuotaExceededException>(() =>
            (limits with { MaxDecodedGraphMembershipBytes = logicalBytes - 1 }).CheckDocument(decoded));
        Assert.Equal(json, DocumentStateJson.Serialize(decoded));
    }

    [Fact]
    public void UnreferencedBackingSlotsDoNotBecomeGraphMembers()
    {
        var state = History(1) with { Revisions = [] };
        var partition = state.Partitions[0];
        ImmutableArray<GraphElementState> backing = [null!, partition.Elements[0], null!];
        var sliced = state with
        {
            Partitions = [partition with { Elements = GraphElementCollection.FromRange(backing, 1, 1) }],
        };
        Assert.Equal(DocumentStateJson.Serialize(state), DocumentStateJson.Serialize(sliced));
        Assert.Single(StorageReferences.Handles(sliced), h => h.Key.StartsWith("graph-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LibrarySavesRetainUniqueImmutableDescriptorsAndBoundedRevisionHeaders()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared-prefix.docx", MinimalDocx.Create("initial"), TestActor.Value))!;
        for (var index = 1; index <= 10; index++)
        {
            var previous = (await provider.Documents.ExportDocumentAsync(state.ResourceId))!;
            state = await RevisionHistoryTests.Save(service,state,"revision " + index);
            var current = (await provider.Documents.ExportDocumentAsync(state.ResourceId))!;
            Assert.True(current.Elements.Length > previous.Elements.Length);
            foreach (var element in previous.Elements)
                Assert.Equal(element,current.Elements.Single(e => e.Scope == element.Scope && e.Element.Id == element.Element.Id));
            Assert.All(current.ApplicationRevisions,revision => Assert.Equal(2,revision.Partitions.Length));
            Assert.Equal(current.Elements.Length,current.Elements.Select(e => (e.Scope,e.Element.Id)).Distinct().Count());
        }
        var history = (await provider.Documents.ReadRevisionsAsync(state.ResourceId,0,20))!;
        Assert.Equal(11,history.Revisions.Length);
        foreach (var revision in history.Revisions)
            Assert.NotEmpty(await StorageTestDocuments.PackageAsync(provider,revision.Package));
    }

    [Fact]
    public void CodecAndAdmissionAllocationsGrowWithUniqueDescriptorsAndRevisionCount()
    {
        // Warm serializer metadata and worker-local codec options before measuring.
        Measure(History(10));
        var small = Measure(History(250));
        var large = Measure(History(1000));
        Assert.True(large < small * 6,
            $"Codec/admission allocated {small} bytes for 250 revisions and {large} for 1000 revisions.");
    }

    private static long Measure(DocumentState state)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var json = DocumentStateJson.Serialize(state);
        var decoded = DocumentStateJson.Deserialize(json, strict: true);
        new StorageLimits().CheckDocument(decoded);
        _ = StorageReferences.Handles(decoded).ToArray();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static DocumentState History(int count)
    {
        var id = new ExtendedId(1, Guid.Empty);
        var identity = new PartitionIdentity(id, id, id, id, id, id, id, id, id, Guid.Empty);
        var handle = new ContentHandle("document", 1, new string('0', 64));
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var resourceId = Guid.NewGuid();
        var author = new SubjectIdentity("history", "history", "history");
        var revisions = ImmutableArray.CreateBuilder<DocumentRevision>();
        var membership = GraphElementCollection.Empty;
        PartitionState partition = new(0, identity, 0, handle, id, membership, []);
        for (var index = 1; index <= count; index++)
        {
            membership = membership.Add(GraphElementCollectionTests.Element((uint)index));
            partition = partition with { Knowledge = (ulong)index, Elements = membership };
            revisions.Add(new(resourceId, 1, (ulong)index, (uint)index, now, author, handle, [partition]));
        }
        return new(DocumentState.CurrentFormat, resourceId, "/history.docx", "/HISTORY.DOCX", now, now,
            (uint)count, 0, handle, [partition], [], CoordinationState.Empty, []) { Revisions = revisions.ToImmutable() };
    }
}
