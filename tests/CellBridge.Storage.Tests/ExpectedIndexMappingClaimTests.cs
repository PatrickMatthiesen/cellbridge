using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;

public sealed class ExpectedIndexMappingClaimTests
{
    private static readonly SubjectIdentity Actor = new("owner", "owner", "Owner");

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    [InlineData(true, 600)]
    [InlineData(false, 600)]
    public async Task UnappliedEnvelopesDoNotReadOrPersistMappingClaims(bool expected, int count)
    {
        var (repository, content, head) = await CreateAsync();
        var control = (await repository.ReadControlAsync(head.ResourceId))!;
        var before = control.Partitions.Single(partition => partition.Kind == 1);
        // Use the server namespace and large values to detect accidental high-water advancement.
        var ignored = Enumerable.Range(0, count).Select(number => new StorageIndexMapping(
            StreamObjectTypeHeaderStart.StorageIndexRevisionMapping, new(1, Guid.NewGuid()), null,
            ExGuid.Null, new(before.Identity.SerialGuid, checked((ulong)number + 100_000)))).ToArray();
        var source = new ObservedSource(repository);
        var prepared = await PrepareAsync(repository, content, head, source, [], ignored, expected);
        Assert.Null(prepared.ErrorCode);
        var publication = Assert.IsType<PreparedDocumentPublication>(prepared.Publication);
        var partition = Assert.Single(publication.Partitions);
        Assert.Empty(source.RequestedClaims);
        Assert.Empty(partition.Mappings);
        Assert.Empty(partition.MappingChanges);
        // The two index envelopes still retain existing DE-level reassignment behavior.
        Assert.Equal(before.Knowledge + 2, partition.After.Knowledge);
        Assert.Equal(PublicationStatus.Accepted, (await repository.PublishAsync(publication)).Status);
        var scope = new IndexedGraphScope(head.ResourceId, head.LifecycleGeneration, 1);
        Assert.Empty(await repository.ReadMappingsAsync(scope, partition.After.PublicationSequence));
        Assert.Empty(await repository.ReadMappingClaimsAsync(scope,
            ignored.Select(mapping => new SerialId(mapping.Serial.Guid, mapping.Serial.Value)).ToArray()));
        var snapshot = (await repository.ExportDocumentAsync(head.ResourceId))!;
        Assert.DoesNotContain(snapshot.MappingClaims, claim => claim.Scope == scope);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpectedSerialAliasDoesNotBecomeAnAppliedClaimButProposedConflictsStillFail(bool conflictingUpdate)
    {
        var (repository, content, head) = await CreateAsync();
        var claimed = new SerialNumber(Guid.NewGuid(), 7);
        var historical = new StorageIndexMapping(StreamObjectTypeHeaderStart.StorageIndexRevisionMapping,
            new(1, Guid.NewGuid()), null, ExGuid.Null, claimed);
        var initial = await PrepareAsync(repository, content, head, new ObservedSource(repository), [historical], [], true);
        var initialPublication = Assert.IsType<PreparedDocumentPublication>(initial.Publication);
        Assert.Equal(PublicationStatus.Accepted, (await repository.PublishAsync(initialPublication)).Status);
        head = (await repository.FindAsync(new DocumentIdentity.Resource(head.ResourceId)))!;
        var appliedSerial = conflictingUpdate ? claimed : new SerialNumber(Guid.NewGuid(), 8);
        var update = new StorageIndexMapping(StreamObjectTypeHeaderStart.StorageIndexManifestMapping,
            null, null, ExGuid.Null, appliedSerial);
        // Target nil matches the absent current manifest. Its serial aliases a different retained key.
        var expectation = update with { Serial = claimed };
        var source = new ObservedSource(repository);
        var prepared = await PrepareAsync(repository, content, head, source, [update], [expectation], true);
        Assert.Equal(new SerialId(appliedSerial.Guid, appliedSerial.Value), Assert.Single(source.RequestedClaims));
        if (conflictingUpdate)
        {
            Assert.Null(prepared.Publication);
            var response = Assert.Single(prepared.Response.SubResponses);
            Assert.True(response.Status);
            Assert.Equal((ulong)CellErrorCode.InvalidObject, response.Error!.ErrorCode);
        }
        else
        {
            Assert.False(Assert.Single(prepared.Response.SubResponses).Status);
            var publication = Assert.IsType<PreparedDocumentPublication>(prepared.Publication);
            Assert.Equal(new SerialId(appliedSerial.Guid, appliedSerial.Value),
                Assert.Single(Assert.Single(publication.Partitions).Mappings).Serial);
            Assert.Equal(PublicationStatus.Accepted, (await repository.PublishAsync(publication)).Status);
        }
        var scope = new IndexedGraphScope(head.ResourceId, head.LifecycleGeneration, 1);
        var originalClaim = Assert.Single(await repository.ReadMappingClaimsAsync(scope, [new(claimed.Guid, claimed.Value)]));
        Assert.Equal(IndexedMappingKind.Revision, originalClaim.Key.Kind);
        Assert.Equal(IndexedDocumentGraph.Capture(historical.Revision!), originalClaim.Key.Revision);
    }

    private static async Task<(InMemoryDocumentRepository Repository, InMemoryContentStore Content, DocumentHead Head)> CreateAsync()
    {
        var content = new InMemoryContentStore();
        var repository = new InMemoryDocumentRepository(content);
        var created = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), "/claims.docx", [1, 2, 3],
            DocumentSecurity.Create(Actor), content, new());
        return (repository, content, (await repository.CreateAsync(created)).Current!);
    }

    private static async Task<IndexedPreparedSave> PrepareAsync(InMemoryDocumentRepository repository, IContentStore content,
        DocumentHead head, IIndexedSaveSource source, IReadOnlyList<StorageIndexMapping> updates,
        IReadOnlyList<StorageIndexMapping> extraMappings, bool expected)
    {
        var selectedId = new ExGuid(1, Guid.NewGuid());
        var extraId = new ExGuid(1, Guid.NewGuid());
        var upload = new DataElementPackage();
        upload.DataElements.Add(new(DataElementType.StorageIndexDataElementData, selectedId, SerialNumber.Null)
            { Data = StorageIndexPatch.Serialize(updates) });
        upload.DataElements.Add(new(DataElementType.StorageIndexDataElementData, extraId, SerialNumber.Null)
            { Data = StorageIndexPatch.Serialize(extraMappings) });
        var request = new FsshttpbCellSubRequest(RequestTypes.PutChanges) { RequestId = 1,
            Data = new PutChangesSubRequestData { StorageIndex = selectedId, ExpectedStorageIndex = expected ? extraId : ExGuid.Null } };
        var identity = IndexedPartitionSavePreparer.Identify(head.LifecycleGeneration, 1, request, upload);
        var keys = updates.Select(mapping => mapping.Revision is { } revision
            ? new IndexedMappingKey(IndexedMappingKind.Revision, Revision: IndexedDocumentGraph.Capture(revision))
            : new IndexedMappingKey(IndexedMappingKind.Manifest)).ToImmutableArray();
        var read = (await repository.PrepareSaveAsync(head.ResourceId, new(1, identity.Operation, keys) { IncludeBindings = true }))!;
        return await IndexedPartitionSavePreparer.PrepareAsync(read, request, upload,
            new(Actor, head.ResourceId, head.LifecycleGeneration, null, DocumentAccess.Read | DocumentAccess.Write)
                { AuthorityGeneration = head.AuthorityGeneration, StateVersion = head.StateVersion },
            ImmutableDictionary<string, string>.Empty, source, content, new());
    }

    private sealed class ObservedSource(IIndexedSaveSource source) : IIndexedSaveSource
    {
        public List<SerialId> RequestedClaims { get; } = [];
        public ValueTask<ImmutableArray<IndexedStorageMapping>> ReadMappingClaimsAsync(IndexedGraphScope scope,
            IReadOnlyCollection<SerialId> serials, CancellationToken cancellationToken = default)
        {
            RequestedClaims.AddRange(serials);
            return source.ReadMappingClaimsAsync(scope, serials, cancellationToken);
        }
        public ValueTask<ImmutableArray<GraphElementState>> ReadElementsAsync(IndexedGraphScope scope,
            IReadOnlyCollection<ExtendedId> ids, CancellationToken cancellationToken = default) => source.ReadElementsAsync(scope, ids, cancellationToken);
        public ValueTask<ImmutableArray<ExtendedId>> ReadVisibleElementIdsAsync(IndexedGraphScope scope, long sequence,
            IReadOnlyCollection<ExtendedId> ids, CancellationToken cancellationToken = default) => source.ReadVisibleElementIdsAsync(scope, sequence, ids, cancellationToken);
        public ValueTask<ImmutableArray<IndexedStorageMapping>> ReadMappingsAsync(IndexedGraphScope scope, long sequence,
            CancellationToken cancellationToken = default) => source.ReadMappingsAsync(scope, sequence, cancellationToken);
        public ValueTask<IndexedStorageMapping?> FindMappingAsync(IndexedGraphScope scope, ExtendedId index,
            IndexedMappingKey key, CancellationToken cancellationToken = default) => source.FindMappingAsync(scope, index, key, cancellationToken);
        public ValueTask<IndexedRevisionManifest?> ReadRevisionAsync(IndexedGraphScope scope, ExtendedId manifest,
            CancellationToken cancellationToken = default) => source.ReadRevisionAsync(scope, manifest, cancellationToken);
        public ValueTask<ImmutableArray<IndexedObjectDefinition>> ReadGroupAsync(IndexedGraphScope scope, ExtendedId group,
            CancellationToken cancellationToken = default) => source.ReadGroupAsync(scope, group, cancellationToken);
    }
}
