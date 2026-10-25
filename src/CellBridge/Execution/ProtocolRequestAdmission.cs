namespace CellBridge.AspNetCore;

internal sealed class ProtocolRequestAdmission(CellBridgeOptions options)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, int> _subjects = new(StringComparer.Ordinal);
    private int _active;

    public Lease? TryAcquire(string subject, out int rejectionStatus)
    {
        lock (_sync)
        {
            var count = _subjects.GetValueOrDefault(subject);
            if (count >= options.MaxConcurrentRequestsPerSubject)
            { rejectionStatus = 429; return null; }
            if (_active >= options.MaxConcurrentRequests)
            { rejectionStatus = 503; return null; }
            _subjects[subject] = count + 1;
            _active++;
            rejectionStatus = 0;
            return new(this, subject);
        }
    }

    private void Release(string subject)
    {
        lock (_sync)
        {
            var count = _subjects[subject];
            if (count == 1) _subjects.Remove(subject);
            else _subjects[subject] = count - 1;
            _active--;
        }
    }

    internal sealed class Lease(ProtocolRequestAdmission owner, string subject) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(subject);
        }
    }
}
