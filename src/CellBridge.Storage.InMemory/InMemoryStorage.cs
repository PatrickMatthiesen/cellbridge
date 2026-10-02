using System.Collections.Concurrent;
using System.Security.Cryptography;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.InMemory;

public sealed class InMemoryStateStore : IDocumentStateStore
{
    private readonly ConcurrentDictionary<Guid, DocumentState> _documents = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private readonly Dictionary<string, Guid> _paths = new(StringComparer.Ordinal);
    public bool Durable => false;
    public bool Shared => false;
    public ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_documents.GetValueOrDefault(id));
    }
    public ValueTask<DocumentState?> FindByPathKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_paths) return ValueTask.FromResult(_paths.TryGetValue(key, out var id) ? _documents[id] : null);
    }
    public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        IReadOnlyList<DocumentSummary> result = _documents.Values.OrderBy(x => x.PathKey, StringComparer.Ordinal)
            .Skip(offset).Take(limit).Select(x => new DocumentSummary(x.ResourceId, x.Path, x.Content.Length,
                x.ContentVersion, x.ModifiedUtc, x.Editors.Count(e => e.ExpiresUtc > DateTime.UtcNow))).ToArray();
        return ValueTask.FromResult(result);
    }
    public ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_paths)
        {
            if (_paths.ContainsKey(state.PathKey) || !_documents.TryAdd(state.ResourceId, state with { StateVersion = 0 }))
                return ValueTask.FromResult(false);
            _paths.Add(state.PathKey, state.ResourceId);
            return ValueTask.FromResult(true);
        }
    }
    public async ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition,
        CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(id, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = _documents.GetValueOrDefault(id) ?? throw new KeyNotFoundException("Document does not exist.");
            var result = transition(current, DateTime.UtcNow);
            if (result.Next is { } next)
            {
                if (next.ResourceId != id || next.PathKey != current.PathKey || next.Path != current.Path)
                    throw new InvalidOperationException("A transition cannot change document identity or path.");
                _documents[id] = next with { StateVersion = checked(current.StateVersion + 1) };
            }
            return result.Result;
        }
        finally { gate.Release(); }
    }
    public ValueTask CheckHealthAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

public sealed class InMemoryContentStore : IContentStore
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    public bool Durable => false;
    public bool Shared => false;
    public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        _objects.TryAdd(hash, bytes);
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
