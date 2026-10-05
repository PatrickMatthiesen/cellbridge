using System.Security.Cryptography;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public enum PublicationAttempt { NoWork, Delivered, Superseded, Blocked, Conflict, ContentUnavailable, TransientFailure }

/// <summary>Trusted host integration. Durable recovery polls the state store, not request completion callbacks.</summary>
public sealed class ExternalRevisionPublisher(StorageProvider provider, IExternalRevisionDestination destination)
{
    public ValueTask<bool> BindAsync(Guid resourceId, long generation, long stateVersion, Guid bindingId,
        string destinationId, string externalRevision, CancellationToken cancellationToken = default)
    {
        if (bindingId == Guid.Empty) throw new ArgumentException("A durable binding identity is required.", nameof(bindingId));
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalRevision);
        return provider.State.TransitionAsync(resourceId, (current, _) =>
        {
            if (current.LifecycleGeneration != generation || current.StateVersion != stateVersion || current.Publication is not null)
                return new StateTransition<bool>(null, false);
            var next = current with { Publication = new(bindingId, destinationId, externalRevision, 1, []) };
            return new StateTransition<bool>(next, true);
        }, cancellationToken);
    }

    /// <summary>Retries the same immutable head and baseline; it never adopts a conflicting remote revision.</summary>
    public ValueTask<bool> RetryAsync(Guid resourceId, Guid bindingId, Guid operationId,
        CancellationToken cancellationToken = default) => provider.State.TransitionAsync(resourceId, (current, _) =>
    {
        if (current.Publication is not { Pending.IsEmpty: false } publication || publication.BindingId != bindingId ||
            publication.Pending[0].OperationId != operationId) return new StateTransition<bool>(null, false);
        return new StateTransition<bool>(current with { Publication = publication with { BlockedReason = null } }, true);
    }, cancellationToken);

    /// <summary>Delivers at most one revision. Destination I/O is outside state coordination and bounded to 30 seconds.</summary>
    public async ValueTask<PublicationAttempt> PublishNextAsync(Guid resourceId, CancellationToken cancellationToken = default)
    {
        var state = await provider.State.FindByResourceIdAsync(resourceId, cancellationToken);
        if (state?.Publication is not { Pending.IsEmpty: false } publication) return PublicationAttempt.NoWork;
        if (publication.BlockedReason is not null) return PublicationAttempt.Blocked;
        var head = publication.Pending[0];
        var request = new ExternalDeliveryRequest(publication.BindingId, publication.Destination, publication.ExpectedRevision, head);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        ExternalDeliveryResult result;
        try
        {
            await using var staging = OpenStaging();
            await VerifyAsync(head.Content, staging, timeout.Token);
            staging.Position = 0;
            result = await destination.CompareExchangeAsync(request, staging, timeout.Token);
        }
        catch (StorageCorruptionException)
        {
            await BlockAsync(resourceId, request, "ContentUnavailable");
            return PublicationAttempt.ContentUnavailable;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // A timeout or broken connection may have committed remotely. Retain exactly the same operation for retry.
            return PublicationAttempt.TransientFailure;
        }
        if (result.Status != ExternalDeliveryStatus.Applied || string.IsNullOrWhiteSpace(result.Revision))
        {
            await BlockAsync(resourceId, request, result.Status == ExternalDeliveryStatus.Conflict ? "DestinationConflict" : "MissingReceipt");
            return PublicationAttempt.Conflict;
        }
        try
        {
            return await provider.State.TransitionAsync(resourceId, (current, _) =>
            {
                if (!Matches(current, request)) return new StateTransition<PublicationAttempt>(null, PublicationAttempt.Superseded);
                var next = current with { Publication = current.Publication! with
                { ExpectedRevision = result.Revision, Pending = current.Publication.Pending.RemoveAt(0), BlockedReason = null } };
                return new StateTransition<PublicationAttempt>(next, PublicationAttempt.Delivered);
            }, CancellationToken.None);
        }
        catch (KeyNotFoundException) { return PublicationAttempt.Superseded; }
    }

    private async ValueTask<bool> BlockAsync(Guid id, ExternalDeliveryRequest request, string reason)
    {
        try
        {
            return await provider.State.TransitionAsync(id, (current, _) => Matches(current, request)
                ? new StateTransition<bool>(current with { Publication = current.Publication! with { BlockedReason = reason } }, true)
                : new StateTransition<bool>(null, false), CancellationToken.None);
        }
        catch (KeyNotFoundException) { return false; }
    }

    private static bool Matches(DocumentState state, ExternalDeliveryRequest request) =>
        state.LifecycleGeneration == request.Revision.LifecycleGeneration && state.Publication is { Pending.IsEmpty: false } publication &&
        publication.BindingId == request.BindingId && publication.Destination == request.Destination &&
        publication.ExpectedRevision == request.ExpectedRevision && publication.Pending[0] == request.Revision;

    private async Task VerifyAsync(ContentHandle handle, Stream target, CancellationToken cancellationToken)
    {
        StorageLimits.Check("external document bytes", handle.Length, provider.Limits.MaxDocumentBytes);
        await using var source = await provider.Content.OpenReadAsync(handle, cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long length = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            length = checked(length + read);
            if (length > handle.Length) throw new StorageCorruptionException("External revision content is too long.");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (length != handle.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != handle.Sha256)
            throw new StorageCorruptionException("External revision content failed verification.");
    }
    private static FileStream OpenStaging()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite,
            Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(Path.Combine(Path.GetTempPath(), "cellbridge-delivery-" + Guid.NewGuid().ToString("N")), options);
    }
}
