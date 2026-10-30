using System.Collections.Immutable;
using System.Text.Json;
using CellBridge.FssHttpB;

namespace CellBridge.Storage.Abstractions;

/// <summary>Provider-independent publication checks. Physical reference accounting remains provider owned.</summary>
public static class DocumentPublicationRules
{
    /// <summary>Checks untrusted physical bytes and their derived indexes before entering a document transaction.</summary>
    public static async ValueTask ValidatePartitionPayloadsAsync(PreparedPartitionPublication partition,
        IContentStore content, StorageLimits limits, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scope = partition.EffectiveChanges.Scope;
        var revisions = partition.Revisions.ToLookup(r => r.ElementId);
        var cells = partition.Cells.ToLookup(c => c.ElementId);
        var storageManifests = partition.StorageManifests.ToLookup(s => s.ElementId);
        var objects = partition.Objects.ToLookup(o => o.GroupElement);
        var owners = partition.AddedElements.Select(e => e.Id).ToHashSet();
        if (revisions.Any(g => !owners.Contains(g.Key)) ||
            cells.Any(g => !owners.Contains(g.Key)) || storageManifests.Any(g => !owners.Contains(g.Key)) ||
            objects.Any(g => !owners.Contains(g.Key)))
            throw new InvalidDataException("Prepared indexes must describe admitted immutable elements.");
        // Check aliases and every per-object bound before issuing any content I/O.
        // Only the small handles/metadata span the publication; payload buffers do not.
        foreach (var handle in ContentStoreReader.UniqueHandles(partition.AddedElements.Select(element => element.Payload)))
        {
            if (handle.Length < 0 || handle.Length > Array.MaxLength)
                throw new StorageCorruptionException("Referenced content length cannot be represented by a byte array.");
            StorageLimits.Check("content read bytes", handle.Length, limits.MaxObjectBytes);
        }
        foreach (var element in partition.AddedElements)
            if (element.Type == (uint)DataElementType.StorageIndexDataElementData)
                throw new InvalidDataException("Storage-index transmission envelopes are not canonical immutable elements.");
        foreach (var batch in PayloadBatches(partition.AddedElements))
        {
            var payloads = await content.ReadManyVerifiedAsync(batch.Select(element => element.Payload), limits.MaxObjectBytes, cancellationToken);
            foreach (var element in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = new DataElement((DataElementType)element.Type, IndexedDocumentGraph.Restore(element.Id),
                    new SerialNumber(element.Serial.Guid, element.Serial.Value)) { Data = payloads[element.Payload.Key] };
                var metadata = IndexedDocumentGraph.ReadMetadata(scope, data);
                var serials = metadata.Mappings.Select(m => m.Serial).ToImmutableArray();
                if (element.MappingSerials.IsDefault || !element.MappingSerials.AsSpan().SequenceEqual(serials.AsSpan()))
                    throw new InvalidDataException("Element mapping serials disagree with its physical payload.");
                Compare(metadata.Revision is { } revision ? [revision] : [], revisions[element.Id], r => r.ElementId);
                Compare(metadata.CellManifest is { } cell ? [cell] : [], cells[element.Id], c => c.ElementId);
                Compare(metadata.StorageManifest is { } storage ? [storage] : [], storageManifests[element.Id], s => s.ElementId);
                Compare(metadata.Objects, objects[element.Id], o => o.Ordinal);
            }
            payloads = null!; // Do not retain the preceding batch across the next read.
        }

        static void Compare<T, TKey>(IEnumerable<T> expected, IEnumerable<T> actual, Func<T, TKey> key) where TKey : notnull
        {
            var entries = expected.ToDictionary(key);
            foreach (var value in actual)
            {
                if (!entries.Remove(key(value), out var matching) || !JsonSerializer.SerializeToUtf8Bytes(matching).AsSpan()
                    .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(value)))
                    throw new InvalidDataException("Prepared graph indexes disagree with the immutable physical payload.");
            }
            if (entries.Count != 0) throw new InvalidDataException("Prepared graph indexes omit physical element metadata.");
        }
    }

    private static IEnumerable<GraphElementState[]> PayloadBatches(ImmutableArray<GraphElementState> elements)
    {
        const long maximumBytes = 1024 * 1024;
        for (var offset = 0; offset < elements.Length;)
        {
            var end = offset;
            long bytes = 0;
            while (end < elements.Length && end - offset < 64)
            {
                var length = elements[end].Payload.Length;
                if (end != offset && length > maximumBytes - bytes) break;
                bytes += length; end++;
                if (bytes >= maximumBytes) break;
            }
            // A single larger admitted object is indivisible and is read alone.
            yield return elements.AsSpan(offset, end - offset).ToArray();
            offset = end;
        }
    }

    public static void ValidateShape(PreparedDocumentPublication value)
    {
        if (value.ResourceId == Guid.Empty || value.Admission.ResourceId != value.ResourceId ||
            value.Admission.LifecycleGeneration != value.Fence.LifecycleGeneration ||
            value.Receipt.Operation.LifecycleGeneration != value.Fence.LifecycleGeneration ||
            value.Receipt.OwnerSubject != value.Admission.Actor.Subject ||
            string.IsNullOrEmpty(value.Receipt.Digest) || string.IsNullOrEmpty(value.Receipt.Operation.OperationKey) ||
            value.Partitions.IsDefault || value.Fence.ExpectedMappings.IsDefault ||
            value.Partitions.Select(p => p.After.Kind).Distinct().Count() != value.Partitions.Length)
            throw new InvalidDataException("Invalid publication identity, receipt, or partition set.");
        if (value.CounterChange.StoredBytes < 0)
            throw new InvalidDataException("Publication metadata charge cannot be negative.");
        foreach (var part in value.Partitions)
        {
            var scope = part.EffectiveChanges.Scope;
            if (scope.ResourceId != value.ResourceId || scope.LifecycleGeneration != value.Fence.LifecycleGeneration ||
                scope.TargetPartition != part.After.Kind || part.Before.Kind != part.After.Kind ||
                part.Before.PublicationSequence != part.EffectiveChanges.PreviousSequence ||
                part.After.PublicationSequence != part.EffectiveChanges.Sequence ||
                part.After.PublicationSequence <= part.Before.PublicationSequence ||
                part.AddedElements.IsDefault || part.RetiredElements.IsDefault || part.Mappings.IsDefault ||
                part.Revisions.IsDefault || part.Objects.IsDefault || part.EffectiveChanges.Changes.IsDefault ||
                part.AddedElements.Select(e => e.Id).Distinct().Count() != part.AddedElements.Length ||
                part.RetiredElements.Distinct().Count() != part.RetiredElements.Length ||
                part.Mappings.Any(m => m.Scope != scope) || part.Revisions.Any(r => r.Scope != scope) ||
                part.Objects.Any(o => o.Scope != scope) || part.Cells.Any(c => c.Scope != scope) ||
                part.StorageManifests.Any(s => s.Scope != scope) ||
                part.EffectiveChanges.Changes.Any(c => c.Key.Scope != scope || c.Value is { } binding && binding.Key != c.Key))
                throw new InvalidDataException("Invalid or cross-scope partition publication.");
            var claims = part.Mappings.Select(claim => (claim.Key, claim.Target, claim.Serial)).ToHashSet();
            if (part.MappingChanges.IsDefault || part.MappingChanges.Select(c => c.Key).Distinct().Count() != part.MappingChanges.Length ||
                part.After.StorageIndex is not null && part.After.StorageIndexSerial is null ||
                part.MappingChanges.Any(change => change.Value is { } value && !claims.Contains((change.Key, value.Target, value.Serial))))
                throw new InvalidDataException("Applied mapping changes require matching immutable serial claims and a logical root serial.");
        }
        if (value.Package is { } package && (package.ResourceId != value.ResourceId ||
            package.LifecycleGeneration != value.Fence.LifecycleGeneration || package.Root.Scope.ResourceId != value.ResourceId ||
            package.Root.Scope.LifecycleGeneration != value.Fence.LifecycleGeneration || package.Root.Scope.TargetPartition != 0 ||
            package.RevisionNumber != value.Receipt.RevisionNumber || package.Length < 0 || package.Sha256.Length != 64 ||
            package.Sha256.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')) ||
            package.ReconstructionVersion != DocumentPackage.CurrentReconstructionVersion))
            throw new InvalidDataException("Invalid package identity.");
        if (value.Revision is { } revision && (revision.ResourceId != value.ResourceId ||
            revision.LifecycleGeneration != value.Fence.LifecycleGeneration || revision.Package != value.Package ||
            revision.RevisionNumber != value.Receipt.RevisionNumber || revision.ContentVersion != value.Receipt.ContentVersion))
            throw new InvalidDataException("Invalid application revision identity.");
    }

    public static PublicationResult MatchReceipt(DocumentHead current, DocumentOperationReceipt existing,
        PreparedDocumentPublication publication, PartitionHead? partition = null)
    {
        if (existing.OwnerSubject != publication.Admission.Actor.Subject || existing.Operation != publication.Receipt.Operation ||
            existing.Digest != publication.Receipt.Digest)
            return Error(current, "InvalidRequest", "The operation identity was already used by another actor or request.");
        if (existing.Operation.Family == SaveOperationFamily.Protocol &&
            (existing.Operation.PartitionKind == 0 && existing.ContentVersion != current.ContentVersion ||
            existing.Operation.PartitionKind != 0 && existing.AcceptedStorageIndex != partition?.StorageIndex))
            return Error(current, "CoherencyFailure", "The accepted operation has been superseded.");
        return new(PublicationStatus.Replay, current) { Receipt = existing };
    }

    public static PublicationResult? CheckAuthority(DocumentControlRead current, PreparedDocumentPublication publication)
    {
        if (current.Head.IsDeleted || current.Head.LifecycleGeneration != publication.Fence.LifecycleGeneration ||
            publication.Admission.AuthorityGeneration != current.Head.AuthorityGeneration ||
            publication.Admission.Binding != current.Head.Security.AuthorizationPolicy)
            return Error(current.Head, "FileUnauthorizedAccess", "The document lifecycle or authority changed.");
        if (publication.Admission.StateVersion != current.Head.StateVersion)
            return new(PublicationStatus.Reprepare, current.Head);
        return DocumentControlReducer.Access(current, publication.Admission).HasFlag(DocumentAccess.Write)
            ? null : Error(current.Head, "FileUnauthorizedAccess", "Current authorization validation failed.");
    }

    public static PublicationResult? CheckFence(DocumentControlRead current, PreparedDocumentPublication publication,
        IReadOnlyDictionary<IndexedMappingKey, IndexedStorageMapping?> currentMappings)
    {
        var head = current.Head;
        var fence = publication.Fence;
        if (CheckAuthority(current, publication) is { } denied) return denied;
        var lockError = publication.Receipt.Operation.Family == SaveOperationFamily.WholeFile
            ? DocumentControlReducer.ValidateWholeFile(current, publication.Admission, publication.HostLockToken)
            : DocumentControlReducer.ValidateWrite(current, publication.Admission, publication.LockAttributes);
        if (lockError is not null)
            return Error(head, lockError, "Current lock validation failed.");
        if (fence.PreparedCoordination is { } preparedCoordination)
        {
            var normalized = DocumentControlReducer.ExpireForPublication(current).Current.Coordination;
            if (CoordinationFencing.Capture(preparedCoordination with { Generation = 0 }, normalized with { Generation = 0 }).Generation != 0)
                return Error(head, "InvalidCoauthSession", "The prepared lease authority changed.");
        }
        if (head.AuthorityGeneration != fence.AuthorityGeneration) return new(PublicationStatus.Reprepare, head);
        if (fence.ExpectedEtag is { } etag && etag != head.Etag)
            return Error(head, "CellRequestFail", "The selected file ETag changed.");
        foreach (var expected in fence.ExpectedMappings)
            if (currentMappings.GetValueOrDefault(expected.Key)?.Target != expected.Expected?.Target)
                return expected.EnforceCoherency ? Error(head, "CoherencyFailure", "An affected mapping changed.")
                    : new(PublicationStatus.Reprepare, head);
        if (fence.ExpectedRevisionNumber is { } number && number != head.LatestRevisionNumber)
            return Error(head, "CoherencyFailure", "The selected file version changed.");
        if (fence.PreparedStateVersion != head.StateVersion || fence.PreparedContentVersion != head.ContentVersion ||
            fence.CoordinationGeneration != current.Coordination.Generation)
            return new(PublicationStatus.Reprepare, head);
        return null;
    }

    public static DocumentCounters NextCounters(DocumentCounters current, PreparedDocumentPublication publication,
        long uniquePhysicalBytes, int addedElements, long addedElementBytes,
        int pendingDeliveryCount = 0, long pendingDeliveryBytes = 0) => current with
    {
        StoredBytes = checked(current.StoredBytes + publication.CounterChange.StoredBytes + uniquePhysicalBytes),
        GraphBytes = checked(current.GraphBytes + addedElementBytes),
        GraphElements = checked(current.GraphElements + addedElements),
        Revisions = checked(current.Revisions + (publication.Revision is null ? 0 : 1)),
        SaveReceipts = checked(current.SaveReceipts + (publication.Receipt.Operation.Family == SaveOperationFamily.Protocol ? 1 : 0)),
        RestoreReceipts = checked(current.RestoreReceipts + (publication.Receipt.Operation.Family == SaveOperationFamily.Restore ? 1 : 0)),
        WholeFileSaveReceipts = checked(current.WholeFileSaveReceipts + (publication.Receipt.Operation.Family == SaveOperationFamily.WholeFile ? 1 : 0)),
        PendingDeliveries = checked(current.PendingDeliveries + pendingDeliveryCount),
        PendingDeliveryBytes = checked(current.PendingDeliveryBytes + pendingDeliveryBytes),
    };

    public static void CheckLimits(DocumentHead head, StorageLimits limits)
    {
        var c = head.Counters;
        StorageLimits.Check("document bytes", head.Package.Length, limits.MaxDocumentBytes);
        StorageLimits.Check("document stored bytes", c.StoredBytes, limits.MaxDocumentStoredBytes);
        StorageLimits.Check("graph bytes", c.GraphBytes, limits.MaxGraphBytes);
        StorageLimits.Check("graph elements", c.GraphElements, limits.MaxGraphElements);
        StorageLimits.Check("save receipts", c.SaveReceipts, limits.MaxSaveReceipts);
        StorageLimits.Check("restore receipts", c.RestoreReceipts, limits.MaxRestoreReceipts);
        StorageLimits.Check("whole-file save receipts", c.WholeFileSaveReceipts, limits.MaxWholeFileSaveReceipts);
        StorageLimits.Check("history revisions", c.Revisions, limits.MaxHistoryRevisions);
        StorageLimits.Check("retired path keys", c.RetiredPaths, limits.MaxRetiredPathKeys);
        StorageLimits.Check("pending external revisions", c.PendingDeliveries, limits.MaxPendingExternalRevisions);
        StorageLimits.Check("pending external bytes", c.PendingDeliveryBytes, limits.MaxPendingExternalBytes);
    }

    private static PublicationResult Error(DocumentHead head, string code, string message) =>
        new(PublicationStatus.ProtocolError, head) { ErrorCode = code, ErrorMessage = message };
}
