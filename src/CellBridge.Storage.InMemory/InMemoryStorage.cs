using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.InMemory;

public sealed partial class InMemoryStateStore : IDocumentStateStore, IDocumentLifecycleStore, ILocalStorageBudgetParticipant, IAtomicDocumentRenameStore, IProviderRecoveryStore
{
    public StorageBudget Budget { get; private set; }
    public InMemoryStateStore(StorageBudget? budget = null) => Budget = budget ?? new StorageBudget();
    public void UseBudget(StorageBudget budget)
    {
        if (ReferenceEquals(Budget, budget)) return;
        if (!_documents.IsEmpty || _recoveryReceipts.Count != 0) throw new InvalidOperationException("Compose storage budgets before creating documents or recovering state.");
        Budget = budget;
    }
    private readonly ConcurrentDictionary<Guid, DocumentState> _documents = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private readonly Dictionary<string, Guid> _paths = new(StringComparer.Ordinal);
    public bool Durable => false;
    public bool Shared => false;
    public ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Budget.SyncRoot) return ValueTask.FromResult(_documents.GetValueOrDefault(id) is { IsDeleted: false } state ? state : null);
    }
    public ValueTask<DocumentState?> FindByPathKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Budget.SyncRoot) lock (_paths) return ValueTask.FromResult(_paths.TryGetValue(key, out var id) && !_documents[id].IsDeleted ? _documents[id] : null);
    }
    public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        lock (Budget.SyncRoot)
        {
            IReadOnlyList<DocumentSummary> result = _documents.Values.Where(x => !x.IsDeleted).OrderBy(x => x.PathKey, StringComparer.Ordinal)
                .Skip(offset).Take(limit).Select(x => new DocumentSummary(x.ResourceId, x.Path, x.Content.Length,
                    x.ContentVersion, x.ModifiedUtc, x.Editors.Count(e => e.ExpiresUtc > DateTime.UtcNow))
                    { Security = x.Security }).ToArray();
            return ValueTask.FromResult(result);
        }
    }
    public ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentPathReservations.ValidateCreate(state);
        lock (Budget.SyncRoot)
        {
            lock (_paths)
            {
                if (_paths.ContainsKey(state.PathKey) || _documents.ContainsKey(state.ResourceId) ||
                    _documents.Values.Any(s => s.RetiredPathKeys.Contains(state.PathKey))) return ValueTask.FromResult(false);
                var next = state with { StateVersion = 0 };
                Budget.Limits.CheckDocument(next);
                Budget.Adjust(JsonSerializer.SerializeToUtf8Bytes(next).LongLength, 1);
                _documents[state.ResourceId] = next;
                _paths.Add(state.PathKey, state.ResourceId);
                return ValueTask.FromResult(true);
            }
        }
    }
    public ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition,
        CancellationToken cancellationToken = default) => TransitionCoreAsync(id, transition, false, cancellationToken);
    public ValueTask<T> RenameAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition,
        CancellationToken cancellationToken = default) => TransitionCoreAsync(id, transition, true, cancellationToken);
    private async ValueTask<T> TransitionCoreAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition,
        bool rename, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(id, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            DocumentState current;
            lock (Budget.SyncRoot)
                current = _documents.GetValueOrDefault(id) is { IsDeleted: false } live ? live : throw new KeyNotFoundException("Document does not exist.");
            var result = transition(current, DateTime.UtcNow);
            if (result.Next is { } next)
            {
                DocumentPathReservations.ValidateTransition(current, next, rename);
                next = next with { StateVersion = checked(current.StateVersion + 1),
                    RetiredPathKeys = DocumentPathReservations.Capture(current, next, rename) };
                Budget.Limits.CheckDocument(next);
                lock (Budget.SyncRoot)
                {
                    lock (_paths)
                    {
                        if (_paths.TryGetValue(next.PathKey, out var owner) && owner != id)
                            throw new InvalidOperationException("The destination filename already exists.");
                        if (next.PathKey != current.PathKey && _documents.Values.Any(s => s.ResourceId != id &&
                            (s.PathKey == next.PathKey || s.RetiredPathKeys.Contains(next.PathKey))))
                            throw new InvalidOperationException("The destination filename is reserved.");
                        AdjustRecoveryTransition(current, next);
                        _paths.Remove(current.PathKey);
                        _paths[next.PathKey] = id;
                        _documents[id] = next;
                    }
                }
            }
            return result.Result;
        }
        finally { gate.Release(); }
    }
    public ValueTask<DocumentState?> FindLifecycleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Budget.SyncRoot) return ValueTask.FromResult(_documents.GetValueOrDefault(id));
    }
    public async ValueTask<bool> TryDeleteAsync(Guid id, long expectedGeneration, long expectedStateVersion,
        CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(id, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            lock (Budget.SyncRoot)
            {
                if (!_documents.TryGetValue(id, out var current) ||
                    DocumentLifecycle.Delete(current, expectedGeneration, expectedStateVersion) is not { } next) return false;
                if (current.IsDeleted) return true;
                next = next with { StateVersion = checked(current.StateVersion + 1) };
                Budget.Limits.CheckDocument(next);
                AdjustRecoveryTransition(current, next);
                _documents[id] = next;
                return true;
            }
        }
        finally { gate.Release(); }
    }
    public async ValueTask<bool> TryRecreateAsync(Guid id, long expectedGeneration, long expectedStateVersion,
        DocumentState replacement, CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(id, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            lock (Budget.SyncRoot)
            lock (_paths)
            {
                if (!_documents.TryGetValue(id, out var retired)) return false;
                if (retired.ReplacedBy == replacement.ResourceId && retired.LifecycleGeneration == expectedGeneration &&
                    retired.StateVersion == expectedStateVersion + 1) return true;
                if (!DocumentLifecycle.CanRecreate(retired, expectedGeneration, expectedStateVersion, replacement) ||
                    _documents.ContainsKey(replacement.ResourceId) || _paths[retired.PathKey] != id ||
                    _documents.Values.Any(s => s.ResourceId != id && s.RetiredPathKeys.Contains(replacement.PathKey))) return false;
                replacement = replacement with { StateVersion = 0 };
                var next = retired with { ReplacedBy = replacement.ResourceId, StateVersion = checked(retired.StateVersion + 1) };
                Budget.Limits.CheckDocument(replacement);
                Budget.Limits.CheckDocument(next);
                AdjustRecoveryTransition(retired, next, replacement);
                _documents[id] = next;
                _documents[replacement.ResourceId] = replacement;
                _paths[retired.PathKey] = replacement.ResourceId;
                return true;
            }
        }
        finally { gate.Release(); }
    }
    public ValueTask CheckHealthAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

public sealed class InMemoryContentStore : IContentStore, ILocalStorageBudgetParticipant
{
    public StorageBudget Budget { get; private set; }
    public InMemoryContentStore(StorageBudget? budget = null) => Budget = budget ?? new StorageBudget();
    public void UseBudget(StorageBudget budget)
    {
        if (ReferenceEquals(Budget, budget)) return;
        if (!_objects.IsEmpty) throw new InvalidOperationException("Compose storage budgets before writing content.");
        Budget = budget;
    }
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    public bool Durable => false;
    public bool Shared => false;
    public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[65536];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) != 0)
        {
            StorageLimits.Check("object bytes", checked(buffer.Length + read), Budget.Limits.MaxObjectBytes);
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        lock (Budget.SyncRoot)
        {
            if (!_objects.ContainsKey(hash))
            {
                Budget.Adjust(bytes.LongLength);
                _objects[hash] = bytes;
            }
        }
        return new ContentHandle(hash, bytes.LongLength, hash);
    }
    public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = _objects.GetValueOrDefault(handle.Key) ?? throw new StorageCorruptionException("Referenced content is missing.");
        if (bytes.LongLength != handle.Length || Convert.ToHexStringLower(SHA256.HashData(bytes)) != handle.Sha256)
            throw new StorageCorruptionException("Referenced content does not match its integrity metadata.");
        return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}
