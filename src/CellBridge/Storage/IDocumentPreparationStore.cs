namespace CellBridge.Storage.Abstractions;

/// <summary>A coherent current document view and the provider's authoritative UTC time.</summary>
public sealed record DocumentPreparationRead(DocumentState State, DateTime UtcNow);

/// <summary>
/// Optional read-only preparation for optimistic saves. The state, current pointer
/// and clock belong to one coherent read. This grants no write authority or
/// reservation: publication must recheck access, receipts, lifecycle, content and
/// coordination under the provider's normal document lock.
/// </summary>
public interface IDocumentPreparationStore
{
    ValueTask<DocumentPreparationRead?> ReadPreparationAsync(Guid resourceId,
        CancellationToken cancellationToken = default);
}
