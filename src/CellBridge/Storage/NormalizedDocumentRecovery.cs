using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CellBridge.Storage.Abstractions;

public sealed record NormalizedPartitionRoot(PartitionHead Head, IndexedReplayPosition ReplayPosition);
public sealed record ScopedGraphElement(IndexedGraphScope Scope, GraphElementState Element);
public sealed record NormalizedElementVisibility(IndexedGraphScope Scope, ExtendedId Element, long StartSequence, long? EndSequence);
public sealed record NormalizedMappingInterval(IndexedStorageMapping Mapping, long StartSequence, long? EndSequence);
public sealed record NormalizedMappingRoot(IndexedGraphScope Scope, ExtendedId Id, SerialId Serial, long Sequence)
{
    public required PartitionHead Head { get; init; }
}
public sealed record NormalizedContentReference(ContentHandle Handle, long ReferenceCount)
{
    /// <summary>A charged staging reservation; its physical bytes may not exist yet.</summary>
    public bool IsPending { get; init; }
    /// <summary>Logical ownership metadata charged when content enters through document staging.</summary>
    public long AdmissionBytes { get; init; }
    public static long StagingMetadataBytes(ContentHandle handle) =>
        checked(NormalizedDocumentRecovery.SerializedBytes(handle) + 16 + sizeof(long) * 2 + sizeof(bool));
}

/// <summary>Complete logical state for one document, including intermediate protocol publications.</summary>
public sealed record NormalizedDocumentSnapshot(DocumentControlRead Current,
    ImmutableArray<NormalizedPartitionRoot> Partitions,
    ImmutableArray<ScopedGraphElement> Elements,
    ImmutableArray<NormalizedElementVisibility> Visibility,
    ImmutableArray<IndexedRevisionManifest> GraphRevisions,
    ImmutableArray<IndexedObjectDefinition> Objects,
    ImmutableArray<IndexedCellManifest> Cells,
    ImmutableArray<IndexedStorageManifest> StorageManifests,
    ImmutableArray<NormalizedMappingInterval> MappingIntervals,
    ImmutableArray<NormalizedMappingRoot> MappingRoots,
    ImmutableArray<IndexedStorageMapping> MappingClaims,
    ImmutableArray<IndexedObjectBinding> CurrentBindings,
    ImmutableArray<IndexedBindingDelta> BindingDeltas,
    ImmutableArray<IndexedGraphCheckpoint> Checkpoints,
    ImmutableArray<DocumentRevisionRecord> ApplicationRevisions,
    ImmutableArray<DocumentOperationReceipt> Receipts,
    ImmutableArray<DocumentDelivery> Deliveries,
    ImmutableArray<string> PathClaims,
    ImmutableArray<NormalizedContentReference> ContentReferences)
{
    public const int CurrentFormat = 1;
    [JsonRequired]
    public int FormatVersion { get; init; } = CurrentFormat;
}

/// <summary>Explicit offline recovery. Ordinary requests do not call these operations.</summary>
public interface IIndexedRepositoryRecovery : IProviderRecoveryStore
{
    ValueTask<NormalizedDocumentSnapshot?> ExportDocumentAsync(Guid resourceId, CancellationToken cancellationToken = default);

}

public static class NormalizedDocumentRecovery
{
    public static long SerializedBytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value).LongLength;
    public static long CanonicalHeadBytes(DocumentHead head) => SerializedBytes(head with
        { Counters = head.Counters with { StoredBytes = 0 } });
    private static long Bytes<T>(T value) => SerializedBytes(value);
    private static bool Same<T>(T first, T second) => JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);

    /// <summary>Logical UTF-8 JSON and path data plus uniquely owned physical references. Row/index overhead is excluded.</summary>
    public static long StoredBytes(NormalizedDocumentSnapshot snapshot)
    {
        long total = snapshot.ContentReferences.Sum(reference => checked(reference.Handle.Length + reference.AdmissionBytes));
        total = checked(total + CanonicalHeadBytes(snapshot.Current.Head) + Bytes(snapshot.Current.Coordination));
        foreach (var editor in snapshot.Current.Editors) total = checked(total + Bytes(editor));
        foreach (var partition in snapshot.Partitions) total = checked(total + Bytes(partition.Head));
        foreach (var element in snapshot.Elements) total = checked(total + Bytes(element.Element));
        foreach (var revision in snapshot.GraphRevisions) total = checked(total + Bytes(revision));
        foreach (var obj in snapshot.Objects) total = checked(total + Bytes(obj));
        foreach (var cell in snapshot.Cells) total = checked(total + Bytes(cell));
        foreach (var manifest in snapshot.StorageManifests) total = checked(total + Bytes(manifest));
        foreach (var binding in snapshot.CurrentBindings) total = checked(total + Bytes(binding));
        foreach (var delta in snapshot.BindingDeltas)
            foreach (var change in delta.Changes) total = checked(total + Bytes(change));
        foreach (var checkpoint in snapshot.Checkpoints)
        {
            total = checked(total + 4); // The empty-checkpoint marker is the UTF-8 JSON value null.
            foreach (var binding in checkpoint.Bindings) total = checked(total + Bytes(binding));
        }
        foreach (var revision in snapshot.ApplicationRevisions) total = checked(total + Bytes(revision));
        foreach (var receipt in snapshot.Receipts) total = checked(total + Bytes(receipt));
        foreach (var delivery in snapshot.Deliveries) total = checked(total + Bytes(delivery));
        foreach (var interval in snapshot.MappingIntervals) total = checked(total + Bytes(interval.Mapping));
        foreach (var claim in snapshot.MappingClaims) total = checked(total + Bytes(claim));
        foreach (var root in snapshot.MappingRoots) total = checked(total + Bytes(root.Head));
        foreach (var partition in snapshot.Partitions)
            foreach (var interval in snapshot.MappingIntervals.Where(interval => interval.Mapping.Scope.TargetPartition == partition.Head.Kind &&
                interval.StartSequence <= partition.Head.PublicationSequence && (interval.EndSequence is null || interval.EndSequence > partition.Head.PublicationSequence)))
                total = checked(total + Bytes(interval.Mapping));
        foreach (var path in snapshot.PathClaims) total = checked(total + Encoding.UTF8.GetByteCount(path));
        return total;
    }

    public static void Validate(NormalizedDocumentSnapshot snapshot, StorageLimits limits, IndexedCheckpointPolicy? checkpointPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        checkpointPolicy ??= IndexedCheckpointPolicy.Default; checkpointPolicy.Validate(); limits.Validate();
        var head = snapshot.Current.Head;
        if (snapshot.FormatVersion != NormalizedDocumentSnapshot.CurrentFormat || head.ResourceId == Guid.Empty || head.LifecycleGeneration <= 0 ||
            head.PathKey != StorageIds.PathKey(head.Path) || head.CoordinationGeneration != snapshot.Current.Coordination.Generation || snapshot.Current.Editors.IsDefault || snapshot.Current.Partitions.IsDefault || snapshot.Partitions.IsDefaultOrEmpty || snapshot.Elements.IsDefault || snapshot.Visibility.IsDefault ||
            snapshot.GraphRevisions.IsDefault || snapshot.Objects.IsDefault || snapshot.Cells.IsDefault || snapshot.StorageManifests.IsDefault ||
            snapshot.MappingIntervals.IsDefault || snapshot.MappingRoots.IsDefault || snapshot.MappingClaims.IsDefault || snapshot.CurrentBindings.IsDefault ||
            snapshot.BindingDeltas.IsDefault || snapshot.Checkpoints.IsDefault || snapshot.ApplicationRevisions.IsDefaultOrEmpty || snapshot.Receipts.IsDefault ||
            snapshot.Deliveries.IsDefault || snapshot.PathClaims.IsDefault || snapshot.ContentReferences.IsDefault)
            throw new StorageCorruptionException("Unsupported or incomplete normalized document snapshot.");
        Unique(snapshot.Partitions, partition => partition.Head.Kind);
        Unique(snapshot.Current.Editors, editor => editor.ClientId);
        Unique(snapshot.ContentReferences, reference => reference.Handle.Key);
        var partitions = snapshot.Partitions.ToDictionary(partition => partition.Head.Kind);
        if (!partitions.ContainsKey(0) || !Same(snapshot.Current.Partitions.OrderBy(partition => partition.Kind).ToArray(),
            snapshot.Partitions.Select(partition => partition.Head).OrderBy(partition => partition.Kind).ToArray()))
            throw new StorageCorruptionException("Recovery control and partition roots disagree.");
        void Scope(IndexedGraphScope scope)
        {
            if (scope.ResourceId != head.ResourceId || scope.LifecycleGeneration != head.LifecycleGeneration || !partitions.ContainsKey(scope.TargetPartition))
                throw new StorageCorruptionException("A normalized recovery record crosses its document scope.");
        }
        var elements = new Dictionary<(int, ExtendedId), GraphElementState>();
        var references = snapshot.ContentReferences.ToDictionary(reference => reference.Handle.Key, StringComparer.Ordinal);
        foreach (var reference in snapshot.ContentReferences)
        {
            if (reference.ReferenceCount <= 0 || reference.Handle.Length < 0 || string.IsNullOrEmpty(reference.Handle.Key) || reference.Handle.Sha256.Length != 64)
                throw new StorageCorruptionException("Invalid document content reference.");
            if (reference.AdmissionBytes != 0 && reference.AdmissionBytes != NormalizedContentReference.StagingMetadataBytes(reference.Handle) ||
                reference.IsPending && reference.AdmissionBytes == 0)
                throw new StorageCorruptionException("Invalid document staging metadata charge.");
            StorageLimits.Check("object bytes", reference.Handle.Length, limits.MaxObjectBytes);
        }
        void Reference(ContentHandle handle)
        {
            if (!references.TryGetValue(handle.Key, out var reference) || reference.Handle != handle || reference.IsPending)
                throw new StorageCorruptionException("A recovery record references absent or inconsistent physical content.");
        }
        foreach (var element in snapshot.Elements)
        {
            Scope(element.Scope);
            if (element.Element.MappingSerials.IsDefault || !elements.TryAdd((element.Scope.TargetPartition, element.Element.Id), element.Element) ||
                element.Element.Type == (uint)CellBridge.FssHttpB.DataElementType.StorageIndexDataElementData)
                throw new StorageCorruptionException("Duplicate or transient immutable graph element in recovery.");
            Reference(element.Element.Payload);
        }
        foreach (var group in snapshot.Visibility.GroupBy(value => (value.Scope, value.Element)))
        {
            Scope(group.Key.Scope);
            if (!elements.ContainsKey((group.Key.Scope.TargetPartition, group.Key.Element))) throw new StorageCorruptionException("Visibility names an absent element.");
            ValidateIntervals(group.Select(value => (value.StartSequence, value.EndSequence)));
        }
        foreach (var group in snapshot.MappingIntervals.GroupBy(value => (value.Mapping.Scope, value.Mapping.Key)))
        {
            Scope(group.Key.Scope); ValidateIntervals(group.Select(value => (value.StartSequence, value.EndSequence)));
            foreach (var interval in group)
                if (interval.Mapping.Target != new ExtendedId(0, Guid.Empty) &&
                    !elements.ContainsKey((interval.Mapping.Scope.TargetPartition, interval.Mapping.Target)))
                    throw new StorageCorruptionException("A mapping target is absent.");
        }
        Unique(snapshot.GraphRevisions, revision => (revision.Scope, revision.ElementId));
        Unique(snapshot.Objects, obj => (obj.Scope, obj.GroupElement, obj.Ordinal));
        Unique(snapshot.Cells, cell => (cell.Scope, cell.ElementId));
        Unique(snapshot.StorageManifests, manifest => (manifest.Scope, manifest.ElementId));
        foreach (var scope in snapshot.GraphRevisions.Select(value => value.Scope).Concat(snapshot.Objects.Select(value => value.Scope))
            .Concat(snapshot.Cells.Select(value => value.Scope)).Concat(snapshot.StorageManifests.Select(value => value.Scope))) Scope(scope);
        foreach (var record in snapshot.GraphRevisions.Select(value => (value.Scope, value.ElementId)).Concat(snapshot.Cells.Select(value => (value.Scope, value.ElementId)))
            .Concat(snapshot.StorageManifests.Select(value => (value.Scope, value.ElementId))).Concat(snapshot.Objects.Select(value => (value.Scope, value.GroupElement))))
            if (!elements.ContainsKey((record.Scope.TargetPartition, record.Item2))) throw new StorageCorruptionException("Indexed graph metadata has no immutable payload.");
        Unique(snapshot.MappingRoots, root => (root.Scope, root.Id));
        var mappingRoots = snapshot.MappingRoots.ToDictionary(root => (root.Scope, root.Id));
        var mappingHistory = snapshot.MappingIntervals.GroupBy(value => (value.Mapping.Scope, value.Mapping.Key))
            .ToDictionary(group => group.Key, group => group.OrderBy(value => value.StartSequence).ToArray());
        var indexedCells = snapshot.Cells.ToDictionary(value => (value.Scope, value.ElementId));
        var indexedRevisions = snapshot.GraphRevisions.ToDictionary(value => (value.Scope, value.ElementId));
        var indexedManifests = snapshot.StorageManifests.ToDictionary(value => (value.Scope, value.ElementId));
        var distinguishedRoot = new ExtendedId(2, CellBridge.FssHttpB.StorageManifestBuilder.RootExtendedGuid);
        IndexedStorageMapping SelectedMapping(IndexedGraphScope scope, IndexedMappingKey key, long sequence)
        {
            if (!mappingHistory.TryGetValue((scope, key), out var intervals))
                throw new StorageCorruptionException("A package root has no corresponding selected mapping.");
            int low = 0, high = intervals.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (intervals[middle].StartSequence <= sequence) low = middle + 1; else high = middle;
            }
            if (low == 0 || intervals[low - 1].EndSequence is { } end && end <= sequence)
                throw new StorageCorruptionException("A package root mapping is outside its publication interval.");
            return intervals[low - 1].Mapping;
        }
        foreach (var root in snapshot.MappingRoots)
        {
            Scope(root.Scope);
            if (root.Sequence < 0 || root.Sequence > partitions[root.Scope.TargetPartition].Head.PublicationSequence ||
                root.Head.Kind != root.Scope.TargetPartition || root.Head.StorageIndex != root.Id || root.Head.StorageIndexSerial != root.Serial ||
                root.Head.PublicationSequence != root.Sequence || root.Head.Identity != partitions[root.Scope.TargetPartition].Head.Identity)
                throw new StorageCorruptionException("Invalid or inconsistent immutable logical mapping root header.");
        }
        Unique(snapshot.MappingClaims, claim => (claim.Scope, claim.Serial));
        foreach (var claim in snapshot.MappingClaims) Scope(claim.Scope);
        Unique(snapshot.CurrentBindings, binding => binding.Key);
        Unique(snapshot.BindingDeltas, delta => (delta.Scope, delta.Sequence));
        Unique(snapshot.Checkpoints, checkpoint => (checkpoint.Scope, checkpoint.Sequence));
        var objectDefinitions = snapshot.Objects.ToDictionary(value => (value.Scope, value.GroupElement, value.Ordinal));
        void Binding(IndexedObjectBinding binding)
        {
            Scope(binding.Key.Scope);
            if (binding.ObjectOrdinal < 0 || !objectDefinitions.TryGetValue((binding.Key.Scope, binding.GroupElement, binding.ObjectOrdinal), out var definition) ||
                definition.ObjectId != binding.Key.ObjectId || definition.ObjectPartitionId != binding.Key.ObjectPartitionId)
                throw new StorageCorruptionException("A resolved recovery binding has no matching immutable object definition.");
        }
        foreach (var binding in snapshot.CurrentBindings) Binding(binding);
        foreach (var checkpoint in snapshot.Checkpoints)
            foreach (var binding in checkpoint.Bindings) Binding(binding);
        foreach (var delta in snapshot.BindingDeltas)
            foreach (var change in delta.Changes)
                if (change.Value is { } binding) Binding(binding);
        foreach (var checkpoint in snapshot.Checkpoints) { Scope(checkpoint.Scope); _ = IndexedDocumentGraph.Replay(checkpoint, [], checkpoint.Sequence, checkpointPolicy); }
        foreach (var delta in snapshot.BindingDeltas)
        { Scope(delta.Scope); _ = IndexedDocumentGraph.RequiresCheckpoint(new(0, 0, 0), delta, new(int.MaxValue, long.MaxValue)); }
        foreach (var partition in snapshot.Partitions)
        {
            var scope = new IndexedGraphScope(head.ResourceId, head.LifecycleGeneration, partition.Head.Kind);
            var sequence = partition.Head.PublicationSequence;
            var visible = snapshot.Visibility.Where(value => value.Scope == scope && value.StartSequence <= sequence && (value.EndSequence is null || value.EndSequence > sequence))
                .Select(value => elements[(scope.TargetPartition, value.Element)]).ToArray();
            if (visible.Length != partition.Head.ElementCount || visible.Sum(element => element.Payload.Length) != partition.Head.ElementBytes)
                throw new StorageCorruptionException("Current graph catalog counters disagree with recovery intervals.");
            var checkpoint = snapshot.Checkpoints.Where(value => value.Scope == scope && value.Sequence <= sequence).OrderByDescending(value => value.Sequence).FirstOrDefault()
                ?? throw new StorageCorruptionException("A partition has no metadata checkpoint.");
            var tail = snapshot.BindingDeltas.Where(value => value.Scope == scope && value.Sequence > checkpoint.Sequence && value.Sequence <= sequence).OrderBy(value => value.Sequence).ToArray();
            var bindings = IndexedDocumentGraph.Replay(checkpoint, tail, sequence, checkpointPolicy);
            var current = IndexedDocumentGraph.Checkpoint(scope, sequence, snapshot.CurrentBindings.Where(value => value.Key.Scope == scope));
            if (!Same(IndexedDocumentGraph.Checkpoint(scope, sequence, bindings), current) ||
                partition.ReplayPosition != new IndexedReplayPosition(checkpoint.Sequence, tail.Length, tail.Sum(value => value.EncodedBytes)))
                throw new StorageCorruptionException("Current bindings or replay counters disagree with immutable history.");
            if (partition.Head.StorageIndex is { } index && !snapshot.MappingRoots.Any(root => root.Scope == scope && root.Id == index && root.Serial == partition.Head.StorageIndexSerial))
                throw new StorageCorruptionException("Current partition logical mapping root is absent.");
        }
        Unique(snapshot.ApplicationRevisions, revision => revision.RevisionNumber);
        foreach (var revision in snapshot.ApplicationRevisions)
        {
            if (revision.ResourceId != head.ResourceId || revision.LifecycleGeneration != head.LifecycleGeneration || revision.RevisionNumber == 0)
                throw new StorageCorruptionException("An application revision crosses its recovery identity.");
            Package(revision.Package);
        }
        void Package(DocumentPackage package)
        {
            StorageLimits.Check("document bytes", package.Length, limits.MaxDocumentBytes);
            Scope(package.Root.Scope);
            if (package.ResourceId != head.ResourceId || package.LifecycleGeneration != head.LifecycleGeneration || package.Root.Scope.TargetPartition != 0 ||
                package.RevisionNumber == 0 || package.ReconstructionVersion != DocumentPackage.CurrentReconstructionVersion || package.Length < 0 ||
                package.Sha256.Length != 64 || package.Sha256.Any(value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                !mappingRoots.TryGetValue((package.Root.Scope, package.Root.StorageIndex), out var logicalRoot))
                throw new StorageCorruptionException("A package has no corresponding immutable mapping root.");
            var scope = package.Root.Scope;
            var cellMapping = SelectedMapping(scope, new(IndexedMappingKind.Cell, Cell: package.Root.Cell), logicalRoot.Sequence);
            var revisionMapping = SelectedMapping(scope, new(IndexedMappingKind.Revision, Revision: package.Root.Revision), logicalRoot.Sequence);
            var manifestMapping = SelectedMapping(scope, new(IndexedMappingKind.Manifest), logicalRoot.Sequence);
            if (!indexedCells.TryGetValue((scope, cellMapping.Target), out var cell) || cell.RevisionId != package.Root.Revision ||
                !indexedRevisions.TryGetValue((scope, revisionMapping.Target), out var revision) || revision.RevisionId != package.Root.Revision ||
                !revision.Roots.Any(root => root.Root == distinguishedRoot && root.Object == package.Root.RootObject) ||
                !indexedManifests.TryGetValue((scope, manifestMapping.Target), out var manifest) ||
                manifest.Schema != CellBridge.FssHttpB.StorageManifestBuilder.StorageManifestSchemaGuid ||
                !manifest.Roots.Any(root => root.Root == distinguishedRoot && root.Cell == package.Root.Cell))
                throw new StorageCorruptionException("A package descriptor disagrees with its selected immutable cell, revision or file root.");
        }
        Package(head.Package);
        if (!snapshot.ApplicationRevisions.Any(revision => revision.RevisionNumber == head.LatestRevisionNumber && revision.Package == head.Package && revision.ContentVersion == head.ContentVersion))
            throw new StorageCorruptionException("The current package disagrees with the latest application revision.");
        Unique(snapshot.Receipts, receipt => receipt.Operation);
        foreach (var receipt in snapshot.Receipts)
        {
            if (receipt.Operation.LifecycleGeneration != head.LifecycleGeneration || receipt.OwnerSubject is not null && string.IsNullOrWhiteSpace(receipt.OwnerSubject) ||
                string.IsNullOrWhiteSpace(receipt.Digest) || !Enum.IsDefined(receipt.Operation.Family)) throw new StorageCorruptionException("Invalid recovery receipt identity.");
            if (receipt.Response is { } response) Reference(response);
        }
        Unique(snapshot.Deliveries, delivery => delivery.OperationId); Unique(snapshot.Deliveries, delivery => delivery.Sequence);
        foreach (var delivery in snapshot.Deliveries)
        { if (delivery.ResourceId != head.ResourceId || delivery.LifecycleGeneration != head.LifecycleGeneration) throw new StorageCorruptionException("Invalid recovery delivery identity."); Package(delivery.Package); }
        if (snapshot.PathClaims.Distinct(StringComparer.Ordinal).Count() != snapshot.PathClaims.Length || snapshot.PathClaims.Any(path => path != StorageIds.PathKey(path)))
            throw new StorageCorruptionException("Duplicate or non-canonical recovery filename claims.");
        var counters = head.Counters;
        if (counters.GraphElements != snapshot.Elements.Length || counters.GraphBytes != snapshot.Elements.Sum(value => value.Element.Payload.Length) ||
            counters.Revisions != snapshot.ApplicationRevisions.Length || counters.SaveReceipts != snapshot.Receipts.Count(value => value.Operation.Family == SaveOperationFamily.Protocol) ||
            counters.RestoreReceipts != snapshot.Receipts.Count(value => value.Operation.Family == SaveOperationFamily.Restore) ||
            counters.WholeFileSaveReceipts != snapshot.Receipts.Count(value => value.Operation.Family == SaveOperationFamily.WholeFile) ||
            counters.PendingDeliveries != snapshot.Deliveries.Length || counters.PendingDeliveryBytes != snapshot.Deliveries.Sum(value => value.Package.Length) ||
            counters.RetiredPaths != snapshot.PathClaims.Length - (snapshot.PathClaims.Contains(head.PathKey) ? 1 : 0) || counters.StoredBytes != StoredBytes(snapshot))
            throw new StorageCorruptionException("Normalized document counters disagree with recovery records.");
        DocumentPublicationRules.CheckLimits(head, limits);
    }
    private static void Unique<T, TKey>(IEnumerable<T> values, Func<T, TKey> key) where TKey : notnull
    { if (values.Select(key).Distinct().Count() != values.Count()) throw new StorageCorruptionException("Duplicate normalized recovery identity."); }
    private static void ValidateIntervals(IEnumerable<(long Start, long? End)> intervals)
    {
        long? end = -1;
        foreach (var value in intervals.OrderBy(value => value.Start))
        {
            if (value.Start < 0 || value.End is { } finish && finish <= value.Start || end is null || value.Start < end)
                throw new StorageCorruptionException("Overlapping or invalid normalized visibility intervals.");
            end = value.End;
        }
    }
}
