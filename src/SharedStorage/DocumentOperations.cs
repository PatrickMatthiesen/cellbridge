using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

/// <summary>Persists document snapshots and coordinates changes across hosts.</summary>
internal sealed partial class DocumentOperations<TContext> : IDocumentStateStore, IDocumentLifecycleStore,
    IAtomicDocumentRenameStore, IDocumentReadStore, IProviderRecoveryStore, IStorageBudgetParticipant where TContext : DbContext
{
    private readonly IDocumentDatabase<TContext> database;
    private readonly StorageLimits limits;
    public bool Durable => true;
    public bool Shared => true;
    public object BudgetScope { get; }

    public DocumentOperations(IDocumentDatabase<TContext> database, StorageLimits? limits, object budgetScope)
    {
        this.database = database;
        this.limits = limits ?? new();
        this.limits.Validate();
        BudgetScope = budgetScope;
    }

    private async Task<T> MutateAsync<T>(Func<TContext, Task<T>> action,
        CancellationToken ct, bool exclusive = false, bool rename = false)
    {
        var commitStarted = false;
        try
        {
            await using var db = database.CreateContext();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await database.ConfigureAsync(db, ct);
            await database.CheckConfigurationAsync(db, ct);
            await database.Locks.PublicationAsync(db, exclusive, ct);
            var result = await action(db);
            // All writes and derived records are accepted together. Never rerun a
            // callback or replace a known commit with caller cancellation.
            commitStarted = true;
            await transaction.CommitAsync(CancellationToken.None);
            return result;
        }
        // Before COMMIT, cancellation has a known rollback even when the provider
        // includes a database exception as its inner exception.
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !commitStarted)
        { throw; }
        catch (Exception ex) when (rename && database.IsUniqueViolation(ex))
        { throw new InvalidOperationException("The destination filename already exists.", ex); }
        catch (Exception ex) when (database.IsDatabaseException(ex))
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
            var documents = db.Set<DocumentRow>().AsNoTracking();
            if (key is not null) documents = database.AtPath(documents, key);
            var row = await (from pointer in documents
                where (id == null || pointer.ResourceId == id) &&
                    (deleted || !pointer.IsDeleted)
                join snapshot in db.Set<StateRow>().AsNoTracking() on new { pointer.ResourceId, pointer.StateVersion }
                    equals new { snapshot.ResourceId, snapshot.StateVersion } into snapshots
                from snapshot in snapshots.DefaultIfEmpty()
                select new { Pointer = pointer, Data = snapshot == null ? null : snapshot.StateJson }).SingleOrDefaultAsync(ct);
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
            await database.Locks.NamespaceAsync(db, cancellationToken);
            await database.Locks.DocumentAsync(db, state.ResourceId, cancellationToken);
            var namespaceStates = await VerifyNamespaceRowsAsync(db, cancellationToken);
            if (await db.Set<DocumentRow>().AnyAsync(row => row.ResourceId == state.ResourceId, cancellationToken) ||
                IsPathReserved(state.PathKey, [], true, namespaceStates)) return false;
            var initial = state with { StateVersion = 0 };
            limits.CheckDocument(initial);
            db.Set<DocumentRow>().Add(Pointer(initial));
            await DocumentBudget.AdjustAsync(database, db, 0, 1, cancellationToken);
            await WriteSnapshotAsync(db, initial, false, cancellationToken);
            await WriteSelectiveReadsAsync(db, initial, cancellationToken);
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
        if (rename) await database.Locks.NamespaceAsync(db, ct);
        await database.Locks.DocumentAsync(db, id, ct);
        var (pointer, current) = await LoadCoordinatedAsync(db, id, ct);
        if (current.IsDeleted && !lifecycle) throw new KeyNotFoundException("Document is deleted.");
        var now = await ReadClockAsync(db, ct);
        var change = transition(current, now);
        if (change.Next is { } proposed)
        {
            if (!lifecycle) DocumentPathReservations.ValidateTransition(current, proposed, rename);
            var version = checked(current.StateVersion + 1);
            var retiredPaths = DocumentPathReservations.Capture(current, proposed, rename);
            // Normal save callbacks already set these provider-owned values.
            // Keep that immutable instance and its encoded-size calculation.
            var next = proposed.StateVersion == version && proposed.RetiredPathKeys == retiredPaths
                ? proposed : proposed with { StateVersion = version, RetiredPathKeys = retiredPaths };
            limits.CheckDocument(next);
            if (rename)
            {
                var namespaceStates = await VerifyNamespaceRowsAsync(db, ct);
                if (next.PathKey != current.PathKey && IsPathReserved(next.PathKey, [id], true, namespaceStates))
                    throw new InvalidOperationException("The destination filename is reserved.");
            }
            await WriteSelectiveReadsAsync(db, next, ct);
            // Reconcile projections before taking the global budget lock.
            await WriteSnapshotAsync(db, next, true, ct);
            UpdatePointer(pointer, next);
            await db.SaveChangesAsync(ct);
        }
        return change.Result;
    }, ct, exclusive: rename, rename: rename);

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
        await database.Locks.NamespaceAsync(db, cancellationToken);
        await database.Locks.DocumentAsync(db, retiredResourceId, cancellationToken);
        await database.Locks.DocumentAsync(db, replacement.ResourceId, cancellationToken);
        var namespaceStates = await VerifyNamespaceRowsAsync(db, cancellationToken);
        var pointer = await db.Set<DocumentRow>().SingleOrDefaultAsync(row => row.ResourceId == retiredResourceId, cancellationToken);
        if (pointer is null) return false;
        var retired = await LoadCurrentAsync(db, pointer, cancellationToken);
        if (retired.ReplacedBy == replacement.ResourceId && retired.LifecycleGeneration == expectedGeneration &&
            retired.StateVersion == expectedStateVersion + 1) return true;
        var ancestors = VerifiedLifecycleAncestors(retired, namespaceStates);
        if (!DocumentLifecycle.CanRecreate(retired, expectedGeneration, expectedStateVersion, replacement) ||
            await db.Set<DocumentRow>().AnyAsync(row => row.ResourceId == replacement.ResourceId, cancellationToken) ||
            IsPathReserved(replacement.PathKey, ancestors, false, namespaceStates)) return false;
        replacement = replacement with { StateVersion = 0 };
        var next = retired with { ReplacedBy = replacement.ResourceId, StateVersion = checked(retired.StateVersion + 1) };
        limits.CheckDocument(replacement);
        limits.CheckDocument(next);
        db.Set<DocumentRow>().Add(Pointer(replacement));
        await DocumentBudget.AdjustAsync(database, db, 0, 1, cancellationToken);
        await WriteSnapshotAsync(db, next, true, cancellationToken);
        await WriteSnapshotAsync(db, replacement, false, cancellationToken);
        await WriteSelectiveReadsAsync(db, next, cancellationToken);
        await WriteSelectiveReadsAsync(db, replacement, cancellationToken);
        UpdatePointer(pointer, next);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }, cancellationToken, exclusive: true);

    public async ValueTask CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        await ReadAsync(async db =>
        {
            await database.CheckConfigurationAsync(db, cancellationToken);
            await VerifyNamespaceRowsAsync(db, cancellationToken);
            return true;
        }, cancellationToken, coherent: true);
    }

    private Task<DateTime> ReadClockAsync(TContext db, CancellationToken ct) => database.ClockAsync(db, ct);

    private DocumentState Decode(string data, DocumentRow? pointer = null, bool strict = false, StorageLimits? decodeLimits = null)
    {
        var state = DocumentStateJson.Deserialize(data, strict, decodeLimits ?? limits);
        if (pointer is not null) ValidatePointer(state, pointer);
        return state;
    }

    private void ValidatePointer(DocumentState state, DocumentRow pointer)
    {
        if (state.ResourceId != pointer.ResourceId || state.StateVersion != pointer.StateVersion ||
            state.PathKey != pointer.PathKey || state.IsDeleted != pointer.IsDeleted)
            throw new StorageCorruptionException("Snapshot identity disagrees with its current pointer.");
        database.ValidatePointer(pointer, state);
    }

    private Microsoft.EntityFrameworkCore.Metadata.IModel? queryModel;
    private readonly Func<TContext, Guid, long, CancellationToken, Task<string?>> stateByVersion =
        EF.CompileAsyncQuery((TContext db, Guid id, long version, CancellationToken token) =>
            db.Set<StateRow>().AsNoTracking().Where(row => row.ResourceId == id && row.StateVersion == version)
                .Select(row => row.StateJson).SingleOrDefault());

    private async Task<(DocumentRow Pointer, DocumentState State)> LoadCoordinatedAsync(TContext db,
        Guid id, CancellationToken ct)
    {
        // The caller already holds document coordination. Keep this statement
        // after that lock, and keep the authoritative clock read after decoding.
        var row = await (from pointer in db.Set<DocumentRow>()
            where pointer.ResourceId == id
            join snapshot in db.Set<StateRow>() on new { pointer.ResourceId, pointer.StateVersion }
                equals new { snapshot.ResourceId, snapshot.StateVersion } into snapshots
            from snapshot in snapshots.DefaultIfEmpty()
            select new { Pointer = pointer, Data = snapshot == null ? null : snapshot.StateJson })
            .AsTracking().SingleOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Document does not exist.");
        return (row.Pointer, Decode(row.Data ?? throw new StorageCorruptionException("The current snapshot is missing."), row.Pointer));
    }

    private async Task<DocumentState> LoadCurrentAsync(TContext db, DocumentRow pointer, CancellationToken ct)
    {
        var model = db.Model;
        var bound = Interlocked.CompareExchange(ref queryModel, model, null) ?? model;
        var json = ReferenceEquals(bound, model)
            ? await stateByVersion(db, pointer.ResourceId, pointer.StateVersion, ct)
            : await db.Set<StateRow>().AsNoTracking().Where(row => row.ResourceId == pointer.ResourceId && row.StateVersion == pointer.StateVersion)
                .Select(row => row.StateJson).SingleOrDefaultAsync(ct);
        return Decode(json ?? throw new StorageCorruptionException("The current snapshot is missing."), pointer);
    }

    private async Task WriteSnapshotAsync(TContext db, DocumentState state, bool prune, CancellationToken ct)
    {
        var bytes = await StageSnapshotAsync(db, state, prune, ct);
        await DocumentBudget.AdjustAsync(database, db, bytes, 0, ct);
    }

    private async Task<long> StageSnapshotAsync(TContext db, DocumentState state, bool prune, CancellationToken ct)
    {
        long freed = 0;
        if (prune)
        {
            var expired = db.Set<StateRow>().Where(row => row.ResourceId == state.ResourceId && row.StateVersion <= state.StateVersion - limits.MaxRetainedStateSnapshots);
            freed = await expired.SumAsync(row => (long?)DocumentSql.ByteLength(row.StateJson), ct) ?? 0;
            if (freed != 0) await expired.ExecuteDeleteAsync(ct);
        }
        var data = DocumentStateJson.Serialize(state);
        var bytes = checked(DocumentStateJson.GetSerializedSize(state) - freed);
        db.Set<StateRow>().Add(new() { ResourceId = state.ResourceId, StateVersion = state.StateVersion, StateJson = data });
        return bytes;
    }

    private DocumentRow Pointer(DocumentState state)
    {
        var row = new DocumentRow { ResourceId = state.ResourceId, PathKey = state.PathKey };
        UpdatePointer(row, state);
        return row;
    }

    private void UpdatePointer(DocumentRow row, DocumentState state)
    {
        row.PathKey = state.PathKey;
        row.StateVersion = state.StateVersion;
        row.IsDeleted = state.IsDeleted;
        database.SetNamespaceFields(row, state);
    }

    private async Task<Dictionary<Guid, DocumentNamespaceState>> VerifyNamespaceRowsAsync(TContext db, CancellationToken ct)
    {
        // Writers hold the publication gate. The provider read scope protects
        // the health inventory across configuration, pointer and reservation reads.
        var states = new Dictionary<Guid, DocumentNamespaceState>();
        await foreach (var row in database.ReadNamespaceRowsAsync(db, ct).WithCancellation(ct))
        {
            var state = DocumentNamespaceState.Parse(row.Json ??
                throw new StorageCorruptionException("The current snapshot is missing."), limits);
            if (state.ResourceId != row.Pointer.ResourceId || state.StateVersion != row.Pointer.StateVersion ||
                state.PathKey != row.Pointer.PathKey || state.IsDeleted != row.Pointer.IsDeleted)
                throw new StorageCorruptionException("Namespace identity disagrees with its current pointer.");
            database.ValidateNamespaceFields(row.Pointer, state);
            states.Add(state.ResourceId, state);
        }
        await database.VerifyReservationsAsync(db, states, ct);
        return states;
    }

    private static Guid[] VerifiedLifecycleAncestors(DocumentState retired, IReadOnlyDictionary<Guid, DocumentNamespaceState> states)
    {
        var ancestors = new List<Guid> { retired.ResourceId };
        var child = DocumentNamespaceState.From(retired);
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

    private static bool IsPathReserved(string key, Guid[] excluded, bool includeDeleted,
        IReadOnlyDictionary<Guid, DocumentNamespaceState> states) => states.Values.Any(state =>
            !excluded.Contains(state.ResourceId) &&
            ((includeDeleted || !state.IsDeleted) && state.PathKey == key || state.RetiredPathKeys.Contains(key)));
}
