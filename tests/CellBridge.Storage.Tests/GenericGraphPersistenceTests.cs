using System.Collections.Immutable;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.FileSystem;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Tests;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public class GenericGraphPersistenceTests
{
    [Fact]
    public async Task OversizedReceiptCannotPublishContentReceiptsOrOutbox()
    {
        var provider = StorageTestDocuments.Memory(limits: new StorageLimits { MaxObjectBytes = 4096 });
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/receipt-bound.docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.True((await provider.Documents.UpdateDeliveryAsync(new DocumentDeliveryCommand.Bind(initial.ResourceId,
            initial.LifecycleGeneration, initial.StateVersion, new(Guid.NewGuid(), "external", "revision-0", 1)))).Applied);
        initial = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
        var file = new GraphFixture(MinimalDocx.Create("after"), blob: true);
        // Many small admitted elements produce a response larger than any payload.
        for (uint i = 0; i < 200; i++)
            file.Elements.Add(GraphFixture.Element(DataElementType.ObjectDataBLOBDataElementData,
                new ExGuid(i + 100, Guid.NewGuid()), w => GraphFixture.Record(w,
                    StreamObjectTypeHeaderStart.ObjectDataBLOB, b => b.WriteBytes([1]))));
        var result = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            await Save(service, initial, file), new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(result.Response.SubResponses).Status);
        Assert.Contains("save receipt bytes", result.Response.SubResponses[0].Error!.ErrorMessage);
        Assert.Empty(result.AcceptedSaves);
        Assert.Equal(initial, await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)));
    }

    [Fact]
    public async Task FileSavePreservesOpaqueMetadataAndItsScopedReceipt()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/opaque-save.docx", MinimalDocx.Create(), TestActor.Value))!;
        var metadata = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.Metadata,
            StorageTestDocuments.MetadataRequest(new GraphFixture([1], blob: true)), new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(metadata.Response.SubResponses).Status);
        var opaque = (await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(1)))!.Partition;
        var scoped = Assert.Single((await provider.Documents.ExportDocumentAsync(initial.ResourceId))!.Receipts);
        var file = new GraphFixture(MinimalDocx.Create("after"), blob: true);
        var request = await Save(service, initial, file);
        var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        Assert.Equal(opaque, (await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(1)))!.Partition);
        Assert.Equal(scoped, (await provider.Documents.ExportDocumentAsync(initial.ResourceId))!.Receipts.Single(r => r.Operation.PartitionKind == 1));
        Assert.Contains((await provider.Documents.ExportDocumentAsync(initial.ResourceId))!.ContentReferences, reference => reference.Handle == scoped.Response);
        var repeat = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(repeat.AcceptedSaves).IsReplay);
        Assert.Equal(opaque, (await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(1)))!.Partition);
        Assert.Equal(scoped, (await provider.Documents.ExportDocumentAsync(initial.ResourceId))!.Receipts.Single(r => r.Operation.PartitionKind == 1));
    }

    [PostgreSqlFact]
    public async Task PostgreSqlReopenedServicesPreserveInheritedBlobAndOpaquePartitionGraphs()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        var provider = new StorageProvider(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/pg-graph-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var f = new GraphFixture(MinimalDocx.Create("after"), blob: true);
        var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            await Save(service, initial, f), new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        var metadata = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.Metadata,
            StorageTestDocuments.MetadataRequest(f), new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(metadata.Response.SubResponses).Status);
        var reopened = new StorageProvider(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));
        var state = (await reopened.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
        Assert.Equal(f.Bytes, await StorageTestDocuments.PackageAsync(reopened, state.Package));
        var opaqueQuery = new FsshttpbCellRequest { SubRequests = { new(RequestTypes.QueryChanges) { RequestId = 1,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } } } };
        var opaqueResult = await new CellBridgeDocumentService(reopened).ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
            opaqueQuery, new Dictionary<string, string>(), TestActor.Value);
        var opaque = GenericPartitionGraphSnapshot.Create(opaqueResult.Response.DataElementPackage!.DataElements,
            Assert.IsType<QueryChangesSubResponseData>(opaqueResult.Response.SubResponses[0].Data).StorageIndexExtendedGuid);
        Assert.Equal(f.Bytes, Assert.Single(opaque.GetObjectPartitions(f.Cell, f.Child)).Content);
        var queries = new FsshttpbCellRequest();
        foreach (var cell in new[] { f.Cell, f.OtherCell }) queries.SubRequests.Add(new(RequestTypes.QueryChanges)
            { RequestId = (ulong)queries.SubRequests.Count + 1, Data = new QueryChangesSubRequestData
                { IncludeStorageManifest = true, IncludeCellChanges = true, CellId = cell } });
        var result = await new CellBridgeDocumentService(reopened).ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, queries, new Dictionary<string, string>(), TestActor.Value);
        Assert.All(result.Response.SubResponses, s => Assert.False(s.Status));
        Assert.Contains(result.Response.DataElementPackage!.DataElements, e => e.DataElementExtendedGuid.Equals(f.Blob));
        Assert.Contains(result.Response.DataElementPackage.DataElements, e => e.DataElementExtendedGuid.Equals(f.OtherGroup));
    }

    [Fact]
    public async Task LegacyGenericCaptureRejectsImmutableIdentityReuseAndReusesExactHandles()
    {
        var content = new InMemoryContentStore();
        var initial = await new DocumentStore().Put("/immutable.docx", MinimalDocx.Create()).CaptureAsync(content);
        var f = new GraphFixture([1], blob: true);
        var captured = await DocumentPartition.CaptureGraphAsync(initial.Partitions[0], f.Generic(), 1, content, new());
        var recaptured = await DocumentPartition.CaptureGraphAsync(captured, f.Generic(), 2, content, new());
        Assert.Equal(captured.Elements.Select(e => e.Payload), recaptured.Elements.Select(e => e.Payload));
        await Assert.ThrowsAsync<InvalidDataException>(() => DocumentPartition.CaptureGraphAsync(captured,
            new GraphFixture([2], blob: true).Generic(), 3, content, new()).AsTask());
        foreach (var changed in new[] { captured with { Elements = captured.Elements.Select(e => e.Id == StorageIds.Capture(f.Blob)
                ? e with { Type = 999 } : e).ToImmutableArray() },
            captured with { Elements = captured.Elements.Select(e => e.Id == StorageIds.Capture(f.Blob)
                ? e with { Serial = new SerialId(Guid.NewGuid(), 9) } : e).ToImmutableArray() } })
            await Assert.ThrowsAsync<InvalidDataException>(() => DocumentPartition.CaptureGraphAsync(changed,
                f.Generic(), 3, content, new()).AsTask());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task LegacySerializedStateAndReopenedContentPreserveRootsInheritedPartitionsAndBlobReferences(int kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "cellbridge-graph-" + Guid.NewGuid().ToString("N"));
        try
        {
            var content = new FileSystemContentStore(root);
            var bytes = MinimalDocx.Create();
            var document = new DocumentStore().Put("/roundtrip.docx", bytes);
            var initial = await document.CaptureAsync(content);
            var f = new GraphFixture(bytes, blob: true);
            var captured = await DocumentPartition.CaptureGraphAsync(initial.Partitions.Single(p => p.Kind == kind),
                f.Generic(), ulong.MaxValue, content, new());
            var state = initial with { Partitions = initial.Partitions.Select(p => p.Kind == kind ? captured : p).ToImmutableArray() };
            state = JsonSerializer.Deserialize<DocumentState>(JsonSerializer.Serialize(state))!;
            captured = state.Partitions.Single(p => p.Kind == kind);
            var reopened = new FileSystemContentStore(root);
            var restored = await DocumentPartition.RestoreGraphAsync(captured, reopened, new());
            Assert.Equal(ulong.MaxValue, captured.Knowledge);
            Assert.Equal(f.Index, restored.StorageIndex);
            Assert.Equal(f.Generic().Roots, restored.Roots);
            Assert.Equal(f.Generic().RequiredElements.OrderBy(i => i.Value), restored.RequiredElements.OrderBy(i => i.Value));
            Assert.Equal(bytes, Assert.Single(restored.GetObjectPartitions(f.Cell, f.Child)).Content);
            var blobHandle = captured.Elements.Single(e => e.Id == StorageIds.Capture(f.Blob)).Payload;
            Assert.Contains(blobHandle, StorageReferences.Handles(state));
            var mapping = captured.Elements.Single(e => e.Id == StorageIds.Capture(f.Index));
            Assert.Equal(f.Generic().MappingSerials[f.Index].Select(s => new SerialId(s.Guid, s.Value)), mapping.MappingSerials);
            var recaptured = await (await StoredDocument.RestoreAsync(state, reopened)).CaptureAsync(reopened);
            Assert.Equal(captured.Elements.Select(e => (e.Id, e.Serial, e.Payload)),
                recaptured.Partitions.Single(p => p.Kind == kind).Elements.Select(e => (e.Id, e.Serial, e.Payload)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LegacyRestoreChecksBudgetsMappingMetadataAndCancellation()
    {
        var content = new InMemoryContentStore();
        var initial = await new DocumentStore().Put("/bounds.docx", MinimalDocx.Create()).CaptureAsync(content);
        var f = new GraphFixture([1], blob: true);
        var partition = await DocumentPartition.CaptureGraphAsync(initial.Partitions[0], f.Generic(), 9, content, new());
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => DocumentPartition.RestoreGraphAsync(partition,
            content, new StorageLimits { MaxGraphElements = 1 }).AsTask());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DocumentPartition.RestoreGraphAsync(partition,
            content, new(), cancel.Token).AsTask());
        var corrupt = partition with { Elements = partition.Elements.Select(e => e.Id == StorageIds.Capture(f.Index)
            ? e with { MappingSerials = [] } : e).ToImmutableArray() };
        await Assert.ThrowsAsync<StorageCorruptionException>(() => DocumentPartition.RestoreGraphAsync(corrupt, content, new()).AsTask());
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => DocumentPartition.CaptureGraphAsync(partition,
            f.Generic(), 10, content, new StorageLimits { MaxObjectBytes = 1 }).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableFileSavePublishesInheritedGraphAndReopensCorrectBytes(bool blob)
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/inherited.docx", MinimalDocx.Create("before"), TestActor.Value))!;
        var f = new GraphFixture(MinimalDocx.Create("after"), blob);
        var request = await Save(service, initial, f);
        var result = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        Assert.Equal(initial.ContentVersion + 1, result.State.ContentVersion);
        Assert.Equal(f.Bytes, await StorageTestDocuments.PackageAsync(provider, result.State.Package));
        Assert.Equal(f.Bytes, (await StorageTestDocuments.GraphAsync(service, result.State)).Materialize());
        Assert.Contains((await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(0) { IncludeCatalog = true }))!.Elements,
            e => e.Id == StorageIds.Capture(f.BaseElement));

    }

    [Fact]
    public async Task MissingInheritedDataCannotPublishOrAdvanceKnowledge()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/missing.docx", MinimalDocx.Create(), TestActor.Value))!;
        var f = new GraphFixture(MinimalDocx.Create("after"), blob: true);
        f.Elements.RemoveAll(e => e.DataElementExtendedGuid.Equals(f.Blob));
        var result = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            await Save(service, initial, f), new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal((ulong)CellErrorCode.InvalidObject, Assert.Single(result.Response.SubResponses).Error!.ErrorCode);
        Assert.Equal(initial, await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)));
    }

    private static async Task<FsshttpbCellRequest> Save(CellBridgeDocumentService service, DocumentHead state, GraphFixture f)
    {
        var package = new DataElementPackage(); package.DataElements.AddRange(f.Elements);
        var request = new FsshttpbCellRequest { DataElementPackage = package, SubRequests = { new(RequestTypes.PutChanges)
        { RequestId = 1, Data = new PutChangesSubRequestData { StorageIndex = f.Index, Flags = 1 } } } };
        StorageTests.Rebase(request, await StorageTestDocuments.GraphAsync(service, state));
        return request;
    }
}
