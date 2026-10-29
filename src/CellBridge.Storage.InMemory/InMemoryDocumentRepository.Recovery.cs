using System.Collections.Immutable;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.InMemory;

public sealed partial class InMemoryDocumentRepository
{
    private readonly SemaphoreSlim _recoveryGuard = new(1);
    private readonly Dictionary<Guid, RecoveryReceipt> _recoveryReceipts = [];
    // Import is a startup operation. Steady-state operations never acquire a recovery gate.
    private int _runtimeState;

    private void EnsureOnline(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = Volatile.Read(ref _runtimeState);
        if (state == 1) return;
        if (state == -1 || Interlocked.CompareExchange(ref _runtimeState, 1, 0) == -1)
            throw new RecoveryConflictException("The repository is importing an offline recovery snapshot.");
    }

    private bool TryDocument(Guid id, CancellationToken cancellationToken, out Entry entry)
    { EnsureOnline(cancellationToken); return _documents.TryGetValue(id, out entry!); }

    private NormalizedDocumentSnapshot Snapshot(Entry entry)
    {
        IndexedGraphScope Scope(Partition partition) => new(entry.Head.ResourceId, entry.Head.LifecycleGeneration, partition.Head.Kind);
        var parts = entry.Partitions.Values.OrderBy(partition => partition.Head.Kind).ToArray();
        return new(Control(entry), parts.Select(partition => new NormalizedPartitionRoot(partition.Head, partition.ReplayPosition)).ToImmutableArray(),
            parts.SelectMany(partition => partition.Elements.Values.Select(element => new ScopedGraphElement(Scope(partition), element))).ToImmutableArray(),
            parts.SelectMany(partition => partition.Visibility.SelectMany(pair => pair.Value.Select(interval =>
                new NormalizedElementVisibility(Scope(partition), pair.Key, interval.Start, interval.End)))).ToImmutableArray(),
            parts.SelectMany(partition => partition.Revisions.Values).ToImmutableArray(),
            parts.SelectMany(partition => partition.Objects.Values).ToImmutableArray(),
            parts.SelectMany(partition => partition.Cells.Values).ToImmutableArray(),
            parts.SelectMany(partition => partition.StorageManifests.Values).ToImmutableArray(),
            parts.SelectMany(partition => partition.MappingIntervals.Values.SelectMany(intervals => intervals)).ToImmutableArray(),
            parts.SelectMany(partition => partition.IndexSequences.Select(pair => new NormalizedMappingRoot(Scope(partition), pair.Key,
                partition.Heads[pair.Value].StorageIndexSerial!, pair.Value) { Head = partition.Heads[pair.Value] })).ToImmutableArray(),
            parts.SelectMany(partition => partition.MappingClaims.Values.SelectMany(claims => claims.Values)).ToImmutableArray(),
            parts.SelectMany(partition => partition.Bindings.Values).ToImmutableArray(),
            parts.SelectMany(partition => partition.Changes.Values).ToImmutableArray(),
            parts.SelectMany(partition => partition.Checkpoints.Values).ToImmutableArray(),
            entry.Revisions.Values.ToImmutableArray(), entry.Receipts.Values.ToImmutableArray(), entry.Deliveries.Values.ToImmutableArray(),
            entry.OwnedPaths.Order(StringComparer.Ordinal).ToImmutableArray(), entry.References.Values.Select(handle =>
                new NormalizedContentReference(handle, entry.ReferenceCounts[handle.Key])
                { IsPending = entry.PendingReferences.Contains(handle.Key), AdmissionBytes = entry.AdmissionBytes.GetValueOrDefault(handle.Key) }).ToImmutableArray());
    }

    public async ValueTask<NormalizedDocumentSnapshot?> ExportDocumentAsync(Guid resourceId, CancellationToken cancellationToken = default)
    {
        await _recoveryGuard.WaitAsync(cancellationToken);
        try { return await ExportCoreAsync(resourceId, cancellationToken); }
        finally { _recoveryGuard.Release(); }
    }

    private async ValueTask<NormalizedDocumentSnapshot?> ExportCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!_documents.TryGetValue(id, out var entry)) return null;
        await entry.Guard.WaitAsync(cancellationToken);
        try
        {
            var snapshot = Snapshot(entry);
            NormalizedDocumentRecovery.Validate(snapshot, _limits, _checkpointPolicy);
            return snapshot;
        }
        finally { entry.Guard.Release(); }
    }

    public ValueTask CheckRecoveryAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); _limits.Validate(); return ValueTask.CompletedTask; }

    /// <summary>The host must drain normal requests before capturing a whole-provider recovery image.</summary>
    public async ValueTask<ProviderSnapshot> CaptureRecoveryAsync(CancellationToken cancellationToken = default)
    {
        await _recoveryGuard.WaitAsync(cancellationToken);
        try
        {
            var snapshots = ImmutableArray.CreateBuilder<NormalizedDocumentSnapshot>();
            foreach (var id in _documents.Keys.Order())
                snapshots.Add(await ExportCoreAsync(id, cancellationToken) ?? throw new StorageCorruptionException("A recovery document disappeared."));
            return new(snapshots.ToImmutable(), _recoveryReceipts.Values.OrderBy(receipt => receipt.OperationId).ToImmutableArray());
        }
        finally { _recoveryGuard.Release(); }
    }

    public async ValueTask<RecoveryReceipt?> FindRecoveryReceiptAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        await _recoveryGuard.WaitAsync(cancellationToken);
        try { return _recoveryReceipts.GetValueOrDefault(operationId); }
        finally { _recoveryGuard.Release(); }
    }

    public async ValueTask<RecoveryReceipt> ImportRecoveryAsync(ProviderSnapshot snapshot, RecoveryReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        await _recoveryGuard.WaitAsync(cancellationToken);
        try
        {
            if (_recoveryReceipts.TryGetValue(receipt.OperationId, out var accepted))
            { ProviderRecovery.CheckReceipt(accepted, receipt); return accepted; }
            if (!_documents.IsEmpty || _recoveryReceipts.Count != 0)
                throw new RecoveryConflictException("Recovery requires an empty destination, including retired identities and recovery receipts.");
            ProviderRecovery.CheckAdmission(snapshot, _limits, UtcNow);
            if (receipt.OperationId == Guid.Empty || receipt.Fingerprint.Length != 64 || receipt.Fingerprint.Any(c => !char.IsAsciiHexDigit(c)) ||
                receipt.Documents != snapshot.Snapshots.Length || receipt.Snapshots != snapshot.Snapshots.Length ||
                snapshot.Receipts.Any(value => value.OperationId == receipt.OperationId) || snapshot.Receipts.Length >= 1000)
                throw new RecoveryConflictException("Invalid recovery receipt, identity collision, or receipt ledger limit.");
            var entries = new Dictionary<Guid, Entry>();
            var paths = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var document in snapshot.Snapshots)
            {
                var entry = await PrepareImportAsync(document, cancellationToken);
                if (!entries.TryAdd(entry.Head.ResourceId, entry)) throw new StorageCorruptionException("Duplicate recovery resource identity.");
                foreach (var path in entry.OwnedPaths)
                    if (!paths.TryAdd(path, entry.Head.ResourceId)) throw new RecoveryConflictException("Recovery documents claim the same path.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref _runtimeState, -1, 0) != 0)
                throw new RecoveryConflictException("Import must finish before this repository instance admits its first normal operation.");
            try
            {
                foreach (var (id, entry) in entries) _documents[id] = entry;
                foreach (var (path, id) in paths) _paths[path] = id;
                foreach (var prior in snapshot.Receipts) _recoveryReceipts.Add(prior.OperationId, prior);
                _recoveryReceipts.Add(receipt.OperationId, receipt);
            }
            catch
            {
                _documents.Clear(); _paths.Clear(); _recoveryReceipts.Clear();
                throw;
            }
            finally { Volatile.Write(ref _runtimeState, 0); }
            return receipt;
        }
        finally { _recoveryGuard.Release(); }
    }

    private async ValueTask<Entry> PrepareImportAsync(NormalizedDocumentSnapshot snapshot, CancellationToken cancellationToken)
    {
        NormalizedDocumentRecovery.Validate(snapshot, _limits, _checkpointPolicy);
        if (!snapshot.Partitions.Select(partition => partition.Head.Kind).Order().SequenceEqual(new[] { 0, 1, 2 }))
            throw new StorageCorruptionException("A document recovery image must include its file, metadata, and editor partitions.");
        var entry = new Entry(snapshot.Current.Head, snapshot.Current.Coordination, snapshot.Current.Editors);
        var objectsByKey = snapshot.Objects.ToDictionary(value => (value.Scope, value.GroupElement, value.Ordinal));
        foreach (var binding in snapshot.CurrentBindings.Concat(snapshot.BindingDeltas.SelectMany(delta => delta.Changes)
            .Where(change => change.Value is not null).Select(change => change.Value!)).Concat(snapshot.Checkpoints.SelectMany(checkpoint => checkpoint.Bindings)))
        {
            if (binding.Key.Scope.ResourceId != entry.Head.ResourceId || binding.Key.Scope.LifecycleGeneration != entry.Head.LifecycleGeneration ||
                !objectsByKey.TryGetValue((binding.Key.Scope, binding.GroupElement, binding.ObjectOrdinal), out var definition) ||
                definition.ObjectId != binding.Key.ObjectId || definition.ObjectPartitionId != binding.Key.ObjectPartitionId)
                throw new StorageCorruptionException("A recovered object binding disagrees with its immutable declaration.");
        }
        entry.OwnedPaths.Clear(); entry.OwnedPaths.UnionWith(snapshot.PathClaims);
        entry.RetiredPaths.UnionWith(snapshot.PathClaims.Where(path => path != entry.Head.PathKey));
        foreach (var reference in snapshot.ContentReferences)
        {
            entry.References.Add(reference.Handle.Key, reference.Handle);
            entry.ReferenceCounts.Add(reference.Handle.Key, reference.ReferenceCount);
            if (reference.AdmissionBytes != 0) entry.AdmissionBytes.Add(reference.Handle.Key, reference.AdmissionBytes);
            if (reference.IsPending) entry.PendingReferences.Add(reference.Handle.Key);
        }
        foreach (var root in snapshot.Partitions)
        {
            var part = new Partition(root.Head) { ReplayPosition = root.ReplayPosition };
            var scope = new IndexedGraphScope(entry.Head.ResourceId, entry.Head.LifecycleGeneration, root.Head.Kind);
            foreach (var element in snapshot.Elements.Where(element => element.Scope == scope))
            { part.Elements.Add(element.Element.Id, element.Element); IndexSerial(part, element.Element.Serial, element.Element.Id); }
            foreach (var group in snapshot.Visibility.Where(value => value.Scope == scope).GroupBy(value => value.Element))
                part.Visibility.Add(group.Key, group.OrderBy(value => value.StartSequence).Select(value => new Visibility(value.StartSequence, value.EndSequence)).ToList());
            foreach (var revision in snapshot.GraphRevisions.Where(value => value.Scope == scope)) part.Revisions.Add(revision.ElementId, revision);
            foreach (var obj in snapshot.Objects.Where(value => value.Scope == scope)) part.Objects.Add((obj.GroupElement, obj.Ordinal), obj);
            foreach (var group in part.Objects.Values.GroupBy(value => value.GroupElement)) part.Groups.Add(group.Key, group.OrderBy(value => value.Ordinal).ToImmutableArray());
            foreach (var element in part.Elements.Values.Where(element => element.Type == (uint)CellBridge.FssHttpB.DataElementType.ObjectGroupDataElementData))
                part.Groups.TryAdd(element.Id, []);
            foreach (var cell in snapshot.Cells.Where(value => value.Scope == scope)) part.Cells.Add(cell.ElementId, cell);
            foreach (var manifest in snapshot.StorageManifests.Where(value => value.Scope == scope)) part.StorageManifests.Add(manifest.ElementId, manifest);
            foreach (var binding in snapshot.CurrentBindings.Where(value => value.Key.Scope == scope)) part.Bindings.Add(binding.Key, binding);
            foreach (var delta in snapshot.BindingDeltas.Where(value => value.Scope == scope).OrderBy(value => value.Sequence)) part.Changes.Add(delta.Sequence, delta);
            foreach (var checkpoint in snapshot.Checkpoints.Where(value => value.Scope == scope).OrderBy(value => value.Sequence)) part.Checkpoints.Add(checkpoint.Sequence, checkpoint);
            foreach (var group in snapshot.MappingIntervals.Where(value => value.Mapping.Scope == scope).GroupBy(value => value.Mapping.Key))
                part.MappingIntervals.Add(group.Key, group.OrderBy(value => value.StartSequence).ToList());
            foreach (var claim in snapshot.MappingClaims.Where(value => value.Scope == scope))
            {
                if (!part.MappingClaims.TryGetValue(claim.Serial, out var claims)) part.MappingClaims.Add(claim.Serial, claims = []);
                claims.Add((claim.Key, claim.Target), claim);
            }
            RebuildMappingRoots(snapshot, scope, part);
            var physical = new PreparedPartitionPublication(root.Head, root.Head, part.Elements.Values.ToImmutableArray(), [], [],
                part.Revisions.Values.ToImmutableArray(), part.Objects.Values.ToImmutableArray(), IndexedDocumentGraph.Difference(scope, 0, 1, [], []))
            { Cells = part.Cells.Values.ToImmutableArray(), StorageManifests = part.StorageManifests.Values.ToImmutableArray() };
            await DocumentPublicationRules.ValidatePartitionPayloadsAsync(physical, _content, _limits, cancellationToken);
            entry.Partitions.Add(root.Head.Kind, part);
        }
        var elementKeys = snapshot.Elements.Select(value => value.Element.Payload.Key).ToHashSet(StringComparer.Ordinal);
        await VerifyReferencesAsync(snapshot.ContentReferences.Where(value => !value.IsPending && !elementKeys.Contains(value.Handle.Key)).Select(value => value.Handle), cancellationToken);
        foreach (var revision in snapshot.ApplicationRevisions.OrderBy(value => value.RevisionNumber)) entry.Revisions.Add(revision.RevisionNumber, revision);
        foreach (var value in snapshot.Receipts) entry.Receipts.Add(value.Operation, value);
        foreach (var delivery in snapshot.Deliveries.OrderBy(value => value.Sequence)) entry.Deliveries.Add(delivery.Sequence, delivery);
        entry.PhysicalBytes = snapshot.ContentReferences.Sum(reference => reference.Handle.Length);
        entry.MetadataBytes = checked(snapshot.Current.Head.Counters.StoredBytes - entry.PhysicalBytes - NormalizedDocumentRecovery.CanonicalHeadBytes(entry.Head));
        return entry;
    }

    private static void RebuildMappingRoots(NormalizedDocumentSnapshot snapshot, IndexedGraphScope scope, Partition part)
    {
        var logicalRoots = snapshot.MappingRoots.Where(root => root.Scope == scope).OrderBy(root => root.Sequence).ToArray();
        foreach (var root in logicalRoots) part.IndexSequences.Add(root.Id, root.Sequence);
        var knownHeads = logicalRoots.Select(root => root.Head).Concat(snapshot.ApplicationRevisions.SelectMany(revision => revision.Partitions).Where(head => head.Kind == scope.TargetPartition))
            .Append(part.Head).GroupBy(head => head.PublicationSequence).ToDictionary(group => group.Key, group => group.Last());
        var sequences = part.Changes.Keys.Concat(part.Checkpoints.Keys).Concat(logicalRoots.Select(root => root.Sequence))
            .Concat(knownHeads.Keys).Distinct().Order().ToArray();
        var mappingEvents = part.MappingIntervals.Values.SelectMany(values => values).SelectMany(interval =>
            interval.EndSequence is { } end ? new[] { (Sequence: interval.StartSequence, Start: true, Interval: interval), (Sequence: end, Start: false, Interval: interval) }
                : [(Sequence: interval.StartSequence, Start: true, Interval: interval)]).OrderBy(value => value.Sequence).ThenBy(value => value.Start).ToArray();
        var visibilityEvents = snapshot.Visibility.Where(value => value.Scope == scope).SelectMany(value =>
            value.EndSequence is { } end ? new[] { (Sequence: value.StartSequence, Count: 1, Element: part.Elements[value.Element]),
                (Sequence: end, Count: -1, Element: part.Elements[value.Element]) } : [(Sequence: value.StartSequence, Count: 1, Element: part.Elements[value.Element])])
            .OrderBy(value => value.Sequence).ToArray();
        var map = ImmutableDictionary<IndexedMappingKey, IndexedMappingValue>.Empty;
        int mappingPosition = 0, visibilityPosition = 0, rootPosition = 0, count = 0;
        long bytes = 0; ulong knowledge = 0; NormalizedMappingRoot? selected = null;
        foreach (var sequence in sequences)
        {
            while (mappingPosition < mappingEvents.Length && mappingEvents[mappingPosition].Sequence <= sequence)
            {
                var change = mappingEvents[mappingPosition++];
                var value = change.Interval.Mapping;
                map = change.Start ? map.SetItem(value.Key, new(value.Target, value.Serial)) : map.Remove(value.Key);
                if (value.Serial.Guid == part.Head.Identity.SerialGuid) knowledge = Math.Max(knowledge, value.Serial.Value);
            }
            while (visibilityPosition < visibilityEvents.Length && visibilityEvents[visibilityPosition].Sequence <= sequence)
            {
                var change = visibilityEvents[visibilityPosition++]; count += change.Count; bytes += change.Count * change.Element.Payload.Length;
                if (change.Element.Serial.Guid == part.Head.Identity.SerialGuid) knowledge = Math.Max(knowledge, change.Element.Serial.Value);
            }
            while (rootPosition < logicalRoots.Length && logicalRoots[rootPosition].Sequence <= sequence)
            {
                selected = logicalRoots[rootPosition++];
                if (selected.Serial.Guid == part.Head.Identity.SerialGuid) knowledge = Math.Max(knowledge, selected.Serial.Value);
            }
            part.MappingRoots.Add(sequence, map);
            part.Heads.Add(sequence, knownHeads.GetValueOrDefault(sequence) ?? part.Head with
            { PublicationSequence = sequence, StorageIndex = selected?.Id, StorageIndexSerial = selected?.Serial,
                ElementCount = count, ElementBytes = bytes, Knowledge = knowledge });
        }
        part.CurrentMappings = part.MappingRoots[part.Head.PublicationSequence];
    }
}
