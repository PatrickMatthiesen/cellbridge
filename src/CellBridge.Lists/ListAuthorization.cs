using System.Collections.Frozen;

namespace CellBridge.Lists;

[Flags]
public enum ListWorkspaceAccess
{
    None = 0,
    UseLists = 1,
    CreateLists = 2,
    ManagePermissions = 4,
    ReadAudit = 8
}

public sealed class ListAuthorizationSnapshot
{
    private const ListWorkspaceAccess AllWorkspaceAccess = ListWorkspaceAccess.UseLists |
        ListWorkspaceAccess.CreateLists | ListWorkspaceAccess.ManagePermissions | ListWorkspaceAccess.ReadAudit;
    private const ListAccess AllListAccess = ListAccess.Read | ListAccess.Write | ListAccess.Design;

    public ListAuthorizationSnapshot(
        string policyDomain,
        string subject,
        Guid workspaceId,
        ListWorkspaceAccess workspaceAccess,
        ListAccess defaultListAccess = ListAccess.None,
        IReadOnlyDictionary<Guid, ListAccess>? listOverrides = null,
        string? revision = null)
    {
        ValidatePolicyDomain(policyDomain, nameof(policyDomain));
        ValidateText(subject, nameof(subject));
        if (workspaceId == Guid.Empty) throw new ArgumentException("The workspace ID cannot be empty.", nameof(workspaceId));
        ValidateWorkspaceAccess(workspaceAccess, nameof(workspaceAccess));
        ValidateListAccess(defaultListAccess, nameof(defaultListAccess));
        if (revision is not null) ValidateText(revision, nameof(revision));

        var overrides = new Dictionary<Guid, ListAccess>(listOverrides?.Count ?? 0);
        if (listOverrides is not null)
        {
            foreach (var (listId, access) in listOverrides)
            {
                if (listId == Guid.Empty) throw new ArgumentException("A list override ID cannot be empty.", nameof(listOverrides));
                ValidateListAccess(access, nameof(listOverrides));
                overrides.Add(listId, access);
            }
        }

        PolicyDomain = policyDomain;
        Subject = subject;
        WorkspaceId = workspaceId;
        WorkspaceAccess = workspaceAccess;
        DefaultListAccess = defaultListAccess;
        ListOverrides = overrides.ToFrozenDictionary();
        Revision = revision;
    }

    public string PolicyDomain { get; }
    public string Subject { get; }
    public Guid WorkspaceId { get; }
    public ListWorkspaceAccess WorkspaceAccess { get; }
    public ListAccess DefaultListAccess { get; }
    public IReadOnlyDictionary<Guid, ListAccess> ListOverrides { get; }
    public string? Revision { get; }

    public ListAccess AccessFor(Guid listId)
    {
        if (listId == Guid.Empty) throw new ArgumentException("The list ID cannot be empty.", nameof(listId));
        if ((WorkspaceAccess & ListWorkspaceAccess.UseLists) == 0) return ListAccess.None;
        return ListOverrides.TryGetValue(listId, out var access) ? access : DefaultListAccess;
    }

    internal static void ValidateText(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || !ListValidation.IsWellFormedText(value) || value.Any(char.IsControl))
            throw new ArgumentException("The value must contain valid non-control text.", parameterName);
    }

    internal static void ValidatePolicyDomain(string? value, string parameterName)
    {
        ValidateText(value, parameterName);
        if (value!.Length > 200)
            throw new ArgumentException("The policy domain cannot exceed 200 characters.", parameterName);
    }

    private static void ValidateWorkspaceAccess(ListWorkspaceAccess access, string parameterName)
    {
        if ((access & ~AllWorkspaceAccess) != 0)
            throw new ArgumentOutOfRangeException(parameterName, "The workspace access contains unknown flags.");
        if (access != ListWorkspaceAccess.None && (access & ListWorkspaceAccess.UseLists) == 0)
            throw new ArgumentException("Workspace capabilities require UseLists.", parameterName);
    }

    private static void ValidateListAccess(ListAccess access, string parameterName)
    {
        if ((access & ~AllListAccess) != 0)
            throw new ArgumentOutOfRangeException(parameterName, "The list access contains unknown flags.");
    }
}

public interface IListAuthorizationReadLease : IAsyncDisposable
{
    ListAuthorizationSnapshot Snapshot { get; }
}

/// <summary>
/// Coordinates authorization reads and writes inside one process. Callers must not reenter a provider lease or
/// detach work that uses a lease after disposal. Hosts with multiple processes or rolling upgrades need a shared
/// coordination mechanism in addition to this contract.
/// </summary>
public interface IListAuthorizationProvider
{
    string PolicyDomain { get; }
    ValueTask<IListAuthorizationReadLease> AcquireReadAsync(ListActor actor, Guid workspaceId, CancellationToken ct = default);

    /// <summary>
    /// Acquires the core workspace barrier. Host permission changes must use the concrete engine helper that also
    /// drains active PostgreSQL transactions; acquiring this lease alone does not provide that guarantee.
    /// </summary>
    ValueTask<IAsyncDisposable> AcquireWriteAsync(Guid workspaceId, CancellationToken ct = default);

    /// <summary>
    /// Acquires the core domain barrier. Host permission changes must use the concrete engine helper that also
    /// drains active PostgreSQL transactions; acquiring this lease alone does not provide that guarantee.
    /// </summary>
    ValueTask<IAsyncDisposable> AcquireGlobalWriteAsync(CancellationToken ct = default);
}

/// <summary>
/// Provides fair, writer-preferring authorization barriers for a single process. Leases have no time-to-live and
/// remain held until disposal. This provider cannot coordinate overlapping processes or rolling upgrades.
/// </summary>
public sealed class SingleProcessListAuthorizationProvider : IListAuthorizationProvider
{
    private readonly Func<ListActor, Guid, CancellationToken, ValueTask<ListAuthorizationSnapshot?>> _loadSnapshot;
    private readonly AsyncReaderWriterLock _domain = new();
    private readonly object _workspaceSync = new();
    private readonly Dictionary<Guid, WorkspaceEntry> _workspaces = [];

    public SingleProcessListAuthorizationProvider(
        string policyDomain,
        Func<ListActor, Guid, CancellationToken, ValueTask<ListAuthorizationSnapshot?>> loadSnapshot)
    {
        ListAuthorizationSnapshot.ValidatePolicyDomain(policyDomain, nameof(policyDomain));
        PolicyDomain = policyDomain;
        _loadSnapshot = loadSnapshot ?? throw new ArgumentNullException(nameof(loadSnapshot));
    }

    public string PolicyDomain { get; }

    public async ValueTask<IListAuthorizationReadLease> AcquireReadAsync(
        ListActor actor,
        Guid workspaceId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ListAuthorizationSnapshot.ValidateText(actor.Subject, nameof(actor));
        ValidateWorkspaceId(workspaceId);

        IAsyncDisposable? domainLease = null;
        WorkspaceReference? workspace = null;
        IAsyncDisposable? workspaceLease = null;
        try
        {
            domainLease = await _domain.AcquireReadAsync(ct).ConfigureAwait(false);
            workspace = RentWorkspace(workspaceId);
            workspaceLease = await workspace.Gate.AcquireReadAsync(ct).ConfigureAwait(false);

            ListAuthorizationSnapshot? snapshot;
            try
            {
                snapshot = await _loadSnapshot(actor, workspaceId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                throw Unavailable();
            }

            if (snapshot is null ||
                !StringComparer.Ordinal.Equals(snapshot.PolicyDomain, PolicyDomain) ||
                !StringComparer.Ordinal.Equals(snapshot.Subject, actor.Subject) ||
                snapshot.WorkspaceId != workspaceId)
                throw Unavailable();

            var result = new ReadLease(domainLease, workspace, workspaceLease, snapshot);
            domainLease = null;
            workspace = null;
            workspaceLease = null;
            return result;
        }
        finally
        {
            if (workspaceLease is not null) await workspaceLease.DisposeAsync().ConfigureAwait(false);
            workspace?.Dispose();
            if (domainLease is not null) await domainLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask<IAsyncDisposable> AcquireWriteAsync(Guid workspaceId, CancellationToken ct = default)
    {
        ValidateWorkspaceId(workspaceId);
        IAsyncDisposable? domainLease = null;
        WorkspaceReference? workspace = null;
        IAsyncDisposable? workspaceLease = null;
        try
        {
            domainLease = await _domain.AcquireReadAsync(ct).ConfigureAwait(false);
            workspace = RentWorkspace(workspaceId);
            workspaceLease = await workspace.Gate.AcquireWriteAsync(ct).ConfigureAwait(false);
            var result = new WriteLease(domainLease, workspace, workspaceLease);
            domainLease = null;
            workspace = null;
            workspaceLease = null;
            return result;
        }
        finally
        {
            if (workspaceLease is not null) await workspaceLease.DisposeAsync().ConfigureAwait(false);
            workspace?.Dispose();
            if (domainLease is not null) await domainLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask<IAsyncDisposable> AcquireGlobalWriteAsync(CancellationToken ct = default) =>
        _domain.AcquireWriteAsync(ct);

    private WorkspaceReference RentWorkspace(Guid workspaceId)
    {
        lock (_workspaceSync)
        {
            if (!_workspaces.TryGetValue(workspaceId, out var entry))
            {
                entry = new WorkspaceEntry();
                _workspaces.Add(workspaceId, entry);
            }
            entry.ReferenceCount++;
            return new WorkspaceReference(this, workspaceId, entry);
        }
    }

    private void ReturnWorkspace(Guid workspaceId, WorkspaceEntry entry)
    {
        lock (_workspaceSync)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0 &&
                _workspaces.TryGetValue(workspaceId, out var current) &&
                ReferenceEquals(entry, current))
                _workspaces.Remove(workspaceId);
        }
    }

    private static void ValidateWorkspaceId(Guid workspaceId)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("The workspace ID cannot be empty.", nameof(workspaceId));
    }

    private static ListException Unavailable() =>
        new(ListFailure.Unavailable, "List authorization is unavailable.");

    private sealed class ReadLease(
        IAsyncDisposable domainLease,
        WorkspaceReference workspace,
        IAsyncDisposable workspaceLease,
        ListAuthorizationSnapshot snapshot) : IListAuthorizationReadLease
    {
        private int _disposed;

        public ListAuthorizationSnapshot Snapshot { get; } = snapshot;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await workspaceLease.DisposeAsync().ConfigureAwait(false);
            workspace.Dispose();
            await domainLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class WriteLease(
        IAsyncDisposable domainLease,
        WorkspaceReference workspace,
        IAsyncDisposable workspaceLease) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await workspaceLease.DisposeAsync().ConfigureAwait(false);
            workspace.Dispose();
            await domainLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class WorkspaceEntry
    {
        public AsyncReaderWriterLock Gate { get; } = new();
        public int ReferenceCount { get; set; }
    }

    private sealed class WorkspaceReference(
        SingleProcessListAuthorizationProvider owner,
        Guid workspaceId,
        WorkspaceEntry entry) : IDisposable
    {
        private SingleProcessListAuthorizationProvider? _owner = owner;

        public AsyncReaderWriterLock Gate => entry.Gate;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReturnWorkspace(workspaceId, entry);
        }
    }

    private sealed class AsyncReaderWriterLock
    {
        private readonly object _sync = new();
        private readonly LinkedList<Waiter> _waiters = new();
        private int _readers;
        private bool _writer;

        public ValueTask<IAsyncDisposable> AcquireReadAsync(CancellationToken ct) => AcquireAsync(write: false, ct);
        public ValueTask<IAsyncDisposable> AcquireWriteAsync(CancellationToken ct) => AcquireAsync(write: true, ct);

        private ValueTask<IAsyncDisposable> AcquireAsync(bool write, CancellationToken ct)
        {
            Waiter? waiter;
            lock (_sync)
            {
                if (ct.IsCancellationRequested) return ValueTask.FromCanceled<IAsyncDisposable>(ct);
                if (_waiters.Count == 0 && !_writer && (!write || _readers == 0))
                {
                    if (write) _writer = true;
                    else _readers++;
                    return ValueTask.FromResult<IAsyncDisposable>(new Guard(this, write));
                }

                waiter = new Waiter(this, write);
                waiter.Node = _waiters.AddLast(waiter);
            }

            waiter.RegisterCancellation(ct);
            return new ValueTask<IAsyncDisposable>(waiter.Task);
        }

        private void Cancel(Waiter waiter, CancellationToken ct)
        {
            List<Waiter>? ready = null;
            var canceled = false;
            lock (_sync)
            {
                if (waiter.Status == WaiterStatus.Queued)
                {
                    _waiters.Remove(waiter.Node!);
                    waiter.Node = null;
                    waiter.Status = WaiterStatus.Canceled;
                    canceled = true;
                    ready = Drain();
                }
            }

            if (!canceled) return;
            waiter.SetCanceled(ct);
            Complete(ready);
        }

        private void Release(bool write)
        {
            List<Waiter>? ready;
            lock (_sync)
            {
                if (write) _writer = false;
                else _readers--;
                ready = Drain();
            }
            Complete(ready);
        }

        private List<Waiter>? Drain()
        {
            if (_writer || _waiters.First is null) return null;
            var ready = new List<Waiter>();
            if (_waiters.First.Value.Write)
            {
                if (_readers != 0) return null;
                _writer = true;
                GrantFirst(ready);
                return ready;
            }

            while (_waiters.First is { Value.Write: false })
            {
                _readers++;
                GrantFirst(ready);
            }
            return ready;
        }

        private void GrantFirst(List<Waiter> ready)
        {
            var waiter = _waiters.First!.Value;
            _waiters.RemoveFirst();
            waiter.Node = null;
            waiter.Status = WaiterStatus.Granted;
            ready.Add(waiter);
        }

        private static void Complete(List<Waiter>? ready)
        {
            if (ready is null) return;
            foreach (var waiter in ready) waiter.SetResult();
        }

        private sealed class Guard(AsyncReaderWriterLock owner, bool write) : IAsyncDisposable
        {
            private AsyncReaderWriterLock? _owner = owner;

            public ValueTask DisposeAsync()
            {
                Interlocked.Exchange(ref _owner, null)?.Release(write);
                return ValueTask.CompletedTask;
            }
        }

        private sealed class Waiter(AsyncReaderWriterLock owner, bool write)
        {
            private readonly AsyncReaderWriterLock _owner = owner;
            private readonly TaskCompletionSource<IAsyncDisposable> _completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private CancellationTokenRegistration _registration;

            public bool Write { get; } = write;
            public LinkedListNode<Waiter>? Node { get; set; }
            public WaiterStatus Status { get; set; } = WaiterStatus.Queued;
            public Task<IAsyncDisposable> Task => _completion.Task;

            public void RegisterCancellation(CancellationToken ct)
            {
                if (!ct.CanBeCanceled) return;
                var registration = ct.Register(static state =>
                {
                    var (waiter, token) = ((Waiter Waiter, CancellationToken Token))state!;
                    waiter._owner.Cancel(waiter, token);
                }, (this, ct));

                var dispose = false;
                lock (_owner._sync)
                {
                    if (Status == WaiterStatus.Queued) _registration = registration;
                    else dispose = true;
                }
                if (dispose) registration.Dispose();
            }

            public void SetResult()
            {
                _registration.Dispose();
                _completion.TrySetResult(new Guard(_owner, Write));
            }

            public void SetCanceled(CancellationToken ct)
            {
                _registration.Dispose();
                _completion.TrySetCanceled(ct);
            }
        }

        private enum WaiterStatus { Queued, Granted, Canceled }
    }
}
