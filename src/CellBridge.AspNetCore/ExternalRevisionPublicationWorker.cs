using System.Threading.Channels;
using CellBridge.Storage.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CellBridge.AspNetCore;

/// <summary>Polls durable pending revisions and resumes delivery after a host restart.</summary>
/// <remarks>Passes are serialized within this instance. Delivery across instances still requires
/// the destination's durable idempotency contract. Conflicts require explicit publisher retry.</remarks>
public sealed class ExternalRevisionPublicationWorker : BackgroundService
{
    private readonly StorageProvider _provider;
    private readonly ExternalRevisionPublisher _publisher;
    private readonly ILogger<ExternalRevisionPublicationWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite, AllowSynchronousContinuations = false });
    private readonly Channel<bool> _pass = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly object _lifecycle = new();
    private bool _stopped;
    private bool _disposed;

    public ExternalRevisionPublicationWorker(StorageProvider provider, ExternalRevisionPublisher publisher,
        CellBridgeExternalPublicationOptions options, ILogger<ExternalRevisionPublicationWorker> logger)
    {
        options.Validate();
        _provider = provider;
        _publisher = publisher;
        _logger = logger;
        _interval = options.PollingInterval;
        _shutdownToken = _shutdown.Token;
        _pass.Writer.TryWrite(true);
    }

    /// <summary>Requests a pass without waiting. Concurrent wake requests coalesce; stopped workers ignore them.</summary>
    public void Wake() => _wake.Writer.TryWrite(true);

    /// <summary>Scans once, attempting at most one pending revision per document.</summary>
    public async Task<int> PublishPendingOnceAsync(CancellationToken cancellationToken = default) =>
        (await RunPassAsync(cancellationToken)).Delivered;

    private async Task<(int Delivered, bool TransientFailure)> RunPassAsync(CancellationToken cancellationToken)
    {
        _shutdownToken.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        var token = linked.Token;
        await _pass.Reader.ReadAsync(token);
        try
        {
            var delivered = 0;
            var transientFailure = false;
            var offset = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var page = await _provider.State.ListAsync(offset, 128, token);
                if (page.Count == 0) break;
                foreach (var summary in page)
                {
                    token.ThrowIfCancellationRequested();
                    var state = await _provider.State.FindByResourceIdAsync(summary.ResourceId, token);
                    if (state?.Publication is not { Pending.IsEmpty: false, BlockedReason: null }) continue;
                    var attempt = await _publisher.PublishNextAsync(summary.ResourceId, token);
                    if (attempt == PublicationAttempt.Delivered) delivered++;
                    else if (attempt == PublicationAttempt.TransientFailure) transientFailure = true;
                    else if (attempt is PublicationAttempt.Conflict or PublicationAttempt.ContentUnavailable)
                        _logger.LogWarning("Destination publication for {ResourceId} stopped with {Attempt}.",
                            summary.ResourceId, attempt);
                }
                offset += page.Count;
                if (page.Count < 128) break;
            }
            return (delivered, transientFailure);
        }
        finally { _pass.Writer.TryWrite(true); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _shutdownToken);
        var token = stopping.Token;
        while (!token.IsCancellationRequested)
        {
            var requiresDelay = false;
            try
            {
                while (true)
                {
                    var pass = await RunPassAsync(token);
                    // Do not immediately retry a failed destination while draining other documents.
                    requiresDelay = pass.TransientFailure;
                    if (pass.Delivered == 0 || pass.TransientFailure) break;
                    token.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                requiresDelay = true;
                _logger.LogError(ex, "The destination publication pass failed.");
            }

            if (requiresDelay)
            {
                try { await Task.Delay(_interval, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                continue;
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(_interval);
            try { await _wake.Reader.ReadAsync(wait.Token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { break; }
        }
    }

    private void Stop()
    {
        lock (_lifecycle)
        {
            if (_stopped) return;
            _stopped = true;
            _shutdown.Cancel();
            _wake.Writer.TryComplete();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Stop();
        await base.StopAsync(cancellationToken);
        // BackgroundService only waits for ExecuteAsync. Also drain a public manual pass
        // before the host disposes its provider and destination dependencies.
        await _pass.Reader.ReadAsync(cancellationToken);
        _pass.Writer.TryWrite(true);
    }

    public override void Dispose()
    {
        Stop();
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Dispose();
            base.Dispose();
        }
    }
}
