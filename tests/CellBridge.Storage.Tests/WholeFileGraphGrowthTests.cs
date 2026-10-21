using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class WholeFileGraphGrowthTests
{
    [Fact]
    public async Task ReplacementsKeepSelectedMappingsBoundedWithoutDroppingHistoryOrReplay()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/repeated-whole-file.docx", MinimalDocx.Create("original"), TestActor.Value))!;
        var originalGraph = await DocumentPartition.RestoreFileGraphAsync(initial, provider.Content, provider.Limits);
        var stale = RevisionHistoryTests.Request(initial, MinimalDocx.Create("stale protocol bytes"));
        var locks = new SharedDocumentLocks(service);
        Assert.True((await locks.ApplyAsync(initial.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value)).Success);
        var packages = Enumerable.Range(0, 20).Select(index => MinimalDocx.Create("replacement " + index)).ToArray();
        WholeFileSaveResult? first = null;
        foreach (var (bytes, index) in packages.Select((bytes, index) => (bytes, index)))
        {
            using var stream = new MemoryStream(bytes);
            var accepted = await service.SaveWholeFileAsync(initial.ResourceId, stream, "save-" + index, TestActor.Value, "lock");
            first ??= accepted;
            Assert.False(accepted.IsReplay);
            Assert.Equal(initial.ContentVersion + index + 1, accepted.Revision.ContentVersion);
            var current = (await provider.State.FindByResourceIdAsync(initial.ResourceId))!;
            var file = current.Partitions.Single(partition => partition.Kind == 0);
            var graph = await DocumentPartition.RestoreGraphAsync(file, provider.Content, provider.Limits);
            var selectedIndex = graph.Elements.Single(element => element.DataElementExtendedGuid.Equals(graph.StorageIndex));
            var mappings = StorageIndexMappingSerials.ReadMappings(selectedIndex.Data!);
            // A full package has its own revision and a fresh publication wrapper.
            // Older revisions remain in retained indexes, not in this selected index.
            Assert.Equal(2, mappings.Count(mapping => mapping.Revision is not null));
            var materialized = PartitionGraphSnapshot.Create(graph.Elements, graph.StorageIndex);
            Assert.Equal(originalGraph.FileCell, materialized.FileCell);
            Assert.Equal(bytes, materialized.Materialize());
            Assert.Equal(bytes, await provider.Content.ReadVerifiedAsync(current.Content));
            Assert.All(initial.Partitions[0].Elements, element => Assert.Contains(file.Elements,
                retained => retained.Id == element.Id && retained.Payload == element.Payload));
            Assert.Equal(JsonSerializer.Serialize(initial.Revisions[0]), JsonSerializer.Serialize(current.Revisions[0]));
        }

        var final = (await provider.State.FindByResourceIdAsync(initial.ResourceId))!;
        Assert.Equal(packages.Length + 1, final.Revisions.Length);
        Assert.Equal(packages.Length, final.WholeFileSaveReceipts.Length);
        foreach (var revision in final.Revisions)
        {
            var file = revision.Partitions.Single(partition => partition.Kind == 0);
            var graph = await DocumentPartition.RestoreGraphAsync(file, provider.Content, provider.Limits);
            var expected = await provider.Content.ReadVerifiedAsync(revision.Content);
            Assert.Equal(expected, PartitionGraphSnapshot.Create(graph.Elements, graph.StorageIndex).Materialize());
        }
        using (var replayBytes = new MemoryStream(packages[0]))
        {
            var replay = await service.SaveWholeFileAsync(initial.ResourceId, replayBytes, "save-0", TestActor.Value, "lock");
            Assert.True(replay.IsReplay);
            Assert.Equal(JsonSerializer.Serialize(first!.Revision), JsonSerializer.Serialize(replay.Revision));
        }
        Assert.Equal(JsonSerializer.Serialize(final), JsonSerializer.Serialize(await provider.State.FindByResourceIdAsync(initial.ResourceId)));
        Assert.True((await locks.ApplyAsync(initial.ResourceId, HostLockOperation.Release, "lock", TestActor.Value)).Success);
        var refused = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, stale,
            new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(refused.Response.SubResponses).Status);
        Assert.Equal(final.Content, refused.State.Content);
        var protocolSaved = await RevisionHistoryTests.Save(service, refused.State, "fresh protocol bytes");
        Assert.Equal(packages.Length + 2, protocolSaved.Revisions.Length);
        Assert.Equal(await provider.Content.ReadVerifiedAsync(protocolSaved.Content),
            (await DocumentPartition.RestoreFileGraphAsync(protocolSaved, provider.Content, provider.Limits)).Materialize());
    }
}
