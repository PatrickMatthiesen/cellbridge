namespace CellBridge.Storage.Abstractions;

/// <summary>Optionally publishes staged immutable content and a document transition in one durable transaction.</summary>
public interface IAtomicDocumentSaveStore
{
    /// <summary>Requires the provider's exact database content store and the same atomic budget scope.</summary>
    bool SupportsContentStore(IContentStore content);

    /// <summary>
    /// Admits at most 64 inputs and 512 MiB, subject to the content store's
    /// per-object limit. Each handle's key and hash are the lowercase SHA-256
    /// digest of its bytes. Callers must keep all input buffers unchanged until
    /// completion. The callback runs once against locked authoritative state.
    /// A null next state can still commit staged objects and their quota usage.
    /// An uncertain commit fails without automatically retrying the callback.
    /// </summary>
    ValueTask<T> TransitionWithContentAsync<T>(Guid resourceId, IContentStore content,
        IReadOnlyList<ReadOnlyMemory<byte>> pending,
        Func<DocumentState, DateTime, StateTransition<T>> transition,
        CancellationToken cancellationToken = default);
}
