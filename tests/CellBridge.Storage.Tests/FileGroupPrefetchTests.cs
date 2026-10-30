using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;
using CellBridge.Tests;
using System.Collections.Immutable;

namespace CellBridge.Storage.Tests;

public sealed class FileGroupPrefetchTests
{
    [Fact]
    public async Task IncrementalSaveBatchesOnlyStoredGroupsSelectedByTheFileBindings()
    {
        var fixture = await CreateSecondSaveAsync();
        var (provider, service, content, workload, head, request, expectedKeys) = fixture;
        content.Reset();

        var saved = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value);

        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        Assert.Contains(content.Batches, batch => expectedKeys.SetEquals(batch));
        Assert.NotEmpty(content.SingleReadKeys);
        Assert.All(content.SingleReadKeys, key => Assert.DoesNotContain(key, expectedKeys));
        Assert.Equal(workload.CreateContent(2), await StorageTestDocuments.PackageAsync(provider, saved.State.Package));
    }

    [Fact]
    public async Task IncrementalSaveBatchStillVerifiesSelectedGroupPayloads()
    {
        var (_, service, content, _, head, request, expectedKeys) = await CreateSecondSaveAsync();
        content.Reset();
        var corruptKey = expectedKeys.First();
        content.CorruptKey = corruptKey;

        await Assert.ThrowsAsync<StorageCorruptionException>(() => service.ExecuteAsync(head.ResourceId,
            DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value).AsTask());

        Assert.Contains(content.Batches, batch => batch.Contains(corruptKey, StringComparer.Ordinal));
    }

    [Fact]
    public async Task SavePreparationProjectionAvoidsStandaloneReadsForItsSubmittedAndCurrentGraph()
    {
        var (provider, _, content, workload, head, request, _) = await CreateSecondSaveAsync();
        var preparation = await ReadPreparationWithGraphAsync(provider, head, request);
        var source = new RecordingIndexedSaveSource(provider.Documents);
        content.Reset();

        var prepared = await IndexedPartitionSavePreparer.PrepareAsync(preparation, request.SubRequests[0],
            request.DataElementPackage!, StorageTestDocuments.Admission(head), new Dictionary<string, string>(),
            source, content, provider.Limits);

        Assert.False(Assert.Single(prepared.Response.SubResponses).Status);
        Assert.NotNull(preparation.Graph);
        var submittedIds = preparation.Graph!.SubmittedElements.Select(element => element.Id).ToHashSet();
        var projectedCurrentIds = preparation.Graph.CurrentElements.Select(element => element.Id).ToHashSet();
        Assert.Empty(source.ElementQueries.SelectMany(ids => ids).Intersect(submittedIds));
        Assert.Empty(source.ElementQueries.SelectMany(ids => ids).Intersect(projectedCurrentIds));
        Assert.Empty(source.ClaimQueries);
        var currentGroups = preparation.Partition.Bindings.Select(binding => binding.GroupElement).ToHashSet();
        Assert.Empty(source.GroupQueries.Intersect(currentGroups));

        var publication = Assert.IsType<PreparedDocumentPublication>(prepared.Publication);
        var published = await provider.Documents.PublishAsync(publication);
        Assert.Equal(PublicationStatus.Accepted, published.Status);
        Assert.Equal(workload.CreateContent(2), await StorageTestDocuments.PackageAsync(provider, published.Current!.Package));
    }

    [Fact]
    public async Task PreparedMappingKeysCacheKnownAbsenceButUnknownKeysStillUseTheSource()
    {
        var (provider, _, content, workload, head, request, _) = await CreateSecondSaveAsync();
        var preparation = await ReadPreparationWithGraphAsync(provider, head, request);
        var scope = new IndexedGraphScope(head.ResourceId, head.LifecycleGeneration, 0);
        var currentIndex = preparation.Partition.Partition.StorageIndex!;
        var unrequestedKey = preparation.Partition.Mappings.First(mapping => mapping.Key.Kind == IndexedMappingKind.Cell).Key;
        preparation = preparation with
        {
            Partition = preparation.Partition with
            {
                Mappings = preparation.Partition.Mappings.Where(mapping => mapping.Key != unrequestedKey).ToImmutableArray(),
            },
            PreparedMappingKeys = preparation.PreparedMappingKeys.Where(key => key != unrequestedKey).ToImmutableArray(),
        };

        var withoutKnownKeys = preparation with { PreparedMappingKeys = [] };
        var initialSource = new RecordingIndexedSaveSource(provider.Documents);
        var initial = await PrepareAsync(withoutKnownKeys, initialSource);
        Assert.False(Assert.Single(initial.Response.SubResponses).Status);
        var currentQueries = initialSource.MappingQueries.Where(query => query.Scope == scope && query.Index == currentIndex).ToArray();
        Assert.Contains(currentQueries, query => query.Mapping is null && preparation.PreparedMappingKeys.Contains(query.Key));
        Assert.Contains(currentQueries, query => query.Mapping is not null && !preparation.PreparedMappingKeys.Contains(query.Key));
        var knownAbsent = currentQueries.First(query => query.Mapping is null && preparation.PreparedMappingKeys.Contains(query.Key));
        var unrequested = currentQueries.First(query => query.Mapping is not null && !preparation.PreparedMappingKeys.Contains(query.Key));

        var preparedSource = new RecordingIndexedSaveSource(provider.Documents);
        var prepared = await PrepareAsync(preparation, preparedSource);
        Assert.False(Assert.Single(prepared.Response.SubResponses).Status);
        Assert.DoesNotContain(preparedSource.MappingQueries,
            query => query.Scope == scope && query.Index == currentIndex && query.Key == knownAbsent.Key);
        Assert.Contains(preparedSource.MappingQueries,
            query => query.Scope == scope && query.Index == currentIndex && query.Key == unrequested.Key);

        var publication = Assert.IsType<PreparedDocumentPublication>(prepared.Publication);
        var published = await provider.Documents.PublishAsync(publication);
        Assert.Equal(PublicationStatus.Accepted, published.Status);
        Assert.Equal(workload.CreateContent(2), await StorageTestDocuments.PackageAsync(provider, published.Current!.Package));

        async Task<IndexedPreparedSave> PrepareAsync(SavePreparationRead selected, RecordingIndexedSaveSource source) =>
            await IndexedPartitionSavePreparer.PrepareAsync(selected, request.SubRequests[0], request.DataElementPackage!,
                StorageTestDocuments.Admission(head), new Dictionary<string, string>(), source, content, provider.Limits);
    }

    private static async Task<(StorageProvider Provider, CellBridgeDocumentService Service, RecordingBatchContent Content,
        IncrementalOfficeWorkload Workload, DocumentHead Head, FsshttpbCellRequest Request, HashSet<string> ExpectedKeys)>
        CreateSecondSaveAsync()
    {
        var content = new RecordingBatchContent();
        var provider = StorageTestDocuments.Memory(content);
        var service = new CellBridgeDocumentService(provider);
        var workload = new IncrementalOfficeWorkload(256);
        var head = (await service.CreateAsync("/prefetch.docx", workload.CreateContent(), TestActor.Value))!;

        var seedGraph = await StorageTestDocuments.GraphAsync(service, head);
        var seed = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents,
            workload.CreateSave(seedGraph, 1).Request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(seed.Response.SubResponses).Status);
        head = seed.State;

        var graph = await StorageTestDocuments.GraphAsync(service, head);
        var request = workload.CreateSave(graph, 2).Request;
        var scope = new IndexedGraphScope(head.ResourceId, head.LifecycleGeneration, 0);
        var revisionElement = request.DataElementPackage!.DataElements.Single(element =>
            element.DataElementType == DataElementType.RevisionManifestDataElementData);
        var revision = IndexedDocumentGraph.ReadMetadata(scope, revisionElement).Revision!;
        var incomingIds = request.DataElementPackage.DataElements
            .Select(element => new ExtendedId(element.DataElementExtendedGuid.Value, element.DataElementExtendedGuid.Guid))
            .ToHashSet();
        var current = (await provider.Documents.ReadPartitionAsync(head.ResourceId,
            new(0) { IncludeBindings = true, IncludeCatalog = true }))!;
        var catalog = current.Elements.Where(element => element.Type == (uint)DataElementType.ObjectGroupDataElementData)
            .ToDictionary(element => element.Id);
        var expectedKeys = revision.ObjectGroups.Where(id => !incomingIds.Contains(id) && catalog.ContainsKey(id))
            .Select(id => catalog[id].Payload.Key).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(expectedKeys);
        return (provider, service, content, workload, head, request, expectedKeys);
    }

    private static async Task<SavePreparationRead> ReadPreparationWithGraphAsync(StorageProvider provider,
        DocumentHead head, FsshttpbCellRequest request)
    {
        var operation = request.SubRequests[0];
        var put = (PutChangesSubRequestData)operation.Data!;
        var package = request.DataElementPackage!;
        var index = package.DataElements.Single(element => element.DataElementExtendedGuid.Equals(put.StorageIndex));
        var scope = new IndexedGraphScope(head.ResourceId, head.LifecycleGeneration, 0);
        var metadata = IndexedDocumentGraph.ReadMetadata(scope, index);
        var submittedIds = package.DataElements.Select(element =>
            new ExtendedId(element.DataElementExtendedGuid.Value, element.DataElementExtendedGuid.Guid)).ToImmutableArray();
        var serials = metadata.Mappings.Select(mapping => mapping.Serial)
            .Where(serial => serial.Guid != Guid.Empty || serial.Value != 0).Distinct().ToImmutableArray();
        var graph = new SaveGraphReadRequest(submittedIds, serials);
        var identity = IndexedPartitionSavePreparer.Identify(head.LifecycleGeneration, 0, operation, package);
        return (await provider.Documents.PrepareSaveAsync(head.ResourceId,
            new(0, identity.Operation, metadata.Mappings.Select(mapping => mapping.Key).Distinct().ToImmutableArray())
            { IncludeBindings = true, Graph = graph }))!;
    }

    private sealed class RecordingIndexedSaveSource(IIndexedSaveSource inner) : IIndexedSaveSource
    {
        public List<ExtendedId[]> ElementQueries { get; } = [];
        public List<SerialId[]> ClaimQueries { get; } = [];
        public List<ExtendedId> GroupQueries { get; } = [];
        public List<(IndexedGraphScope Scope, ExtendedId Index, IndexedMappingKey Key, IndexedStorageMapping? Mapping)> MappingQueries { get; } = [];

        public async ValueTask<IndexedStorageMapping?> FindMappingAsync(IndexedGraphScope scope, ExtendedId storageIndex,
            IndexedMappingKey key, CancellationToken cancellationToken = default)
        {
            var mapping = await inner.FindMappingAsync(scope, storageIndex, key, cancellationToken);
            MappingQueries.Add((scope, storageIndex, key, mapping));
            return mapping;
        }

        public ValueTask<IndexedRevisionManifest?> ReadRevisionAsync(IndexedGraphScope scope, ExtendedId manifest,
            CancellationToken cancellationToken = default) => inner.ReadRevisionAsync(scope, manifest, cancellationToken);

        public ValueTask<ImmutableArray<IndexedObjectDefinition>> ReadGroupAsync(IndexedGraphScope scope,
            ExtendedId group, CancellationToken cancellationToken = default)
        {
            GroupQueries.Add(group);
            return inner.ReadGroupAsync(scope, group, cancellationToken);
        }

        public ValueTask<ImmutableArray<GraphElementState>> ReadElementsAsync(IndexedGraphScope scope,
            IReadOnlyCollection<ExtendedId> ids, CancellationToken cancellationToken = default)
        {
            ElementQueries.Add(ids.ToArray());
            return inner.ReadElementsAsync(scope, ids, cancellationToken);
        }

        public ValueTask<ImmutableArray<ExtendedId>> ReadVisibleElementIdsAsync(IndexedGraphScope scope,
            long publicationSequence, IReadOnlyCollection<ExtendedId> elementIds,
            CancellationToken cancellationToken = default) =>
            inner.ReadVisibleElementIdsAsync(scope, publicationSequence, elementIds, cancellationToken);

        public ValueTask<ImmutableArray<IndexedStorageMapping>> ReadMappingClaimsAsync(IndexedGraphScope scope,
            IReadOnlyCollection<SerialId> serials, CancellationToken cancellationToken = default)
        {
            ClaimQueries.Add(serials.ToArray());
            return inner.ReadMappingClaimsAsync(scope, serials, cancellationToken);
        }

        public ValueTask<ImmutableArray<IndexedStorageMapping>> ReadMappingsAsync(IndexedGraphScope scope,
            long publicationSequence, CancellationToken cancellationToken = default) =>
            inner.ReadMappingsAsync(scope, publicationSequence, cancellationToken);
    }

    private sealed class RecordingBatchContent : IContentStore, IContentBatchReader
    {
        private readonly InMemoryContentStore inner = new();
        public List<string[]> Batches { get; } = [];
        public List<string> BatchKeys { get; } = [];
        public List<string> SingleReadKeys { get; } = [];
        public string? CorruptKey { get; set; }
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;

        public void Reset()
        {
            Batches.Clear();
            BatchKeys.Clear();
            SingleReadKeys.Clear();
            CorruptKey = null;
        }

        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(source, cancellationToken);

        public async ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
        {
            SingleReadKeys.Add(handle.Key);
            return await inner.OpenReadAsync(handle, cancellationToken);
        }

        public async ValueTask<IReadOnlyDictionary<string, byte[]>> ReadManyAsync(
            IReadOnlyCollection<ContentHandle> handles, CancellationToken cancellationToken = default)
        {
            var keys = handles.Select(handle => handle.Key).ToArray();
            Batches.Add(keys);
            BatchKeys.AddRange(keys);
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var handle in handles)
            {
                var bytes = await inner.ReadVerifiedAsync(handle, cancellationToken: cancellationToken);
                if (handle.Key == CorruptKey && bytes.Length > 0) bytes[0] ^= 0xFF;
                result.Add(handle.Key, bytes);
            }
            return result;
        }
    }
}
