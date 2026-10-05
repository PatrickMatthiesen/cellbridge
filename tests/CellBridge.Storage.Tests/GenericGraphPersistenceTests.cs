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
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public class GenericGraphPersistenceTests
{
    [PostgreSqlFact]
    public async Task PostgreSqlReopenedServicesPreserveInheritedBlobAndOpaquePartitionGraphs()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        var provider = new StorageProvider(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/pg-graph-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var f = new GraphFixture(MinimalDocx.Create("after"), blob: true);
        var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            Save(initial, f), new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        var metadata = await DocumentPartition.CaptureGraphAsync(saved.State.Partitions[1], f.Generic(), 9, provider.Content, new());
        await provider.State.TransitionAsync(initial.ResourceId, (state, now) => new StateTransition<bool>(state with
            { Partitions = state.Partitions.Select(p => p.Kind == 1 ? metadata : p).ToImmutableArray() }, true));
        var reopened = new StorageProvider(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));
        var state = (await reopened.State.FindByResourceIdAsync(initial.ResourceId))!;
        Assert.Equal(f.Bytes, (await StoredDocument.RestoreAsync(state, reopened.Content)).Content);
        var opaque = await DocumentPartition.RestoreGraphAsync(state.Partitions[1], reopened.Content, new());
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
    public async Task GenericCaptureRejectsImmutableIdentityReuseAndReusesExactHandles()
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
    public async Task SerializedStateAndReopenedContentPreserveRootsInheritedPartitionsAndBlobReferences(int kind)
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
    public async Task RestoreChecksBudgetsMappingMetadataAndCancellation()
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
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/inherited.docx", MinimalDocx.Create("before"), TestActor.Value))!;
        var f = new GraphFixture(MinimalDocx.Create("after"), blob);
        var request = Save(initial, f);
        var result = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        Assert.Equal(initial.ContentVersion + 1, result.State.ContentVersion);
        var reopened = await StoredDocument.RestoreAsync(result.State, provider.Content);
        Assert.Equal(f.Bytes, reopened.Content);
        Assert.Equal(f.Bytes, reopened.FilePartition.FileGraph.Materialize());
        Assert.True(result.State.Partitions[0].Elements.Any(e => e.Id == StorageIds.Capture(f.BaseElement)));
    }

    [Fact]
    public async Task MissingInheritedDataCannotPublishOrAdvanceKnowledge()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/missing.docx", MinimalDocx.Create(), TestActor.Value))!;
        var f = new GraphFixture(MinimalDocx.Create("after"), blob: true);
        f.Elements.RemoveAll(e => e.DataElementExtendedGuid.Equals(f.Blob));
        var result = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            Save(initial, f), new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal((ulong)CellErrorCode.InvalidObject, Assert.Single(result.Response.SubResponses).Error!.ErrorCode);
        Assert.Equal(initial, await provider.State.FindByResourceIdAsync(initial.ResourceId));
    }

    private static FsshttpbCellRequest Save(DocumentState state, GraphFixture f)
    {
        var package = new DataElementPackage(); package.DataElements.AddRange(f.Elements);
        return new() { DataElementPackage = package, SubRequests = { new(RequestTypes.PutChanges)
        { RequestId = 1, Data = new PutChangesSubRequestData { StorageIndex = f.Index,
            ExpectedStorageIndex = StorageIds.Restore(state.Partitions.Single(p => p.Kind == 0).StorageIndex!), Flags = 1 } } } };
    }
}
