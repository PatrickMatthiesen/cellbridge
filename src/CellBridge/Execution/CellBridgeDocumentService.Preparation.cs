using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public sealed partial class CellBridgeDocumentService
{
    private async ValueTask<T> PrepareSaveAsync<T>(Guid resourceId,
        Func<DocumentState, DateTime, StateTransition<T>> prepare, CancellationToken cancellationToken,
        DocumentPreparationRead? preparedView = null)
    {
        if (preparedView is null)
        {
            if (provider.State is not IDocumentPreparationStore reads)
                return await provider.State.TransitionAsync(resourceId, prepare, cancellationToken);
            preparedView = await reads.ReadPreparationAsync(resourceId, cancellationToken)
                ?? throw new KeyNotFoundException("Document does not exist.");
        }

        var prepared = prepare(preparedView.State, preparedView.UtcNow);
        if (prepared.Next is not null)
            throw new InvalidOperationException("Save preparation must not change document state.");
        return prepared.Result;
    }
}
