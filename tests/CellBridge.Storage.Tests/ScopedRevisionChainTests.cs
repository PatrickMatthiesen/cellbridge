using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;
using CellBridge.Tests;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class ScopedRevisionChainTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    public async Task AcceptedIncrementalHistoryRemainsQueryableInSelectedAndReferencedCells(bool updateReferencedCell, bool file, bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var service = new CellBridgeDocumentService(provider);
        var head = (await service.CreateAsync("/scoped-history-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var fixture = new GraphFixture(file ? MinimalDocx.Create("saved file") : [9, 8, 7], blob: true);
        fixture.ConnectCells(cycle: false);
        var kind = file ? DocumentPartitionKind.FileContents : DocumentPartitionKind.Metadata;
        var upload = StorageTestDocuments.MetadataRequest(fixture);
        var originalGraph = file ? await StorageTestDocuments.GraphAsync(service, head) : null;
        if (originalGraph is not null) StorageTests.Rebase(upload, originalGraph);
        var initial = await service.ExecuteAsync(head.ResourceId, kind,
            upload, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(initial.Response.SubResponses).Status);
        var expectedIndex = fixture.Elements.Single(element => element.DataElementType == DataElementType.StorageIndexDataElementData);
        for (var number = 0; number < 260; number++)
        {
            var patch = fixture.CreatePatch(expectedIndex, other: updateReferencedCell);
            // An unconditional mapping update is valid; each new revision still
            // inherits the preceding accepted revision of this cell.
            patch.DataElementPackage!.DataElements.Remove(expectedIndex);
            ((PutChangesSubRequestData)patch.SubRequests.Single().Data!).ExpectedStorageIndex = ExGuid.Null;
            var revision = IndexedGraphRecords.ReadCellRevision(patch.DataElementPackage.DataElements
                .Single(element => element.DataElementType == DataElementType.CellManifestDataElementData));
            var saved = await service.ExecuteAsync(head.ResourceId, kind,
                patch, new Dictionary<string, string>(), TestActor.Value);
            Assert.False(Assert.Single(saved.Response.SubResponses).Status);
            Assert.Equal(number + 2, saved.State.Counters.SaveReceipts);
            if (updateReferencedCell) fixture.OtherRevision = revision;
            else fixture.Revision = revision;
        }
        var query = new FsshttpbCellRequest { SubRequests = { new(RequestTypes.QueryChanges) { RequestId = 1,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true, CellId = fixture.Cell } } } };
        var response = (await new CellBridgeDocumentService(provider).ExecuteAsync(head.ResourceId,
            kind, query, new Dictionary<string, string>(), TestActor.Value)).Response;
        Assert.False(Assert.Single(response.SubResponses).Status);
        var selected = Assert.IsType<QueryChangesSubResponseData>(response.SubResponses[0].Data).StorageIndexExtendedGuid;
        // A scoped response omits unrelated cells that this client already has.
        var elements = response.DataElementPackage!.DataElements
            .Concat(originalGraph?.Elements ?? []).DistinctBy(element => element.DataElementExtendedGuid);
        var graph = GenericPartitionGraphSnapshot.Create(elements, selected,
            new(MaxRevisionDepth: provider.Limits.MaxGraphElements));
        Assert.Equal(fixture.Bytes, Assert.Single(graph.GetObjectPartitions(fixture.Cell, fixture.Child)).Content);
        var current = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(head.ResourceId)))!;
        Assert.Equal(261, current.Counters.SaveReceipts);
        Assert.Equal(262UL, current.LatestRevisionNumber);
        Assert.Equal(file ? fixture.Bytes : await StorageTestDocuments.PackageAsync(provider, head.Package),
            await StorageTestDocuments.PackageAsync(provider, current.Package));
        var restored = await service.RestoreRevisionAsync(head.ResourceId, current.LatestRevisionNumber - 1,
            current.LatestRevisionNumber, "restore-deep", TestActor.Value);
        Assert.False(restored.IsReplay);
        Assert.Equal(current.LatestRevisionNumber + 1, restored.Revision.RevisionNumber);
        Assert.Equal(await StorageTestDocuments.PackageAsync(provider, current.Package),
            await StorageTestDocuments.PackageAsync(provider, restored.Revision.Package));
        var queriedRestore = await service.ExecuteAsync(head.ResourceId, kind, query,
            new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(queriedRestore.Response.SubResponses).Status);
        if (!file && !postgreSql && !updateReferencedCell)
        {
            using var files = new PortabilityTests.TemporaryFiles();
            var archive = new PortableArchive();
            await archive.ExportAsync(provider, files.Path("deep.zip"), PortabilityTests.Context);
            var recovered = StorageTestDocuments.Memory();
            await archive.ImportAsync(recovered, files.Path("deep.zip"), PortabilityTests.Context);
            var recoveredQuery = await new CellBridgeDocumentService(recovered).ExecuteAsync(head.ResourceId,
                kind, query, new Dictionary<string, string>(), TestActor.Value);
            Assert.False(Assert.Single(recoveredQuery.Response.SubResponses).Status);
        }
    }
}
