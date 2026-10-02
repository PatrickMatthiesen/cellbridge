using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;

namespace CellBridge.Packages.Tests;

// Implements the public contract using only package references.
internal sealed class CustomStateProvider : IDocumentStateStore
{
    private readonly InMemoryStateStore _inner = new();

    public bool Durable => false;
    public bool Shared => false;

    public ValueTask<DocumentState?> FindByResourceIdAsync(
        Guid resourceId, CancellationToken cancellationToken = default)
        => _inner.FindByResourceIdAsync(resourceId, cancellationToken);

    public ValueTask<DocumentState?> FindByPathKeyAsync(
        string pathKey, CancellationToken cancellationToken = default)
        => _inner.FindByPathKeyAsync(pathKey, cancellationToken);

    public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(
        int offset, int limit, CancellationToken cancellationToken = default)
        => _inner.ListAsync(offset, limit, cancellationToken);

    public ValueTask<bool> TryCreateAsync(
        DocumentState state, CancellationToken cancellationToken = default)
        => _inner.TryCreateAsync(state, cancellationToken);

    public ValueTask<T> TransitionAsync<T>(
        Guid resourceId,
        Func<DocumentState, DateTime, StateTransition<T>> callback,
        CancellationToken cancellationToken = default)
        => _inner.TransitionAsync(resourceId, callback, cancellationToken);

    public ValueTask CheckHealthAsync(CancellationToken cancellationToken = default)
        => _inner.CheckHealthAsync(cancellationToken);
}
