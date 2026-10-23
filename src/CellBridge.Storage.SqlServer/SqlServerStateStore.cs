using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.SqlServer;

/// <summary>Detached document snapshots with explicit cross-host coordination.</summary>
public sealed partial class SqlServerStateStore : IDocumentStateStore, IDocumentLifecycleStore,
    IAtomicDocumentRenameStore, IDocumentReadStore, IProviderRecoveryStore, IStorageBudgetParticipant
{
    private readonly DbContextOptions<SqlServerDocumentDbContext> options;
    private readonly StorageLimits limits;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const string NamespaceGate = "CellBridge.Documents.Namespace";
    public bool Durable => true;
    public bool Shared => true;
    public object BudgetScope { get; }

    public SqlServerStateStore(DbContextOptions<SqlServerDocumentDbContext> options, StorageLimits? limits = null)
    {
        this.options = SqlServerDocumentDatabase.Normalize(options);
        this.limits = limits ?? new();
        this.limits.Validate();
        BudgetScope = options;
    }

    private static byte[] PathHash(string path) => SHA256.HashData(Encoding.BigEndianUnicode.GetBytes(path));
    private static Task LockDocumentAsync(SqlServerDocumentDbContext db, Guid id, CancellationToken ct) =>
        SqlServerDocumentDatabase.LockAsync(db, "CellBridge.Document." + id.ToString("N"), ct);

    private async Task<T> MutateAsync<T>(Func<SqlServerDocumentDbContext, Task<T>> action,
        CancellationToken ct, bool exclusive = false)
    {
        try
        {
            await using var db = new SqlServerDocumentDbContext(options);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await SqlServerDocumentDatabase.ConfigureTransactionAsync(db, ct);
            await SqlServerDocumentDatabase.CheckConfigurationAsync(db, ct);
            await SqlServerDocumentDatabase.LockAsync(db, SqlServerDocumentDatabase.PublicationGate, ct, exclusive ? "Exclusive" : "Shared");
            var result = await action(db);
            // All writes and derived records are accepted together. Never rerun a
            // callback or replace a known commit with caller cancellation.
            await transaction.CommitAsync(CancellationToken.None);
            return result;
        }
        catch (Exception ex) when (SqlServerDocumentDatabase.IsDatabaseException(ex))
        { throw new StorageUnavailableException("The document operation failed or its commit outcome is unknown. Retry with the same operation identity.", ex); }
    }

    public ValueTask<DocumentState?> FindByResourceIdAsync(Guid resourceId, CancellationToken cancellationToken = default) =>
        FindAsync(resourceId, null, false, cancellationToken);
    public ValueTask<DocumentState?> FindByPathKeyAsync(string pathKey, CancellationToken cancellationToken = default) =>
        FindAsync(null, pathKey, false, cancellationToken);
    public ValueTask<DocumentState?> FindLifecycleAsync(Guid resourceId, CancellationToken cancellationToken = default) =>
        FindAsync(resourceId, null, true, cancellationToken);

    private async ValueTask<DocumentState?> FindAsync(Guid? id, string? key, bool deleted, CancellationToken ct)
    {
        return await ReadAsync(async db =>
        {
            var hash = key is null ? null : PathHash(key);
            var row = await (from pointer in db.Documents.AsNoTracking()
                where (id == null || pointer.ResourceId == id) &&
                    (key == null || pointer.PathHash == hash && pointer.PathKey == key) && (deleted || !pointer.IsDeleted)
                join snapshot in db.Snapshots.AsNoTracking() on new { pointer.ResourceId, pointer.StateVersion }
                    equals new { snapshot.ResourceId, snapshot.StateVersion } into snapshots
                from snapshot in snapshots.DefaultIfEmpty()
                select new { Pointer = pointer, Data = snapshot == null ? null : snapshot.Data }).SingleOrDefaultAsync(ct);
            if (row is null)
            {
                if (key is not null) await VerifyNamespaceRowsAsync(db, ct);
                return null;
            }
            return Decode(row.Data ?? throw new StorageCorruptionException("The current snapshot is missing."), row.Pointer);
        }, ct);
    }

    public async ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken cancellationToken = default)
    {
        DocumentPathReservations.ValidateCreate(state);
        return await MutateAsync(async db =>
        {
            await SqlServerDocumentDatabase.LockAsync(db, NamespaceGate, cancellationToken);
            await LockDocumentAsync(db, state.ResourceId, cancellationToken);
            await VerifyNamespaceRowsAsync(db, cancellationToken);
            if (await db.Documents.AnyAsync(row => row.ResourceId == state.ResourceId, cancellationToken) ||
                await IsPathReservedAsync(db, state.PathKey, [], true, cancellationToken)) return false;
            var initial = state with { StateVersion = 0 };
            limits.CheckDocument(initial);
            db.Documents.Add(Pointer(initial));
            await SqlServerDocumentDatabase.AdjustUsageAsync(db, 0, 1, cancellationToken);
            await WriteSnapshotAsync(db, initial, false, cancellationToken);
            await WriteIndexesAsync(db, initial, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken, exclusive: true);
    }

    public ValueTask<T> TransitionAsync<T>(Guid resourceId, Func<DocumentState, DateTime, StateTransition<T>> transition,
        CancellationToken cancellationToken = default) => TransitionCoreAsync(resourceId, transition, false, false, cancellationToken);
    public ValueTask<T> RenameAsync<T>(Guid resourceId, Func<DocumentState, DateTime, StateTransition<T>> transition,
        CancellationToken cancellationToken = default) => TransitionCoreAsync(resourceId, transition, true, false, cancellationToken);

    private async ValueTask<T> TransitionCoreAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition,
        bool rename, bool lifecycle, CancellationToken ct) => await MutateAsync(async db =>
    {
        if (rename) await SqlServerDocumentDatabase.LockAsync(db, NamespaceGate, ct);
        await LockDocumentAsync(db, id, ct);
        var pointer = await db.Documents.SingleOrDefaultAsync(row => row.ResourceId == id, ct)
            ?? throw new KeyNotFoundException("Document does not exist.");
        var current = await LoadCurrentAsync(db, pointer, ct);
        if (current.IsDeleted && !lifecycle) throw new KeyNotFoundException("Document is deleted.");
        var now = await ReadClockAsync(db, ct);
        var change = transition(current, now);
        if (change.Next is { } proposed)
        {
            if (!lifecycle) DocumentPathReservations.ValidateTransition(current, proposed, rename);
            var next = proposed with { StateVersion = checked(current.StateVersion + 1),
                RetiredPathKeys = DocumentPathReservations.Capture(current, proposed, rename) };
            limits.CheckDocument(next);
            if (rename)
            {
                await VerifyNamespaceRowsAsync(db, ct);
                if (next.PathKey != current.PathKey && await IsPathReservedAsync(db, next.PathKey, [id], true, ct))
                    throw new InvalidOperationException("The destination filename is reserved.");
            }
            await WriteSnapshotAsync(db, next, true, ct);
            await WriteIndexesAsync(db, next, ct);
            UpdatePointer(pointer, next);
            await db.SaveChangesAsync(ct);
        }
        return change.Result;
    }, ct, exclusive: rename);

    public async ValueTask<bool> TryDeleteAsync(Guid resourceId, long expectedGeneration, long expectedStateVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await TransitionCoreAsync(resourceId, (current, _) =>
            {
                var next = DocumentLifecycle.Delete(current, expectedGeneration, expectedStateVersion);
                return new StateTransition<bool>(current.IsDeleted ? null : next, next is not null);
            }, false, true, cancellationToken);
        }
        catch (KeyNotFoundException) { return false; }
    }

    public async ValueTask<bool> TryRecreateAsync(Guid retiredResourceId, long expectedGeneration, long expectedStateVersion,
        DocumentState replacement, CancellationToken cancellationToken = default) => await MutateAsync(async db =>
    {
        await SqlServerDocumentDatabase.LockAsync(db, NamespaceGate, cancellationToken);
        await LockDocumentAsync(db, retiredResourceId, cancellationToken);
        await LockDocumentAsync(db, replacement.ResourceId, cancellationToken);
        var namespaceStates = await VerifyNamespaceRowsAsync(db, cancellationToken);
        var pointer = await db.Documents.SingleOrDefaultAsync(row => row.ResourceId == retiredResourceId, cancellationToken);
        if (pointer is null) return false;
        var retired = await LoadCurrentAsync(db, pointer, cancellationToken);
        if (retired.ReplacedBy == replacement.ResourceId && retired.LifecycleGeneration == expectedGeneration &&
            retired.StateVersion == expectedStateVersion + 1) return true;
        var ancestors = VerifiedLifecycleAncestors(retired, namespaceStates);
        if (!DocumentLifecycle.CanRecreate(retired, expectedGeneration, expectedStateVersion, replacement) ||
            await db.Documents.AnyAsync(row => row.ResourceId == replacement.ResourceId, cancellationToken) ||
            await IsPathReservedAsync(db, replacement.PathKey, ancestors, false, cancellationToken)) return false;
        replacement = replacement with { StateVersion = 0 };
        var next = retired with { ReplacedBy = replacement.ResourceId, StateVersion = checked(retired.StateVersion + 1) };
        limits.CheckDocument(replacement);
        limits.CheckDocument(next);
        db.Documents.Add(Pointer(replacement));
        await SqlServerDocumentDatabase.AdjustUsageAsync(db, 0, 1, cancellationToken);
        await WriteSnapshotAsync(db, next, true, cancellationToken);
        await WriteSnapshotAsync(db, replacement, false, cancellationToken);
        await WriteIndexesAsync(db, next, cancellationToken);
        await WriteIndexesAsync(db, replacement, cancellationToken);
        UpdatePointer(pointer, next);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }, cancellationToken, exclusive: true);

    public async ValueTask CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        await ReadAsync(async db =>
        {
            await SqlServerDocumentDatabase.CheckConfigurationAsync(db, cancellationToken);
            await VerifyNamespaceRowsAsync(db, cancellationToken);
            return true;
        }, cancellationToken);
    }

    private static Task<DateTime> ReadClockAsync(SqlServerDocumentDbContext db, CancellationToken ct) =>
        ReadUtcClockAsync(db, ct);
    private static async Task<DateTime> ReadUtcClockAsync(SqlServerDocumentDbContext db, CancellationToken ct) =>
        DateTime.SpecifyKind(await db.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS Value").SingleAsync(ct), DateTimeKind.Utc);

    private DocumentState Decode(byte[] data, DocumentPointer? pointer = null, bool strict = false, StorageLimits? decodeLimits = null)
    {
        var state = SqlServerDocumentDatabase.DecodeSnapshot(data, strict, decodeLimits ?? limits);
        if (pointer is not null) ValidatePointer(state, pointer);
        return state;
    }

    internal static void ValidatePointer(DocumentState state, DocumentPointer pointer)
    {
        if (state.ResourceId != pointer.ResourceId || state.StateVersion != pointer.StateVersion ||
            state.PathKey != pointer.PathKey || state.IsDeleted != pointer.IsDeleted || state.RetiredPathKeys.Length != pointer.RetiredPathCount)
            throw new StorageCorruptionException("Snapshot identity disagrees with its current pointer.");
    }

    private async Task<DocumentState> LoadCurrentAsync(SqlServerDocumentDbContext db, DocumentPointer pointer, CancellationToken ct) =>
        Decode(await db.Snapshots.AsNoTracking().Where(row => row.ResourceId == pointer.ResourceId && row.StateVersion == pointer.StateVersion)
            .Select(row => row.Data).SingleOrDefaultAsync(ct) ?? throw new StorageCorruptionException("The current snapshot is missing."), pointer);

    private async Task WriteSnapshotAsync(SqlServerDocumentDbContext db, DocumentState state, bool prune, CancellationToken ct)
    {
        long freed = 0;
        if (prune)
        {
            var expired = db.Snapshots.Where(row => row.ResourceId == state.ResourceId && row.StateVersion <= state.StateVersion - limits.MaxRetainedStateSnapshots);
            freed = await expired.SumAsync(row => (long?)SqlServerDocumentDbContext.ByteLength(row.Data), ct) ?? 0;
            if (freed != 0) await expired.ExecuteDeleteAsync(ct);
        }
        var data = Utf8.GetBytes(DocumentStateJson.Serialize(state));
        await SqlServerDocumentDatabase.AdjustUsageAsync(db, checked(data.LongLength - freed), 0, ct);
        db.Snapshots.Add(new() { ResourceId = state.ResourceId, StateVersion = state.StateVersion, Data = data });
    }

    private static DocumentPointer Pointer(DocumentState state) => new()
    {
        ResourceId = state.ResourceId, PathKey = state.PathKey, PathHash = PathHash(state.PathKey),
        StateVersion = state.StateVersion, IsDeleted = state.IsDeleted, RetiredPathCount = state.RetiredPathKeys.Length
    };
    private static void UpdatePointer(DocumentPointer row, DocumentState state)
    {
        row.PathKey = state.PathKey; row.PathHash = PathHash(state.PathKey); row.StateVersion = state.StateVersion;
        row.IsDeleted = state.IsDeleted; row.RetiredPathCount = state.RetiredPathKeys.Length;
    }

    private async Task<Dictionary<Guid, DocumentState>> VerifyNamespaceRowsAsync(SqlServerDocumentDbContext db, CancellationToken ct)
    {
        // Admission holds the exclusive publication lock. Health uses a serializable
        // read transaction. Both see coherent current pointers and authoritative
        // namespace fields without reading any content objects or chunks.
        var heads = await (from pointer in db.Documents.AsNoTracking()
            join snapshot in db.Snapshots.AsNoTracking() on new { pointer.ResourceId, pointer.StateVersion }
                equals new { snapshot.ResourceId, snapshot.StateVersion } into snapshots
            from snapshot in snapshots.DefaultIfEmpty()
            select new { Pointer = pointer, Data = snapshot == null ? null : snapshot.Data }).ToListAsync(ct);
        var states = new Dictionary<Guid, DocumentState>();
        foreach (var row in heads)
        {
            var state = Decode(row.Data ?? throw new StorageCorruptionException("The current snapshot is missing."), row.Pointer);
            if (!row.Pointer.PathHash.AsSpan().SequenceEqual(PathHash(state.PathKey)))
                throw new StorageCorruptionException("The current path hash disagrees with its snapshot.");
            states.Add(state.ResourceId, state);
        }
        var reservations = await db.Reservations.AsNoTracking().ToListAsync(ct);
        foreach (var row in reservations)
        {
            if (!states.TryGetValue(row.ResourceId, out var state) || row.Ordinal < 0 || row.Ordinal >= state.RetiredPathKeys.Length ||
                row.PathKey != state.RetiredPathKeys[row.Ordinal] || !row.PathHash.AsSpan().SequenceEqual(PathHash(state.RetiredPathKeys[row.Ordinal])))
                throw new StorageCorruptionException("A retired path reservation disagrees with its authoritative snapshot.");
        }
        var counts = reservations.GroupBy(row => row.ResourceId).ToDictionary(group => group.Key, group => group.Count());
        if (states.Values.Any(state => state.RetiredPathKeys.Length != counts.GetValueOrDefault(state.ResourceId)))
            throw new StorageCorruptionException("The retired path reservation index is incomplete.");
        return states;
    }

    private static Guid[] VerifiedLifecycleAncestors(DocumentState retired, IReadOnlyDictionary<Guid, DocumentState> states)
    {
        var ancestors = new List<Guid> { retired.ResourceId };
        var child = retired;
        while (true)
        {
            var parents = states.Values.Where(state => state.ReplacedBy == child.ResourceId).ToArray();
            if (parents.Length == 0) return ancestors.ToArray();
            if (parents.Length != 1 || !parents[0].IsDeleted || child.LifecycleGeneration <= 1 ||
                parents[0].LifecycleGeneration != child.LifecycleGeneration - 1 || ancestors.Contains(parents[0].ResourceId))
                throw new StorageCorruptionException("The document lifecycle ancestry is inconsistent.");
            child = parents[0];
            ancestors.Add(child.ResourceId);
        }
    }

    private static async Task<bool> IsPathReservedAsync(SqlServerDocumentDbContext db, string key, Guid[] excluded,
        bool includeDeleted, CancellationToken ct)
    {
        var hash = PathHash(key);
        return await db.Documents.AnyAsync(row => !excluded.Contains(row.ResourceId) && (includeDeleted || !row.IsDeleted) &&
                row.PathHash == hash && row.PathKey == key, ct) ||
            await db.Reservations.AnyAsync(row => !excluded.Contains(row.ResourceId) && row.PathHash == hash && row.PathKey == key, ct);
    }
}
