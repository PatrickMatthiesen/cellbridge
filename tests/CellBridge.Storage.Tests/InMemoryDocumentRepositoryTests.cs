using System.Collections.Immutable;
using System.Security.Cryptography;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Tests;

public sealed partial class InMemoryDocumentRepositoryTests
{
    private static readonly SubjectIdentity Actor = new("owner", "owner", "Owner");

    [Fact]
    public async Task ConcurrentCreatesEnforcePathUniquenessAndRetiredClaimsStayReserved()
    {
        var content = new InMemoryContentStore();
        var repository = new InMemoryDocumentRepository(content);
        var a = await SeedAsync(content, "/same.docx");
        var b = await SeedAsync(content, "/same.docx");
        var results = await Task.WhenAll(repository.CreateAsync(a).AsTask(), repository.CreateAsync(b).AsTask());
        var winner = Assert.Single(results, r => r.Created).Current!;
        var renamed = await repository.ChangePathAsync(new(winner.ResourceId, Admission(winner), "/renamed.docx", StorageIds.PathKey("/renamed.docx"), ImmutableDictionary<string, string>.Empty));
        Assert.True(renamed.Changed);
        Assert.Null(await repository.FindAsync(new DocumentIdentity.Path(StorageIds.PathKey("/same.docx"))));
        Assert.False((await repository.CreateAsync(await SeedAsync(content, "/same.docx"))).Created);
        Assert.Equal(1, renamed.Current.Counters.RetiredPaths);
    }

    [Fact]
    public async Task StalePublicationRequiresPreparationAndCannotOverwriteAnAcceptedBinding()
    {
        var (repository, _, seed) = await RepositoryAsync();
        var read = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
        var binding = seed.Partitions[0].EffectiveChanges.Changes.First(c => c.Value is not null).Value!;
        var first = Publication(read, "first", new IndexedBindingChange(binding.Key, null));
        var stale = Publication(read, "stale");
        Assert.Equal(PublicationStatus.Accepted, (await repository.PublishAsync(first)).Status);
        Assert.Equal(PublicationStatus.Reprepare, (await repository.PublishAsync(stale)).Status);
        var current = (await repository.ReadPartitionAsync(seed.Head.ResourceId, new(0) { IncludeBindings = true }))!;
        Assert.DoesNotContain(current.Bindings, b => b.Key == binding.Key);
        Assert.Equal(1, current.Current.Counters.SaveReceipts);
    }

    [Fact]
    public async Task ReceiptsRequireTheActorDigestAndCurrentAuthorization()
    {
        var (repository, _, seed) = await RepositoryAsync();
        var read = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
        var publication = Publication(read, "once");
        Assert.Equal(PublicationStatus.Accepted, (await repository.PublishAsync(publication)).Status);
        var current = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
        var retry = publication with { Admission = Admission(current.Head) };
        Assert.Equal(PublicationStatus.Replay, (await repository.PublishAsync(retry)).Status);
        Assert.Equal("InvalidRequest", (await repository.PublishAsync(retry with
            { Receipt = retry.Receipt with { Digest = "different" } })).ErrorCode);
        Assert.Equal("InvalidRequest", (await repository.PublishAsync(retry with
        {
            Admission = retry.Admission with { Actor = new("other", "other", "Other") },
            Receipt = retry.Receipt with { OwnerSubject = "other" },
        })).ErrorCode);
        var revoked = await repository.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(seed.Head.ResourceId,
            Admission(current.Head), DocumentSecurity.Empty, current.Head.AuthorityGeneration));
        Assert.True(revoked.Changed);
        Assert.Equal(PublicationStatus.ProtocolError, (await repository.PublishAsync(retry)).Status);
    }

    [Fact]
    public async Task HistoricalBindingsReplayTheSelectedEffectiveChangesAndRetiredPayloadsStayAddressable()
    {
        var (repository, _, seed) = await RepositoryAsync();
        var read = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
        var before = (await repository.ReadPartitionAsync(seed.Head.ResourceId, new(0) { IncludeBindings = true }))!;
        var binding = before.Bindings[0];
        var publication = Publication(read, "branch", new IndexedBindingChange(binding.Key, null));
        var part = publication.Partitions[0];
        var retired = seed.Partitions[0].AddedElements.First(e => e.Type == (uint)DataElementType.ObjectGroupDataElementData);
        publication = publication with { Partitions = [part with
        {
            RetiredElements = [retired.Id],
            After = part.After with { ElementCount = part.After.ElementCount - 1, ElementBytes = part.After.ElementBytes - retired.Payload.Length },
        }] };
        Assert.Equal(PublicationStatus.Accepted, (await repository.PublishAsync(publication)).Status);
        var old = (await repository.ReadPartitionAsync(seed.Head.ResourceId, new(0) { RevisionNumber = 1, IncludeBindings = true, IncludeCatalog = true }))!;
        Assert.Equal(before.Bindings.OrderBy(b => b.Key.ObjectId.Value), old.Bindings.OrderBy(b => b.Key.ObjectId.Value));
        Assert.Contains(old.Elements, e => e.Id == retired.Id);
        var current = (await repository.ReadPartitionAsync(seed.Head.ResourceId, new(0) { IncludeCatalog = true }))!;
        Assert.DoesNotContain(current.Elements, e => e.Id == retired.Id);
        Assert.Single(await repository.ReadElementsAsync(seed.Head.Package.Root.Scope, [retired.Id]));
        var requested = new[] { retired.Id, retired.Id, new ExtendedId(1, Guid.NewGuid()) };
        Assert.Equal(new[] { retired.Id }, await repository.ReadVisibleElementIdsAsync(seed.Head.Package.Root.Scope,
            before.Partition.PublicationSequence, requested));
        Assert.Empty(await repository.ReadVisibleElementIdsAsync(seed.Head.Package.Root.Scope,
            current.Partition.PublicationSequence, requested));
        Assert.Empty(await repository.ReadVisibleElementIdsAsync(seed.Head.Package.Root.Scope with { LifecycleGeneration = 2 },
            before.Partition.PublicationSequence, requested));
    }

    [Fact]
    public async Task InvalidPhysicalMetadataCannotChangeTheHeadOrCounters()
    {
        var (repository, content, seed) = await RepositoryAsync();
        var read = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
        var publication = Publication(read, "invalid");
        var existing = seed.Partitions[0].AddedElements.First(e => e.Type == (uint)DataElementType.ObjectGroupDataElementData);
        using var source = new MemoryStream([1, 2, 3]);
        var wrongPayload = await content.WriteAsync(source);
        var part = publication.Partitions[0];
        publication = publication with { Partitions = [part with
            { AddedElements = [existing with { Payload = wrongPayload with { Sha256 = new string('0', 64) } }] }] };
        await Assert.ThrowsAsync<StorageCorruptionException>(async () => await repository.PublishAsync(publication));
        Assert.Equal(read.Head, (await repository.ReadControlAsync(seed.Head.ResourceId))!.Head);
    }

    [Fact]
    public async Task BoundDeliveryCompletesItsFirstRevisionWhileNewerPublicationsRemainQueued()
    {
        var (repository, _, seed) = await RepositoryAsync();
        var head = (await repository.ReadControlAsync(seed.Head.ResourceId))!.Head;
        var binding = new DocumentDeliveryBinding(Guid.NewGuid(), "destination", "remote-0", 1);
        Assert.True((await repository.UpdateDeliveryAsync(new DocumentDeliveryCommand.Bind(head.ResourceId,
            head.LifecycleGeneration, head.StateVersion, binding))).Applied);
        await PublishFileAsync(repository, head.ResourceId, "file-1");
        var first = (await repository.ReadNextDeliveryAsync(head.ResourceId))!;
        await PublishFileAsync(repository, head.ResourceId, "file-2");
        Assert.True((await repository.UpdateDeliveryAsync(new DocumentDeliveryCommand.Complete(head.ResourceId, first, "remote-1"))).Applied);
        var second = (await repository.ReadNextDeliveryAsync(head.ResourceId))!;
        Assert.Equal(2, second.Revision.Sequence);
        Assert.Equal("remote-1", second.Binding.ExpectedRevision);
        Assert.Equal(1, second.Current.Counters.PendingDeliveries);
        Assert.False((await repository.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(head.ResourceId,
            second.Current.LifecycleGeneration, second.Current.StateVersion))).Applied);
        Assert.False((await repository.UpdateDeliveryAsync(new DocumentDeliveryCommand.Complete(head.ResourceId, first, "remote-stale"))).Applied);
    }

    [Fact]
    public async Task DeleteAndRecreatePermanentlyRetireTheOldResourceAndFenceStaleWork()
    {
        var (repository, content, seed) = await RepositoryAsync();
        var old = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
        var stale = Publication(old, "late");
        Assert.True((await repository.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(old.Head.ResourceId, 1, 0))).Applied);
        var deleted = (await repository.FindAsync(new DocumentIdentity.Resource(old.Head.ResourceId, true)))!;
        var replacement = await SeedAsync(content, old.Head.Path, lifecycle: 2);
        var result = await repository.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(old.Head.ResourceId, 1, deleted.StateVersion, replacement));
        Assert.True(result.Applied);
        Assert.Null(await repository.FindAsync(new DocumentIdentity.Resource(old.Head.ResourceId)));
        Assert.Equal(replacement.Head.ResourceId, (await repository.FindAsync(new DocumentIdentity.Path(old.Head.PathKey)))!.ResourceId);
        Assert.Equal(PublicationStatus.ProtocolError, (await repository.PublishAsync(stale)).Status);
    }

    [Fact]
    public async Task CheckpointBoundaryRejectsAnUnstagedCheckpointWithoutPublishing()
    {
        var content = new InMemoryContentStore();
        var repository = new InMemoryDocumentRepository(content);
        var seed = await SeedAsync(content, "/checkpoint.docx");
        Assert.True((await repository.CreateAsync(seed)).Created);
        for (var index = 0; index < IndexedCheckpointPolicy.Default.MaxReplayChanges - 1; index++)
        {
            var preceding = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
            Assert.Equal(PublicationStatus.Accepted, (await repository.PublishAsync(Publication(preceding, "tail-" + index))).Status);
        }
        var control = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
        var publication = Publication(control, "checkpoint");
        await Assert.ThrowsAsync<InvalidDataException>(async () => await repository.PublishAsync(publication));
        Assert.Equal(control.Head, (await repository.ReadControlAsync(seed.Head.ResourceId))!.Head);
        var part = publication.Partitions[0];
        var bindings = (await repository.ReadPartitionAsync(seed.Head.ResourceId, new(0) { IncludeBindings = true }))!.Bindings;
        publication = publication with { Partitions = [part with
            { Checkpoint = IndexedDocumentGraph.Checkpoint(seed.Head.Package.Root.Scope, part.After.PublicationSequence, bindings) }] };
        Assert.Equal(PublicationStatus.Accepted, (await repository.PublishAsync(publication)).Status);
        var operation = new SaveOperationIdentity(1, SaveOperationFamily.Protocol, 0, "next");
        var prepared = (await repository.PrepareSaveAsync(seed.Head.ResourceId, new(0, operation, [])))!;
        Assert.Equal(part.After.PublicationSequence, prepared.ReplayPosition.CheckpointSequence);
        Assert.Equal(0, prepared.ReplayPosition.Changes);
    }

    [Fact]
    public async Task PerDocumentLimitsRejectBeforeChangingRootsReceiptsOrCounters()
    {
        var content = new InMemoryContentStore();
        var repository = new InMemoryDocumentRepository(content, new StorageLimits { MaxSaveReceipts = 1 });
        var seed = await SeedAsync(content, "/bounded.docx");
        Assert.True((await repository.CreateAsync(seed)).Created);
        var control = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
        Assert.Equal(PublicationStatus.Accepted, (await repository.PublishAsync(Publication(control, "first"))).Status);
        var current = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
        await Assert.ThrowsAsync<StorageQuotaExceededException>(async () => await repository.PublishAsync(Publication(current, "overflow")));
        var after = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
        Assert.Equal(current.Head, after.Head);
        Assert.True(current.Partitions.SequenceEqual(after.Partitions));
        Assert.Null((await repository.ReadReceiptAsync(seed.Head.ResourceId, new(1, SaveOperationFamily.Protocol, 0, "overflow")))!.Receipt);
    }

    [Fact]
    public async Task MappingUpdatesPreserveUntouchedKeysAndHistoricalRootsWithoutRetainingWireIndexes()
    {
        var (repository, _, seed) = await RepositoryAsync();
        var scope = seed.Head.Package.Root.Scope;
        var old = await repository.ReadMappingsAsync(scope, 1);
        var updated = old.First(m => m.Key.Kind == IndexedMappingKind.Manifest);
        var untouched = old.First(m => m.Key.Kind == IndexedMappingKind.Cell);
        var read = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
        var publication = Publication(read, "map");
        var part = publication.Partitions[0];
        var newRoot = new ExtendedId(1, Guid.NewGuid());
        var newSerial = new SerialId(Guid.NewGuid(), 7);
        publication = publication with
        {
            Receipt = publication.Receipt with { AcceptedStorageIndex = newRoot },
            Partitions = [part with
            {
                After = part.After with { StorageIndex = newRoot, StorageIndexSerial = new(Guid.NewGuid(), 1) },
                Mappings = [new(scope, newRoot, updated.Key, updated.Target, newSerial)],
                MappingChanges = [new(updated.Key, new(updated.Target, newSerial))],
            }],
        };
        Assert.Equal(PublicationStatus.Accepted, (await repository.PublishAsync(publication)).Status);
        var current = await repository.ReadMappingsAsync(scope, 2);
        Assert.Equal(old.Length, current.Length);
        Assert.Equal(untouched.Target, current.Single(m => m.Key == untouched.Key).Target);
        Assert.Equal(untouched.Serial, current.Single(m => m.Key == untouched.Key).Serial);
        Assert.Equal(updated.Serial, (await repository.FindMappingAsync(scope, seed.Partitions[0].After.StorageIndex!, updated.Key))!.Serial);
        Assert.Equal(newSerial, (await repository.FindMappingAsync(scope, newRoot, updated.Key))!.Serial);
        Assert.DoesNotContain((await repository.ReadPartitionAsync(seed.Head.ResourceId, new(0) { IncludeCatalog = true }))!.Elements,
            e => e.Type == (uint)DataElementType.StorageIndexDataElementData);
        var preparation = (await repository.PrepareSaveAsync(seed.Head.ResourceId,
            new(0, new(1, SaveOperationFamily.Protocol, 0, "targeted"), [untouched.Key])))!;
        Assert.Equal(untouched.Key, Assert.Single(preparation.Partition.Mappings).Key);

        var latest = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
        var matchingTarget = Publication(latest, "same-target") with
        { Fence = new(1, latest.Head.AuthorityGeneration, latest.Coordination.Generation, latest.Head.StateVersion,
            latest.Head.ContentVersion, [new(updated.Key, updated)]) };
        Assert.Equal(PublicationStatus.Accepted, (await repository.PublishAsync(matchingTarget)).Status);

        var nextControl = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
        var replacement = Publication(nextControl, "new-target");
        var replacementPart = replacement.Partitions[0];
        var manifest = seed.Partitions[0].AddedElements.Single(e => e.Id == updated.Target);
        var newManifest = manifest with { Id = new(99, Guid.NewGuid()), Serial = new(Guid.NewGuid(), 1) };
        var latestRoot = new ExtendedId(2, Guid.NewGuid());
        var latestSerial = new SerialId(Guid.NewGuid(), 8);
        replacement = replacement with
        {
            Receipt = replacement.Receipt with { AcceptedStorageIndex = latestRoot },
            Partitions = [replacementPart with
            {
                After = replacementPart.After with
                { StorageIndex = latestRoot, StorageIndexSerial = new(Guid.NewGuid(), 1),
                    ElementCount = replacementPart.After.ElementCount + 1,
                    ElementBytes = replacementPart.After.ElementBytes + newManifest.Payload.Length },
                AddedElements = [newManifest],
                StorageManifests = [seed.Partitions[0].StorageManifests.Single(m => m.ElementId == manifest.Id) with { ElementId = newManifest.Id }],
                Mappings = [new(scope, latestRoot, updated.Key, newManifest.Id, latestSerial)],
                MappingChanges = [new(updated.Key, new(newManifest.Id, latestSerial))],
            }],
        };
        Assert.Equal(PublicationStatus.Accepted, (await repository.PublishAsync(replacement)).Status);
        Assert.Equal(updated.Target, (await repository.FindMappingAsync(scope, newRoot, updated.Key))!.Target);
        Assert.Equal(newManifest.Id, (await repository.FindMappingAsync(scope, latestRoot, updated.Key))!.Target);
        Assert.Null(await repository.FindMappingAsync(scope with { LifecycleGeneration = 2 }, latestRoot, updated.Key));
        Assert.Null(await repository.FindMappingAsync(scope with { TargetPartition = 1 }, latestRoot, updated.Key));
        Assert.Equal(updated.Target, (await repository.ReadMappingsAsync(scope, 2)).Single(m => m.Key == updated.Key).Target);
    }

    [Fact]
    public async Task MissingObjectGroupsRemainDistinctFromAdmittedEmptyGroups()
    {
        var (repository, content, seed) = await RepositoryAsync();
        var group = GraphFixture.Element(DataElementType.ObjectGroupDataElementData, new(88, Guid.NewGuid()), writer =>
        {
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.ObjectGroupDeclarations, 0).Serialize(writer);
            new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupDeclarations).Serialize(writer);
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.ObjectGroupData, 0).Serialize(writer);
            new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupData).Serialize(writer);
        });
        using var bytes = new MemoryStream(group.Data!);
        var payload = await content.WriteAsync(bytes);
        var descriptor = new GraphElementState(IndexedDocumentGraph.Capture(group.DataElementExtendedGuid), (uint)group.DataElementType,
            new(group.SerialNumber.Guid, group.SerialNumber.Value), payload) { MappingSerials = [] };
        var control = (await repository.ReadControlAsync(seed.Head.ResourceId))!;
        var publication = Publication(control, "empty-group");
        var part = publication.Partitions[0];
        publication = publication with { Partitions = [part with { AddedElements = [descriptor], After = part.After with
            { ElementCount = part.After.ElementCount + 1, ElementBytes = part.After.ElementBytes + payload.Length } }] };
        Assert.Equal(PublicationStatus.Accepted, (await repository.PublishAsync(publication)).Status);
        var scope = seed.Head.Package.Root.Scope;
        Assert.False((await repository.ReadGroupAsync(scope, descriptor.Id)).IsDefault);
        Assert.Empty(await repository.ReadGroupAsync(scope, descriptor.Id));
        var missing = new ExtendedId(89, Guid.NewGuid());
        Assert.True((await repository.ReadGroupAsync(scope, missing)).IsDefault);
        Assert.True((await repository.ReadGroupAsync(scope, seed.Partitions[0].StorageManifests[0].ElementId)).IsDefault);
        var revision = new IndexedRevisionManifest(scope, new(90, Guid.NewGuid()), new(91, Guid.NewGuid()), new(0, Guid.Empty), [], [descriptor.Id]);
        var probe = new GroupProbeSource(repository, revision);
        var resolved = await IndexedDocumentGraph.ResolveCellAsync(scope, seed.Head.Package.Root.StorageIndex,
            seed.Head.Package.Root.Cell, revision.RevisionId, probe);
        Assert.Empty(resolved.Bindings);
        var absent = revision with { ObjectGroups = [missing] };
        await Assert.ThrowsAsync<InvalidDataException>(async () => await IndexedDocumentGraph.ResolveCellAsync(scope,
            seed.Head.Package.Root.StorageIndex, seed.Head.Package.Root.Cell, absent.RevisionId, new GroupProbeSource(repository, absent)));
    }

    private sealed class GroupProbeSource(IIndexedGraphSource groups, IndexedRevisionManifest revision) : IIndexedGraphSource
    {
        public ValueTask<IndexedStorageMapping?> FindMappingAsync(IndexedGraphScope scope, ExtendedId storageIndex, IndexedMappingKey key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IndexedStorageMapping?>(new(scope, storageIndex, key, revision.ElementId, new(Guid.NewGuid(), 1)));
        public ValueTask<IndexedRevisionManifest?> ReadRevisionAsync(IndexedGraphScope scope, ExtendedId manifest, CancellationToken cancellationToken = default) => ValueTask.FromResult<IndexedRevisionManifest?>(revision);
        public ValueTask<ImmutableArray<IndexedObjectDefinition>> ReadGroupAsync(IndexedGraphScope scope, ExtendedId group, CancellationToken cancellationToken = default) => groups.ReadGroupAsync(scope, group, cancellationToken);
    }

    private static async Task PublishFileAsync(InMemoryDocumentRepository repository, Guid id, string operation)
    {
        var read = (await repository.ReadControlAsync(id))!;
        var number = read.Head.LatestRevisionNumber + 1;
        var package = read.Head.Package with { RevisionNumber = number };
        var publication = Publication(read, operation);
        var part = publication.Partitions[0];
        publication = publication with
        {
            Package = package,
            Receipt = publication.Receipt with { ContentVersion = read.Head.ContentVersion + 1, RevisionNumber = number },
            Revision = new(id, read.Head.LifecycleGeneration, number, read.Head.ContentVersion + 1, read.UtcNow, Actor, package,
                read.Partitions.Where(p => p.Kind != 2).Select(p => p.Kind == 0 ? part.After : p).ToImmutableArray()),
            DeliveryOperationId = Guid.NewGuid(),
        };
        Assert.Equal(PublicationStatus.Accepted, (await repository.PublishAsync(publication)).Status);
    }

    private static PreparedDocumentPublication Publication(DocumentControlRead control, string operation,
        params IndexedBindingChange[] changes)
    {
        var before = control.Partitions.Single(p => p.Kind == 0);
        var scope = control.Head.Package.Root.Scope;
        var delta = IndexedDocumentGraph.Difference(scope, before.PublicationSequence, before.PublicationSequence + 1,
            changes.Where(c => c.Value is null).Select(c => new IndexedObjectBinding(c.Key, new(0, Guid.Empty), 0)),
            changes.Where(c => c.Value is not null).Select(c => c.Value!));
        // Difference computes the canonical encoded size; the removal's old value is immaterial.
        var part = new PreparedPartitionPublication(before, before with { PublicationSequence = before.PublicationSequence + 1 },
            [], [], [], [], [], delta);
        return new(control.Head.ResourceId, Admission(control.Head), new(control.Head.LifecycleGeneration,
            control.Head.AuthorityGeneration, control.Coordination.Generation, control.Head.StateVersion, control.Head.ContentVersion, []),
            new(new(control.Head.LifecycleGeneration, SaveOperationFamily.Protocol, 0, operation), "digest-" + operation,
                Actor.Subject, control.Head.ContentVersion, control.Head.LatestRevisionNumber, before.StorageIndex, null), [part]);
    }

    private static DocumentActorAdmission Admission(DocumentHead head) => new(Actor, head.ResourceId,
        head.LifecycleGeneration, head.Security.AuthorizationPolicy, DocumentAccess.Read | DocumentAccess.Write)
        { AuthorityGeneration = head.AuthorityGeneration, StateVersion = head.StateVersion };

    private static async Task<(InMemoryDocumentRepository Repository, InMemoryContentStore Content, PreparedDocumentCreate Seed)> RepositoryAsync()
    {
        var content = new InMemoryContentStore();
        var repository = new InMemoryDocumentRepository(content);
        var seed = await SeedAsync(content, "/file.docx");
        Assert.True((await repository.CreateAsync(seed)).Created);
        return (repository, content, seed);
    }

    private static async Task<PreparedDocumentCreate> SeedAsync(InMemoryContentStore content, string path, long lifecycle = 1)
    {
        var fixture = new GraphFixture("fixture package"u8.ToArray());
        var scope = new IndexedGraphScope(Guid.NewGuid(), lifecycle, 0);
        var metadata = fixture.Elements.Select(e => IndexedDocumentGraph.ReadMetadata(scope, e)).ToArray();
        var elements = ImmutableArray.CreateBuilder<GraphElementState>();
        foreach (var element in fixture.Elements.Where(e => e.DataElementType != DataElementType.StorageIndexDataElementData))
        {
            using var stream = new MemoryStream(element.Data!);
            var handle = await content.WriteAsync(stream);
            var record = IndexedDocumentGraph.ReadMetadata(scope, element);
            elements.Add(new(IndexedDocumentGraph.Capture(element.DataElementExtendedGuid), (uint)element.DataElementType,
                new(element.SerialNumber.Guid, element.SerialNumber.Value), handle)
                { MappingSerials = record.Mappings.Select(m => m.Serial).ToImmutableArray() });
        }
        var id = IndexedDocumentGraph.Capture(fixture.Index);
        var source = new Source(metadata);
        var resolved = await IndexedDocumentGraph.ResolveFileCellAsync(scope, id,
            IndexedDocumentGraph.Capture(fixture.Cell), IndexedDocumentGraph.Capture(fixture.Revision), source);
        ExtendedId Capture(ExGuid value) => IndexedDocumentGraph.Capture(value);
        var identity = new PartitionIdentity(Capture(fixture.Manifest), Capture(fixture.CellManifest), Capture(fixture.RevisionElement),
            Capture(fixture.CurrentGroup), Capture(fixture.Blob), Capture(fixture.RootObject), Capture(fixture.Revision),
            Capture(fixture.Cell.LongId), Capture(fixture.Cell.ShortId), Guid.NewGuid());
        var after = new PartitionHead(0, identity, 1, id, 1, [])
            { ElementCount = elements.Count, ElementBytes = elements.Sum(e => e.Payload.Length),
                StorageIndexSerial = new(fixture.Elements[0].SerialNumber.Guid, fixture.Elements[0].SerialNumber.Value) };
        var part = new PreparedPartitionPublication(after with { PublicationSequence = 0, Knowledge = 0, StorageIndex = null, StorageIndexSerial = null, ElementCount = 0, ElementBytes = 0 },
            after, elements.ToImmutable(), [], metadata.SelectMany(m => m.Mappings).ToImmutableArray(),
            metadata.Where(m => m.Revision is not null).Select(m => m.Revision!).ToImmutableArray(),
            metadata.SelectMany(m => m.Objects).ToImmutableArray(), IndexedDocumentGraph.Difference(scope, 0, 1, [], resolved.Bindings))
        {
            Checkpoint = IndexedDocumentGraph.Checkpoint(scope, 1, resolved.Bindings),
            Cells = metadata.Where(m => m.CellManifest is not null).Select(m => m.CellManifest!).ToImmutableArray(),
            StorageManifests = metadata.Where(m => m.StorageManifest is not null).Select(m => m.StorageManifest!).ToImmutableArray(),
            MappingChanges = metadata.SelectMany(m => m.Mappings).Select(m => new IndexedMappingChange(m.Key, new(m.Target, m.Serial))).ToImmutableArray(),
        };
        var package = new DocumentPackage(scope.ResourceId, lifecycle, 1, 1,
            new(scope, id, IndexedDocumentGraph.Capture(fixture.Cell), Capture(fixture.Revision), Capture(fixture.RootObject)),
            fixture.Bytes.Length, Convert.ToHexStringLower(SHA256.HashData(fixture.Bytes)));
        var now = DateTime.UtcNow;
        var head = new DocumentHead(scope.ResourceId, path, StorageIds.PathKey(path), now, now, 1, 0, lifecycle, false,
            package, DocumentSecurity.Create(Actor), 1, new());
        PreparedPartitionPublication Empty(int kind)
        {
            var emptyScope = scope with { TargetPartition = kind };
            var before = new PartitionHead(kind, identity, 0, null, 0, []);
            var emptyAfter = before with { PublicationSequence = 1 };
            return new(before, emptyAfter, [], [], [], [], [], IndexedDocumentGraph.Difference(emptyScope, 0, 1, [], []))
                { Checkpoint = IndexedDocumentGraph.Checkpoint(emptyScope, 1, []) };
        }
        var metadataPartition = Empty(1);
        return new(head, CoordinationState.Empty, [], [part, metadataPartition, Empty(2)],
            new(scope.ResourceId, lifecycle, 1, 1, now, Actor, package, [after, metadataPartition.After]));
    }

    private sealed class Source(IndexedElementMetadata[] records) : IIndexedGraphSource
    {
        public ValueTask<IndexedStorageMapping?> FindMappingAsync(IndexedGraphScope scope, ExtendedId storageIndex, IndexedMappingKey key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(records.SelectMany(r => r.Mappings).FirstOrDefault(m => m.Scope == scope && m.StorageIndex == storageIndex && m.Key == key));
        public ValueTask<IndexedRevisionManifest?> ReadRevisionAsync(IndexedGraphScope scope, ExtendedId manifest, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(records.Select(r => r.Revision).FirstOrDefault(r => r is not null && r.Scope == scope && r.ElementId == manifest));
        public ValueTask<ImmutableArray<IndexedObjectDefinition>> ReadGroupAsync(IndexedGraphScope scope, ExtendedId group, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(records.SelectMany(r => r.Objects).Where(o => o.Scope == scope && o.GroupElement == group).ToImmutableArray());
    }
}
