using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.InMemory;

public sealed partial class InMemoryDocumentRepository
{
    public async ValueTask<bool> ReserveContentAsync(DocumentActorAdmission admission,
        IReadOnlyCollection<ContentHandle> handles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handles);
        if (handles.Count > IContentBatchWriter.MaxBatchObjects)
            throw new ArgumentOutOfRangeException(nameof(handles), "Document staging admits at most 64 handles.");
        var references = ContentStoreReader.UniqueHandles(handles);
        foreach (var reference in references)
        {
            if (reference.Key != reference.Sha256 || reference.Length < 0 || reference.Length > Array.MaxLength || reference.Sha256.Length != 64 ||
                reference.Sha256.Any(value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new StorageCorruptionException("Invalid staging content descriptor.");
            StorageLimits.Check("object bytes", reference.Length, _limits.MaxObjectBytes);
        }
        if (!TryDocument(admission.ResourceId, cancellationToken, out var entry)) return false;
        await entry.Guard.WaitAsync(cancellationToken);
        try
        {
            if (!DocumentControlReducer.Access(entry.Head, admission).HasFlag(DocumentAccess.Write)) return false;
            foreach (var reference in references)
                if (entry.References.TryGetValue(reference.Key, out var existing) && existing != reference)
                    throw new StorageCorruptionException("Conflicting document content descriptors share a key.");
            var added = references.Where(reference => !entry.References.ContainsKey(reference.Key)).ToArray();
            var physicalBytes = added.Sum(reference => reference.Length);
            var metadataBytes = added.Sum(NormalizedContentReference.StagingMetadataBytes);
            var next = AccountedHead(entry, entry.Head, metadataBytes, physicalBytes);
            DocumentPublicationRules.CheckLimits(next, _limits);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var reference in added)
            {
                entry.References.Add(reference.Key, reference);
                entry.ReferenceCounts.Add(reference.Key, 1);
                entry.AdmissionBytes.Add(reference.Key, NormalizedContentReference.StagingMetadataBytes(reference));
                entry.PendingReferences.Add(reference.Key);
            }
            entry.MetadataBytes = checked(entry.MetadataBytes + metadataBytes);
            entry.PhysicalBytes = checked(entry.PhysicalBytes + physicalBytes);
            Volatile.Write(ref entry.Head, next);
            return true;
        }
        finally { entry.Guard.Release(); }
    }

    public async ValueTask<CreateResult> CreateAsync(PreparedDocumentCreate create, CancellationToken cancellationToken = default)
    {
        EnsureOnline(cancellationToken);
        var entry = await PrepareCreateAsync(create, cancellationToken);
        // Claims are never removed by rename/delete. A stale Office path cannot become another document.
        if (!_paths.TryAdd(entry.Head.PathKey, entry.Head.ResourceId))
            return new(false, await FindAsync(new DocumentIdentity.Path(entry.Head.PathKey), cancellationToken));
        if (_documents.TryAdd(entry.Head.ResourceId, entry)) return new(true, entry.Head);
        _paths.TryRemove(new KeyValuePair<string, Guid>(entry.Head.PathKey, entry.Head.ResourceId));
        return new(false, await FindAsync(new DocumentIdentity.Resource(entry.Head.ResourceId, true), cancellationToken));
    }

    private async ValueTask<Entry> PrepareCreateAsync(PreparedDocumentCreate create, CancellationToken cancellationToken)
    {
        var now = UtcNow;
        var head = create.Head with
        {
            CoordinationGeneration = create.Coordination.Generation,
            ActiveEditors = create.Editors.IsDefaultOrEmpty ? 0 : create.Editors.Count(editor => editor.ExpiresUtc > now),
            NextEditorExpiryUtc = create.Editors.IsDefaultOrEmpty ? null : create.Editors.Min(editor => editor.ExpiresUtc),
        };
        if (head.ResourceId == Guid.Empty || string.IsNullOrEmpty(head.Path) || string.IsNullOrEmpty(head.PathKey) ||
            head.PathKey != StorageIds.PathKey(head.Path) ||
            head.IsDeleted || head.LifecycleGeneration <= 0 || head.StateVersion != 0 || head.DeletedFromStateVersion is not null ||
            head.ReplacedBy is not null || head.Delivery is not null || create.Partitions.IsDefault || create.Editors.IsDefault ||
            create.Partitions.Select(p => p.After.Kind).Distinct().Count() != create.Partitions.Length ||
            !create.Partitions.Select(p => p.After.Kind).Order().SequenceEqual(new[] { 0, 1, 2 }) ||
            create.InitialRevision.ResourceId != head.ResourceId || create.InitialRevision.LifecycleGeneration != head.LifecycleGeneration ||
            create.InitialRevision.Package != head.Package || create.InitialRevision.RevisionNumber != head.LatestRevisionNumber ||
            create.InitialRevision.ContentVersion != head.ContentVersion || head.LatestRevisionNumber == 0)
            throw new InvalidDataException("Invalid initial document identity or history.");
        var references = ContentStoreReader.UniqueHandles(create.Partitions.SelectMany(p => p.AddedElements).Select(e => e.Payload));
        foreach (var partition in create.Partitions)
            await DocumentPublicationRules.ValidatePartitionPayloadsAsync(partition, _content, _limits, cancellationToken);
        var entry = new Entry(head, create.Coordination, create.Editors);
        foreach (var reference in references)
        { entry.References.Add(reference.Key, reference); entry.ReferenceCounts.Add(reference.Key, 1); }
        foreach (var prepared in create.Partitions)
        {
            if (prepared.Before.PublicationSequence != 0)
                throw new InvalidDataException("Initial publication has a nonempty predecessor.");
            var part = new Partition(prepared.Before);
            var scope = new IndexedGraphScope(head.ResourceId, head.LifecycleGeneration, prepared.After.Kind);
            part.Checkpoints.Add(0, IndexedDocumentGraph.Checkpoint(scope, 0, []));
            ValidatePartition(part, prepared, scope);
            ApplyPartition(part, prepared);
            if (prepared.Checkpoint is not null) part.Checkpoints.Remove(0);
            entry.Partitions.Add(part.Head.Kind, part);
        }
        if (!entry.Partitions.TryGetValue(0, out var file) || file.Head.StorageIndex != head.Package.Root.StorageIndex ||
            !EqualMetadata(create.InitialRevision.Partitions.ToArray(), entry.Partitions.Values.Where(p => p.Head.Kind != 2).OrderBy(p => p.Head.Kind).Select(p => p.Head).ToArray()))
            throw new InvalidDataException("The initial revision does not identify the current partition heads.");
        entry.Revisions.Add(create.InitialRevision.RevisionNumber, create.InitialRevision);
        var counters = new DocumentCounters
        {
            GraphBytes = entry.Partitions.Values.Sum(p => p.Elements.Values.Sum(e => e.Payload.Length)),
            GraphElements = entry.Partitions.Values.Sum(p => p.Elements.Count), Revisions = 1,
        };
        entry.Head = head with { Counters = counters };
        entry.PhysicalBytes = references.Sum(h => h.Length);
        var initial = Snapshot(entry);
        entry.MetadataBytes = checked(NormalizedDocumentRecovery.StoredBytes(initial) - entry.PhysicalBytes -
            NormalizedDocumentRecovery.CanonicalHeadBytes(entry.Head));
        entry.Head = AccountedHead(entry, entry.Head);
        DocumentPublicationRules.CheckLimits(entry.Head, _limits);
        return entry;
    }

    public async ValueTask<ControlResult> ExecuteControlAsync(DocumentControlCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!TryDocument(command.ResourceId, cancellationToken, out var entry)) throw new KeyNotFoundException("Document does not exist.");
        await entry.Guard.WaitAsync(cancellationToken);
        try
        {
            var current = Control(entry);
            var result = DocumentControlReducer.Apply(current, command, _limits);
            if (!result.Changed) return result;
            var metadataDelta = checked(ControlMetadataBytes(result.Current) - ControlMetadataBytes(current));
            var next = AccountedHead(entry, result.Current.Head with { StateVersion = checked(entry.Head.StateVersion + 1) }, metadataDelta);
            DocumentPublicationRules.CheckLimits(next, _limits);
            entry.Coordination = result.Current.Coordination;
            entry.Editors = result.Current.Editors;
            foreach (var partition in result.Current.Partitions) entry.Partitions[partition.Kind].Head = partition;
            entry.MetadataBytes = checked(entry.MetadataBytes + metadataDelta);
            Volatile.Write(ref entry.Head, next);
            return result with { Current = result.Current with { Head = next } };
        }
        finally { entry.Guard.Release(); }
    }

    public async ValueTask<PublicationResult> PublishAsync(PreparedDocumentPublication publication,
        CancellationToken cancellationToken = default)
    {
        EnsureOnline(cancellationToken);
        DocumentPublicationRules.ValidateShape(publication);
        // Only immutable object I/O runs before entering the publication guard.
        var references = ContentStoreReader.UniqueHandles(publication.NewDocumentReferences
            .Concat(publication.Partitions.SelectMany(p => p.AddedElements).Select(e => e.Payload))
            .Concat(publication.Receipt.Response is { } response ? [response] : []));
        var elementKeys = publication.Partitions.SelectMany(p => p.AddedElements).Select(e => e.Payload.Key).ToHashSet(StringComparer.Ordinal);
        await VerifyReferencesAsync(references.Where(h => !elementKeys.Contains(h.Key)), cancellationToken);
        foreach (var partition in publication.Partitions)
            await DocumentPublicationRules.ValidatePartitionPayloadsAsync(partition, _content, _limits, cancellationToken);
        if (!TryDocument(publication.ResourceId, cancellationToken, out var entry)) throw new KeyNotFoundException("Document does not exist.");
        await entry.Guard.WaitAsync(cancellationToken);
        try
        {
            var current = Control(entry);
            if (DocumentPublicationRules.CheckAuthority(current, publication) is { } authorityFailure) return authorityFailure;
            if (entry.Receipts.TryGetValue(publication.Receipt.Operation, out var receipt))
                return DocumentPublicationRules.MatchReceipt(entry.Head, receipt, publication,
                    entry.Partitions.GetValueOrDefault(receipt.Operation.PartitionKind)?.Head);
            var mappings = new Dictionary<IndexedMappingKey, IndexedStorageMapping?>();
            var operationPartition = entry.Partitions.GetValueOrDefault(publication.Receipt.Operation.PartitionKind);
            foreach (var expectation in publication.Fence.ExpectedMappings)
                mappings[expectation.Key] = operationPartition?.Head.StorageIndex is { } index
                    ? Mapping(operationPartition, new(entry.Head.ResourceId, entry.Head.LifecycleGeneration,
                        operationPartition.Head.Kind), index, expectation.Key) : null;
            if (DocumentPublicationRules.CheckFence(current, publication, mappings) is { } failure) return failure;
            foreach (var reference in references)
            {
                if (entry.References.TryGetValue(reference.Key, out var existing) && existing != reference)
                    throw new StorageCorruptionException("Conflicting document content descriptors share a key.");
                _ = checked(entry.ReferenceCounts.GetValueOrDefault(reference.Key) + 1);
            }
            foreach (var part in publication.Partitions)
            {
                if (!entry.Partitions.TryGetValue(part.After.Kind, out var stored) || !EqualMetadata(stored.Head, part.Before))
                    return new(PublicationStatus.Reprepare, entry.Head);
                ValidatePartition(stored, part, part.EffectiveChanges.Scope);
            }
            var fileChanged = publication.Receipt.ContentVersion != entry.Head.ContentVersion;
            var applicationChanged = publication.Revision is not null;
            var acceptedPartition = publication.Partitions.FirstOrDefault(p => p.After.Kind == publication.Receipt.Operation.PartitionKind)?.After
                ?? entry.Partitions.GetValueOrDefault(publication.Receipt.Operation.PartitionKind)?.Head;
            if (publication.Receipt.Operation.Family == SaveOperationFamily.Protocol && publication.Receipt.Operation.PartitionKind != 0 &&
                publication.Receipt.AcceptedStorageIndex != acceptedPartition?.StorageIndex)
                throw new InvalidDataException("The receipt does not identify the accepted partition root.");
            if (fileChanged && (publication.Revision is null || (ulong)publication.Receipt.ContentVersion != (ulong)entry.Head.ContentVersion + 1 ||
                !publication.Partitions.Any(partition => partition.After.Kind == 0) ||
                publication.Package!.Root.StorageIndex != (publication.Partitions.FirstOrDefault(p => p.After.Kind == 0)?.After.StorageIndex ?? entry.Partitions[0].Head.StorageIndex)))
                throw new InvalidDataException("A changed file requires the next application revision.");
            if (applicationChanged && (publication.Package is null || publication.Receipt.RevisionNumber != checked(entry.Head.LatestRevisionNumber + 1)))
                throw new InvalidDataException("An application publication must append the next immutable revision.");
            if (!applicationChanged && (publication.Package is not null || publication.Receipt.RevisionNumber != entry.Head.LatestRevisionNumber))
                throw new InvalidDataException("A package descriptor requires an application revision.");
            if (!fileChanged && publication.Package is { } metadataPackage &&
                metadataPackage with { RevisionNumber = entry.Head.Package.RevisionNumber } != entry.Head.Package)
                throw new InvalidDataException("A metadata revision cannot change the current file package.");
            if (publication.Revision is { } revision && (entry.Revisions.ContainsKey(revision.RevisionNumber) ||
                !EqualMetadata(revision.Partitions.ToArray(), entry.Partitions.Values.Where(p => p.Head.Kind != 2).OrderBy(p => p.Head.Kind)
                    .Select(p => publication.Partitions.FirstOrDefault(n => n.After.Kind == p.Head.Kind)?.After ?? p.Head).ToArray())))
                throw new InvalidDataException("The revision does not capture the newly published heads.");
            var newPhysicalBytes = references.Where(h => !entry.References.ContainsKey(h.Key)).Sum(h => h.Length);
            var added = publication.Partitions.SelectMany(p => p.AddedElements.Select(e => (Part: entry.Partitions[p.After.Kind], Element: e)))
                .Where(p => !p.Part.Elements.ContainsKey(p.Element.Id)).ToArray();
            DocumentDelivery? delivery = null;
            var binding = entry.Head.Delivery;
            if (fileChanged && binding is not null)
            {
                if (publication.DeliveryOperationId is not { } operation || operation == Guid.Empty || entry.Deliveries.Values.Any(d => d.OperationId == operation))
                    throw new InvalidDataException("A bound file publication requires a new stable delivery operation identity.");
                delivery = new(operation, entry.Head.ResourceId, entry.Head.LifecycleGeneration,
                    binding.NextSequence, publication.Receipt.ContentVersion, publication.Package!);
                binding = binding with { NextSequence = checked(binding.NextSequence + 1) };
            }
            var expiry = DocumentControlReducer.ExpireForPublication(current);
            var expiryMetadata = expiry.Changed ? checked(ControlMetadataBytes(expiry.Current) - ControlMetadataBytes(current)) : 0;
            var metadataBytes = checked(PublicationMetadataDelta(entry, publication, delivery) + expiryMetadata);
            var charged = publication with { CounterChange = publication.CounterChange with { StoredBytes = metadataBytes } };
            var counters = DocumentPublicationRules.NextCounters(entry.Head.Counters, charged, newPhysicalBytes,
                added.Length, added.Sum(p => p.Element.Payload.Length), delivery is null ? 0 : 1, delivery?.Package.Length ?? 0);
            var next = expiry.Current.Head with
            {
                StateVersion = checked(entry.Head.StateVersion + 1), Package = publication.Package ?? entry.Head.Package,
                ContentVersion = fileChanged ? publication.Receipt.ContentVersion : entry.Head.ContentVersion,
                LatestRevisionNumber = applicationChanged ? publication.Receipt.RevisionNumber : entry.Head.LatestRevisionNumber,
                ModifiedUtc = fileChanged ? publication.RequestedModifiedUtc ?? current.UtcNow : entry.Head.ModifiedUtc,
                Security = fileChanged ? entry.Head.Security with { ModifiedBy = publication.Admission.Actor } : entry.Head.Security,
                Counters = counters, Delivery = binding,
            };
            next = AccountedHead(entry, next, metadataBytes, newPhysicalBytes);
            DocumentPublicationRules.CheckLimits(next, _limits);
            foreach (var part in publication.Partitions) ApplyPartition(entry.Partitions[part.After.Kind], part);
            if (expiry.Changed)
            {
                entry.Coordination = expiry.Current.Coordination;
                entry.Editors = expiry.Current.Editors;
                entry.Partitions[2].Head = expiry.Current.Partitions.Single(partition => partition.Kind == 2);
            }
            foreach (var reference in references)
            {
                entry.References.TryAdd(reference.Key, reference);
                if (!entry.PendingReferences.Contains(reference.Key))
                    entry.ReferenceCounts[reference.Key] = checked(entry.ReferenceCounts.GetValueOrDefault(reference.Key) + 1);
                entry.PendingReferences.Remove(reference.Key);
            }
            if (publication.Revision is { } accepted) entry.Revisions.Add(accepted.RevisionNumber, accepted);
            entry.Receipts.Add(publication.Receipt.Operation, publication.Receipt);
            if (delivery is not null) entry.Deliveries.Add(delivery.Sequence, delivery);
            entry.MetadataBytes = checked(entry.MetadataBytes + metadataBytes);
            entry.PhysicalBytes = checked(entry.PhysicalBytes + newPhysicalBytes);
            Volatile.Write(ref entry.Head, next);
            return new(PublicationStatus.Accepted, next) { Receipt = publication.Receipt };
        }
        finally { entry.Guard.Release(); }
    }

    private async ValueTask VerifyReferencesAsync(IEnumerable<ContentHandle> handles, CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        foreach (var handle in handles)
        {
            StorageLimits.Check("object bytes", handle.Length, _limits.MaxObjectBytes);
            await using var stream = await _content.OpenReadAsync(handle, cancellationToken);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0; int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
            {
                length = checked(length + count);
                if (length > handle.Length) throw new StorageCorruptionException("Referenced content exceeds its declared length.");
                hash.AppendData(buffer, 0, count);
            }
            if (length != handle.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != handle.Sha256)
                throw new StorageCorruptionException("Referenced content failed integrity verification.");
        }
    }
}
