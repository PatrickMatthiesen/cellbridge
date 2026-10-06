using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;

namespace CellBridge.DocumentLibrary;

public sealed class PublicationWorker(
    StorageProvider provider,
    ExternalRevisionPublisher publisher,
    DocumentLibraryOptions options,
    ILogger<PublicationWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    public void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    public async Task<int> PublishPendingOnceAsync(CancellationToken cancellationToken = default)
    {
        var delivered = 0;
        var offset = 0;
        while (true)
        {
            var page = await provider.State.ListAsync(offset, 128, cancellationToken);
            if (page.Count == 0) break;
            foreach (var summary in page)
            {
                var state = await provider.State.FindByResourceIdAsync(summary.ResourceId, cancellationToken);
                if (state?.Publication is not { Pending.IsEmpty: false, BlockedReason: null }) continue;
                var attempt = await publisher.PublishNextAsync(summary.ResourceId, cancellationToken);
                if (attempt == PublicationAttempt.Delivered) delivered++;
                else if (attempt is PublicationAttempt.Conflict or PublicationAttempt.ContentUnavailable)
                    logger.LogWarning("Destination publication for {ResourceId} stopped with {Attempt}.",
                        summary.ResourceId, attempt);
            }
            offset += page.Count;
            if (page.Count < 128) break;
        }
        return delivered;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                do { } while (await PublishPendingOnceAsync(stoppingToken) > 0);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The destination publication pass failed.");
            }

            try
            {
                await _wake.WaitAsync(options.PublicationInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
