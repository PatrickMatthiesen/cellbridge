namespace CellBridge.Client;

internal sealed class OfficeRevisionSession
{
    internal const long DefaultMaxSessionBytes = 512L * 1024 * 1024;
    private const int MaxRetainedRevisions = 8;
    private const int MaxCommitAttempts = 4;

    private readonly CellBridgeDocumentSession _document;
    private readonly Action<byte[]> _validate;
    private readonly long _maxSessionBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LinkedList<CellBridgeDocumentSnapshot> _history = [];
    private CellBridgeDocumentSnapshot _current;
    private long _retainedBytes;
    private Proposal? _pending;

    internal OfficeRevisionSession(CellBridgeDocumentSession documentSession, long maxSessionBytes, Action<byte[]> validate)
    {
        ArgumentNullException.ThrowIfNull(documentSession);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSessionBytes);
        _document = documentSession;
        _maxSessionBytes = maxSessionBytes;
        _validate = validate;
        _current = documentSession.GetSnapshot();
        CheckSize(_current.Package.LongLength);
        _validate(_current.Package);
        _history.AddLast(_current);
        _retainedBytes = _current.Package.LongLength;
    }

    internal string Revision => Volatile.Read(ref _current).Revision;
    internal bool HasPendingCommit => Volatile.Read(ref _pending) is not null;

    internal TResult Read<TResult>(Func<byte[], TResult> read) => read(Volatile.Read(ref _current).Package);

    internal TResult Diff<TResult>(string beforeRevision, string afterRevision, int maxChanges,
        Func<byte[], byte[], TResult> diff)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(beforeRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(afterRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChanges);
        _gate.Wait();
        try
        {
            var before = Find(beforeRevision) ?? throw new KeyNotFoundException($"Revision '{beforeRevision}' is not retained.");
            var after = Find(afterRevision) ?? throw new KeyNotFoundException($"Revision '{afterRevision}' is not retained.");
            return diff(before.Package, after.Package);
        }
        finally { _gate.Release(); }
    }

    internal async Task<string> SyncAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            ThrowIfPending();
            await SyncCoreAsync(cancellationToken).ConfigureAwait(false);
            return _current.Revision;
        }
        finally { _gate.Release(); }
    }

    internal async Task<OfficeWriteResult> ApplyAsync(string baseRevision, Func<byte[], byte[], MergeResult> merge,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRevision);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            ThrowIfPending();
            var original = Find(baseRevision);
            if (original is null)
                return new(OfficeWriteStatus.BaseRevisionUnavailable, _current.Revision, []);

            await SyncCoreAsync(cancellationToken, original).ConfigureAwait(false);
            for (var attempt = 0; attempt < MaxCommitAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = merge(original.Package, _current.Package);
                if (result.Package is not { } package)
                    return new(OfficeWriteStatus.Conflict, _current.Revision, result.Conflicts);

                CheckSize(package.LongLength + _current.Package.LongLength +
                    (original == _current ? 0 : original.Package.LongLength));
                TrimHistory(package.LongLength, original);
                var proposal = new Proposal(_current.Revision, package, result.AddedSlideId);
                if (await SendAsync(proposal, cancellationToken).ConfigureAwait(false) is { } outcome)
                    return outcome;

                if (attempt + 1 < MaxCommitAttempts)
                {
                    var ceiling = 20 << attempt;
                    await Task.Delay(Random.Shared.Next(ceiling / 2, ceiling + 1), cancellationToken).ConfigureAwait(false);
                }
                await SyncCoreAsync(cancellationToken, original).ConfigureAwait(false);
            }

            return new(OfficeWriteStatus.RetryExhausted, _current.Revision, []);
        }
        finally { _gate.Release(); }
    }

    internal async Task<OfficeWriteResult> RetryPendingCommitAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            var pending = _pending ?? throw new InvalidOperationException("There is no unresolved Office proposal.");
            if (await SendAsync(pending, cancellationToken).ConfigureAwait(false) is { } outcome)
                return outcome;

            await SyncCoreAsync(cancellationToken).ConfigureAwait(false);
            return new(OfficeWriteStatus.RetryExhausted, _current.Revision, [],
                Detail: "The recovered proposal had a confirmed revision conflict. Resubmit the edit to rebase.");
        }
        finally { _gate.Release(); }
    }

    // A null result permits rebasing because the raw session confirmed a coherency conflict.
    private async Task<OfficeWriteResult?> SendAsync(Proposal proposal, CancellationToken cancellationToken)
    {
        CellBridgeCommitResult result;
        try
        {
            result = await _document.TryCommitAsync(proposal.Revision, proposal.Package, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException error) when (!cancellationToken.IsCancellationRequested)
        {
            if (!RememberPending(proposal))
                throw;
            return new(OfficeWriteStatus.OutcomeUnknown, _current.Revision, [], Detail: error.Message);
        }
        catch
        {
            RememberPending(proposal);
            throw;
        }

        Volatile.Write(ref _pending, null);
        if (result.Succeeded)
        {
            Remember(new(result.Revision, proposal.Package));
            return new(OfficeWriteStatus.Success, result.Revision, [], proposal.AddedSlideId);
        }

        return result.Status == CellBridgeCommitStatus.OutcomeUnknown
            ? new(OfficeWriteStatus.OutcomeUnknown, result.Revision, [],
                Detail: "The replay conflicted after a lost response. Sync and inspect the document before editing.")
            : null;
    }

    private bool RememberPending(Proposal proposal)
    {
        var pending = _document.HasPendingCommit(proposal.Revision, proposal.Package);
        Volatile.Write(ref _pending, pending ? proposal : null);
        return pending;
    }

    private async Task SyncCoreAsync(CancellationToken cancellationToken, CellBridgeDocumentSnapshot? keep = null)
    {
        var snapshot = await _document.SyncAsync(cancellationToken).ConfigureAwait(false);
        if (Find(snapshot.Revision) is null)
        {
            CheckSize(snapshot.Package.LongLength + (keep?.Package.LongLength ?? 0));
            _validate(snapshot.Package);
        }
        Remember(snapshot, keep);
    }

    private CellBridgeDocumentSnapshot? Find(string revision) =>
        _history.FirstOrDefault(snapshot => snapshot.Revision == revision);

    private void Remember(CellBridgeDocumentSnapshot snapshot, CellBridgeDocumentSnapshot? keep = null)
    {
        var retained = Find(snapshot.Revision);
        if (retained is null)
            _retainedBytes += snapshot.Package.LongLength;
        else
            _history.Remove(retained);
        snapshot = retained ?? snapshot;
        _history.AddLast(snapshot);
        Volatile.Write(ref _current, snapshot);
        TrimHistory(0, keep);
    }

    private void TrimHistory(long reservedBytes, CellBridgeDocumentSnapshot? keep)
    {
        while (_history.Count > MaxRetainedRevisions || _retainedBytes + reservedBytes > _maxSessionBytes)
        {
            var eviction = _history.First!;
            while (eviction.Value == _current || eviction.Value == keep)
                eviction = eviction.Next!;
            _retainedBytes -= eviction.Value.Package.LongLength;
            _history.Remove(eviction);
        }
    }

    private void CheckSize(long bytes)
    {
        if (bytes > _maxSessionBytes)
            throw new InvalidDataException("The document revisions and proposed package exceed the Office session byte limit.");
    }

    private void ThrowIfPending()
    {
        if (_pending is not null)
            throw new InvalidOperationException("Retry the unresolved commit before syncing or editing this document.");
    }

    internal sealed record MergeResult(byte[]? Package, IReadOnlyList<string> Conflicts, uint? AddedSlideId = null);
    private sealed record Proposal(string Revision, byte[] Package, uint? AddedSlideId);
}
