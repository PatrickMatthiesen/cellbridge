using CellBridge.Diagnostics;
using System.Collections.Immutable;
using System.Text;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed partial class RelationalDocumentRepository<TContext> where TContext : DbContext
{
    private async ValueTask<T> MutateAsync<T>(Guid id, Func<TContext, RepositoryHeadRow?, DateTime, Task<T>> operation, CancellationToken ct, bool measurePublication = false)
    {
        using var timing = measurePublication ? CellBridgeMetrics.Measure(CellBridgePhase.GuardedPublication) : default;
        try
        {
            await using var db = database.CreateContext();
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction openedTransaction;
            using (measurePublication ? CellBridgeMetrics.Measure(CellBridgePhase.PublicationConnectionAndTransaction) : default)
                openedTransaction = await db.Database.BeginTransactionAsync(ct);
            await using var transaction = openedTransaction;
            await database.ConfigureAsync(db, ct);
            using (measurePublication ? CellBridgeMetrics.Measure(CellBridgePhase.DocumentGuard) : default)
                await database.GuardAsync(db, id, ct);
            T result;
            using (measurePublication ? CellBridgeMetrics.Measure(CellBridgePhase.PublicationWork) : default)
            {
                var row = await HeadRowAsync(db, id, ct);
                result = await operation(db, row, await database.ClockAsync(db, ct));
            }
            ct.ThrowIfCancellationRequested();
            using (measurePublication ? CellBridgeMetrics.Measure(CellBridgePhase.PublicationSaveChanges) : default)
                await db.SaveChangesAsync(ct);
            try
            {
                using var commitTiming = measurePublication ? CellBridgeMetrics.Measure(CellBridgePhase.PublicationCommit) : default;
                await transaction.CommitAsync(CancellationToken.None);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (IOException error)
            { throw new StorageUnavailableException("The document commit outcome is unknown. Resolve it through its operation receipt before retrying.", error); }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (database.IsUniqueViolation(error)) { throw new RepositoryConflictException(error); }
        catch (Exception error) when (database.IsDatabaseException(error))
        { throw new StorageUnavailableException("Document publication failed or its commit outcome is unknown. Resolve it through the operation receipt before retrying.", error); }
    }
    private sealed class RepositoryConflictException(Exception inner) : IOException("A document identity or path was claimed concurrently.", inner);
    private DocumentHead SetHead(RepositoryHeadRow row, DocumentHead head)
    {
        var previous = row.Json.Length == 0 ? 0 : NormalizedDocumentRecovery.CanonicalHeadBytes(Decode<DocumentHead>(row.Json));
        head = head with { Counters = head.Counters with { StoredBytes = checked(head.Counters.StoredBytes +
            NormalizedDocumentRecovery.CanonicalHeadBytes(head) - previous) } };
        CheckCounters(head.Counters);
        row.ResourceId = head.ResourceId; row.PathKey = head.PathKey; row.PathHash = Hash(head.PathKey); row.Lifecycle = head.LifecycleGeneration;
        row.StateVersion = head.StateVersion; row.IsDeleted = head.IsDeleted; row.PendingDeliveries = head.Counters.PendingDeliveries; row.DeliveryBlocked = head.Delivery?.BlockedReason is not null; row.Json = Encode(head);
        return head;
    }
    private void CheckCounters(DocumentCounters counters)
    {
        StorageLimits.Check("document stored bytes", counters.StoredBytes, limits.MaxDocumentStoredBytes);
        StorageLimits.Check("graph bytes", counters.GraphBytes, limits.MaxGraphBytes);
        StorageLimits.Check("graph elements", counters.GraphElements, limits.MaxGraphElements);
        StorageLimits.Check("history revisions", counters.Revisions, limits.MaxHistoryRevisions);
        StorageLimits.Check("save receipts", counters.SaveReceipts, limits.MaxSaveReceipts);
        StorageLimits.Check("restore receipts", counters.RestoreReceipts, limits.MaxRestoreReceipts);
        StorageLimits.Check("whole-file save receipts", counters.WholeFileSaveReceipts, limits.MaxWholeFileSaveReceipts);
        StorageLimits.Check("retired path keys", counters.RetiredPaths, limits.MaxRetiredPathKeys);
        StorageLimits.Check("pending external revisions", counters.PendingDeliveries, limits.MaxPendingExternalRevisions);
        StorageLimits.Check("pending external bytes", counters.PendingDeliveryBytes, limits.MaxPendingExternalBytes);
    }
    private static void ValidatePackage(DocumentHead head, DocumentPackage package)
    {
        if (package.ResourceId != head.ResourceId || package.LifecycleGeneration != head.LifecycleGeneration || package.RevisionNumber == 0 ||
            package.Root.Scope.ResourceId != head.ResourceId || package.Root.Scope.LifecycleGeneration != head.LifecycleGeneration ||
            package.Root.Scope.TargetPartition != 0 || package.ReconstructionVersion != DocumentPackage.CurrentReconstructionVersion ||
            package.Length < 0 || package.Sha256.Length != 64 || package.Sha256.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new StorageCorruptionException("Invalid immutable package descriptor.");
    }
    private async Task<long> AddReferencesAsync(TContext db, Guid id, IEnumerable<ContentHandle> handles, CancellationToken ct)
    {
        var unique = handles.GroupBy(handle => handle.Key, StringComparer.Ordinal).Select(group =>
        {
            var handle = group.First();
            if (group.Any(value => value != handle)) throw new StorageCorruptionException("Conflicting content descriptors in one publication.");
            StorageLimits.Check("object bytes", handle.Length, limits.MaxObjectBytes);
            return handle;
        }).ToArray();
        var keys = unique.Select(handle => handle.Key).ToArray();
        var physical = await PrefetchAsync(db, db.Set<ObjectRow>().AsNoTracking(), keys,
            key => row => row.ObjectKey == key, row => row.ObjectKey, _ => true, "physical-content", ct, includeTracked: false);
        var references = await PrefetchAsync(db, db.Set<RepositoryReferenceRow>().Where(row => row.ResourceId == id), keys,
            key => row => row.ObjectKey == key, row => row.ObjectKey, row => row.ResourceId == id, "references", ct);
        long addedBytes = 0;
        foreach (var handle in unique)
        {
            var stored = physical[handle.Key];
            if (stored is null || stored.Length != handle.Length || stored.Sha256 != handle.Sha256)
                throw new StorageCorruptionException("Publication references missing or inconsistent durable content.");
            var existing = references[handle.Key];
            if (existing is null)
            {
                var added = new RepositoryReferenceRow { ResourceId = id, ObjectKey = handle.Key, Length = handle.Length,
                    Sha256 = handle.Sha256, ReferenceCount = 1 };
                db.Set<RepositoryReferenceRow>().Add(added);
                references[handle.Key] = added;
                addedBytes = checked(addedBytes + handle.Length);
            }
            else
            {
                if (existing.Length != handle.Length || existing.Sha256 != handle.Sha256)
                    throw new StorageCorruptionException("Document content reference metadata changed.");
                existing.ReferenceCount = checked(existing.ReferenceCount + 1);
            }
        }
        return addedBytes;
    }
    private static async Task<long> AddReceiptAsync(TContext db, Guid id, DocumentOperationReceipt receipt, CancellationToken ct, bool allowOwnerless = false)
    {
        var operation = receipt.Operation;
        if (operation.LifecycleGeneration <= 0 || string.IsNullOrWhiteSpace(operation.OperationKey) ||
            (!allowOwnerless || receipt.OwnerSubject is not null) && string.IsNullOrWhiteSpace(receipt.OwnerSubject) ||
            string.IsNullOrWhiteSpace(receipt.Digest) || !Enum.IsDefined(operation.Family))
            throw new ArgumentException("A receipt requires its complete scoped operation identity and actor.");
        var json = Encode(receipt);
        var row = await db.Set<RepositoryReceiptRow>().FindAsync([id, operation.LifecycleGeneration, (int)operation.Family,
            operation.PartitionKind, Hash(operation.OperationKey)], ct);
        if (row is not null)
        {
            if (row.Json != json) throw new StorageCorruptionException("An immutable operation receipt changed.");
            return 0;
        }
        db.Set<RepositoryReceiptRow>().Add(new() { ResourceId = id, Lifecycle = operation.LifecycleGeneration,
            Family = (int)operation.Family, Partition = operation.PartitionKind, OperationHash = Hash(operation.OperationKey), Json = json });
        return Encoding.UTF8.GetByteCount(json);
    }
    private static async Task<long> AddRevisionAsync(TContext db, DocumentHead head, DocumentRevisionRecord revision, CancellationToken ct)
    {
        if (revision.ResourceId != head.ResourceId || revision.LifecycleGeneration != head.LifecycleGeneration || revision.RevisionNumber == 0 ||
            revision.Package.RevisionNumber != revision.RevisionNumber)
            throw new StorageCorruptionException("Application revision identity disagrees with its document.");
        ValidatePackage(head, revision.Package);
        var json = Encode(revision);
        var existing = await db.Set<RepositoryRevisionRow>().FindAsync([head.ResourceId, head.LifecycleGeneration, (decimal)revision.RevisionNumber], ct);
        if (existing is not null)
        {
            if (existing.Json != json) throw new StorageCorruptionException("An immutable application revision changed.");
            return 0;
        }
        db.Set<RepositoryRevisionRow>().Add(new() { ResourceId = head.ResourceId, Lifecycle = head.LifecycleGeneration,
            RevisionNumber = revision.RevisionNumber, Json = json });
        return Encoding.UTF8.GetByteCount(json);
    }
    private static async Task<long> WriteControlAsync(TContext db, DocumentControlRead control, CancellationToken ct)
    {
        var id = control.Head.ResourceId;
        long metadata = 0;
        var coordination = await db.Set<RepositoryCoordinationRow>().FindAsync([id], ct);
        var coordinationJson = Encode(control.Coordination);
        metadata = Encoding.UTF8.GetByteCount(coordinationJson) - (coordination is null ? 0 : Encoding.UTF8.GetByteCount(coordination.Json));
        if (coordination is null) db.Set<RepositoryCoordinationRow>().Add(new() { ResourceId = id, Json = coordinationJson });
        else coordination.Json = coordinationJson;
        var existing = await db.Set<RepositoryEditorRow>().Where(row => row.ResourceId == id).ToDictionaryAsync(row => row.ClientId, ct);
        foreach (var editor in control.Editors)
        {
            var json = Encode(editor);
            if (existing.Remove(editor.ClientId, out var row))
            { metadata = checked(metadata + Encoding.UTF8.GetByteCount(json) - Encoding.UTF8.GetByteCount(row.Json)); row.Json = json; row.ExpiresUtc = editor.ExpiresUtc; }
            else
            { metadata = checked(metadata + Encoding.UTF8.GetByteCount(json)); db.Set<RepositoryEditorRow>().Add(new() { ResourceId = id, ClientId = editor.ClientId, ExpiresUtc = editor.ExpiresUtc, Json = json }); }
        }
        metadata = checked(metadata - existing.Values.Sum(row => (long)Encoding.UTF8.GetByteCount(row.Json)));
        db.Set<RepositoryEditorRow>().RemoveRange(existing.Values);
        foreach (var partition in control.Partitions)
        {
            var row = await db.Set<RepositoryPartitionRow>().FindAsync([id, control.Head.LifecycleGeneration, partition.Kind], ct);
            if (row is null) continue;
            var json = Encode(partition);
            metadata = checked(metadata + Encoding.UTF8.GetByteCount(json) - Encoding.UTF8.GetByteCount(row.Json));
            row.Json = json;
        }
        return metadata;
    }
    private static async Task<bool> ClaimPathAsync(TContext db, Guid id, string key, bool allowOwned, CancellationToken ct)
    {
        var hash = Hash(key);
        var claim = await db.Set<RepositoryPathRow>().FindAsync([hash], ct);
        if (claim is not null)
        {
            if (claim.PathKey != key) throw new StorageCorruptionException("A path hash collision was detected.");
            return allowOwned && claim.ResourceId == id;
        }
        db.Set<RepositoryPathRow>().Add(new() { PathHash = hash, PathKey = key, ResourceId = id });
        return true;
    }
    private async Task<DocumentHead> CreateRowsAsync(TContext db, PreparedDocumentCreate create, DateTime now, CancellationToken ct)
    {
        var head = create.Head with { CoordinationGeneration = create.Coordination.Generation,
            ActiveEditors = create.Editors.Count(editor => editor.ExpiresUtc > now),
            NextEditorExpiryUtc = create.Editors.IsEmpty ? null : create.Editors.Min(editor => editor.ExpiresUtc) };
        if (head.ResourceId == Guid.Empty || head.LifecycleGeneration <= 0 || head.StateVersion != 0 || head.IsDeleted || head.LatestRevisionNumber == 0 ||
            head.PathKey != StorageIds.PathKey(head.Path) || create.Partitions.Select(p => p.After.Kind).Distinct().Count() != create.Partitions.Length)
            throw new InvalidDataException("Invalid prepared document identity.");
        if (create.InitialRevision.RevisionNumber != head.LatestRevisionNumber || create.InitialRevision.ContentVersion != head.ContentVersion || create.InitialRevision.Package != head.Package)
            throw new InvalidDataException("The initial application revision must describe the current package.");
        if (Encode(create.InitialRevision.Partitions.OrderBy(partition => partition.Kind)) !=
            Encode(create.Partitions.Select(partition => partition.After).Where(partition => partition.Kind != 2).OrderBy(partition => partition.Kind)))
            throw new InvalidDataException("The initial application revision must select the prepared file and metadata partition heads.");
        ValidatePackage(head, head.Package);
        StorageLimits.Check("document bytes", head.Package.Length, limits.MaxDocumentBytes);
        var row = new RepositoryHeadRow { ResourceId = head.ResourceId, PathKey = head.PathKey, PathHash = Hash(head.PathKey), Json = "" };
        db.Set<RepositoryHeadRow>().Add(row);
        long metadataBytes = Encoding.UTF8.GetByteCount(head.PathKey) + await WriteControlAsync(db, new(head, head.CreatedUtc, create.Coordination, create.Editors, create.Partitions.Select(p => p.After).ToImmutableArray()), ct);
        foreach (var partition in create.Partitions) metadataBytes = checked(metadataBytes + (await WritePartitionAsync(db, head, partition, initial: true, ct)).MetadataBytes);
        metadataBytes = checked(metadataBytes + await AddRevisionAsync(db, head, create.InitialRevision, ct));
        var contentBytes = await AddReferencesAsync(db, head.ResourceId, create.Partitions.SelectMany(p => p.AddedElements).Select(e => e.Payload), ct);
        var counters = new DocumentCounters
        {
            StoredBytes = checked(contentBytes + metadataBytes),
            GraphBytes = create.Partitions.Sum(p => p.After.ElementBytes), GraphElements = create.Partitions.Sum(p => p.After.ElementCount), Revisions = 1,
        };
        CheckCounters(counters);
        head = head with { Counters = counters };
        return SetHead(row, head);
    }
    public async ValueTask<CreateResult> CreateAsync(PreparedDocumentCreate create, CancellationToken cancellationToken = default)
    {
        await ValidatePayloadsAsync(create.Partitions, cancellationToken, initial: true);
        try
        {
            return await MutateAsync(create.Head.ResourceId, async (db, row, now) =>
            {
                if (row is not null) return new CreateResult(false, Head(row));
                if (!await ClaimPathAsync(db, create.Head.ResourceId, create.Head.PathKey, false, cancellationToken)) return new(false, null);
                var head = await CreateRowsAsync(db, create, now, cancellationToken);
                return new CreateResult(true, head);
            }, cancellationToken);
        }
        catch (RepositoryConflictException) { return new(false, await FindAsync(new DocumentIdentity.Path(create.Head.PathKey), cancellationToken)); }
    }
    public ValueTask<ControlResult> ExecuteControlAsync(DocumentControlCommand command, CancellationToken cancellationToken = default) =>
        MutateAsync(command.ResourceId, async (db, row, now) =>
        {
            var head = row is null ? throw new KeyNotFoundException("Document does not exist.") : Head(row);
            var control = await ControlAsync(db, head, cancellationToken);
            var result = DocumentControlReducer.Apply(control with { UtcNow = now }, command, limits);
            if (result.Changed)
            {
                if (result.Current.Head.ResourceId != head.ResourceId || result.Current.Head.LifecycleGeneration != head.LifecycleGeneration)
                    throw new InvalidOperationException("Control mutations cannot change document identity.");
                var metadata = await WriteControlAsync(db, result.Current, cancellationToken);
                var next = result.Current.Head with { StateVersion = checked(head.StateVersion + 1), Counters = result.Current.Head.Counters with
                    { StoredBytes = checked(head.Counters.StoredBytes + metadata) } };
                next = SetHead(row!, next);
                result = result with { Current = result.Current with { Head = next } };
            }
            return result;
        }, cancellationToken);
    private async Task ValidatePayloadsAsync(IEnumerable<PreparedPartitionPublication> partitions, CancellationToken ct, bool initial = false)
    {
        using var timing = CellBridgeMetrics.Measure(CellBridgePhase.ProviderPayloadValidation);
        await using var db = database.CreateContext();
        var reader = new RepositoryContentReader<TContext>(db, validationContent, limits.MaxObjectBytes, database);
        foreach (var partition in partitions)
        {
            await DocumentPublicationRules.ValidatePartitionPayloadsAsync(partition, reader, limits, ct);
            if (partition.Checkpoint is not { } supplied) continue;
            var scope = partition.EffectiveChanges.Scope;
            var before = initial ? [] : await ReadAsync(async snapshot =>
                await snapshot.Set<RepositoryPartitionRow>().AnyAsync(row => row.ResourceId == scope.ResourceId && row.Lifecycle == scope.LifecycleGeneration &&
                    row.Partition == scope.TargetPartition, ct) ? await BindingsAsync(snapshot, scope, partition.Before.PublicationSequence, ct) : [], ct);
            var checkpoint = IndexedDocumentGraph.Checkpoint(scope, partition.Before.PublicationSequence, before);
            var after = IndexedDocumentGraph.Replay(checkpoint, [partition.EffectiveChanges], partition.After.PublicationSequence,
                new IndexedCheckpointPolicy(int.MaxValue, long.MaxValue));
            var expected = IndexedDocumentGraph.Checkpoint(scope, partition.After.PublicationSequence, after);
            if (Encode(expected) != Encode(supplied)) throw new InvalidDataException("The prepared checkpoint disagrees with its effective changes.");
        }
    }
    public async ValueTask<PublicationResult> PublishAsync(PreparedDocumentPublication publication, CancellationToken cancellationToken = default)
    {
        DocumentPublicationRules.ValidateShape(publication);
        await ValidatePayloadsAsync(publication.Partitions, cancellationToken);
        return await PublishCoreAsync(publication, cancellationToken);
    }
    private ValueTask<PublicationResult> PublishCoreAsync(PreparedDocumentPublication publication, CancellationToken cancellationToken) =>
        MutateAsync(publication.ResourceId, async (db, row, now) =>
        {
            var head = row is null || row.IsDeleted ? throw new KeyNotFoundException("Document does not exist.") : Head(row);
            var control = (await ControlAsync(db, head, cancellationToken)) with { UtcNow = now };
            if (DocumentPublicationRules.CheckAuthority(control, publication) is { } denied) return denied;
            var receipt = await ReceiptAsync(db, head.ResourceId, publication.Receipt.Operation, cancellationToken);
            if (receipt is not null) return DocumentPublicationRules.MatchReceipt(head, receipt, publication,
                control.Partitions.SingleOrDefault(partition => partition.Kind == publication.Receipt.Operation.PartitionKind));
            var selectedPartition = control.Partitions.SingleOrDefault(p => p.Kind == publication.Receipt.Operation.PartitionKind);
            var currentMappings = selectedPartition is not null
                ? await CurrentMappingsAsync(db, Scope(head, selectedPartition.Kind), selectedPartition,
                    publication.Fence.ExpectedMappings.Select(expected => expected.Key), cancellationToken)
                : publication.Fence.ExpectedMappings.Select(expected => expected.Key).Distinct().ToDictionary(key => key, _ => (IndexedStorageMapping?)null);
            if (DocumentPublicationRules.CheckFence(control, publication, currentMappings) is { } conflict) return conflict;
            long metadata = 0;
            int addedElements = 0;
            long addedElementBytes = 0;
            foreach (var partition in publication.Partitions)
            {
                var current = control.Partitions.SingleOrDefault(p => p.Kind == partition.Before.Kind);
                if (current is null || current.PublicationSequence != partition.Before.PublicationSequence || current.StorageIndex != partition.Before.StorageIndex)
                    return new PublicationResult(PublicationStatus.Reprepare, head);
            }
            var expiration = DocumentControlReducer.ExpireForPublication(control);
            if (expiration.Changed) metadata = checked(metadata + await WriteControlAsync(db, expiration.Current, cancellationToken));
            foreach (var partition in publication.Partitions)
            {
                var written = await WritePartitionAsync(db, head, partition, false, cancellationToken);
                metadata = checked(metadata + written.MetadataBytes);
                addedElements = checked(addedElements + written.AddedElements);
                addedElementBytes = checked(addedElementBytes + written.AddedElementBytes);
            }
            if (publication.Package is { } package)
            { ValidatePackage(head, package); StorageLimits.Check("document bytes", package.Length, limits.MaxDocumentBytes); }
            if (publication.Revision is { } revision)
            {
                if (revision.RevisionNumber != checked(head.LatestRevisionNumber + 1) || publication.Package != revision.Package)
                    throw new InvalidOperationException("A file publication must append the next immutable application revision.");
                metadata = checked(metadata + await AddRevisionAsync(db, head, revision, cancellationToken));
            }
            else if (publication.Package is not null) throw new InvalidOperationException("A new package requires an application revision.");
            metadata = checked(metadata + await AddReceiptAsync(db, head.ResourceId, publication.Receipt, cancellationToken));
            var references = publication.NewDocumentReferences.Concat(publication.Partitions.SelectMany(p => p.AddedElements).Select(e => e.Payload));
            if (publication.Receipt.Response is { } response) references = references.Append(response);
            var contentBytes = await AddReferencesAsync(db, head.ResourceId, references, cancellationToken);
            var counters = DocumentPublicationRules.NextCounters(head.Counters,
                publication with { CounterChange = publication.CounterChange with { StoredBytes = metadata } },
                contentBytes, addedElements, addedElementBytes);
            var filePublication = publication.Receipt.ContentVersion != head.ContentVersion;
            if (filePublication && (publication.Receipt.ContentVersion != checked(head.ContentVersion + 1) || publication.Package is null ||
                publication.Revision is null || !publication.Partitions.Any(partition => partition.After.Kind == 0)))
                throw new InvalidOperationException("A file publication must advance the file version and publish its canonical root and application revision.");
            if (!filePublication && publication.Package is { } metadataPackage &&
                (metadataPackage.Root != head.Package.Root || metadataPackage.Length != head.Package.Length || metadataPackage.Sha256 != head.Package.Sha256))
                throw new InvalidOperationException("A metadata publication cannot change the selected file root or bytes.");
            var next = expiration.Current.Head with { StateVersion = checked(head.StateVersion + 1), ModifiedUtc = filePublication ? publication.RequestedModifiedUtc ?? now : head.ModifiedUtc,
                ContentVersion = publication.Receipt.ContentVersion, Package = publication.Package ?? head.Package,
                LatestRevisionNumber = publication.Revision?.RevisionNumber ?? head.LatestRevisionNumber,
                Security = filePublication ? head.Security with { ModifiedBy = publication.Admission.Actor } : head.Security, Counters = counters };
            if (head.Delivery is not null && filePublication && (publication.DeliveryOperationId is null || publication.DeliveryOperationId == Guid.Empty))
                throw new InvalidOperationException("A destination-bound file publication requires a stable outbox operation identity.");
            if (publication.DeliveryOperationId is { } deliveryId && head.Delivery is { } binding && filePublication)
            {
                var delivery = new DocumentDelivery(deliveryId, head.ResourceId, head.LifecycleGeneration, binding.NextSequence, next.ContentVersion, next.Package);
                db.Set<RepositoryDeliveryRow>().Add(new() { ResourceId = head.ResourceId, OperationId = deliveryId, Sequence = delivery.Sequence, Json = Encode(delivery) });
                next = next with { Delivery = binding with { NextSequence = checked(binding.NextSequence + 1) }, Counters = next.Counters with
                    { PendingDeliveries = checked(next.Counters.PendingDeliveries + 1), PendingDeliveryBytes = checked(next.Counters.PendingDeliveryBytes + next.Package.Length),
                      StoredBytes = checked(next.Counters.StoredBytes + Encoding.UTF8.GetByteCount(Encode(delivery))) } };
            }
            DocumentPublicationRules.CheckLimits(next, limits);
            next = SetHead(row!, next);
            return new PublicationResult(PublicationStatus.Accepted, next) { Receipt = publication.Receipt };
        }, cancellationToken, measurePublication: true);
}
