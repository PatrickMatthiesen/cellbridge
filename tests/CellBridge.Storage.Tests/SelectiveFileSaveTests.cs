using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;
using CellBridge.Tests;

namespace CellBridge.Storage.Tests;

public sealed class SelectiveFileSaveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupportedSavesPreserveCompleteCatalogAndResponseWithoutReadingOldObjectHistory(bool incremental)
    {
        var content = new CountingContent(new InMemoryContentStore());
        var provider = new StorageProvider(new InMemoryStateStore(), content);
        var service = new CellBridgeDocumentService(provider);
        var corpus = new IncrementalOfficeWorkload(256);
        var state = (await service.CreateAsync("/selective.docx", corpus.CreateContent(), TestActor.Value))!;
        var client = (await StoredDocument.RestoreAsync(state, content)).FilePartition.FileGraph;
        long? firstRead = null;
        for (var revision = 1; revision <= 24; revision++)
        {
            var document = StoredDocument.RestoreMetadata(state, DateTime.UtcNow);
            var save = incremental ? corpus.CreateSave(client, revision).Request :
                SyntheticWorkload.Save(await StoredDocument.RestoreAsync(state, content), corpus.CreateContent(revision), "full", revision, new Random(revision));
            var operation = Assert.Single(save.SubRequests);
            var package = save.DataElementPackage!;
            var put = Assert.IsType<PutChangesSubRequestData>(operation.Data);
            // The full graph is available only in the client and reference preparation.
            if (!incremental) put.ExpectedStorageIndex = client.StorageIndex;
            var reference = await FilePartitionSaveHandler.PrepareAsync(state, document, operation, package,
                content, provider.Limits, default, forceFullGraph: true);
            content.ObjectKeys = state.Partitions.Single(p => p.Kind == 0).Elements
                .Where(e => e.Type == (uint)DataElementType.ObjectGroupDataElementData).Select(e => e.Payload.Key).ToHashSet();
            content.ObjectBytesRead = 0;
            var selected = await FilePartitionSaveHandler.PrepareAsync(state, document, operation, package,
                content, provider.Limits, default);
            Assert.False(selected.Response.Status);
            Assert.NotNull(selected.AddedElements);
            Assert.Equal(ResponseBytes(reference), ResponseBytes(selected));
            Assert.Equal(reference.KnowledgeSequence, selected.KnowledgeSequence);
            Assert.Equal(reference.Graph!.Materialize(), selected.Graph!.Materialize());
            Assert.Equal(reference.AddedBytes, selected.AddedBytes);
            Assert.Equal(reference.AddedCount, selected.AddedCount);
            if (revision >= 2)
            {
                firstRead ??= content.ObjectBytesRead;
                Assert.Equal(firstRead.Value, content.ObjectBytesRead);
                if (!incremental) Assert.Equal(0, content.ObjectBytesRead);
            }
            var selectedReadBytes = content.ObjectBytesRead;
            var handle = await content.WriteAsync(new MemoryStream(corpus.CreateContent(revision)));
            var oldCapture = await DocumentPartition.CaptureFileGraphAsync(state.Partitions.Single(p => p.Kind == 0),
                reference.Graph!, reference.KnowledgeSequence, handle, content);
            var newCapture = await DocumentPartition.CaptureFileDeltaAsync(state.Partitions.Single(p => p.Kind == 0),
                selected.Graph.StorageIndex, selected.AddedElements!, selected.KnowledgeSequence, handle, content);
            AssertCatalogEqual(oldCapture, newCapture);
            var before = state;
            content.ObjectBytesRead = 0;
            var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, save,
                new Dictionary<string, string>(), TestActor.Value);
            Assert.False(Assert.Single(result.Response.SubResponses).Status);
            Assert.Equal(selectedReadBytes, content.ObjectBytesRead);
            Assert.Equal(ResponseBytes(reference), result.Response.ToByteArray());
            AssertCatalogEqual(oldCapture, result.State.Partitions.Single(p => p.Kind == 0));
            foreach (var old in before.Partitions.Single(p => p.Kind == 0).Elements)
                Assert.Same(old, result.State.Partitions.Single(p => p.Kind == 0).Elements.Single(e => e.Id == old.Id));
            state = result.State;
            client = client.Merge(package.DataElements, put.StorageIndex);
        }
        Assert.Equal(corpus.CreateContent(24), await content.ReadVerifiedAsync(state.Content));
        Assert.Equal(corpus.CreateContent(24), (await DocumentPartition.RestoreFileGraphAsync(state, content, provider.Limits)).Materialize());
    }

    [Fact]
    public async Task DeltaPersistenceUsesValidatedOwnedBytesAfterTheCallerMutatesRequestBuffers()
    {
        var content = new InMemoryContentStore();
        var provider = new StorageProvider(new InMemoryStateStore(), content);
        var service = new CellBridgeDocumentService(provider);
        var corpus = new IncrementalOfficeWorkload(256);
        var state = (await service.CreateAsync("/owned-delta.docx", corpus.CreateContent(), TestActor.Value))!;
        var graph = (await StoredDocument.RestoreAsync(state, content)).FilePartition.FileGraph;
        var save = corpus.CreateSave(graph, 1);
        var prepared = await FilePartitionSaveHandler.PrepareAsync(state,
            StoredDocument.RestoreMetadata(state, DateTime.UtcNow), save.Request.SubRequests[0],
            save.Request.DataElementPackage!, content, provider.Limits, default);
        Assert.False(prepared.Response.Status);
        Assert.NotNull(prepared.AddedElements);
        Assert.Equal(save.Content, prepared.Graph!.Materialize());
        var originalResponse = ResponseBytes(prepared);
        foreach (var element in save.Request.DataElementPackage!.DataElements)
            Array.Fill(element.Data!, (byte)0xA5);
        Assert.Equal(originalResponse, ResponseBytes(prepared));
        Assert.Equal(save.Content, prepared.Graph.Materialize());
        var packageHandle = await content.WriteAsync(new MemoryStream(save.Content, writable: false));
        var partition = await DocumentPartition.CaptureFileDeltaAsync(state.Partitions.Single(p => p.Kind == 0),
            prepared.Graph.StorageIndex, prepared.AddedElements!, prepared.KnowledgeSequence, packageHandle, content);
        var candidate = state with { Content = packageHandle,
            Partitions = state.Partitions.Select(p => p.Kind == 0 ? partition : p).ToImmutableArray() };
        var persisted = await DocumentPartition.RestoreFileGraphAsync(candidate, content, provider.Limits);
        Assert.Equal(save.Content, persisted.Materialize());
        foreach (var expected in prepared.AddedElements!)
        {
            var stored = partition.Elements.Single(e => e.Id == StorageIds.Capture(expected.DataElementExtendedGuid));
            Assert.Equal(expected.Data, await content.ReadVerifiedAsync(stored.Payload));
        }
    }

    [Fact]
    public async Task MissingCurrentIndexUsesTheSameCorruptionErrorAsFullPreparation()
    {
        var content = new InMemoryContentStore();
        var provider = new StorageProvider(new InMemoryStateStore(), content);
        var service = new CellBridgeDocumentService(provider);
        var corpus = new IncrementalOfficeWorkload(256);
        var state = (await service.CreateAsync("/missing-current-index.docx", corpus.CreateContent(), TestActor.Value))!;
        var graph = (await StoredDocument.RestoreAsync(state, content)).FilePartition.FileGraph;
        var save = corpus.CreateSave(graph, 1).Request;
        var source = state.Partitions.Single(p => p.Kind == 0);
        source = source with { Elements = source.Elements.Where(e => e.Id != source.StorageIndex).ToImmutableArray() };
        state = state with { Partitions = state.Partitions.Select(p => p.Kind == 0 ? source : p).ToImmutableArray() };
        var metadata = StoredDocument.RestoreMetadata(state, DateTime.UtcNow);
        var expected = await Record.ExceptionAsync(async () =>
        {
            await FilePartitionSaveHandler.PrepareAsync(state, metadata, save.SubRequests[0], save.DataElementPackage!,
                content, provider.Limits, default, forceFullGraph: true);
        });
        var actual = await Record.ExceptionAsync(async () =>
        {
            await FilePartitionSaveHandler.PrepareAsync(state, metadata, save.SubRequests[0], save.DataElementPackage!,
                content, provider.Limits, default);
        });
        Assert.IsType<InvalidDataException>(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expected.Message, actual.Message);
    }

    [Fact]
    public async Task HistoricalCorruptionIsDeferredButSelectedAndTouchedObjectsAreVerified()
    {
        var content = new CountingContent(new InMemoryContentStore());
        var provider = new StorageProvider(new InMemoryStateStore(), content);
        var service = new CellBridgeDocumentService(provider);
        var corpus = new IncrementalOfficeWorkload(256);
        var state = (await service.CreateAsync("/corruption.docx", corpus.CreateContent(), TestActor.Value))!;
        var client = (await StoredDocument.RestoreAsync(state, content)).FilePartition.FileGraph;
        var historic = state.Partitions.Single(p => p.Kind == 0).Elements.Single(e => e.Type == (uint)DataElementType.ObjectGroupDataElementData);
        var seed = corpus.CreateSave(client, 1);
        state = (await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, seed.Request,
            new Dictionary<string, string>(), TestActor.Value)).State;
        client = client.Merge(seed.Request.DataElementPackage!.DataElements,
            ((PutChangesSubRequestData)seed.Request.SubRequests[0].Data!).StorageIndex);
        content.CorruptKey = historic.Payload.Key;
        var next = corpus.CreateSave(client, 2);
        var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, next.Request,
            new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        await Assert.ThrowsAsync<StorageCorruptionException>(async () =>
            await DocumentPartition.RestoreFileGraphAsync(saved.State, content, provider.Limits));
        content.CorruptKey = state.Partitions.Single(p => p.Kind == 0).Elements
            .First(e => e.Type == (uint)DataElementType.ObjectGroupDataElementData && e.Id != historic.Id &&
                !seed.Request.DataElementPackage!.DataElements.Last(e => e.DataElementType == DataElementType.ObjectGroupDataElementData)
                    .DataElementExtendedGuid.Equals(StorageIds.Restore(e.Id))).Payload.Key;
        await Assert.ThrowsAsync<StorageCorruptionException>(async () =>
            await FilePartitionSaveHandler.PrepareAsync(state, StoredDocument.RestoreMetadata(state, DateTime.UtcNow),
                next.Request.SubRequests[0], next.Request.DataElementPackage!, content, provider.Limits, default));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("type")]
    [InlineData("content")]
    [InlineData("serial")]
    public async Task DuplicatesIdentityChecksAndMappingSerialsMatchFullPreparation(string mutation)
    {
        var content = new InMemoryContentStore();
        var provider = new StorageProvider(new InMemoryStateStore(), content);
        var service = new CellBridgeDocumentService(provider);
        var corpus = new IncrementalOfficeWorkload(256);
        var state = (await service.CreateAsync("/identities.docx", corpus.CreateContent(), TestActor.Value))!;
        var graph = (await StoredDocument.RestoreAsync(state, content)).FilePartition.FileGraph;
        var save = corpus.CreateSave(graph, 1).Request;
        var package = save.DataElementPackage!;
        if (mutation == "duplicate") package.DataElements.Add(package.DataElements[0]);
        else if (mutation is "type" or "content")
        {
            var old = graph.Elements.First(e => e.DataElementType == DataElementType.ObjectGroupDataElementData);
            package.DataElements.Add(new DataElement(mutation == "type" ? DataElementType.ObjectDataBLOBDataElementData : old.DataElementType,
                old.DataElementExtendedGuid, SerialNumber.Null) { Data = mutation == "content" ? [1, 2, 3] : old.Data });
        }
        else
        {
            var index = graph.StorageIndexes.Single();
            var mapping = StorageIndexMappingSerials.ReadMappings(index.Data!).First();
            var target = new ExGuid(99, Guid.NewGuid());
            var writer = new BinaryWriterEx();
            var body = new BinaryWriterEx(); target.Serialize(body); mapping.Serial.Serialize(body);
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.StorageIndexManifestMapping, body.Length).Serialize(writer);
            writer.WriteBytes(body.ToArray());
            package.DataElements.Add(new DataElement(DataElementType.StorageIndexDataElementData,
                new ExGuid(98, Guid.NewGuid()), SerialNumber.Null) { Data = writer.ToArray() });
        }
        var metadata = StoredDocument.RestoreMetadata(state, DateTime.UtcNow);
        var expected = await FilePartitionSaveHandler.PrepareAsync(state, metadata, save.SubRequests[0], package,
            content, provider.Limits, default, forceFullGraph: true);
        var actual = await FilePartitionSaveHandler.PrepareAsync(state, metadata, save.SubRequests[0], package,
            content, provider.Limits, default);
        Assert.Equal(ResponseBytes(expected), ResponseBytes(actual));
        Assert.Equal(mutation != "duplicate", actual.Response.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteCatalogQuotasApplyEvenWhenSelectedGraphIsSmall(bool bytes)
    {
        var content = new InMemoryContentStore();
        var provider = new StorageProvider(new InMemoryStateStore(), content);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/quota.docx", MinimalDocx.Create(), TestActor.Value))!;
        var corpus = new IncrementalOfficeWorkload(256);
        var client = (await StoredDocument.RestoreAsync(state, content)).FilePartition.FileGraph;
        for (var revision = 1; revision <= 3; revision++)
        {
            var save = corpus.CreateSave(client, revision);
            state = (await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, save.Request,
                new Dictionary<string, string>(), TestActor.Value)).State;
            client = client.Merge(save.Request.DataElementPackage!.DataElements,
                ((PutChangesSubRequestData)save.Request.SubRequests[0].Data!).StorageIndex);
        }
        var next = corpus.CreateSave(client, 4).Request;
        var totalElements = state.Partitions.Sum(p => p.Elements.Length);
        var totalBytes = state.Partitions.SelectMany(p => p.Elements).Sum(e => e.Payload.Length);
        var limits = bytes ? provider.Limits with { MaxGraphBytes = totalBytes + 1 } :
            provider.Limits with { MaxGraphElements = totalElements + 1 };
        await Assert.ThrowsAsync<StorageQuotaExceededException>(async () =>
            await FilePartitionSaveHandler.PrepareAsync(state, StoredDocument.RestoreMetadata(state, DateTime.UtcNow),
                next.SubRequests[0], next.DataElementPackage!, content, limits, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedNullSerialsAndServerSerialMaximumMatchFullPreparation(bool nullSerial)
    {
        var content = new InMemoryContentStore();
        var provider = new StorageProvider(new InMemoryStateStore(), content);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/serial-maximum.docx", MinimalDocx.Create(), TestActor.Value))!;
        var client = (await StoredDocument.RestoreAsync(state, content)).FilePartition.FileGraph;
        var old = client.Elements.Single(e => e.DataElementType == DataElementType.ObjectGroupDataElementData);
        var source = state.Partitions.Single(p => p.Kind == 0);
        var serial = nullSerial ? new SerialId(Guid.Empty, 0) : new SerialId(source.Identity.SerialGuid, 900000);
        source = source with { Knowledge = 1, Elements = source.Elements.Select(e =>
            e.Id == StorageIds.Capture(old.DataElementExtendedGuid) ? e with { Serial = serial } : e).ToImmutableArray() };
        state = state with { Partitions = state.Partitions.Select(p => p.Kind == 0 ? source : p).ToImmutableArray(), Revisions = [] };
        var save = new IncrementalOfficeWorkload(256).CreateSave(client, 1).Request;
        save.DataElementPackage!.DataElements.Add(old);
        var metadata = StoredDocument.RestoreMetadata(state, DateTime.UtcNow);
        var reference = await FilePartitionSaveHandler.PrepareAsync(state, metadata, save.SubRequests[0], save.DataElementPackage,
            content, provider.Limits, default, forceFullGraph: true);
        var selected = await FilePartitionSaveHandler.PrepareAsync(state, metadata, save.SubRequests[0], save.DataElementPackage,
            content, provider.Limits, default);
        Assert.False(selected.Response.Status);
        Assert.NotNull(selected.AddedElements);
        Assert.Equal(ResponseBytes(reference), ResponseBytes(selected));
        if (!nullSerial) Assert.True(selected.KnowledgeSequence > 900000);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InheritedBlobAndMultipleCellGraphsUseTheFullFallback(bool blob, bool multipleCells)
    {
        var content = new InMemoryContentStore();
        var provider = new StorageProvider(new InMemoryStateStore(), content);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/fallback-selected.docx", MinimalDocx.Create(), TestActor.Value))!;
        var fixture = new GraphFixture(MinimalDocx.Create("inherited"), blob);
        if (!multipleCells)
        {
            var index = fixture.Elements.Single(e => e.DataElementExtendedGuid.Equals(fixture.Index));
            var reader = new BinaryReaderEx(index.Data!);
            var writer = new BinaryWriterEx();
            while (reader.Remaining > 0)
            {
                var start = reader.Position;
                var header = StreamObjectHeaderStart.Parse(reader);
                var body = new BinaryReaderEx(reader.ReadMemory(header.Length));
                if (header.Type == StreamObjectTypeHeaderStart.StorageIndexCellMapping &&
                    CellId.Deserialize(body).Equals(fixture.OtherCell)) continue;
                var end = reader.Position;
                reader.Position = start;
                writer.WriteBytes(reader.ReadBytes(end - start));
            }
            index.Data = writer.ToArray();
            var manifest = fixture.Elements.Single(e => e.DataElementExtendedGuid.Equals(fixture.Manifest));
            fixture.Replace(StorageManifestBuilder.BuildStorageManifestDataElement(fixture.Manifest,
                manifest.SerialNumber, fixture.Root, fixture.Cell));
        }
        var package = new DataElementPackage();
        package.DataElements.AddRange(fixture.Elements);
        var operation = new FsshttpbCellSubRequest(RequestTypes.PutChanges)
        {
            RequestId = 1,
            Data = new PutChangesSubRequestData { StorageIndex = fixture.Index,
                ExpectedStorageIndex = StorageIds.Restore(state.Partitions.Single(p => p.Kind == 0).StorageIndex!) },
        };
        var metadata = StoredDocument.RestoreMetadata(state, DateTime.UtcNow);
        var expected = await FilePartitionSaveHandler.PrepareAsync(state, metadata, operation, package,
            content, provider.Limits, default, forceFullGraph: true);
        var actual = await FilePartitionSaveHandler.PrepareAsync(state, metadata, operation, package,
            content, provider.Limits, default);
        Assert.False(actual.Response.Status);
        Assert.Null(actual.AddedElements);
        Assert.Equal(ResponseBytes(expected), ResponseBytes(actual));
        Assert.Equal(fixture.Bytes, actual.Graph!.Materialize());
    }

    private static byte[] ResponseBytes(FilePartitionSaveHandler.PreparedFileSave prepared)
    {
        var response = new FsshttpbResponse { DataElementPackage = prepared.ResponsePackage };
        response.SubResponses.Add(prepared.Response);
        return response.ToByteArray();
    }

    private static void AssertCatalogEqual(PartitionState expected, PartitionState actual)
    {
        Assert.Equal(expected.StorageIndex, actual.StorageIndex);
        Assert.Equal(expected.Knowledge, actual.Knowledge);
        Assert.Equal(expected.Elements.Length, actual.Elements.Length);
        foreach (var (left, right) in expected.Elements.Zip(actual.Elements))
        {
            Assert.Equal(left.Id, right.Id);
            Assert.Equal(left.Type, right.Type);
            Assert.Equal(left.Serial, right.Serial);
            Assert.Equal(left.Payload, right.Payload);
            Assert.Equal(left.MappingSerials.ToArray(), right.MappingSerials.ToArray());
        }
    }

    private sealed class CountingContent(IContentStore inner) : IContentStore
    {
        public HashSet<string> ObjectKeys = [];
        public long ObjectBytesRead;
        public string? CorruptKey;
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) => inner.WriteAsync(source, cancellationToken);
        public async ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
        {
            if (ObjectKeys.Contains(handle.Key)) ObjectBytesRead += handle.Length;
            if (handle.Key == CorruptKey) return new MemoryStream(new byte[checked((int)handle.Length)], writable: false);
            return await inner.OpenReadAsync(handle, cancellationToken);
        }
    }
}
