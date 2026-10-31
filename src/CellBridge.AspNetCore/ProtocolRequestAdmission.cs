using System.Threading.RateLimiting;

namespace CellBridge.AspNetCore;

internal sealed class ProtocolRequestAdmission : IDisposable
{
    private readonly ConcurrencyLimiter _global;
    private readonly PartitionedRateLimiter<string> _subjects;

    public ProtocolRequestAdmission(CellBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrentRequests);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrentRequestsPerSubject);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxQueuedRequests);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxQueuedRequestsPerSubject);

        _global = new(new ConcurrencyLimiterOptions
        {
            PermitLimit = options.MaxConcurrentRequests,
            QueueLimit = options.MaxQueuedRequests,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
        _subjects = PartitionedRateLimiter.Create<string, string>(subject =>
            RateLimitPartition.GetConcurrencyLimiter(subject, _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = options.MaxConcurrentRequestsPerSubject,
                QueueLimit = options.MaxQueuedRequestsPerSubject,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));
    }

    public async ValueTask<AdmissionResult> AcquireAsync(string subject, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        // Acquire subject capacity first. A request waiting behind the subject limit must not
        // reserve one of the process-wide permits while it waits.
        var subjectLease = await _subjects.AcquireAsync(subject, 1, cancellationToken).ConfigureAwait(false);
        if (!subjectLease.IsAcquired)
        {
            subjectLease.Dispose();
            return new(null, 429);
        }

        RateLimitLease globalLease;
        try
        {
            globalLease = await _global.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            subjectLease.Dispose();
            throw;
        }

        if (!globalLease.IsAcquired)
        {
            globalLease.Dispose();
            subjectLease.Dispose();
            return new(null, 503);
        }

        return new(new Lease(subjectLease, globalLease), 0);
    }

    public void Dispose()
    {
        _subjects.Dispose();
        _global.Dispose();
    }

    internal sealed class Lease(RateLimitLease subjectLease, RateLimitLease globalLease) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            globalLease.Dispose();
            subjectLease.Dispose();
        }
    }

    internal sealed record AdmissionResult(Lease? Lease, int RejectionStatus);
}
