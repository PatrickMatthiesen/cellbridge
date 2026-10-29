using System.Collections.Immutable;
using System.Globalization;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed partial class RelationalDocumentRepository<TContext> where TContext : DbContext
{
    private static IndexedGraphScope RowScope(Guid id, long lifecycle, int partition) => new(id, lifecycle, partition);
    private static ExtendedId ParseElementKey(string key)
    {
        if (key.Length != 40 || !Guid.TryParseExact(key[..32], "N", out var guid) ||
            !uint.TryParse(key[32..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            throw new StorageCorruptionException("Invalid normalized graph identity key.");
        return new(value, guid);
    }
    public ValueTask<NormalizedDocumentSnapshot?> ExportDocumentAsync(Guid resourceId, CancellationToken cancellationToken = default) =>
        ReadAsync(db => ExportDocumentAsync(db, resourceId, cancellationToken), cancellationToken);
    internal async Task ValidateOfflineInventoryAsync(TContext db, CancellationToken ct)
    {
        await RepositoryInventory.ValidateOwnersAsync(db, ct);
        var ids = await db.Set<RepositoryHeadRow>().AsNoTracking().OrderBy(row => row.ResourceId).Select(row => row.ResourceId).ToArrayAsync(ct);
        foreach (var id in ids)
            _ = await ExportDocumentAsync(db, id, ct) ?? throw new StorageCorruptionException("A normalized maintenance owner is missing.");
    }
    private async Task<NormalizedDocumentSnapshot?> ExportDocumentAsync(TContext db, Guid id, CancellationToken ct)
    {
        var head = await HeadAsync(db, id, ct);
        if (head is null) return null;
        var control = await ControlAsync(db, head, ct);
        var partitions = await db.Set<RepositoryPartitionRow>().AsNoTracking().Where(row => row.ResourceId == id).ToArrayAsync(ct);
        var elements = await db.Set<RepositoryElementRow>().AsNoTracking().Where(row => row.ResourceId == id).ToArrayAsync(ct);
        var visibility = await db.Set<RepositoryVisibilityRow>().AsNoTracking().Where(row => row.ResourceId == id).ToArrayAsync(ct);
        var graph = await db.Set<RepositoryGraphRow>().AsNoTracking().Where(row => row.ResourceId == id).ToArrayAsync(ct);
        if (graph.Any(row => row.Kind is not ("revision" or "object" or "cell" or "manifest")))
            throw new StorageCorruptionException("Unknown normalized immutable graph record kind.");
        foreach (var row in graph)
        {
            var scope = RowScope(id, row.Lifecycle, row.Partition);
            var valid = row.Kind switch
            {
                "revision" => Decode<IndexedRevisionManifest>(row.Json) is { } revision && revision.Scope == scope && ElementKey(revision.ElementId) == row.OwnerKey && row.RecordKey == "",
                "object" => Decode<IndexedObjectDefinition>(row.Json) is { } obj && obj.Scope == scope && ElementKey(obj.GroupElement) == row.OwnerKey && obj.Ordinal.ToString("D10") == row.RecordKey,
                "cell" => Decode<IndexedCellManifest>(row.Json) is { } cell && cell.Scope == scope && ElementKey(cell.ElementId) == row.OwnerKey && row.RecordKey == "",
                "manifest" => Decode<IndexedStorageManifest>(row.Json) is { } manifest && manifest.Scope == scope && ElementKey(manifest.ElementId) == row.OwnerKey && row.RecordKey == "",
                _ => false,
            };
            if (!valid) throw new StorageCorruptionException("An indexed graph row disagrees with its recovery identity.");
        }
        var intervals = await db.Set<RepositoryMappingIntervalRow>().AsNoTracking().Where(row => row.ResourceId == id).ToArrayAsync(ct);
        foreach (var row in intervals)
        {
            var mapping = Decode<IndexedStorageMapping>(row.Json);
            if (mapping.Scope != RowScope(id, row.Lifecycle, row.Partition) || RecordKey(mapping.Key) != row.MappingKey)
                throw new StorageCorruptionException("A mapping interval disagrees with its recovery identity.");
        }
        var roots = await db.Set<RepositoryMappingRootRow>().AsNoTracking().Where(row => row.ResourceId == id).ToArrayAsync(ct);
        var claims = await db.Set<RepositoryMappingClaimRow>().AsNoTracking().Where(row => row.ResourceId == id).Select(row => row.Json).ToArrayAsync(ct);
        var bindings = await db.Set<RepositoryBindingRow>().AsNoTracking().Where(row => row.ResourceId == id).Select(row => row.Json).ToArrayAsync(ct);
        var headers = await db.Set<RepositoryDeltaRow>().AsNoTracking().Where(row => row.ResourceId == id).ToArrayAsync(ct);
        var changes = await db.Set<RepositoryBindingChangeRow>().AsNoTracking().Where(row => row.ResourceId == id).ToArrayAsync(ct);
        var checkpoints = await db.Set<RepositoryCheckpointRow>().AsNoTracking().Where(row => row.ResourceId == id).ToArrayAsync(ct);
        var revisions = await db.Set<RepositoryRevisionRow>().AsNoTracking().Where(row => row.ResourceId == id).OrderBy(row => row.RevisionNumber).Select(row => row.Json).ToArrayAsync(ct);
        var receipts = await db.Set<RepositoryReceiptRow>().AsNoTracking().Where(row => row.ResourceId == id).Select(row => row.Json).ToArrayAsync(ct);
        var deliveries = await db.Set<RepositoryDeliveryRow>().AsNoTracking().Where(row => row.ResourceId == id).OrderBy(row => row.Sequence).Select(row => row.Json).ToArrayAsync(ct);
        var paths = await db.Set<RepositoryPathRow>().AsNoTracking().Where(row => row.ResourceId == id).OrderBy(row => row.PathKey).Select(row => row.PathKey).ToArrayAsync(ct);
        var references = await db.Set<RepositoryReferenceRow>().AsNoTracking().Where(row => row.ResourceId == id).ToArrayAsync(ct);
        var changesBySequence = changes.ToLookup(row => (row.Lifecycle, row.Partition, row.Sequence));
        var deltaRecords = ImmutableArray.CreateBuilder<IndexedBindingDelta>();
        foreach (var header in headers)
        {
            var selected = changesBySequence[(header.Lifecycle, header.Partition, header.Sequence)].Select(row => Decode<IndexedBindingChange>(row.Json));
            var ordered = selected.OrderBy(change => change.Key.Cell.LongId.Guid).ThenBy(change => change.Key.Cell.LongId.Value)
                .ThenBy(change => change.Key.Cell.ShortId.Guid).ThenBy(change => change.Key.Cell.ShortId.Value)
                .ThenBy(change => change.Key.ObjectId.Guid).ThenBy(change => change.Key.ObjectId.Value).ThenBy(change => change.Key.ObjectPartitionId).ToImmutableArray();
            if (ordered.Length != header.ChangeCount) throw new StorageCorruptionException("Recovery found an incomplete binding delta.");
            deltaRecords.Add(new(RowScope(id, header.Lifecycle, header.Partition), header.PreviousSequence, header.Sequence, ordered, header.EncodedBytes));
        }
        var headerKeys = headers.Select(header => (header.Lifecycle, header.Partition, header.Sequence)).ToHashSet();
        if (changes.Any(change => !headerKeys.Contains((change.Lifecycle, change.Partition, change.Sequence))))
            throw new StorageCorruptionException("Recovery found orphan binding changes.");
        var checkpointRecords = ImmutableArray.CreateBuilder<IndexedGraphCheckpoint>();
        foreach (var group in checkpoints.GroupBy(row => (row.Lifecycle, row.Partition, row.Sequence)))
        {
            if (group.Count(row => row.BindingKey == "" && row.Json == "null") != 1) throw new StorageCorruptionException("A recovery checkpoint marker is absent or duplicated.");
            checkpointRecords.Add(IndexedDocumentGraph.Checkpoint(RowScope(id, group.Key.Lifecycle, group.Key.Partition), group.Key.Sequence,
                group.Where(row => row.BindingKey != "").Select(row => Decode<IndexedObjectBinding>(row.Json))));
        }
        var snapshot = new NormalizedDocumentSnapshot(control,
            partitions.Select(row => new NormalizedPartitionRoot(Decode<PartitionHead>(row.Json), new(row.CheckpointSequence, row.ReplayChanges, row.ReplayBytes))).ToImmutableArray(),
            elements.Select(row => new ScopedGraphElement(RowScope(id, row.Lifecycle, row.Partition), Decode<GraphElementState>(row.Json))).ToImmutableArray(),
            visibility.Select(row => new NormalizedElementVisibility(RowScope(id, row.Lifecycle, row.Partition), ParseElementKey(row.ElementKey), row.StartSequence, row.EndSequence)).ToImmutableArray(),
            graph.Where(row => row.Kind == "revision").Select(row => Decode<IndexedRevisionManifest>(row.Json)).ToImmutableArray(),
            graph.Where(row => row.Kind == "object").Select(row => Decode<IndexedObjectDefinition>(row.Json)).ToImmutableArray(),
            graph.Where(row => row.Kind == "cell").Select(row => Decode<IndexedCellManifest>(row.Json)).ToImmutableArray(),
            graph.Where(row => row.Kind == "manifest").Select(row => Decode<IndexedStorageManifest>(row.Json)).ToImmutableArray(),
            intervals.Select(row => new NormalizedMappingInterval(Decode<IndexedStorageMapping>(row.Json), row.StartSequence, row.EndSequence)).ToImmutableArray(),
            roots.Select(row => new NormalizedMappingRoot(RowScope(id, row.Lifecycle, row.Partition), ParseElementKey(row.IndexKey), new(row.SerialGuid, (ulong)row.SerialValue), row.Sequence) { Head = Decode<PartitionHead>(row.Json) }).ToImmutableArray(),
            claims.Select(Decode<IndexedStorageMapping>).ToImmutableArray(), bindings.Select(Decode<IndexedObjectBinding>).ToImmutableArray(), deltaRecords.ToImmutable(), checkpointRecords.ToImmutable(),
            revisions.Select(Decode<DocumentRevisionRecord>).ToImmutableArray(), receipts.Select(Decode<DocumentOperationReceipt>).ToImmutableArray(),
            deliveries.Select(Decode<DocumentDelivery>).ToImmutableArray(), paths.ToImmutableArray(), references.Select(row => new NormalizedContentReference(new(row.ObjectKey, row.Length, row.Sha256), row.ReferenceCount)).ToImmutableArray());
        NormalizedDocumentRecovery.Validate(snapshot, limits, checkpointPolicy);
        var actualMappings = await db.Set<RepositoryMappingRow>().AsNoTracking().Where(row => row.ResourceId == id).ToArrayAsync(ct);
        var expectedMappings = intervals.Where(interval => interval.EndSequence is null).ToDictionary(interval => (interval.Lifecycle, interval.Partition, interval.MappingKey));
        if (actualMappings.Length != expectedMappings.Count || actualMappings.Any(row => !expectedMappings.TryGetValue((row.Lifecycle, row.Partition, row.MappingKey), out var interval) ||
            interval.StartSequence != row.StartSequence || interval.Json != row.Json))
            throw new StorageCorruptionException("Current mapping rows disagree with immutable recovery intervals.");
        return snapshot;
    }
    public ValueTask CheckRecoveryAsync(CancellationToken cancellationToken = default) => CheckHealthAsync(cancellationToken);
    public ValueTask<ProviderSnapshot> CaptureRecoveryAsync(CancellationToken cancellationToken = default) => ReadAsync(async db =>
    {
        var ids = await db.Set<RepositoryHeadRow>().AsNoTracking().OrderBy(row => row.ResourceId).Select(row => row.ResourceId).ToArrayAsync(cancellationToken);
        var documents = ImmutableArray.CreateBuilder<NormalizedDocumentSnapshot>();
        foreach (var id in ids) documents.Add(await ExportDocumentAsync(db, id, cancellationToken)
            ?? throw new StorageCorruptionException("A document disappeared within the recovery snapshot."));
        var receipts = await db.Set<RecoveryReceiptRow>().AsNoTracking().OrderBy(row => row.OperationId).Select(row => row.ReceiptJson).ToArrayAsync(cancellationToken);
        return new ProviderSnapshot(documents.ToImmutable(), receipts.Select(Decode<RecoveryReceipt>).ToImmutableArray());
    }, cancellationToken);
    public ValueTask<RecoveryReceipt?> FindRecoveryReceiptAsync(Guid operationId, CancellationToken cancellationToken = default) => ReadAsync(async db =>
    {
        var json = await db.Set<RecoveryReceiptRow>().AsNoTracking().Where(row => row.OperationId == operationId).Select(row => row.ReceiptJson).SingleOrDefaultAsync(cancellationToken);
        var receipt = json is null ? null : Decode<RecoveryReceipt>(json);
        if (receipt is not null && receipt.OperationId != operationId) throw new StorageCorruptionException("Recovery receipt identity disagrees with its ledger key.");
        return receipt;
    }, cancellationToken);
    public async ValueTask<RecoveryReceipt> ImportRecoveryAsync(ProviderSnapshot snapshot, RecoveryReceipt receipt, CancellationToken cancellationToken = default)
    {
        if (snapshot.Snapshots.IsDefault || snapshot.Receipts.IsDefault || receipt.OperationId == Guid.Empty || string.IsNullOrEmpty(receipt.Fingerprint) ||
            snapshot.Snapshots.Select(document => document.Current.Head.ResourceId).Distinct().Count() != snapshot.Snapshots.Length ||
            snapshot.Receipts.Select(value => value.OperationId).Distinct().Count() != snapshot.Receipts.Length ||
            snapshot.Receipts.Any(value => value.OperationId == receipt.OperationId) || snapshot.Receipts.Length >= 1000 ||
            receipt.Documents != snapshot.Snapshots.Length || receipt.Snapshots != snapshot.Snapshots.Length)
            throw new StorageCorruptionException("Invalid normalized provider recovery identity, counts or receipt ledger.");
        foreach (var document in snapshot.Snapshots) NormalizedDocumentRecovery.Validate(document, limits, checkpointPolicy);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in snapshot.Snapshots)
            foreach (var path in document.PathClaims)
                if (!claimed.Add(path)) throw new StorageCorruptionException("Recovery documents claim the same filename.");
        if (await FindRecoveryReceiptAsync(receipt.OperationId, cancellationToken) is { } accepted)
        { ProviderRecovery.CheckReceipt(accepted, receipt); return accepted; }
        await using (var verification = database.CreateContext())
        {
            var content = new RepositoryContentReader<TContext>(verification, validationContent, limits.MaxObjectBytes, database);
            await using var verified = await RecoveryContentSpool.CreateAsync(content,
                snapshot.Snapshots.SelectMany(document => document.ContentReferences).Select(value => value.Handle),
                limits.MaxObjectBytes, cancellationToken);
            foreach (var document in snapshot.Snapshots) await ValidatePhysicalSnapshotAsync(document, verified, cancellationToken);
        }
        try
        {
            await using var db = database.CreateContext();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await database.ConfigureAsync(db, cancellationToken);
            await database.RecoveryGuardAsync(db, cancellationToken);
            var previous = await db.Set<RecoveryReceiptRow>().SingleOrDefaultAsync(row => row.OperationId == receipt.OperationId, cancellationToken);
            if (previous is not null)
            { var replay = Decode<RecoveryReceipt>(previous.ReceiptJson); ProviderRecovery.CheckReceipt(replay, receipt); return replay; }
            ProviderRecovery.CheckAdmission(snapshot with { Receipts = snapshot.Receipts.Add(receipt) }, limits, await database.ClockAsync(db, cancellationToken));
            if (await db.Set<RepositoryHeadRow>().AnyAsync(cancellationToken) || await db.Set<RecoveryReceiptRow>().AnyAsync(cancellationToken))
                throw new RecoveryConflictException("Recovery requires an empty normalized document namespace and receipt ledger.");
            var batch = new RecoveryImportBatch(db);
            foreach (var document in snapshot.Snapshots)
                foreach (var row in ImportRows(document)) await batch.AddAsync(row, cancellationToken);
            // All document batches and the ledger share this transaction. Intermediate
            // saves release tracked objects, never the namespace's atomic commit.
            foreach (var retained in snapshot.Receipts.Append(receipt))
                await batch.AddAsync(new RecoveryReceiptRow { OperationId = retained.OperationId, ReceiptJson = Encode(retained) }, cancellationToken);
            await batch.FlushAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            try { await transaction.CommitAsync(CancellationToken.None); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (IOException error)
            { throw new StorageUnavailableException("The recovery commit outcome is unknown. Resolve it through its durable recovery receipt before retrying.", error); }
            return receipt;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (database.IsUniqueViolation(error))
        { throw new RecoveryConflictException("A document identity or filename was claimed during recovery. No normalized state was imported."); }
        catch (Exception error) when (database.IsDatabaseException(error))
        { throw new StorageUnavailableException("Recovery publication failed or its commit outcome is unknown. Resolve the operation through its durable recovery receipt.", error); }
    }
    private async Task ValidatePhysicalSnapshotAsync(NormalizedDocumentSnapshot snapshot, IContentStore content, CancellationToken ct)
    {
        var id = snapshot.Current.Head.ResourceId;
        var lifecycle = snapshot.Current.Head.LifecycleGeneration;
        var elements = snapshot.Elements.ToDictionary(value => (value.Scope, value.Element.Id), value => value.Element);
        foreach (var partition in snapshot.Partitions)
        {
            var scope = new IndexedGraphScope(id, lifecycle, partition.Head.Kind);
            var before = partition.Head with { PublicationSequence = 0 };
            var after = partition.Head with { PublicationSequence = 1 };
            var projection = new PreparedPartitionPublication(before, after, snapshot.Elements.Where(value => value.Scope == scope).Select(value => value.Element).ToImmutableArray(),
                [], [], snapshot.GraphRevisions.Where(value => value.Scope == scope).ToImmutableArray(), snapshot.Objects.Where(value => value.Scope == scope).ToImmutableArray(),
                new(scope, 0, 1, [], 2))
            { Cells = snapshot.Cells.Where(value => value.Scope == scope).ToImmutableArray(), StorageManifests = snapshot.StorageManifests.Where(value => value.Scope == scope).ToImmutableArray() };
            await DocumentPublicationRules.ValidatePartitionPayloadsAsync(projection, content, limits, ct);
        }
        var reader = new GraphDocumentPackageReader((package, token) =>
        {
            var root = snapshot.MappingRoots.Single(value => value.Scope == package.Root.Scope && value.Id == package.Root.StorageIndex);
            var checkpoint = snapshot.Checkpoints.Where(value => value.Scope == root.Scope && value.Sequence <= root.Sequence).OrderByDescending(value => value.Sequence).First();
            var tail = snapshot.BindingDeltas.Where(value => value.Scope == root.Scope && value.Sequence > checkpoint.Sequence && value.Sequence <= root.Sequence).OrderBy(value => value.Sequence);
            IReadOnlyList<IndexedObjectBinding> bindings = IndexedDocumentGraph.Replay(checkpoint, tail, root.Sequence, checkpointPolicy);
            return ValueTask.FromResult(bindings);
        }, async (scope, elementId, token) =>
        {
            if (!elements.TryGetValue((scope, elementId), out var element)) throw new StorageCorruptionException("A recovered package references absent immutable metadata.");
            return new CellBridge.FssHttpB.DataElement((CellBridge.FssHttpB.DataElementType)element.Type, IndexedDocumentGraph.Restore(element.Id),
                new(element.Serial.Guid, element.Serial.Value)) { Data = await content.ReadVerifiedAsync(element.Payload, limits.MaxObjectBytes, token) };
        }, limits.MaxDocumentBytes);
        foreach (var package in snapshot.ApplicationRevisions.Select(value => value.Package).Append(snapshot.Current.Head.Package)
            .Concat(snapshot.Deliveries.Select(value => value.Package)).Distinct())
        { await using var verified = await reader.OpenReadAsync(package, ct); }
    }
    // Recovery has validated every immutable identity and owns an empty namespace.
    // Direct construction avoids ordinary publication's read-before-insert queries;
    // database constraints still reject conflicting or orphaned preexisting rows.
    private static IEnumerable<object> ImportRows(NormalizedDocumentSnapshot snapshot)
    {
        var head = snapshot.Current.Head; var id = head.ResourceId;
        yield return new RepositoryHeadRow { ResourceId = id, PathKey = head.PathKey, PathHash = Hash(head.PathKey), Lifecycle = head.LifecycleGeneration,
            StateVersion = head.StateVersion, IsDeleted = head.IsDeleted, PendingDeliveries = head.Counters.PendingDeliveries, DeliveryBlocked = head.Delivery?.BlockedReason is not null, Json = Encode(head) };
        yield return new RepositoryCoordinationRow { ResourceId = id, Json = Encode(snapshot.Current.Coordination) };
        foreach (var editor in snapshot.Current.Editors) yield return new RepositoryEditorRow { ResourceId = id, ClientId = editor.ClientId, ExpiresUtc = editor.ExpiresUtc, Json = Encode(editor) };
        foreach (var partition in snapshot.Partitions) yield return new RepositoryPartitionRow { ResourceId = id, Lifecycle = head.LifecycleGeneration,
            Partition = partition.Head.Kind, Sequence = partition.Head.PublicationSequence, CheckpointSequence = partition.ReplayPosition.CheckpointSequence,
            ReplayChanges = partition.ReplayPosition.Changes, ReplayBytes = partition.ReplayPosition.Bytes, Json = Encode(partition.Head) };
        foreach (var value in snapshot.Elements) yield return new RepositoryElementRow { ResourceId = id, Lifecycle = value.Scope.LifecycleGeneration,
            Partition = value.Scope.TargetPartition, ElementKey = ElementKey(value.Element.Id), SerialGuid = value.Element.Serial.Guid, SerialValue = value.Element.Serial.Value, Json = Encode(value.Element) };
        foreach (var value in snapshot.Visibility) yield return new RepositoryVisibilityRow { ResourceId = id, Lifecycle = value.Scope.LifecycleGeneration,
            Partition = value.Scope.TargetPartition, ElementKey = ElementKey(value.Element), StartSequence = value.StartSequence, EndSequence = value.EndSequence };
        foreach (var value in snapshot.GraphRevisions) yield return Graph(value.Scope, "revision", ElementKey(value.ElementId), "", Encode(value));
        foreach (var value in snapshot.Objects) yield return Graph(value.Scope, "object", ElementKey(value.GroupElement), value.Ordinal.ToString("D10"), Encode(value));
        foreach (var value in snapshot.Cells) yield return Graph(value.Scope, "cell", ElementKey(value.ElementId), "", Encode(value));
        foreach (var value in snapshot.StorageManifests) yield return Graph(value.Scope, "manifest", ElementKey(value.ElementId), "", Encode(value));
        foreach (var value in snapshot.MappingIntervals)
        {
            var mapping = value.Mapping; var json = Encode(mapping);
            yield return new RepositoryMappingIntervalRow { ResourceId = id, Lifecycle = mapping.Scope.LifecycleGeneration, Partition = mapping.Scope.TargetPartition,
                MappingKey = RecordKey(mapping.Key), StartSequence = value.StartSequence, EndSequence = value.EndSequence, Json = json };
            if (value.EndSequence is null) yield return new RepositoryMappingRow { ResourceId = id, Lifecycle = mapping.Scope.LifecycleGeneration,
                Partition = mapping.Scope.TargetPartition, MappingKey = RecordKey(mapping.Key), StartSequence = value.StartSequence, Json = json };
        }
        foreach (var value in snapshot.MappingRoots) yield return new RepositoryMappingRootRow { ResourceId = id, Lifecycle = value.Scope.LifecycleGeneration,
            Partition = value.Scope.TargetPartition, IndexKey = ElementKey(value.Id), Sequence = value.Sequence, SerialGuid = value.Serial.Guid, SerialValue = value.Serial.Value, Json = Encode(value.Head) };
        foreach (var value in snapshot.MappingClaims) yield return new RepositoryMappingClaimRow { ResourceId = id, Lifecycle = value.Scope.LifecycleGeneration,
            Partition = value.Scope.TargetPartition, SerialGuid = value.Serial.Guid, SerialValue = value.Serial.Value, Json = Encode(value) };
        foreach (var value in snapshot.CurrentBindings) yield return new RepositoryBindingRow { ResourceId = id, Lifecycle = value.Key.Scope.LifecycleGeneration,
            Partition = value.Key.Scope.TargetPartition, BindingKey = RecordKey(value.Key), Json = Encode(value) };
        foreach (var delta in snapshot.BindingDeltas)
        {
            yield return new RepositoryDeltaRow { ResourceId = id, Lifecycle = delta.Scope.LifecycleGeneration, Partition = delta.Scope.TargetPartition,
                Sequence = delta.Sequence, PreviousSequence = delta.PreviousSequence, ChangeCount = delta.Changes.Length, EncodedBytes = delta.EncodedBytes };
            foreach (var value in delta.Changes) yield return new RepositoryBindingChangeRow { ResourceId = id, Lifecycle = delta.Scope.LifecycleGeneration,
                Partition = delta.Scope.TargetPartition, Sequence = delta.Sequence, BindingKey = RecordKey(value.Key), Json = Encode(value) };
        }
        foreach (var checkpoint in snapshot.Checkpoints)
        {
            yield return new RepositoryCheckpointRow { ResourceId = id, Lifecycle = checkpoint.Scope.LifecycleGeneration,
                Partition = checkpoint.Scope.TargetPartition, Sequence = checkpoint.Sequence, BindingKey = "", Json = "null" };
            foreach (var value in checkpoint.Bindings) yield return new RepositoryCheckpointRow { ResourceId = id, Lifecycle = checkpoint.Scope.LifecycleGeneration,
                Partition = checkpoint.Scope.TargetPartition, Sequence = checkpoint.Sequence, BindingKey = RecordKey(value.Key), Json = Encode(value) };
        }
        foreach (var value in snapshot.ApplicationRevisions)
        {
            if (value.Package.RevisionNumber != value.RevisionNumber)
                throw new StorageCorruptionException("Application revision identity disagrees with its package.");
            yield return new RepositoryRevisionRow { ResourceId = id, Lifecycle = head.LifecycleGeneration,
                RevisionNumber = value.RevisionNumber, Json = Encode(value) };
        }
        foreach (var value in snapshot.Receipts)
        {
            if (string.IsNullOrWhiteSpace(value.Operation.OperationKey))
                throw new StorageCorruptionException("A recovery receipt requires its scoped operation key.");
            yield return new RepositoryReceiptRow { ResourceId = id, Lifecycle = value.Operation.LifecycleGeneration,
                Family = (int)value.Operation.Family, Partition = value.Operation.PartitionKind, OperationHash = Hash(value.Operation.OperationKey), Json = Encode(value) };
        }
        foreach (var value in snapshot.Deliveries) yield return new RepositoryDeliveryRow { ResourceId = id, OperationId = value.OperationId, Sequence = value.Sequence, Json = Encode(value) };
        foreach (var path in snapshot.PathClaims) yield return new RepositoryPathRow { ResourceId = id, PathHash = Hash(path), PathKey = path };
        foreach (var value in snapshot.ContentReferences) yield return new RepositoryReferenceRow { ResourceId = id, ObjectKey = value.Handle.Key,
            Length = value.Handle.Length, Sha256 = value.Handle.Sha256, ReferenceCount = value.ReferenceCount };

        static RepositoryGraphRow Graph(IndexedGraphScope scope, string kind, string owner, string key, string json) => new()
        { ResourceId = scope.ResourceId, Lifecycle = scope.LifecycleGeneration, Partition = scope.TargetPartition, Kind = kind, OwnerKey = owner, RecordKey = key, Json = json };
    }
}
