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
    public async Task ReplacementsKeepCurrentBindingsBoundedAndPreserveHistoricalMappingKeysAndReplay()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/repeated-whole-file.docx", MinimalDocx.Create("original"), TestActor.Value))!;
        var originalGraph = await StorageTestDocuments.GraphAsync(service, initial);
        var originalRevision = (await provider.Documents.ReadRevisionAsync(initial.ResourceId, 1))!.Revision!;
        var originalCatalog = (await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(0) { IncludeCatalog = true }))!.Elements;
        var stale = await StorageTestDocuments.SaveRequestAsync(service, initial, MinimalDocx.Create("stale protocol bytes"));
        var locks = new SharedDocumentLocks(service);
        Assert.True((await locks.ApplyAsync(initial.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value)).Success);
        var packages = Enumerable.Range(0, 20).Select(index => MinimalDocx.Create("replacement " + index)).ToArray();
        WholeFileSaveResult? first = null;
        int? bindingCount = null;
        foreach (var (bytes, index) in packages.Select((bytes, index) => (bytes, index)))
        {
            using var stream = new MemoryStream(bytes);
            var accepted = await service.SaveWholeFileAsync(initial.ResourceId, stream, "save-" + index, TestActor.Value, "lock");
            first ??= accepted;
            Assert.False(accepted.IsReplay);
            Assert.Equal(initial.ContentVersion + index + 1, accepted.Revision.ContentVersion);
            var current = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
            var partition = (await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(0) { IncludeBindings = true }))!;
            var graph = await StorageTestDocuments.GraphAsync(service, current);
            Assert.Equal(originalGraph.FileCell, graph.FileCell);
            Assert.Equal(bytes, graph.Materialize());
            Assert.Equal(bytes, await StorageTestDocuments.PackageAsync(provider, current.Package));
            var snapshot = (await provider.Documents.ExportDocumentAsync(initial.ResourceId))!;
            Assert.All(originalCatalog, element => Assert.Contains(snapshot.Elements,
                retained => retained.Scope.TargetPartition == 0 && retained.Element == element));
            Assert.Equal(JsonSerializer.Serialize(originalRevision), JsonSerializer.Serialize((await provider.Documents.ReadRevisionAsync(initial.ResourceId, 1))!.Revision));
            bindingCount ??= partition.Bindings.Length;
            Assert.True(bindingCount > 0);
            Assert.Equal(bindingCount, partition.Bindings.Length);
            // Mapping keys are retained by protocol; publication rows contain only changed keys.
            var lastRoot = snapshot.MappingRoots.Single(root => root.Id == partition.Partition.StorageIndex);
            Assert.InRange(snapshot.MappingIntervals.Count(mapping => mapping.Mapping.Scope.TargetPartition == 0 && mapping.StartSequence == lastRoot.Sequence), 1, 4);

        }

        var final = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
        Assert.Equal(packages.Length + 1, final.Counters.Revisions);
        Assert.Equal(packages.Length, final.Counters.WholeFileSaveReceipts);
        var history = await service.ListRevisionsAsync(initial.ResourceId, TestActor.Value);
        foreach (var revision in history)
        {
            var expected = revision.RevisionNumber == 1 ? MinimalDocx.Create("original") : packages[checked((int)revision.RevisionNumber - 2)];
            if (revision.RevisionNumber == 1) expected = await StorageTestDocuments.PackageAsync(provider, initial.Package);
            Assert.Equal(expected, await StorageTestDocuments.PackageAsync(provider, revision.Package));
        }
        using (var replayBytes = new MemoryStream(packages[0]))
        {
            var replay = await service.SaveWholeFileAsync(initial.ResourceId, replayBytes, "save-0", TestActor.Value, "lock");
            Assert.True(replay.IsReplay);
            Assert.Equal(JsonSerializer.Serialize(first!.Revision), JsonSerializer.Serialize(replay.Revision));
        }
        Assert.Equal(JsonSerializer.Serialize(final), JsonSerializer.Serialize(await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId))));
        Assert.True((await locks.ApplyAsync(initial.ResourceId, HostLockOperation.Release, "lock", TestActor.Value)).Success);
        var refused = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, stale,
            new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(refused.Response.SubResponses).Status);
        Assert.Equal(final.Package, refused.State.Package);
        var protocolSaved = await RevisionHistoryTests.Save(service, refused.State, "fresh protocol bytes");
        Assert.Equal(packages.Length + 2, protocolSaved.Counters.Revisions);
        Assert.Equal(await StorageTestDocuments.PackageAsync(provider, protocolSaved.Package),
            (await StorageTestDocuments.GraphAsync(service, protocolSaved)).Materialize());
    }
}
