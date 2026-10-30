using CellBridge.Diagnostics;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed partial class RelationalDocumentRepository<TContext>(IRepositoryDatabase<TContext> database, StorageLimits? configuredLimits, IContentStore? validationContent = null)
    : IDocumentRepository, IIndexedSaveSource where TContext : DbContext
{
    private readonly StorageLimits limits = ValidateLimits(configuredLimits);
    private readonly IndexedCheckpointPolicy checkpointPolicy = IndexedCheckpointPolicy.Default;
    public bool Durable => true;
    public bool Shared => true;
    private static StorageLimits ValidateLimits(StorageLimits? configured)
    {
        var result = configured ?? new(); result.Validate(); return result;
    }
    private static string Encode<T>(T value) => JsonSerializer.Serialize(value);
    private static T Decode<T>(string json) => RecoveryJson.Read<T>(json);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string ElementKey(ExtendedId id) => id.Guid.ToString("N") + id.Value.ToString("x8");
    private static string RecordKey<T>(T value) => Hash(Encode(value));
    private static IndexedGraphScope Scope(DocumentHead head, int partition) => new(head.ResourceId, head.LifecycleGeneration, partition);
    private static DocumentHead Head(RepositoryHeadRow row)
    {
        var head = Decode<DocumentHead>(row.Json);
        if (head.ResourceId != row.ResourceId || head.LifecycleGeneration != row.Lifecycle ||
            head.StateVersion != row.StateVersion || head.IsDeleted != row.IsDeleted || head.PathKey != row.PathKey || Hash(head.PathKey) != row.PathHash ||
            head.Counters.PendingDeliveries != row.PendingDeliveries || (head.Delivery?.BlockedReason is not null) != row.DeliveryBlocked)
            throw new StorageCorruptionException("Normalized document head disagrees with its indexed identity.");
        return head;
    }
    private static Task<RepositoryHeadRow?> HeadRowAsync(TContext db, Guid id, CancellationToken ct) =>
        db.Set<RepositoryHeadRow>().SingleOrDefaultAsync(row => row.ResourceId == id, ct);
    private static async Task<DocumentHead?> HeadAsync(TContext db, Guid id, CancellationToken ct)
    {
        var row = await db.Set<RepositoryHeadRow>().AsNoTracking().SingleOrDefaultAsync(row => row.ResourceId == id, ct);
        return row is null ? null : Head(row);
    }
    private async ValueTask<T> ReadAsync<T>(Func<TContext, Task<T>> read, CancellationToken ct)
    {
        try
        {
            await using var db = database.CreateContext();
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction openedTransaction;
            using (CellBridgeMetrics.Measure(CellBridgePhase.CoherentReadConnectionAndTransaction))
                openedTransaction = await db.Database.BeginTransactionAsync(database.ReadIsolation, ct);
            await using var transaction = openedTransaction;
            await database.ConfigureAsync(db, ct);
            var result = await read(db);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (database.IsDatabaseException(error))
        { throw new StorageUnavailableException("The normalized document read failed.", error); }
    }
    // A single head statement and exact immutable identities need no transaction
    // shared with another mutable record. Keep one connection for chunked reads.
    // EF's open count preserves an explicitly supplied caller-owned connection.
    private async ValueTask<T> ReadWithoutTransactionAsync<T>(Func<TContext, Task<T>> read, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await using var db = database.CreateContext();
            using (CellBridgeMetrics.Measure(CellBridgePhase.ImmutableReadConnection))
                await db.Database.OpenConnectionAsync(ct);
            try
            {
                var result = await read(db);
                ct.ThrowIfCancellationRequested();
                return result;
            }
            finally { await db.Database.CloseConnectionAsync(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (database.IsDatabaseException(error))
        { throw new StorageUnavailableException("The normalized document read failed.", error); }
    }
    public ValueTask<DocumentHead?> FindAsync(DocumentIdentity identity, CancellationToken cancellationToken = default)
    {
        if (identity is DocumentIdentity.Resource resource)
            return ReadWithoutTransactionAsync(async db =>
            {
                var head = await HeadAsync(db, resource.ResourceId, cancellationToken);
                return head is not null && (resource.IncludeDeleted || !head.IsDeleted) ? head : null;
            }, cancellationToken);
        return ReadAsync(async db =>
        {
            var path = (DocumentIdentity.Path)identity;
            var hash = Hash(path.PathKey);
            var claim = await db.Set<RepositoryPathRow>().AsNoTracking().SingleOrDefaultAsync(row => row.PathHash == hash, cancellationToken);
            if (claim is null) return null;
            if (claim.PathKey != path.PathKey) throw new StorageCorruptionException("A document path hash collision was detected.");
            var selected = await HeadAsync(db, claim.ResourceId, cancellationToken);
            return selected is { IsDeleted: false } && selected.PathKey == path.PathKey ? selected : null;
        }, cancellationToken);
    }
    public ValueTask<IReadOnlyList<DocumentHead>> ListAsync(int offset, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return ReadAsync<IReadOnlyList<DocumentHead>>(async db =>
            (await db.Set<RepositoryHeadRow>().AsNoTracking().Where(row => !row.IsDeleted)
                .OrderBy(row => row.PathKey).Skip(offset).Take(limit).ToArrayAsync(cancellationToken)).Select(Head).ToArray(), cancellationToken);
    }
    private static Task<RepositoryPartitionRow[]> PartitionRowsAsync(TContext db, DocumentHead head, CancellationToken ct) =>
        db.Set<RepositoryPartitionRow>().AsNoTracking().Where(row => row.ResourceId == head.ResourceId && row.Lifecycle == head.LifecycleGeneration)
            .OrderBy(row => row.Partition).ToArrayAsync(ct);
    private static PartitionHead ReadPartitionHead(DocumentHead head, RepositoryPartitionRow row)
    {
        var partition = Decode<PartitionHead>(row.Json);
        if (row.ResourceId != head.ResourceId || row.Lifecycle != head.LifecycleGeneration || partition.Kind != row.Partition ||
            partition.PublicationSequence != row.Sequence)
            throw new StorageCorruptionException("A partition head disagrees with its indexed identity.");
        return partition;
    }
    private async Task<DocumentControlRead> ControlAsync(TContext db, DocumentHead head, CancellationToken ct,
        IReadOnlyList<RepositoryPartitionRow>? knownPartitions = null, DateTime? knownNow = null)
    {
        var rows = await database.ReadControlRowsAsync(db, head.ResourceId, head.LifecycleGeneration,
            includePartitions: knownPartitions is null, includeClock: knownNow is null, ct);
        var coordinationRow = rows.SingleOrDefault(row => row.Kind == 1)
            ?? throw new StorageCorruptionException("Missing normalized coordination record.");
        var coordination = ControlJson(coordinationRow);
        var decodedCoordination = Decode<CoordinationState>(coordination);
        if (head.CoordinationGeneration != decodedCoordination.Generation)
            throw new StorageCorruptionException("The document head disagrees with its coordination generation.");
        var partitions = knownPartitions ?? rows.Where(row => row.Kind == 3).Select(row => new RepositoryPartitionRow
        {
            ResourceId = head.ResourceId, Lifecycle = head.LifecycleGeneration,
            Partition = row.Partition ?? throw new StorageCorruptionException("Missing control partition identity."),
            Sequence = row.Sequence ?? throw new StorageCorruptionException("Missing control partition sequence."),
            Json = ControlJson(row),
        }).ToArray();
        var now = knownNow ?? DateTime.SpecifyKind(rows.Single(row => row.Kind == 0).UtcNow
            ?? throw new StorageCorruptionException("Missing provider control time."), DateTimeKind.Utc);
        return new(head, now, decodedCoordination,
            rows.Where(row => row.Kind == 2).Select(row => Decode<EditorState>(ControlJson(row))).ToImmutableArray(),
            partitions.Select(row => ReadPartitionHead(head, row)).ToImmutableArray());
    }
    private static string ControlJson(RepositoryControlRow row) => row.Json ?? (row.EncodedJson is { } bytes
        ? DocumentEncoding.Decode(bytes) : throw new StorageCorruptionException("Missing control metadata."));
    public ValueTask<DocumentControlRead?> ReadControlAsync(Guid id, CancellationToken cancellationToken = default) =>
        ReadAsync(async db => await HeadAsync(db, id, cancellationToken) is { IsDeleted: false } head
            ? await ControlAsync(db, head, cancellationToken) : null, cancellationToken);
    public ValueTask<DocumentRevisionPage?> ReadRevisionsAsync(Guid id, int offset, int limit, ulong? beforeRevision = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 256);
        return ReadAsync(async db =>
        {
            var head = await HeadAsync(db, id, cancellationToken);
            if (head is null || head.IsDeleted) return null;
            var query = db.Set<RepositoryRevisionRow>().AsNoTracking()
                .Where(row => row.ResourceId == id && row.Lifecycle == head.LifecycleGeneration);
            if (beforeRevision is { } bound)
            {
                var value = (decimal)bound;
                query = query.Where(row => row.RevisionNumber < value);
            }
            var rows = await query.OrderByDescending(row => row.RevisionNumber).Skip(offset).Take(beforeRevision.HasValue ? limit + 1 : limit)
                .Select(row => new { row.Json, row.RevisionNumber }).ToArrayAsync(cancellationToken);
            var count = Math.Min(limit, rows.Length);
            var hasMore = beforeRevision.HasValue ? rows.Length > limit : offset + count < head.Counters.Revisions;
            return new DocumentRevisionPage(head, rows.Take(count).Select(row => ReadRevisionRecord(head, row.RevisionNumber, row.Json)).ToImmutableArray(),
                hasMore ? offset + count : null);
        }, cancellationToken);
    }
    private static async Task<DocumentRevisionRecord?> RevisionAsync(TContext db, DocumentHead head, ulong number, CancellationToken ct)
    {
        var value = (decimal)number;
        var json = await db.Set<RepositoryRevisionRow>().AsNoTracking().Where(row => row.ResourceId == head.ResourceId &&
            row.Lifecycle == head.LifecycleGeneration && row.RevisionNumber == value).Select(row => row.Json).SingleOrDefaultAsync(ct);
        return json is null ? null : ReadRevisionRecord(head, number, json);
    }
    private static DocumentRevisionRecord ReadRevisionRecord(DocumentHead head, decimal number, string json)
    {
        var revision = Decode<DocumentRevisionRecord>(json);
        if (number is < 1 or > ulong.MaxValue || revision.ResourceId != head.ResourceId || revision.LifecycleGeneration != head.LifecycleGeneration ||
            revision.RevisionNumber != (ulong)number || revision.Package.RevisionNumber != revision.RevisionNumber || revision.ContentVersion == 0)
            throw new StorageCorruptionException("An application revision disagrees with its indexed identity.");
        ValidatePackage(head, revision.Package);
        return revision;
    }
    public ValueTask<DocumentRevisionSelection?> ReadRevisionAsync(Guid id, ulong number, CancellationToken cancellationToken = default) =>
        ReadAsync(async db => await HeadAsync(db, id, cancellationToken) is { } head
            ? new DocumentRevisionSelection(head, await RevisionAsync(db, head, number, cancellationToken)) : null, cancellationToken);
    private static async Task<DocumentOperationReceipt?> ReceiptAsync(TContext db, Guid id, SaveOperationIdentity operation, CancellationToken ct)
    {
        var key = Hash(operation.OperationKey);
        var json = await db.Set<RepositoryReceiptRow>().AsNoTracking().Where(row => row.ResourceId == id &&
            row.Lifecycle == operation.LifecycleGeneration && row.Family == (int)operation.Family && row.Partition == operation.PartitionKind &&
            row.OperationHash == key).Select(row => row.Json).SingleOrDefaultAsync(ct);
        var receipt = json is null ? null : Decode<DocumentOperationReceipt>(json);
        if (receipt is not null && receipt.Operation != operation) throw new StorageCorruptionException("A receipt identity hash collision was detected.");
        return receipt;
    }
    public ValueTask<SaveReceiptRead?> ReadReceiptAsync(Guid id, SaveOperationIdentity operation, CancellationToken cancellationToken = default) =>
        ReadAsync(async db => await HeadAsync(db, id, cancellationToken) is { IsDeleted: false } head
            ? new SaveReceiptRead(head, await ReceiptAsync(db, id, operation, cancellationToken)) : null, cancellationToken);
    private static IQueryable<RepositoryGraphRow> GraphRows(TContext db, IndexedGraphScope scope, string kind, string owner) =>
        db.Set<RepositoryGraphRow>().AsNoTracking().Where(row => row.ResourceId == scope.ResourceId && row.Lifecycle == scope.LifecycleGeneration &&
            row.Partition == scope.TargetPartition && row.Kind == kind && row.OwnerKey == owner);
    private static async Task<ImmutableArray<T>> GraphRecordsAsync<T>(TContext db, IndexedGraphScope scope, string kind, string owner, CancellationToken ct) =>
        (await GraphRows(db, scope, kind, owner).OrderBy(row => row.RecordKey).Select(row => row.Json).ToArrayAsync(ct)).Select(Decode<T>).ToImmutableArray();
    public ValueTask<IndexedStorageMapping?> FindMappingAsync(IndexedGraphScope scope, ExtendedId index, IndexedMappingKey key, CancellationToken cancellationToken = default) =>
        ReadAsync(db => MappingAsync(db, scope, index, key, cancellationToken), cancellationToken);
    ValueTask<IndexedRevisionManifest?> IIndexedGraphSource.ReadRevisionAsync(IndexedGraphScope scope, ExtendedId manifest, CancellationToken ct) =>
        ReadWithoutTransactionAsync(async db => (await GraphRecordsAsync<IndexedRevisionManifest>(db, scope, "revision", ElementKey(manifest), ct)).SingleOrDefault(), ct);
    public ValueTask<ImmutableArray<IndexedObjectDefinition>> ReadGroupAsync(IndexedGraphScope scope, ExtendedId group, CancellationToken cancellationToken = default) =>
        ReadAsync(async db =>
        {
            var records = await GraphRecordsAsync<IndexedObjectDefinition>(db, scope, "object", ElementKey(group), cancellationToken);
            var elementJson = records.IsEmpty ? await db.Set<RepositoryElementRow>().Where(row => row.ResourceId == scope.ResourceId && row.Lifecycle == scope.LifecycleGeneration &&
                row.Partition == scope.TargetPartition && row.ElementKey == ElementKey(group)).Select(row => row.Json).SingleOrDefaultAsync(cancellationToken) : null;
            return records.IsEmpty && (elementJson is null || Decode<GraphElementState>(elementJson).Type != (uint)CellBridge.FssHttpB.DataElementType.ObjectGroupDataElementData)
                ? default : records;
        }, cancellationToken);
    private async Task<PartitionRead?> PartitionAsync(TContext db, DocumentHead head, PartitionReadRequest request, CancellationToken ct)
    {
        PartitionHead? partition;
        RepositoryPartitionRow? current = null;
        if (request.RevisionNumber is { } number)
            partition = (await RevisionAsync(db, head, number, ct))?.Partitions.SingleOrDefault(p => p.Kind == request.PartitionKind);
        else
        {
            current = await db.Set<RepositoryPartitionRow>().AsNoTracking().SingleOrDefaultAsync(row => row.ResourceId == head.ResourceId &&
                row.Lifecycle == head.LifecycleGeneration && row.Partition == request.PartitionKind, ct);
            partition = current is null ? null : ReadPartitionHead(head, current);
        }
        if (partition is null) return null;
        return await ProjectPartitionAsync(db, head, partition, request, ct, current?.Sequence);
    }
    private async Task<PartitionRead?> ProjectPartitionAsync(TContext db, DocumentHead head, PartitionHead partition,
        PartitionReadRequest request, CancellationToken ct, long? knownCurrentSequence = null)
    {
        var scope = Scope(head, partition.Kind);
        var sequence = partition.PublicationSequence;
        if (request.StorageIndex is { } requested)
        {
            var key = ElementKey(requested);
            var root = await db.Set<RepositoryMappingRootRow>().AsNoTracking().SingleOrDefaultAsync(row => row.ResourceId == scope.ResourceId &&
                row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition && row.IndexKey == key, ct);
            if (root is null) return null;
            var historical = ReadMappingRoot(scope, requested, partition.Identity, root);
            sequence = root.Sequence;
            partition = historical;
        }
        var visible = from element in db.Set<RepositoryElementRow>().AsNoTracking()
            join membership in db.Set<RepositoryVisibilityRow>().AsNoTracking()
            on new { element.ResourceId, element.Lifecycle, element.Partition, element.ElementKey }
            equals new { membership.ResourceId, membership.Lifecycle, membership.Partition, membership.ElementKey }
            where element.ResourceId == head.ResourceId && element.Lifecycle == head.LifecycleGeneration && element.Partition == partition.Kind &&
                membership.StartSequence <= sequence && (membership.EndSequence == null || membership.EndSequence > sequence)
            select element;
        var selected = new Dictionary<string, GraphElementState>(StringComparer.Ordinal);
        if (request.IncludeCatalog)
            foreach (var json in await visible.OrderBy(row => row.ElementKey).Select(row => row.Json).ToArrayAsync(ct))
            { var element = Decode<GraphElementState>(json); selected[ElementKey(element.Id)] = element; }
        else
        {
            if (!request.ElementIds.IsDefaultOrEmpty)
            {
                var keys = request.ElementIds.Select(ElementKey).ToArray();
                foreach (var json in await visible.Where(row => keys.Contains(row.ElementKey)).Select(row => row.Json).ToArrayAsync(ct))
                { var element = Decode<GraphElementState>(json); selected[ElementKey(element.Id)] = element; }
            }
            foreach (var range in request.SerialRanges)
                foreach (var json in await visible.Where(row => row.SerialGuid == range.Namespace && row.SerialValue >= (decimal)range.First &&
                    row.SerialValue <= (decimal)range.Last).Select(row => row.Json).ToArrayAsync(ct))
                { var element = Decode<GraphElementState>(json); selected[ElementKey(element.Id)] = element; }
        }
        var bindings = request.IncludeBindings ? await BindingsAsync(db, scope, sequence, ct, knownCurrentSequence) : [];
        return new(head, partition, selected.Values.ToImmutableArray(), bindings);
    }
    public ValueTask<PartitionRead?> ReadPartitionAsync(Guid id, PartitionReadRequest request, CancellationToken cancellationToken = default) =>
        ReadAsync(async db => await HeadAsync(db, id, cancellationToken) is { } head && (!head.IsDeleted || request.RevisionNumber is not null)
            ? await PartitionAsync(db, head, request, cancellationToken) : null, cancellationToken);
    public ValueTask<SavePreparationRead?> PrepareSaveAsync(Guid id, SavePreparationRequest request, CancellationToken cancellationToken = default) =>
        ReadAsync(async db =>
        {
            var head = await HeadAsync(db, id, cancellationToken);
            if (head is null || head.IsDeleted) return null;
            var rows = await PartitionRowsAsync(db, head, cancellationToken);
            var control = await ControlAsync(db, head, cancellationToken, rows);
            var selected = control.Partitions.SingleOrDefault(partition => partition.Kind == request.PartitionKind);
            if (selected is null) return null;
            var position = rows.Single(row => row.Partition == request.PartitionKind);
            var partition = (await ProjectPartitionAsync(db, head, selected, new(request.PartitionKind)
                { IncludeCatalog = request.IncludeCatalog, IncludeBindings = request.IncludeBindings }, cancellationToken, position.Sequence))!;
            var mappings = await CurrentMappingsAsync(db, Scope(head, selected.Kind), selected, request.AffectedMappings, cancellationToken);
            partition = partition with { Mappings = request.AffectedMappings.Select(key => mappings[key]).OfType<IndexedStorageMapping>().ToImmutableArray() };
            return new SavePreparationRead(control, partition, await ReceiptAsync(db, id, request.Operation, cancellationToken))
                { ReplayPosition = new(position.CheckpointSequence, position.ReplayChanges, position.ReplayBytes) };
        }, cancellationToken);
    private async Task<ImmutableArray<IndexedObjectBinding>> BindingsAsync(TContext db, IndexedGraphScope scope, long sequence, CancellationToken ct,
        long? knownCurrentSequence = null)
    {
        var currentSequence = knownCurrentSequence ?? await db.Set<RepositoryPartitionRow>().AsNoTracking().Where(row => row.ResourceId == scope.ResourceId &&
            row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition).Select(row => (long?)row.Sequence).SingleOrDefaultAsync(ct)
            ?? throw new StorageCorruptionException("Missing partition binding root.");
        if (currentSequence == sequence)
            return (await db.Set<RepositoryBindingRow>().AsNoTracking().Where(row => row.ResourceId == scope.ResourceId && row.Lifecycle == scope.LifecycleGeneration &&
                row.Partition == scope.TargetPartition).Select(row => row.Json).ToArrayAsync(ct)).Select(Decode<IndexedObjectBinding>).ToImmutableArray();
        var checkpointSequence = await db.Set<RepositoryCheckpointRow>().AsNoTracking().Where(row => row.ResourceId == scope.ResourceId && row.Lifecycle == scope.LifecycleGeneration &&
            row.Partition == scope.TargetPartition && row.Sequence <= sequence).MaxAsync(row => (long?)row.Sequence, ct)
            ?? throw new StorageCorruptionException("Historical bindings have no checkpoint.");
        var checkpointJson = await db.Set<RepositoryCheckpointRow>().AsNoTracking().Where(row => row.ResourceId == scope.ResourceId && row.Lifecycle == scope.LifecycleGeneration &&
            row.Partition == scope.TargetPartition && row.Sequence == checkpointSequence && row.BindingKey != "").Select(row => row.Json).ToArrayAsync(ct);
        var checkpoint = IndexedDocumentGraph.Checkpoint(scope, checkpointSequence, checkpointJson.Select(Decode<IndexedObjectBinding>));
        var headers = await db.Set<RepositoryDeltaRow>().AsNoTracking().Where(row => row.ResourceId == scope.ResourceId && row.Lifecycle == scope.LifecycleGeneration &&
            row.Partition == scope.TargetPartition && row.Sequence > checkpointSequence && row.Sequence <= sequence).OrderBy(row => row.Sequence).ToArrayAsync(ct);
        if (headers.Length > checkpointPolicy.MaxReplayChanges || headers.Sum(row => row.EncodedBytes) > checkpointPolicy.MaxReplayBytes)
            throw new StorageCorruptionException("Historical binding replay exceeds its checkpoint bound.");
        var tail = new List<IndexedBindingDelta>();
        foreach (var header in headers)
        {
            var changes = await db.Set<RepositoryBindingChangeRow>().AsNoTracking().Where(row => row.ResourceId == scope.ResourceId && row.Lifecycle == scope.LifecycleGeneration &&
                row.Partition == scope.TargetPartition && row.Sequence == header.Sequence).Select(row => row.Json).ToArrayAsync(ct);
            var ordered = changes.Select(Decode<IndexedBindingChange>).OrderBy(change => change.Key.Cell.LongId.Guid).ThenBy(change => change.Key.Cell.LongId.Value)
                .ThenBy(change => change.Key.Cell.ShortId.Guid).ThenBy(change => change.Key.Cell.ShortId.Value).ThenBy(change => change.Key.ObjectId.Guid)
                .ThenBy(change => change.Key.ObjectId.Value).ThenBy(change => change.Key.ObjectPartitionId).ToImmutableArray();
            if (ordered.Length != header.ChangeCount) throw new StorageCorruptionException("A historical binding delta is incomplete.");
            tail.Add(new(scope, header.PreviousSequence, header.Sequence, ordered, header.EncodedBytes));
        }
        return IndexedDocumentGraph.Replay(checkpoint, tail, sequence, checkpointPolicy);
    }
    public ValueTask CheckHealthAsync(CancellationToken cancellationToken = default) => new(ReadAsync(async db =>
        { await database.CheckHealthAsync(db, cancellationToken); return true; }, cancellationToken).AsTask());
}
