using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public enum PublicationAttempt { NoWork, Delivered, Superseded, Blocked, Conflict, ContentUnavailable, TransientFailure }

/// <summary>Delivers the oldest accepted package through indexed delivery records.</summary>
public sealed class ExternalRevisionPublisher(StorageProvider provider, IExternalRevisionDestination destination)
{
    public async ValueTask<bool> BindAsync(Guid resourceId, long generation, long stateVersion, Guid bindingId,
        string destinationId, string externalRevision, CancellationToken cancellationToken = default)
    {
        if (bindingId == Guid.Empty) throw new ArgumentException("A destination binding ID is required.", nameof(bindingId));
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalRevision);
        return (await provider.Documents.UpdateDeliveryAsync(new DocumentDeliveryCommand.Bind(resourceId, generation,
            stateVersion, new(bindingId, destinationId, externalRevision, 1)), cancellationToken)).Applied;
    }

    public async ValueTask<bool> RetryAsync(Guid resourceId, Guid bindingId, Guid operationId,
        CancellationToken cancellationToken = default) => (await provider.Documents.UpdateDeliveryAsync(
            new DocumentDeliveryCommand.Retry(resourceId, bindingId, operationId), cancellationToken)).Applied;

    public async ValueTask<PublicationAttempt> PublishNextAsync(Guid resourceId, CancellationToken cancellationToken = default)
    {
        var read = await provider.Documents.ReadNextDeliveryAsync(resourceId, cancellationToken);
        if (read is null) return PublicationAttempt.NoWork;
        if (read.Binding.BlockedReason is not null) return PublicationAttempt.Blocked;
        var request = new ExternalDeliveryRequest(read.Binding.BindingId, read.Binding.Destination,
            read.Binding.ExpectedRevision, read.Revision);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        ExternalDeliveryResult result;
        try
        {
            await using var stream = await provider.Packages.OpenReadAsync(read.Revision.Package, timeout.Token);
            result = await destination.CompareExchangeAsync(request, stream, timeout.Token);
        }
        catch (StorageCorruptionException)
        {
            await BlockAsync(read, "ContentUnavailable");
            return PublicationAttempt.ContentUnavailable;
        }
        catch (Exception error) when (error is IOException or TimeoutException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // A lost destination reply can follow an accepted write. Keep the same operation for retry.
            return PublicationAttempt.TransientFailure;
        }
        if (result.Status != ExternalDeliveryStatus.Applied || string.IsNullOrWhiteSpace(result.Revision))
        {
            await BlockAsync(read, result.Status == ExternalDeliveryStatus.Conflict ? "DestinationConflict" : "MissingReceipt");
            return PublicationAttempt.Conflict;
        }
        try
        {
            return (await provider.Documents.UpdateDeliveryAsync(new DocumentDeliveryCommand.Complete(resourceId, read,
                result.Revision), CancellationToken.None)).Applied ? PublicationAttempt.Delivered : PublicationAttempt.Superseded;
        }
        catch (KeyNotFoundException) { return PublicationAttempt.Superseded; }
    }

    private async ValueTask BlockAsync(DeliveryRead expected, string reason)
    {
        try { await provider.Documents.UpdateDeliveryAsync(new DocumentDeliveryCommand.Block(expected.Current.ResourceId,
            expected, reason), CancellationToken.None); }
        catch (KeyNotFoundException) { }
    }
}
