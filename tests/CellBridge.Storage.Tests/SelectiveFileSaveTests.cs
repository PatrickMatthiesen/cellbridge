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
    public async Task SupportedSavesPreserveHistoryWithoutReadingOldObjectPayloads(bool incremental)
    {
        var content = new CountingContent(new InMemoryContentStore());
        var provider = StorageTestDocuments.Memory(content);
        var service = new CellBridgeDocumentService(provider);
        var corpus = new IncrementalOfficeWorkload(256);
        var head = (await service.CreateAsync("/selective.docx", corpus.CreateContent(), TestActor.Value))!;
        long? firstRead = null;
        for (var revision = 1; revision <= 24; revision++)
        {
            var graph = await StorageTestDocuments.GraphAsync(service, head);
            var request = incremental ? corpus.CreateSave(graph, revision).Request :
                SyntheticWorkload.Save(graph, corpus.CreateContent(revision), "full", revision, new Random(revision));
            var before = (await provider.Documents.ExportDocumentAsync(head.ResourceId))!;
            content.ObjectKeys = before.Elements.Where(e => e.Element.Type == (uint)DataElementType.ObjectGroupDataElementData)
                .Select(e => e.Element.Payload.Key).ToHashSet();
            content.ObjectBytesRead = 0;
            var saved = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents, request,
                new Dictionary<string, string>(), TestActor.Value);
            Assert.False(Assert.Single(saved.Response.SubResponses).Status);
            if (revision >= 2)
            {
                firstRead ??= content.ObjectBytesRead;
                Assert.Equal(firstRead.Value, content.ObjectBytesRead);
                if (!incremental) Assert.Equal(0, content.ObjectBytesRead);
            }
            head = saved.State;
            var after = (await provider.Documents.ExportDocumentAsync(head.ResourceId))!;
            foreach (var old in before.Elements)
                Assert.Equal(old, after.Elements.Single(e => e.Scope == old.Scope && e.Element.Id == old.Element.Id));
            Assert.Equal(corpus.CreateContent(revision), await StorageTestDocuments.PackageAsync(provider, head.Package));
            Assert.Equal((ulong)revision + 1, head.LatestRevisionNumber);
        }
        Assert.Equal(corpus.CreateContent(24), (await StorageTestDocuments.GraphAsync(service, head)).Materialize());
    }

    [Fact]
    public async Task DeltaPersistenceUsesValidatedOwnedBytesAfterTheCallerMutatesRequestBuffers()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var corpus = new IncrementalOfficeWorkload(256);
        var head = (await service.CreateAsync("/owned-delta.docx", corpus.CreateContent(), TestActor.Value))!;
        var save = corpus.CreateSave(await StorageTestDocuments.GraphAsync(service, head), 1);
        var prepared = await Prepare(provider, head, save.Request);
        Assert.False(Assert.Single(prepared.Response.SubResponses).Status);
        var publication = Assert.IsType<PreparedDocumentPublication>(prepared.Publication);
        var originalResponse = prepared.Response.ToByteArray();
        var originals = publication.Partitions.Single(p => p.After.Kind == 0).AddedElements.ToDictionary(e => e.Id,
            e => save.Request.DataElementPackage!.DataElements.Single(i => StorageIds.Capture(i.DataElementExtendedGuid) == e.Id).Data!.ToArray());
        foreach (var element in save.Request.DataElementPackage!.DataElements) Array.Fill(element.Data!, (byte)0xA5);
        Assert.Equal(originalResponse, prepared.Response.ToByteArray());
        var result = await provider.Documents.PublishAsync(publication);
        Assert.Equal(PublicationStatus.Accepted, result.Status);
        Assert.Equal(save.Content, await StorageTestDocuments.PackageAsync(provider, result.Current.Package));
        foreach (var element in publication.Partitions.Single(p => p.After.Kind == 0).AddedElements)
            Assert.Equal(originals[element.Id], await provider.Content.ReadVerifiedAsync(element.Payload));
    }

    [Fact]
    public async Task MissingCurrentMappingRejectsWithoutPublishing()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var head = (await service.CreateAsync("/missing-current-index.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = new IncrementalOfficeWorkload(256).CreateSave(await StorageTestDocuments.GraphAsync(service, head), 1).Request;
        var preparation = await ReadPreparation(provider, head, request);
        preparation = preparation with { Partition = preparation.Partition with {
            Partition = preparation.Partition.Partition with { StorageIndex = null } } };
        var result = await IndexedPartitionSavePreparer.PrepareAsync(preparation, request.SubRequests[0], request.DataElementPackage!,
            StorageTestDocuments.Admission(head), new Dictionary<string,string>(), provider.Documents, provider.Content, provider.Limits);
        Assert.Null(result.Publication);
        Assert.True(Assert.Single(result.Response.SubResponses).Status);
        Assert.Equal(head, await provider.Documents.FindAsync(new DocumentIdentity.Resource(head.ResourceId)));
    }

    [Fact]
    public async Task HistoricalCorruptionIsDeferredButSelectedObjectsAreVerified()
    {
        var content = new CountingContent(new InMemoryContentStore());
        var provider = StorageTestDocuments.Memory(content);
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/corruption.docx", MinimalDocx.Create("initial"), TestActor.Value))!;
        var historic = (await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(0) { IncludeCatalog = true }))!.Elements
            .Single(e => e.Type == (uint)DataElementType.ObjectGroupDataElementData);
        var head = (await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            await StorageTestDocuments.SaveRequestAsync(service, initial, MinimalDocx.Create("replacement")),
            new Dictionary<string,string>(), TestActor.Value)).State;
        var nextBytes = MinimalDocx.Create("next");
        var request = await StorageTestDocuments.SaveRequestAsync(service, head, nextBytes);
        content.CorruptKey = historic.Payload.Key;
        var saved = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string,string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        Assert.Equal(nextBytes, await StorageTestDocuments.PackageAsync(provider, saved.State.Package));
        await Assert.ThrowsAsync<StorageCorruptionException>(() => StorageTestDocuments.PackageAsync(provider, initial.Package));
        content.CorruptKey = null;
        var current = (await provider.Documents.ReadPartitionAsync(head.ResourceId, new(0) { IncludeBindings = true }))!;
        var group = current.Bindings.First().GroupElement;
        content.CorruptKey = (await provider.Documents.ReadElementsAsync(new(head.ResourceId, head.LifecycleGeneration, 0), [group])).Single().Payload.Key;
        await Assert.ThrowsAsync<StorageCorruptionException>(() => StorageTestDocuments.PackageAsync(provider, saved.State.Package));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("type")]
    [InlineData("content")]
    [InlineData("serial")]
    public async Task DuplicateIdentityAndMappingSerialValidationIsPreserved(string mutation)
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var head = (await service.CreateAsync("/identities.docx", MinimalDocx.Create(), TestActor.Value))!;
        var graph = await StorageTestDocuments.GraphAsync(service, head);
        var request = new IncrementalOfficeWorkload(256).CreateSave(graph, 1).Request;
        var package = request.DataElementPackage!;
        if (mutation == "duplicate") package.DataElements.Add(package.DataElements[0]);
        else if (mutation is "type" or "content")
        {
            var old = graph.Elements.First(e => e.DataElementType == DataElementType.ObjectGroupDataElementData);
            package.DataElements.Add(new DataElement(mutation == "type" ? DataElementType.ObjectDataBLOBDataElementData : old.DataElementType,
                old.DataElementExtendedGuid, SerialNumber.Null) { Data = mutation == "content" ? [1,2,3] : old.Data });
        }
        else
        {
            var retained = StorageIndexMappingSerials.ReadMappings(graph.StorageIndexes.Single().Data!)
                .First(mapping => !mapping.Serial.IsNull);
            var put = (PutChangesSubRequestData)Assert.Single(request.SubRequests).Data!;
            var selected = package.DataElements.Single(element => element.DataElementExtendedGuid.Equals(put.StorageIndex));
            var updates = StorageIndexPatch.Read(selected).ToArray();
            var conflicting = Array.FindIndex(updates, update => update.Type != retained.Type ||
                !Equals(update.Revision, retained.Revision) || !Equals(update.Cell, retained.Cell) ||
                !Equals(update.Target, retained.Target));
            Assert.True(conflicting >= 0);
            updates[conflicting] = updates[conflicting] with { Serial = retained.Serial };
            selected.Data = StorageIndexPatch.Serialize(updates);
        }
        var prepared = await Prepare(provider, head, request);
        Assert.Equal(mutation != "duplicate", Assert.Single(prepared.Response.SubResponses).Status);
        Assert.Equal(mutation == "duplicate", prepared.Publication is not null);
        Assert.Equal(head, await provider.Documents.FindAsync(new DocumentIdentity.Resource(head.ResourceId)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteCatalogQuotasApplyEvenWhenSelectedGraphIsSmall(bool bytes)
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var head = (await service.CreateAsync("/quota.docx", MinimalDocx.Create(), TestActor.Value))!;
        for (var revision = 1; revision <= 3; revision++) head = await RevisionHistoryTests.Save(service, head, "revision-" + revision);
        var next = await StorageTestDocuments.SaveRequestAsync(service, head, MinimalDocx.Create("next"));
        var limits = bytes ? provider.Limits with { MaxGraphBytes = head.Counters.GraphBytes + 1 }
            : provider.Limits with { MaxGraphElements = head.Counters.GraphElements + 1 };
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => Prepare(provider, head, next, limits));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedNullSerialsAndServerSerialMaximumArePreserved(bool nullSerial)
    {
        var provider = StorageTestDocuments.Memory();
        var created = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), "/serial-maximum.docx", MinimalDocx.Create(),
            DocumentSecurity.Create(TestActor.Value.Identity), provider.Content, provider.Limits);
        var file = created.Partitions.Single(p => p.After.Kind == 0);
        var serial = nullSerial ? new SerialId(Guid.Empty,0) : new SerialId(file.After.Identity.SerialGuid,900000);
        file = file with { AddedElements = file.AddedElements.Select(e => e.Type == (uint)DataElementType.ObjectGroupDataElementData
            ? e with { Serial = serial } : e).ToImmutableArray(), After = file.After with { Knowledge = nullSerial ? file.After.Knowledge : 900000 } };
        created = created with { Partitions = created.Partitions.Select(p => p.After.Kind == 0 ? file : p).ToImmutableArray(),
            InitialRevision = created.InitialRevision with { Partitions = created.InitialRevision.Partitions.Select(p => p.Kind == 0 ? file.After : p).ToImmutableArray() } };
        var head = (await provider.Documents.CreateAsync(created)).Current!;
        var service = new CellBridgeDocumentService(provider);
        var graph = await StorageTestDocuments.GraphAsync(service, head);
        var request = new IncrementalOfficeWorkload(256).CreateSave(graph, 1).Request;
        request.DataElementPackage!.DataElements.Add(graph.Elements.Single(e => e.DataElementType == DataElementType.ObjectGroupDataElementData));
        var prepared = await Prepare(provider, head, request);
        Assert.False(Assert.Single(prepared.Response.SubResponses).Status);
        Assert.NotNull(prepared.Publication);
        if (!nullSerial) Assert.True(prepared.Publication.Partitions.Single(p => p.After.Kind == 0).After.Knowledge > 900000);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InheritedBlobAndMultipleCellGraphsUseIndexedPreparation(bool blob)
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var head = (await service.CreateAsync("/inherited.docx", MinimalDocx.Create(), TestActor.Value))!;
        var fixture = new GraphFixture(MinimalDocx.Create("inherited"), blob);
        var request = StorageTestDocuments.MetadataRequest(fixture);
        StorageTests.Rebase(request, await StorageTestDocuments.GraphAsync(service, head));
        var prepared = await Prepare(provider, head, request);
        Assert.False(Assert.Single(prepared.Response.SubResponses).Status);
        var result = await provider.Documents.PublishAsync(prepared.Publication!);
        Assert.Equal(PublicationStatus.Accepted, result.Status);
        Assert.Equal(fixture.Bytes, await StorageTestDocuments.PackageAsync(provider, result.Current.Package));
    }

    private static async Task<SavePreparationRead> ReadPreparation(StorageProvider provider, DocumentHead head, FsshttpbCellRequest request)
    {
        var operation = request.SubRequests[0];
        var put = (PutChangesSubRequestData)operation.Data!;
        var index = request.DataElementPackage!.DataElements.First(e => e.DataElementExtendedGuid.Equals(put.StorageIndex));
        var identity = IndexedPartitionSavePreparer.Identify(head.LifecycleGeneration, 0, operation, request.DataElementPackage);
        var keys = IndexedDocumentGraph.ReadMetadata(new(head.ResourceId, head.LifecycleGeneration,0), index).Mappings.Select(m => m.Key).Distinct().ToImmutableArray();
        return (await provider.Documents.PrepareSaveAsync(head.ResourceId, new(0, identity.Operation,keys) { IncludeBindings = true }))!;
    }

    private static async Task<IndexedPreparedSave> Prepare(StorageProvider provider, DocumentHead head,
        FsshttpbCellRequest request, StorageLimits? limits = null) =>
        await IndexedPartitionSavePreparer.PrepareAsync(await ReadPreparation(provider,head,request), request.SubRequests[0],request.DataElementPackage!,
            StorageTestDocuments.Admission(head), new Dictionary<string,string>(), provider.Documents,provider.Content,limits ?? provider.Limits);

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
