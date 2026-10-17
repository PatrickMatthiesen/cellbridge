namespace CellBridge.Authorization.Internal;

internal readonly record struct AuthorizationCacheKey(
    string PolicyDomain,
    string Subject,
    string? TenantId,
    string? WorkspaceId,
    string ResourceId,
    string Lifecycle,
    string Action);

internal readonly record struct AuthorizationCacheDecision<T>(
    T Value,
    bool Allowed,
    DateTimeOffset? EvaluatedAtUtc = null,
    DateTimeOffset? FreshUntilUtc = null);

internal readonly record struct BoundedAuthorizationCacheOptions(
    int Capacity,
    int MaximumKeyLength,
    TimeSpan DecisionTtl,
    int MaximumInvalidationRetries = 2);

internal static class AuthorizationDecisionFreshness
{
    internal static bool IsValid(
        DateTimeOffset? evaluatedAtUtc,
        DateTimeOffset? freshUntilUtc,
        DateTimeOffset callStartedUtc,
        DateTimeOffset nowUtc) =>
        TryValidate(evaluatedAtUtc, freshUntilUtc, callStartedUtc, nowUtc, out _);

    internal static bool TryValidate(
        DateTimeOffset? evaluatedAtUtc,
        DateTimeOffset? freshUntilUtc,
        DateTimeOffset callStartedUtc,
        DateTimeOffset nowUtc,
        out DateTimeOffset originalEvaluationUtc)
    {
        var started = callStartedUtc.ToUniversalTime();
        var now = nowUtc.ToUniversalTime();
        var evaluated = evaluatedAtUtc?.ToUniversalTime();
        var freshUntil = freshUntilUtc?.ToUniversalTime();

        originalEvaluationUtc = evaluated ?? started;
        if (evaluated > now) return false;
        if (freshUntil is not { } deadline) return true;
        if (evaluated is { } sourceTime && deadline <= sourceTime) return false;
        return deadline > now;
    }
}

/// <summary>
/// A process-local authorization decision cache. Successful admission is the return boundary of
/// <see cref="AuthorizeAsync"/>. Invalidation does not cancel work admitted before that boundary.
/// </summary>
internal sealed class BoundedAuthorizationCache<T>
{
    private readonly object _gate = new();
    private readonly BoundedAuthorizationCacheOptions _options;
    private readonly Func<T> _deniedDecision;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<AuthorizationCacheKey, Entry> _entries = [];
    private readonly LinkedList<AuthorizationCacheKey> _leastRecentlyUsed = [];
    private long _epoch;

    internal (int Entries, int LruNodes) Counts
    {
        get { lock (_gate) return (_entries.Count, _leastRecentlyUsed.Count); }
    }

    internal BoundedAuthorizationCache(
        BoundedAuthorizationCacheOptions options,
        Func<T> deniedDecision,
        TimeProvider? timeProvider = null)
    {
        if (options.Capacity <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaximumKeyLength <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.DecisionTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaximumInvalidationRetries < 0) throw new ArgumentOutOfRangeException(nameof(options));

        _options = options;
        _deniedDecision = deniedDecision ?? throw new ArgumentNullException(nameof(deniedDecision));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async ValueTask<T> AuthorizeAsync(
        AuthorizationCacheKey key,
        Func<CancellationToken, ValueTask<AuthorizationCacheDecision<T>>> evaluate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluate);
        if (!IsValidKey(key)) return _deniedDecision();

        for (var attempt = 0; attempt <= _options.MaximumInvalidationRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entry? ownedEntry = null;
            Task<PendingResult>? pending = null;
            long joinedEpoch = 0;
            T cachedValue = default!;
            var cacheHit = false;

            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var existing))
                {
                    if (existing.Pending is not null)
                    {
                        pending = existing.Pending.Task;
                        joinedEpoch = _epoch;
                    }
                    else if (IsFresh(existing))
                    {
                        Touch(existing);
                        cachedValue = existing.Value;
                        cacheHit = true;
                    }
                    else
                    {
                        Remove(existing, PendingResult.Retry);
                    }
                }

                if (!cacheHit && pending is null)
                {
                    MakeRoom();
                    var completion = new TaskCompletionSource<PendingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var node = _leastRecentlyUsed.AddLast(key);
                    ownedEntry = new Entry(key, node, _epoch, completion);
                    _entries.Add(key, ownedEntry);
                }
            }

            if (cacheHit) return cachedValue;

            if (pending is not null)
            {
                var pendingResult = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (pendingResult.ReturnValue) return pendingResult.Value;
                // A normal coalesced completion or capacity eviction is not an
                // invalidation retry. The waiter still acquires its own admission below.
                lock (_gate)
                    if (joinedEpoch == _epoch) attempt--;
                continue;
            }

            var evaluationStartedUtc = _timeProvider.GetUtcNow();
            var evaluationStartedTimestamp = _timeProvider.GetTimestamp();
            AuthorizationCacheDecision<T> decision;
            try
            {
                decision = await evaluate(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Abandon(key, ownedEntry!, PendingResult.Retry);
                throw;
            }
            catch
            {
                var denied = _deniedDecision();
                Abandon(key, ownedEntry!, PendingResult.Return(denied));
                return denied;
            }

            var admitted = TryAdmit(key, ownedEntry!, decision, evaluationStartedUtc, evaluationStartedTimestamp);
            if (admitted.Admitted) return admitted.Value;
            if (!admitted.Retry) return admitted.Value;
        }

        return _deniedDecision();
    }

    internal void InvalidateSubject(string subject)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        Invalidate(key => string.Equals(key.Subject, subject, StringComparison.Ordinal));
    }

    internal void InvalidateResource(string resourceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceId);
        Invalidate(key => string.Equals(key.ResourceId, resourceId, StringComparison.Ordinal));
    }

    internal void InvalidateWorkspace(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceId);
        Invalidate(key => string.Equals(key.WorkspaceId, workspaceId, StringComparison.Ordinal));
    }

    internal void InvalidateAll() => Invalidate(static _ => true);

    private AdmissionResult TryAdmit(
        AuthorizationCacheKey key,
        Entry ownedEntry,
        AuthorizationCacheDecision<T> decision,
        DateTimeOffset evaluationStartedUtc,
        long evaluationStartedTimestamp)
    {
        lock (_gate)
        {
            _entries.TryGetValue(key, out var current);
            if (ownedEntry.Epoch != _epoch)
            {
                if (ReferenceEquals(current, ownedEntry)) Remove(ownedEntry, PendingResult.Retry);
                else ownedEntry.Pending!.TrySetResult(PendingResult.Retry);
                return AdmissionResult.TryAgain;
            }

            var nowUtc = _timeProvider.GetUtcNow();
            var nowTimestamp = _timeProvider.GetTimestamp();
            if (!AuthorizationDecisionFreshness.TryValidate(
                    decision.EvaluatedAtUtc,
                    decision.FreshUntilUtc,
                    evaluationStartedUtc,
                    nowUtc,
                    out var originalEvaluationUtc) ||
                !TryGetFreshness(
                    decision.FreshUntilUtc,
                    originalEvaluationUtc,
                    nowUtc,
                    evaluationStartedTimestamp,
                    nowTimestamp,
                    out var freshness,
                    out var absoluteDeadlineUtc))
            {
                var denied = _deniedDecision();
                if (ReferenceEquals(current, ownedEntry)) Remove(ownedEntry, PendingResult.Return(denied));
                else ownedEntry.Pending!.TrySetResult(PendingResult.Return(denied));
                return AdmissionResult.Denied(denied);
            }

            // Capacity eviction is not revocation. The completed decision remains valid for this
            // operation, but admitting it must not grow the bounded cache or displace a newer key.
            if (!ReferenceEquals(current, ownedEntry))
                return AdmissionResult.Success(decision.Value);

            var completion = ownedEntry.Pending!;
            ownedEntry.Pending = null;
            ownedEntry.Value = decision.Value;
            ownedEntry.AdmittedTimestamp = nowTimestamp;
            ownedEntry.Freshness = freshness;
            ownedEntry.AbsoluteDeadlineUtc = absoluteDeadlineUtc;
            Touch(ownedEntry);
            completion.TrySetResult(PendingResult.Retry);
            return AdmissionResult.Success(decision.Value);
        }
    }

    private bool TryGetFreshness(
        DateTimeOffset? freshUntilUtc,
        DateTimeOffset originalEvaluationUtc,
        DateTimeOffset nowUtc,
        long evaluationStartedTimestamp,
        long nowTimestamp,
        out TimeSpan freshness,
        out DateTimeOffset absoluteDeadlineUtc)
    {
        var elapsed = _timeProvider.GetElapsedTime(evaluationStartedTimestamp, nowTimestamp);
        var localRemaining = _options.DecisionTtl - elapsed;
        absoluteDeadlineUtc = SafeAdd(originalEvaluationUtc, _options.DecisionTtl);
        if (freshUntilUtc is { } sourceDeadline)
            absoluteDeadlineUtc = Min(absoluteDeadlineUtc, sourceDeadline.ToUniversalTime());
        var originalRemaining = absoluteDeadlineUtc - nowUtc.ToUniversalTime();
        freshness = Min(localRemaining, originalRemaining);
        return freshness > TimeSpan.Zero;
    }

    private bool IsFresh(Entry entry)
    {
        var elapsed = _timeProvider.GetElapsedTime(entry.AdmittedTimestamp, _timeProvider.GetTimestamp());
        return elapsed >= TimeSpan.Zero && elapsed < entry.Freshness &&
            _timeProvider.GetUtcNow().ToUniversalTime() < entry.AbsoluteDeadlineUtc;
    }

    private bool IsValidKey(AuthorizationCacheKey key)
    {
        if (string.IsNullOrEmpty(key.PolicyDomain) || string.IsNullOrEmpty(key.Subject) ||
            string.IsNullOrEmpty(key.ResourceId) || string.IsNullOrEmpty(key.Lifecycle) ||
            string.IsNullOrEmpty(key.Action)) return false;

        var remaining = _options.MaximumKeyLength;
        return Fits(key.PolicyDomain, ref remaining) &&
            Fits(key.Subject, ref remaining) &&
            Fits(key.TenantId, ref remaining) &&
            Fits(key.WorkspaceId, ref remaining) &&
            Fits(key.ResourceId, ref remaining) &&
            Fits(key.Lifecycle, ref remaining) &&
            Fits(key.Action, ref remaining);
    }

    private static bool Fits(string? value, ref int remaining)
    {
        if (value is null) return true;
        if (value.Length > remaining) return false;
        remaining -= value.Length;
        return true;
    }

    private void Invalidate(Func<AuthorizationCacheKey, bool> matches)
    {
        lock (_gate)
        {
            _epoch++;
            foreach (var entry in _entries.Values.Where(entry => matches(entry.Key)).ToArray())
                Remove(entry, PendingResult.Retry);
        }
    }

    private void Abandon(AuthorizationCacheKey key, Entry entry, PendingResult result)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) Remove(entry, result);
            else entry.Pending?.TrySetResult(result);
        }
    }

    private void MakeRoom()
    {
        while (_entries.Count >= _options.Capacity)
        {
            var key = _leastRecentlyUsed.First!.Value;
            Remove(_entries[key], PendingResult.Retry);
        }
    }

    private void Touch(Entry entry)
    {
        _leastRecentlyUsed.Remove(entry.Node);
        _leastRecentlyUsed.AddLast(entry.Node);
    }

    private void Remove(Entry entry, PendingResult pendingResult)
    {
        _entries.Remove(entry.Key);
        _leastRecentlyUsed.Remove(entry.Node);
        entry.Pending?.TrySetResult(pendingResult);
    }

    private static DateTimeOffset SafeAdd(DateTimeOffset value, TimeSpan duration)
    {
        if (duration > DateTimeOffset.MaxValue - value) return DateTimeOffset.MaxValue;
        return value + duration;
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    private sealed class Entry(
        AuthorizationCacheKey key,
        LinkedListNode<AuthorizationCacheKey> node,
        long epoch,
        TaskCompletionSource<PendingResult> pending)
    {
        internal AuthorizationCacheKey Key { get; } = key;
        internal LinkedListNode<AuthorizationCacheKey> Node { get; } = node;
        internal long Epoch { get; } = epoch;
        internal TaskCompletionSource<PendingResult>? Pending { get; set; } = pending;
        internal T Value { get; set; } = default!;
        internal long AdmittedTimestamp { get; set; }
        internal TimeSpan Freshness { get; set; }
        internal DateTimeOffset AbsoluteDeadlineUtc { get; set; }
    }

    private readonly record struct PendingResult(bool ReturnValue, T Value)
    {
        internal static PendingResult Retry => default;
        internal static PendingResult Return(T value) => new(true, value);
    }

    private readonly record struct AdmissionResult(bool Admitted, bool Retry, T Value)
    {
        internal static AdmissionResult Success(T value) => new(true, false, value);
        internal static AdmissionResult Denied(T value) => new(false, false, value);
        internal static AdmissionResult TryAgain => new(false, true, default!);
    }
}
