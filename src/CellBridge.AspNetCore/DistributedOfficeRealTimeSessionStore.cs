using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;

namespace CellBridge.AspNetCore;

/// <summary>Cross-process Office RTC sessions. Redis owns membership and event order;
/// this process owns only its SignalR connections and bounded dispatch queues.</summary>
internal sealed class DistributedOfficeRealTimeSessionStore : IOfficeRealTimeSessionStore, IHostedService, IAsyncDisposable
{
    private const int MaxConnectionsPerHost = 128;
    private const int MaxConnectionsPerRoom = 32;
    private const int MaxPayloadBytes = 64 * 1024;
    private const int MaxEventsPerPoll = 32;
    private const int MaxBytesPerPoll = 256 * 1024;
    private const int MaxQueueEventsPerRoom = 64;
    private const int MaxQueueBytesPerRoom = 1024 * 1024;
    private const int MaxQueueEventsPerHost = 256;
    private const int MaxQueueBytesPerHost = 4 * 1024 * 1024;
    private const int MaxEventBytes = 80 * 1024;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReapInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AuthorizationInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BrokerDeadline = TimeSpan.FromSeconds(5);
    // The same independent timer checks local ticket deadlines at 50 ms and
    // applies the slower fallback room poll without waiting on worker capacity.
    private static readonly TimeSpan SchedulerInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly HashSet<string> ChangeTokenFields = new(StringComparer.Ordinal)
    {
        "ChangeToken", "ClientId", "CorrelationId", "EventCommitTimestampUtc", "EventReason",
        "EventRequestId", "EventSourceApp", "IsVerificationEvent", "PartIds", "Properties",
        "Source", "SubscriptionId", "SyncToken"
    };

    private readonly object _sync = new();
    private readonly Dictionary<string, LocalConnection> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<OfficeRealTimeRoomKey, LocalRoom> _rooms = [];
    private readonly IOfficeRealTimeAuthorizer _authorizer;
    private readonly IHubContext<OfficeRealTimeHub> _hub;
    private readonly IOfficeRealTimeBackplane _backplane;
    private readonly TimeProvider _time;
    private readonly Guid _hostId = Guid.NewGuid();
    private readonly SemaphoreSlim _workers = new(4, 4);
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private readonly Queue<ReadyWork> _readyWork = new();
    private readonly Dictionary<Guid, OfficeRealTimeBackplaneMember> _cleanupQueue = [];
    private int _queuedHostEvents;
    private int _queuedHostBytes;
    private int _connectReservations;
    private int _disposed;
    private int _pumpScheduled;
    private int _stopRequested;
    private bool _cleanupReady;
    private bool _cleanupActive;
    private long _authorizationSweeps;
    private long _heartbeatCalls;
    private long _pollCalls;
    private long _expiryAborts;
    private Task? _scheduler;
    private CancellationTokenSource? _stopping;

    internal OfficeRealTimeSchedulerDiagnostics Diagnostics
    {
        get
        {
            lock (_sync) return new(_authorizationSweeps, _heartbeatCalls, _pollCalls, _rooms.Count,
                4 - _workers.CurrentCount, _rooms.Values.Sum(x => x.PendingWork.Count) + _cleanupQueue.Count, _expiryAborts,
                _backplane is RedisOfficeRealTimeBackplane redis ? redis.ScriptEvaluationCount : 0,
                _backplane is RedisOfficeRealTimeBackplane redisArgs ? redisArgs.ScriptArgumentBytes : 0,
                _backplane is RedisOfficeRealTimeBackplane wakeCount ? wakeCount.WakeHintsReceived : 0,
                _backplane is RedisOfficeRealTimeBackplane wakeBytes ? wakeBytes.WakeHintBytesReceived : 0);
        }
    }

    public DistributedOfficeRealTimeSessionStore(IOfficeRealTimeAuthorizer authorizer,
        IHubContext<OfficeRealTimeHub> hub, IOfficeRealTimeBackplane backplane, TimeProvider? timeProvider = null)
    {
        _authorizer = authorizer;
        _hub = hub;
        _backplane = backplane;
        _time = timeProvider ?? TimeProvider.System;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsStopping, this);
        _stopping = new CancellationTokenSource();
        if (_backplane is IOfficeRealTimeBackplaneWakeSource wakeSource)
            wakeSource.RoomChanged += OnRoomChanged;
        _scheduler = RunSchedulerAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_sync) Volatile.Write(ref _stopRequested, 1);
        if (Volatile.Read(ref _disposed) == 0) _stopping?.Cancel();
        if (_backplane is IOfficeRealTimeBackplaneWakeSource wakeSource)
            wakeSource.RoomChanged -= OnRoomChanged;
        if (_scheduler is not null)
        {
            try { await _scheduler.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stopping?.IsCancellationRequested == true) { }
        }
        LocalConnection[] connections;
        lock (_sync) connections = _connections.Values.ToArray();
        foreach (var connection in connections) connection.Context.Abort();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Volatile.Write(ref _stopRequested, 1);
            Volatile.Write(ref _disposed, 1);
        }
        _stopping?.Cancel();
        if (_backplane is IOfficeRealTimeBackplaneWakeSource wakeSource)
            wakeSource.RoomChanged -= OnRoomChanged;
        if (_scheduler is not null)
        {
            try { await _scheduler.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        LocalConnection[] connections;
        lock (_sync) connections = _connections.Values.ToArray();
        foreach (var connection in connections) connection.Context.Abort();
        // Keep each worker slot reserved until its underlying I/O task has actually
        // ended. Cancellation alone is not evidence that the dependency stopped.
        while (!_running.IsEmpty)
        {
            Task[] active = _running.Keys.ToArray();
            if (active.Length == 0) continue;
            await Task.WhenAll(active).ConfigureAwait(false);
        }
        _stopping?.Dispose();
        _workers.Dispose();
    }

    public async Task<bool> ConnectAsync(OfficeRealTimeTicket ticket, HubCallerContext context,
        CancellationToken cancellationToken)
    {
        if (IsStopping) return false;
        using var deadline = CreateDeadline(cancellationToken);
        if (IsExpired(ticket) || !await AuthorizeAsync(ticket, DocumentAccess.Read, true, deadline.Token).ConfigureAwait(false)) return false;

        var roomKey = new OfficeRealTimeRoomKey(ticket.ResourceId, ticket.LifecycleGeneration);
        var member = new OfficeRealTimeBackplaneMember(roomKey, context.ConnectionId, Guid.NewGuid(), _hostId, ticket);
        LocalRoom room;
        lock (_sync)
        {
            if (IsStopping || _connections.Count + _connectReservations >= MaxConnectionsPerHost || _connections.ContainsKey(context.ConnectionId)) return false;
            _connectReservations++;
            if (!_rooms.TryGetValue(roomKey, out room!)) _rooms.Add(roomKey, room = new LocalRoom(roomKey));
            // Reserve local capacity before the Redis await. The room is registered,
            // but its worker cannot dispatch until the atomic snapshot is installed.
            room.Reservations++;
        }

        if (!await EnterCoordinatorAsync(room, deadline.Token).ConfigureAwait(false))
        { ReleaseReservation(room); return false; }

        long admissionGeneration = _backplane.Generation;
        OfficeRealTimeBackplaneConnectResult admitted;
        try { admitted = await _backplane.ConnectAsync(member, deadline.Token).ConfigureAwait(false); }
        catch (Exception)
        {
            InvalidateRoom(room);
            ReleaseReservation(room);
            QueueCleanup(member);
            room.Coordinator.Release();
            return false;
        }

        if (admitted.Status != OfficeRealTimeBackplaneStatus.Ok || admitted.Snapshot is null || IsExpired(ticket))
        {
            ReleaseReservation(room);
            if (admitted.Status == OfficeRealTimeBackplaneStatus.Ok)
                QueueCleanup(member);
            room.Coordinator.Release();
            return false;
        }

        LocalConnection? replaced = null;
        bool accepted;
        lock (_sync)
        {
            room.Reservations--;
            _connectReservations--;
            if (IsStopping || room.Invalidated || !_rooms.TryGetValue(room.Key, out var currentRoom) || !ReferenceEquals(currentRoom, room) ||
                _connections.Count >= MaxConnectionsPerHost || IsExpired(ticket) || !_backplane.IsHealthy ||
                _backplane.Generation != admissionGeneration ||
                (room.Epoch is not null && room.BackplaneGeneration != admissionGeneration) ||
                admitted.Snapshot.Epoch != room.Epoch && room.Epoch is not null)
            {
                accepted = false;
            }
            else
            {
                if (room.Epoch is null)
                {
                    room.Epoch = admitted.Snapshot.Epoch;
                    room.Cursor = admitted.Snapshot.Watermark;
                    room.InitialPresence = admitted.Snapshot.Presence.ToDictionary(x => x.ClientKey, StringComparer.Ordinal);
                    room.Initialized = true;
                }
                string clientKey = ClientKey(ticket.ClientId);
                replaced = room.Connections.Values.FirstOrDefault(x => x.Member.Ticket.ClientId == ticket.ClientId);
                if (replaced is not null && !StringComparer.Ordinal.Equals(replaced.Member.Ticket.Actor.Identity.Subject,
                    ticket.Actor.Identity.Subject))
                {
                    accepted = false;
                }
                else
                {
                    bool firstLocalMember = room.Connections.Count == 0;
                    var connection = new LocalConnection(member, context, room, admitted.Snapshot.Watermark);
                    _connections[context.ConnectionId] = connection;
                    room.Connections[context.ConnectionId] = connection;
                    if (firstLocalMember)
                    {
                        room.LastHeartbeatUtc = _time.GetUtcNow();
                        room.LastAuthorizationUtc = _time.GetUtcNow();
                        room.LastPollUtc = _time.GetUtcNow();
                        room.LastReapUtc = _time.GetUtcNow();
                        room.LastSuccessfulBrokerUtc = _time.GetUtcNow();
                        room.BackplaneGeneration = admissionGeneration;
                    }
                    room.PresenceWatermark[member.Incarnation] = admitted.Snapshot.Watermark;
                    if (replaced is not null) RemoveLocalLocked(replaced);
                    accepted = true;
                }
            }
        }

        if (!accepted)
        {
            QueueCleanup(member);
            ReleaseEmptyRoom(room);
            room.Coordinator.Release();
            return false;
        }
        replaced?.Context.Abort();
        room.Coordinator.Release();
        return true;
    }

    public Task<Dictionary<string, string>> GetCompletePresenceStateAsync(OfficeRealTimeTicket ticket,
        string connectionId, CancellationToken cancellationToken) => SnapshotAsync(ticket, connectionId, cancellationToken);

    private async Task<Dictionary<string, string>> SnapshotAsync(OfficeRealTimeTicket ticket,
        string connectionId, CancellationToken cancellationToken)
    {
        if (IsStopping) return [];
        using var deadline = CreateDeadline(cancellationToken);
        if (!TryGet(connectionId, ticket, out var requester)) return [];
        if (!await AuthorizeAsync(ticket, DocumentAccess.Read, true, deadline.Token).ConfigureAwait(false))
        {
            await DenyAsync(requester, deadline.Token).ConfigureAwait(false);
            return [];
        }
        if (!await EnterCoordinatorAsync(requester.Room, deadline.Token).ConfigureAwait(false)) return [];
        try
        {
            var result = await _backplane.ReadSnapshotAsync(requester.Member.Room, deadline.Token).ConfigureAwait(false);
            if (!SnapshotValid(requester.Room, result.Status, result.Snapshot))
            {
                InvalidateRoom(requester.Room);
                return [];
            }
            var snapshot = result.Snapshot!;
            if (snapshot.Epoch != requester.Room.Epoch) { InvalidateRoom(requester.Room); return []; }
            if (!snapshot.Members.Any(x => x.Incarnation == requester.Member.Incarnation))
            { DetachAndAbort(requester); return []; }
            var sources = snapshot.Presence.Join(snapshot.Members,
                    p => (p.ClientKey, p.Incarnation), m => (ClientKey(m.Ticket.ClientId), m.Incarnation),
                    (p, m) => (Presence: p, Member: m), EqualityComparer<(string, Guid)>.Default)
                .Take(MaxConnectionsPerRoom).ToArray();
            while (true)
            {
                long authorizationGeneration = _backplane.Generation;
                var checks = new List<OfficeRealTimeTicketCheck> { new(ticket, DocumentAccess.Read, true) };
                checks.AddRange(sources.Select(x => new OfficeRealTimeTicketCheck(x.Member.Ticket, DocumentAccess.Read, true)));
                var allowed = await _authorizer.AuthorizeTogetherAsync(checks, deadline.Token).ConfigureAwait(false);
                if (allowed.Length != checks.Count || !allowed[0])
                {
                    await DenyAsync(requester, deadline.Token).ConfigureAwait(false);
                    return [];
                }
                bool removed = false;
                for (int i = 0; i < sources.Length; i++)
                    if (!allowed[i + 1] || IsExpired(sources[i].Member.Ticket))
                    {
                        await RemoveQuietlyAsync(sources[i].Member, sources[i].Presence.ClientKey).ConfigureAwait(false);
                        removed = true;
                    }
                if (removed)
                {
                    var refreshed = await _backplane.ReadSnapshotAsync(requester.Member.Room, deadline.Token).ConfigureAwait(false);
                    if (!SnapshotValid(requester.Room, refreshed.Status, refreshed.Snapshot))
                    { InvalidateRoom(requester.Room); return []; }
                    var next = refreshed.Snapshot!;
                    if (next.Epoch != requester.Room.Epoch)
                    { InvalidateRoom(requester.Room); return []; }
                    if (!next.Members.Any(x => x.Incarnation == requester.Member.Incarnation))
                    { DetachAndAbort(requester); return []; }
                    snapshot = next;
                    sources = next.Presence.Join(next.Members,
                            p => (p.ClientKey, p.Incarnation), m => (ClientKey(m.Ticket.ClientId), m.Incarnation),
                            (p, m) => (Presence: p, Member: m), EqualityComparer<(string, Guid)>.Default)
                        .Take(MaxConnectionsPerRoom).ToArray();
                    continue;
                }
                lock (_sync)
                {
                    if (!IsCurrentLocked(requester) || IsExpired(ticket) || snapshot.Epoch != requester.Room.Epoch ||
                        !_backplane.IsHealthy || _backplane.Generation != authorizationGeneration) return [];
                    var output = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (int i = 0; i < sources.Length; i++)
                        if (allowed[i + 1] && !IsExpired(sources[i].Member.Ticket))
                            output[sources[i].Presence.ClientKey] = sources[i].Presence.Payload;
                    // The recipient watermark applies only to queued presence/departure events.
                    requester.Room.PresenceWatermark[requester.Member.Incarnation] = snapshot.Watermark;
                    return output;
                }
            }
        }
        catch (OfficeRealTimeBusyException) { throw; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return []; }
        finally { requester.Room.Coordinator.Release(); }
    }

    public async Task<bool> UpdatePresenceAsync(OfficeRealTimeTicket ticket, string connectionId,
        string payload, CancellationToken cancellationToken)
    {
        if (IsStopping) return false;
        if (!TryValidatePayload(payload, out var document) || document is null) return false;
        using (document)
            if (!IdentityMatches(document.RootElement, "A", ticket.ClientId) ||
                !IdentityMatches(document.RootElement, "clientId", ticket.ClientId) ||
                !IdentityMatches(document.RootElement, "Cid", ticket.ClientId, true)) return false;
        if (!TryGet(connectionId, ticket, out var sender)) return false;
        using var deadline = CreateDeadline(cancellationToken);
        if (!await AuthorizeAsync(ticket, DocumentAccess.Read, true, deadline.Token).ConfigureAwait(false))
        { await DenyAsync(sender, deadline.Token).ConfigureAwait(false); return false; }
        return await PublishAsync(sender, "presence", "presenceUpdated", payload,
            DocumentAccess.Read, deadline.Token).ConfigureAwait(false);
    }

    public async Task<bool> SendUpdateNotificationAsync(OfficeRealTimeTicket ticket, string connectionId,
        int channel, string payload, CancellationToken cancellationToken)
    {
        if (IsStopping) return false;
        if ((!IsClientChannel(channel) && channel != 4) || !TryValidatePayload(payload, out var document) || document is null)
            return false;
        using (document)
        {
            var root = document.RootElement;
            if (channel == 4)
            {
                if (root.EnumerateObject().Count() != 2 || !root.TryGetProperty("Id", out var id) ||
                    id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out _) ||
                    !root.TryGetProperty("TD", out var delay) || delay.ValueKind != JsonValueKind.Number ||
                    !delay.TryGetInt32(out var milliseconds) || milliseconds < 0) return false;
                using var checkDeadline = CreateDeadline(cancellationToken);
                return await AuthorizeAsync(ticket, DocumentAccess.Read, true, checkDeadline.Token).ConfigureAwait(false) &&
                    TryGet(connectionId, ticket, out _);
            }
            if (channel == 1 && !IsValidChannelOne(root)) return false;
            if (channel != 1 && root.TryGetProperty("A", out var senderValue) &&
                (senderValue.ValueKind != JsonValueKind.String || !Guid.TryParse(senderValue.GetString(), out var suppliedId) || suppliedId != ticket.ClientId)) return false;
            if (channel == 3 && (!TryGetSender(root, out var senderId) || senderId != ticket.ClientId)) return false;
            if (channel == 8 && !root.TryGetProperty("SL2", out _)) return false;
        }
        if (!TryGet(connectionId, ticket, out var connection)) return false;
        using var deadline = CreateDeadline(cancellationToken);
        if (!await AuthorizeAsync(ticket, DocumentAccess.Write, true, deadline.Token).ConfigureAwait(false))
        { await DenyAsync(connection, deadline.Token).ConfigureAwait(false); return false; }
        return await PublishAsync(connection, "notification", "updateNotification", JsonSerializer.Serialize(new { channel, payload }),
            DocumentAccess.Write, deadline.Token).ConfigureAwait(false);
    }

    private async Task<bool> PublishAsync(LocalConnection sender, string kind, string method,
        string payload, DocumentAccess access, CancellationToken cancellationToken)
    {
        if (!await EnterCoordinatorAsync(sender.Room, cancellationToken).ConfigureAwait(false)) return false;
        try
        {
            if (!IsCurrent(sender) || IsExpired(sender.Member.Ticket)) return false;
            var members = await _backplane.ObserveMembersAsync(sender.Member.Room, cancellationToken).ConfigureAwait(false);
            if (members.Status != OfficeRealTimeBackplaneStatus.Ok || members.Epoch != sender.Room.Epoch)
            { InvalidateRoom(sender.Room); return false; }
            if (!members.Members.Any(x => x.Incarnation == sender.Member.Incarnation))
            { DetachAndAbort(sender); return false; }
            if (!await AuthorizeAsync(sender.Member.Ticket, access, true, cancellationToken).ConfigureAwait(false))
            { await DenyAsync(sender, cancellationToken).ConfigureAwait(false); return false; }
            if (!IsCurrent(sender)) return false;
            var envelope = payload;
            var mutation = await _backplane.MutateAsync(sender.Member, sender.Room.Epoch!, Guid.NewGuid().ToString("N"),
                kind, ClientKey(sender.Member.Ticket.ClientId), method, envelope, cancellationToken).ConfigureAwait(false);
            // A definitive size/validation rejection did not publish an event.
            // Preserve healthy peers; epoch changes still require a reconnect.
            if (mutation.Status == OfficeRealTimeBackplaneStatus.Denied && mutation.Epoch == sender.Room.Epoch)
                return false;
            if (mutation.Status != OfficeRealTimeBackplaneStatus.Ok || mutation.Epoch != sender.Room.Epoch)
            { InvalidateRoom(sender.Room); return false; }
            // Only the bounded dispatcher workers fetch event batches. Hub calls
            // acknowledge the accepted mutation rather than allocating and sending
            // another batch outside those worker slots.
            SignalRoomChanged(sender.Room.Key);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { InvalidateRoom(sender.Room); return false; }
        finally { sender.Room.Coordinator.Release(); }
    }

    public async Task DisconnectAsync(string connectionId, CancellationToken cancellationToken)
    {
        if (IsStopping) return;
        LocalConnection? connection;
        lock (_sync) _connections.TryGetValue(connectionId, out connection);
        if (connection is null) return;
        DetachAndAbort(connection, queueCleanup: false);
        await RemoveQuietlyAsync(connection.Member, ClientKey(connection.Member.Ticket.ClientId)).ConfigureAwait(false);
    }

    private async Task RemoveQuietlyAsync(OfficeRealTimeBackplaneMember member, string? presenceKey)
    {
        try
        {
            using var deadline = new CancellationTokenSource(OperationTimeout);
            await _backplane.RemoveAsync(member, presenceKey, Guid.NewGuid().ToString("N"), deadline.Token).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task RunSchedulerAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(SchedulerInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
            {
                LocalRoom[] rooms;
                lock (_sync) rooms = _rooms.Values.Where(x => x.Connections.Count > 0).ToArray();
                var now = _time.GetUtcNow();
                // This pass never acquires a room coordinator or one of the four I/O slots.
                LocalConnection[] expired;
                lock (_sync) expired = _connections.Values.Where(x => IsExpired(x.Member.Ticket)).ToArray();
                foreach (var connection in expired) { Interlocked.Increment(ref _expiryAborts); DetachAndAbort(connection); }
                foreach (var room in rooms)
                {
                    if (room.Connections.Count == 0 || !room.Initialized) continue;
                    if (now - room.LastHeartbeatUtc >= HeartbeatInterval) QueuePeriodic(room, WorkKind.Heartbeat);
                    if (now - room.LastAuthorizationUtc >= AuthorizationInterval) QueuePeriodic(room, WorkKind.Authorization);
                    if (now - room.LastPollUtc >= PollInterval) QueuePeriodic(room, WorkKind.Poll);
                    if (now - room.LastReapUtc >= ReapInterval) QueuePeriodic(room, WorkKind.Reap);
                    if (!_backplane.IsHealthy || room.BackplaneGeneration != _backplane.Generation ||
                        now - room.LastHeartbeatUtc >= HeartbeatInterval + OperationTimeout ||
                        now - room.LastAuthorizationUtc >= AuthorizationInterval + OperationTimeout ||
                        now - room.LastSuccessfulBrokerUtc >= BrokerDeadline) InvalidateRoom(room);
                }
                lock (_sync) QueueCleanupWorkLocked();
                PumpQueuedWorkers(stopping);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private void QueuePeriodic(LocalRoom room, WorkKind kind)
    {
        lock (_sync)
        {
            if (IsStopping || !_rooms.TryGetValue(room.Key, out var current) || !ReferenceEquals(current, room) ||
                room.Connections.Count == 0 || !room.Initialized) return;
            if (!room.Queued.Add(kind)) return;
            room.PendingWork.Enqueue(new(kind, _time.GetUtcNow()));
            EnqueueRoomLocked(room);
        }
        SchedulePump();
    }

    private void QueueCleanup(OfficeRealTimeBackplaneMember member)
    {
        lock (_sync)
        {
            if (_cleanupQueue.Count < MaxConnectionsPerHost)
                _cleanupQueue[member.Incarnation] = member;
            QueueCleanupWorkLocked();
        }
        SchedulePump();
    }

    private void OnRoomChanged(OfficeRealTimeRoomKey key) => SignalRoomChanged(key);

    private void SignalRoomChanged(OfficeRealTimeRoomKey key)
    {
        lock (_sync)
        {
            if (IsStopping || !_rooms.TryGetValue(key, out var room) || room.Connections.Count == 0 || !room.Initialized)
                return;
            if (room.Queued.Add(WorkKind.Poll))
                room.PendingWork.Enqueue(new(WorkKind.Poll, _time.GetUtcNow()));
            EnqueueRoomLocked(room);
        }
        SchedulePump();
    }

    private void EnqueueRoomLocked(LocalRoom room)
    {
        if (room.ActiveWorkers != 0 || room.ReadyEnqueued || room.PendingWork.Count == 0 || IsStopping ||
            !_rooms.TryGetValue(room.Key, out var current) || !ReferenceEquals(current, room)) return;
        room.ReadyEnqueued = true;
        _readyWork.Enqueue(new(room, null));
    }

    private void QueueCleanupWorkLocked()
    {
        if (_cleanupQueue.Count == 0 || _cleanupReady || _cleanupActive || IsStopping) return;
        _cleanupReady = true;
        _readyWork.Enqueue(new(null, new(WorkKind.Cleanup, _time.GetUtcNow())));
    }

    // One ThreadPool callback drains as many immediately available slots as
    // possible. The callback itself is never invoked recursively by completion.
    private void SchedulePump()
    {
        lock (_sync)
        {
            if (IsStopping || _stopping is null || Interlocked.CompareExchange(ref _pumpScheduled, 1, 0) != 0) return;
            // Admission and shutdown share the lock. Capture the token now and
            // track the pump itself before disposal can drain its dependencies.
            CancellationToken token = _stopping.Token;
            Task pump = Task.Run(() =>
            {
                try { PumpQueuedWorkers(token); }
                finally
                {
                    Interlocked.Exchange(ref _pumpScheduled, 0);
                    bool ready;
                    lock (_sync)
                        ready = !IsStopping && _readyWork.Count > 0 && _workers.CurrentCount > 0;
                    if (ready) SchedulePump();
                }
            }, CancellationToken.None);
            _running.TryAdd(pump, 0);
            _ = pump.ContinueWith(t => _running.TryRemove(t, out _), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void PumpQueuedWorkers(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested && !IsStopping && _workers.Wait(0))
        {
            ReadyWork ready;
            lock (_sync)
            {
                if (IsStopping || stopping.IsCancellationRequested)
                { _workers.Release(); return; }
                if (_readyWork.Count == 0)
                { _workers.Release(); return; }
                ready = _readyWork.Dequeue();
                if (ready.Room is { } room)
                {
                    room.ReadyEnqueued = false;
                    if (!_rooms.TryGetValue(room.Key, out var current) || !ReferenceEquals(current, room) ||
                        room.Connections.Count == 0 || room.ActiveWorkers != 0 || room.PendingWork.Count == 0)
                    { _workers.Release(); continue; }
                    room.ActiveWorkers++;
                    var request = room.PendingWork.Dequeue();
                    room.Queued.Remove(request.Kind);
                    StartWorkerLocked(room, request, stopping);
                }
                else
                {
                    _cleanupReady = false;
                    if (_cleanupActive || _cleanupQueue.Count == 0) { _workers.Release(); continue; }
                    _cleanupActive = true;
                    StartCleanupWorkerLocked(ready.Request!, stopping);
                }
            }
        }
    }

    private void StartWorkerLocked(LocalRoom room, WorkRequest request, CancellationToken stopping)
    {
        Task task = Task.Run(() => ExecuteWorkAsync(room, request, stopping), CancellationToken.None);
        _running.TryAdd(task, 0);
        _ = task.ContinueWith(t => WorkerCompleted(t, room, cleanup: false), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void StartCleanupWorkerLocked(WorkRequest request, CancellationToken stopping)
    {
        Task task = Task.Run(() => ExecuteScheduledCleanupAsync(request, stopping), CancellationToken.None);
        _running.TryAdd(task, 0);
        _ = task.ContinueWith(t => WorkerCompleted(t, null, cleanup: true), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task ExecuteScheduledCleanupAsync(WorkRequest request, CancellationToken stopping)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var remaining = OperationTimeout - (_time.GetUtcNow() - request.QueuedUtc);
        deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));
        if (remaining <= TimeSpan.Zero) return;
        try { await DrainCleanupAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested) { }
        catch { }
    }

    private void WorkerCompleted(Task task, LocalRoom? room, bool cleanup)
    {
        // Continuations can run inline on a Redis or transport completion thread.
        // Move scheduler work to one coalesced ThreadPool callback instead.
        if (room is not null)
        {
            lock (_sync)
            {
                EnqueueRoomLocked(room);
            }
        }
        if (cleanup)
        {
            lock (_sync)
            {
                _cleanupActive = false;
                QueueCleanupWorkLocked();
            }
        }
        // The continuation runs only after the underlying operation task settles.
        // Release before removing it so DisposeAsync cannot dispose the semaphore
        // in the small gap between those operations.
        _workers.Release();
        _running.TryRemove(task, out _);
        if (!IsStopping) SchedulePump();
    }

    private async Task ExecuteWorkAsync(LocalRoom room, WorkRequest request, CancellationToken stopping)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var remaining = OperationTimeout - (_time.GetUtcNow() - request.QueuedUtc);
        deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));
        try
        {
            if (remaining <= TimeSpan.Zero) { InvalidateRoom(room); return; }
            switch (request.Kind)
            {
                case WorkKind.Heartbeat:
                {
                    LocalConnection[] members;
                    lock (_sync) members = room.Connections.Values.ToArray();
                    Interlocked.Increment(ref _heartbeatCalls);
                    var result = await _backplane.HeartbeatAsync(members.Select(x => x.Member).ToArray(), deadline.Token).ConfigureAwait(false);
                    if (result.Status != OfficeRealTimeBackplaneStatus.Ok)
                    { InvalidateRoom(room); return; }
                    foreach (var member in members)
                        if (!result.CurrentIncarnations.Contains(member.Member.Incarnation)) DetachAndAbort(member);
                    room.LastHeartbeatUtc = _time.GetUtcNow();
                    room.LastSuccessfulBrokerUtc = room.LastHeartbeatUtc;
                    break;
                }
                case WorkKind.Authorization:
                    Interlocked.Increment(ref _authorizationSweeps);
                    await AuthorizeRoomAsync(room, deadline.Token).ConfigureAwait(false);
                    room.LastAuthorizationUtc = _time.GetUtcNow();
                    break;
                case WorkKind.Reap:
                {
                    var observed = await _backplane.ObserveMembersAsync(room.Key, deadline.Token).ConfigureAwait(false);
                    if (observed.Status == OfficeRealTimeBackplaneStatus.Busy)
                    { room.LastReapUtc = _time.GetUtcNow(); break; }
                    if (observed.Status != OfficeRealTimeBackplaneStatus.Ok || observed.Epoch != room.Epoch)
                    { InvalidateRoom(room); return; }
                    LocalConnection[] local;
                    lock (_sync) local = room.Connections.Values.ToArray();
                    foreach (var member in local)
                        if (!observed.Members.Any(current => current.Incarnation == member.Member.Incarnation)) DetachAndAbort(member);
                    room.LastReapUtc = _time.GetUtcNow();
                    break;
                }
                case WorkKind.Poll:
                    Interlocked.Increment(ref _pollCalls);
                    if (!await EnterCoordinatorAsync(room, deadline.Token).ConfigureAwait(false))
                    { InvalidateRoom(room); return; }
                    try
                    {
                        if (!await ProcessEventsAsync(room, deadline.Token).ConfigureAwait(false)) return;
                    }
                    finally { room.Coordinator.Release(); }
                    // Keep the cadence relative to the requested poll. Measuring
                    // from completion would skip the next timer tick after any I/O.
                    room.LastPollUtc = request.QueuedUtc;
                    room.LastSuccessfulBrokerUtc = _time.GetUtcNow();
                    break;
                case WorkKind.Cleanup:
                    await DrainCleanupAsync(deadline.Token).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested) { InvalidateRoom(room); }
        catch { InvalidateRoom(room); }
        finally { lock (_sync) room.ActiveWorkers--; }
    }

    private async Task AuthorizeRoomAsync(LocalRoom room, CancellationToken cancellationToken)
    {
        LocalConnection[] connections;
        lock (_sync) connections = room.Connections.Values.ToArray();
        if (connections.Length == 0) return;
        var checks = connections.Select(x => new OfficeRealTimeTicketCheck(x.Member.Ticket, DocumentAccess.Read, true)).ToArray();
        var allowed = await _authorizer.AuthorizeTogetherAsync(checks, cancellationToken).ConfigureAwait(false);
        if (allowed.Length != connections.Length) { InvalidateRoom(room); return; }
        for (int i = 0; i < connections.Length; i++)
            if (!allowed[i] || IsExpired(connections[i].Member.Ticket)) DetachAndAbort(connections[i]);
    }

    private async Task DrainCleanupAsync(CancellationToken cancellationToken)
    {
        OfficeRealTimeBackplaneMember[] batch;
        lock (_sync)
        {
            batch = _cleanupQueue.Values.Take(32).ToArray();
            foreach (var member in batch) _cleanupQueue.Remove(member.Incarnation);
        }
        for (int index = 0; index < batch.Length; index++)
        {
            var member = batch[index];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _backplane.RemoveAsync(member, ClientKey(member.Ticket.ClientId), Guid.NewGuid().ToString("N"), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                for (int retry = index; retry < batch.Length; retry++) QueueCleanup(batch[retry]);
                return;
            }
        }
    }

    private async Task DrainCleanupWorkerAsync(CancellationToken stopping)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        deadline.CancelAfter(OperationTimeout);
        await DrainCleanupAsync(deadline.Token).ConfigureAwait(false);
    }

    private async Task<bool> ProcessEventsAsync(LocalRoom room, CancellationToken cancellationToken)
    {
        string? epoch;
        long cursor;
        lock (_sync) { epoch = room.Epoch; cursor = room.Cursor; }
        if (!_backplane.IsHealthy || room.BackplaneGeneration != _backplane.Generation)
        { InvalidateRoom(room); return false; }
        if (epoch is null) return false;
        var read = await _backplane.ReadEventsAsync(room.Key, epoch, cursor, MaxEventsPerPoll, MaxBytesPerPoll, cancellationToken)
            .ConfigureAwait(false);
        var sizedEvents = read.Events.Select(item => (Item: item, Bytes: item.EncodedBytes)).ToArray();
        if (read.Status != OfficeRealTimeBackplaneStatus.Ok || read.NeedsCleanup || read.HasGap || read.Epoch != epoch ||
            read.Events.Count > MaxEventsPerPoll || sizedEvents.Any(item => item.Bytes is <= 0 or > MaxEventBytes) ||
            sizedEvents.Sum(item => (long)item.Bytes) > MaxBytesPerPoll ||
            (read.Events.Count == 0 && read.Watermark > cursor) ||
            (read.Events.Count > 0 && read.Events[0].Sequence > cursor + 1))
        { InvalidateRoom(room); return false; }

        foreach (var sized in sizedEvents.OrderBy(x => x.Item.Sequence))
        {
            var item = sized.Item;
            if (item.Sequence <= room.Cursor) continue;
            if (item.Sequence != room.Cursor + 1 || item.Sender.Room != room.Key ||
                read.RedisTimeMilliseconds - item.RedisTimeMilliseconds > 30_000 || item.Epoch != epoch)
            { InvalidateRoom(room); return false; }
            if (!Enqueue(room, item, sized.Bytes)) { InvalidateRoom(room); return false; }
            try
            {
                if (!await DispatchEventAsync(room, item, cancellationToken).ConfigureAwait(false)) return false;
                lock (_sync) room.Cursor = item.Sequence;
            }
            finally { Dequeue(room, item, sized.Bytes); }
        }
        return true;
    }

    private async Task<bool> DispatchEventAsync(LocalRoom room, OfficeRealTimeBackplaneEvent item, CancellationToken cancellationToken)
    {
        LocalConnection[] recipients;
        lock (_sync) recipients = room.Connections.Values.Where(x => x.Member.Incarnation != item.Sender.Incarnation).ToArray();
        bool departure = item.Kind == "departure";
        // Arguments are constructed once and never changed while sends are outstanding.
        object?[] arguments;
        if (departure) arguments = [new Dictionary<string, string>(), new[] { item.ClientKey }];
        else if (item.Kind == "presence") arguments = [new Dictionary<string, string> { [item.ClientKey] = item.PayloadJson }, Array.Empty<string>()];
        else
        {
            using var payload = JsonDocument.Parse(item.PayloadJson);
            arguments = [payload.RootElement.GetProperty("channel").GetInt32(), payload.RootElement.GetProperty("payload").GetString()!];
        }

        // A room worker reserves capacity for at most four concurrent sends. Four
        // workers therefore admit at most sixteen sends, including canceled I/O
        // that has not physically completed. The coordinator retains event order.
        foreach (var candidates in recipients.Chunk(4))
        {
            var members = await _backplane.ObserveMembersAsync(room.Key, cancellationToken).ConfigureAwait(false);
            if (members.Status != OfficeRealTimeBackplaneStatus.Ok || members.Epoch != room.Epoch)
            { InvalidateRoom(room); return false; }
            var incarnations = members.Members.Select(x => x.Incarnation).ToHashSet();
            if (!departure && !incarnations.Contains(item.Sender.Incarnation)) return true;
            var eligible = new List<LocalConnection>(candidates.Length);
            foreach (var recipient in candidates)
            {
                // A fresh incarnation never replays its pre-admission backlog.
                // Later presence snapshots only change the presence watermark.
                if (item.Sequence <= recipient.AdmissionWatermark) continue;
                if (!incarnations.Contains(recipient.Member.Incarnation) || IsExpired(recipient.Member.Ticket))
                { DetachAndAbort(recipient); continue; }
                lock (_sync)
                {
                    if ((departure || item.Kind == "presence") &&
                        room.PresenceWatermark.TryGetValue(recipient.Member.Incarnation, out long watermark) && item.Sequence <= watermark) continue;
                }
                eligible.Add(recipient);
            }
            if (eligible.Count == 0) continue;
            var checks = new List<OfficeRealTimeTicketCheck>(eligible.Count + 1);
            if (!departure) checks.Add(new(item.Sender.Ticket, item.Kind == "notification" ? DocumentAccess.Write : DocumentAccess.Read, true));
            checks.AddRange(eligible.Select(x => new OfficeRealTimeTicketCheck(x.Member.Ticket, DocumentAccess.Read, false)));
            long authorizationGeneration = _backplane.Generation;
            var allowed = await _authorizer.AuthorizeTogetherAsync(checks.ToArray(), cancellationToken).ConfigureAwait(false);
            if (allowed.Length != checks.Count) { InvalidateRoom(room); return false; }
            bool senderAllowed = departure || allowed[0];
            if (!senderAllowed)
            {
                LocalConnection? localSender;
                lock (_sync) localSender = _connections.Values.FirstOrDefault(x => x.Member.Incarnation == item.Sender.Incarnation);
                if (localSender is not null) DetachAndAbort(localSender);
                else QueueCleanup(item.Sender);
            }
            var sends = new List<(LocalConnection Recipient, Task Pending, CancellationTokenSource Deadline)>(eligible.Count);
            for (int index = 0; index < eligible.Count; index++)
            {
                var recipient = eligible[index];
                if (!allowed[index + (departure ? 0 : 1)]) { DetachAndAbort(recipient); continue; }
                if (!senderAllowed) continue;
                // No broker await or another recipient's send await after the
                // common authorization. Remote membership is valid at observation.
                lock (_sync)
                {
                    if (!IsCurrentLocked(recipient) || IsExpired(recipient.Member.Ticket) || room.Epoch != item.Epoch ||
                        !_backplane.IsHealthy || _backplane.Generation != authorizationGeneration) continue;
                    if (!departure && (IsExpired(item.Sender.Ticket) ||
                        (item.Sender.HostId == _hostId && !_connections.Values.Any(x => x.Member.Incarnation == item.Sender.Incarnation)))) continue;
                    if (departure && members.Members.Any(x => ClientKey(x.Ticket.ClientId) == item.ClientKey &&
                        x.Incarnation != item.Sender.Incarnation)) continue;
                }
                var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(SendTimeout);
                try
                {
                    var pending = _hub.Clients.Client(recipient.Context.ConnectionId).SendCoreAsync(item.Method, arguments, deadline.Token);
                    sends.Add((recipient, pending, deadline));
                }
                catch { deadline.Dispose(); DetachAndAbort(recipient); }
            }
            // Each monitor enforces its own deadline. One transport ignoring
            // cancellation cannot postpone the other recipients' local aborts.
            await Task.WhenAll(sends.Select(send => MonitorSendAsync(send.Recipient, send.Pending, send.Deadline))).ConfigureAwait(false);
        }
        return true;
    }

    private async Task MonitorSendAsync(LocalConnection recipient, Task pending, CancellationTokenSource deadline)
    {
        using (deadline)
        {
            try { await pending.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch { DetachAndAbort(recipient); }
            finally
            {
                if (!pending.IsCompleted)
                    try { await pending.ConfigureAwait(false); }
                    catch { }
            }
        }
    }

    private async Task DenyAsync(LocalConnection connection, CancellationToken cancellationToken)
    {
        DetachAndAbort(connection, queueCleanup: false);
        await RemoveQuietlyAsync(connection.Member, ClientKey(connection.Member.Ticket.ClientId)).ConfigureAwait(false);
    }

    private void DetachAndAbort(LocalConnection connection, bool queueCleanup = true)
    {
        lock (_sync) RemoveLocalLocked(connection);
        connection.Context.Abort();
        if (queueCleanup) QueueCleanup(connection.Member);
    }

    private void RemoveLocalLocked(LocalConnection connection)
    {
        _connections.Remove(connection.Context.ConnectionId);
        connection.Room.Connections.Remove(connection.Context.ConnectionId);
        connection.Room.PresenceWatermark.Remove(connection.Member.Incarnation);
        if (connection.Room.Connections.Count == 0 && connection.Room.Reservations == 0)
        {
            connection.Room.Invalidated = true;
            RemoveRoomLocked(connection.Room);
        }
    }

    private void InvalidateRoom(LocalRoom room)
    {
        LocalConnection[] removed;
        lock (_sync)
        {
            if (room.Invalidated) return;
            room.Invalidated = true;
            removed = room.Connections.Values.ToArray();
            foreach (var connection in removed) RemoveLocalLocked(connection);
            RemoveRoomLocked(room);
        }
        foreach (var connection in removed) connection.Context.Abort();
        foreach (var connection in removed) QueueCleanup(connection.Member);
    }

    private bool Enqueue(LocalRoom room, OfficeRealTimeBackplaneEvent item, int bytes)
    {
        if (bytes > MaxEventBytes) return false;
        lock (_sync)
        {
            if (room.QueueEvents + 1 > MaxQueueEventsPerRoom || room.QueueBytes + bytes > MaxQueueBytesPerRoom ||
                _queuedHostEvents + 1 > MaxQueueEventsPerHost || _queuedHostBytes + bytes > MaxQueueBytesPerHost) return false;
            room.QueueEvents++;
            room.QueueBytes += bytes;
            _queuedHostEvents++;
            _queuedHostBytes += bytes;
            room.PendingEvents.Enqueue((item.Sequence, bytes));
        }
        return true;
    }

    private void Dequeue(LocalRoom room, OfficeRealTimeBackplaneEvent item, int bytes)
    {
        lock (_sync)
        {
            if (room.PendingEvents.Count > 0 && room.PendingEvents.Peek().Sequence == item.Sequence)
                room.PendingEvents.Dequeue();
            room.QueueEvents--;
            room.QueueBytes -= bytes;
            _queuedHostEvents--;
            _queuedHostBytes -= bytes;
        }
    }

    private async Task<bool> EnterCoordinatorAsync(LocalRoom room, CancellationToken cancellationToken)
    {
        using var deadline = CreateDeadline(cancellationToken);
        try { await room.Coordinator.WaitAsync(deadline.Token).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
    }

    private bool TryGet(string connectionId, OfficeRealTimeTicket ticket, out LocalConnection connection)
    {
        lock (_sync)
            if (_connections.TryGetValue(connectionId, out connection!) && connection.Member.Ticket == ticket && IsCurrentLocked(connection))
                return true;
        connection = null!;
        return false;
    }

    private bool IsCurrent(LocalConnection connection)
    { lock (_sync) return IsCurrentLocked(connection); }

    private bool IsCurrentLocked(LocalConnection connection) =>
        !connection.Room.Invalidated && _backplane.IsHealthy && connection.Room.BackplaneGeneration == _backplane.Generation &&
        _connections.TryGetValue(connection.Context.ConnectionId, out var current) && ReferenceEquals(current, connection);

    private bool SnapshotValid(LocalRoom room, OfficeRealTimeBackplaneStatus status, OfficeRealTimeBackplaneSnapshot? snapshot) =>
        status == OfficeRealTimeBackplaneStatus.Ok && snapshot is not null && !room.Invalidated &&
        _backplane.IsHealthy && room.BackplaneGeneration == _backplane.Generation;

    private void ReleaseReservation(LocalRoom room)
    { lock (_sync) { room.Reservations--; _connectReservations--; if (room.Reservations == 0 && room.Connections.Count == 0) RemoveRoomLocked(room); } }

    private void ReleaseEmptyRoom(LocalRoom room)
    { lock (_sync) if (room.Reservations == 0 && room.Connections.Count == 0) RemoveRoomLocked(room); }

    private void RemoveRoomLocked(LocalRoom room)
    {
        if (!_rooms.TryGetValue(room.Key, out var current) || !ReferenceEquals(current, room)) return;
        _rooms.Remove(room.Key);
        room.PendingWork.Clear();
        room.Queued.Clear();
        room.ReadyEnqueued = false;
        if (_readyWork.Count == 0) return;
        var retained = _readyWork.Where(item => !ReferenceEquals(item.Room, room)).ToArray();
        _readyWork.Clear();
        foreach (var item in retained) _readyWork.Enqueue(item);
    }

    private bool IsExpired(OfficeRealTimeTicket ticket) => ticket.ExpiresUtc <= _time.GetUtcNow();
    private bool IsStopping => Volatile.Read(ref _stopRequested) != 0 ||
        _stopping?.IsCancellationRequested == true || Volatile.Read(ref _disposed) != 0;
    private ValueTask<bool> AuthorizeAsync(OfficeRealTimeTicket ticket, DocumentAccess access, bool owns, CancellationToken token) =>
        _authorizer.AuthorizeAsync(ticket, access, owns, token);

    private CancellationTokenSource CreateDeadline(CancellationToken token)
    {
        var cts = _stopping is null
            ? CancellationTokenSource.CreateLinkedTokenSource(token)
            : CancellationTokenSource.CreateLinkedTokenSource(token, _stopping.Token);
        cts.CancelAfter(OperationTimeout);
        return cts;
    }

    private static string ClientKey(Guid clientId) => clientId.ToString("B").ToLowerInvariant();
    private static bool IsClientChannel(int channel) => channel is 1 or 3 or 8 or 14 or 18;
    private static bool IsValidChannelOne(JsonElement root)
    {
        HashSet<string> allowed = new(StringComparer.Ordinal) { "HQ", "Id", "NT", "WEB" };
        foreach (var property in root.EnumerateObject())
            if (ChangeTokenFields.Contains(property.Name) || !allowed.Contains(property.Name)) return false;
        return root.EnumerateObject().Any();
    }
    private static bool TryGetSender(JsonElement root, out Guid sender)
    {
        sender = default;
        return root.TryGetProperty("A", out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out sender);
    }
    private static bool IdentityMatches(JsonElement root, string name, Guid client, bool allowEmpty = false)
    {
        if (!root.TryGetProperty(name, out var value)) return true;
        if (value.ValueKind != JsonValueKind.String) return false;
        string? text = value.GetString();
        return allowEmpty && string.IsNullOrEmpty(text) || Guid.TryParse(text, out var parsed) && parsed == client;
    }
    private static bool TryValidatePayload(string? payload, out JsonDocument? document)
    {
        document = null;
        if (payload is null || Encoding.UTF8.GetByteCount(payload) > MaxPayloadBytes) return false;
        try
        {
            var parsed = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow });
            if (parsed.RootElement.ValueKind != JsonValueKind.Object || HasDuplicate(parsed.RootElement)) { parsed.Dispose(); return false; }
            document = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }
    private static bool HasDuplicate(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject()) if (!names.Add(property.Name) || HasDuplicate(property.Value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray()) if (HasDuplicate(value)) return true;
        return false;
    }

    private enum WorkKind { Heartbeat, Authorization, Poll, Reap, Cleanup }
    private sealed record WorkRequest(WorkKind Kind, DateTimeOffset QueuedUtc);
    private sealed record ReadyWork(LocalRoom? Room, WorkRequest? Request);
    private sealed class LocalRoom(OfficeRealTimeRoomKey key)
    {
        internal OfficeRealTimeRoomKey Key { get; } = key;
        internal SemaphoreSlim Coordinator { get; } = new(1, 1);
        internal Dictionary<string, LocalConnection> Connections { get; } = new(StringComparer.Ordinal);
        internal Dictionary<Guid, long> PresenceWatermark { get; } = [];
        internal Dictionary<string, OfficeRealTimeBackplanePresence> InitialPresence { get; set; } = new(StringComparer.Ordinal);
        internal Queue<(long Sequence, int Bytes)> PendingEvents { get; } = new();
        internal Queue<WorkRequest> PendingWork { get; } = new();
        internal HashSet<WorkKind> Queued { get; } = [];
        internal string? Epoch { get; set; }
        internal long Cursor { get; set; }
        internal long BackplaneGeneration { get; set; }
        internal int Reservations { get; set; }
        internal int ActiveWorkers { get; set; }
        internal bool ReadyEnqueued { get; set; }
        internal int QueueEvents { get; set; }
        internal int QueueBytes { get; set; }
        internal bool Initialized { get; set; }
        internal bool Invalidated { get; set; }
        internal DateTimeOffset LastHeartbeatUtc { get; set; } = DateTimeOffset.MinValue;
        internal DateTimeOffset LastAuthorizationUtc { get; set; } = DateTimeOffset.MinValue;
        internal DateTimeOffset LastPollUtc { get; set; } = DateTimeOffset.MinValue;
        internal DateTimeOffset LastReapUtc { get; set; } = DateTimeOffset.MinValue;
        internal DateTimeOffset LastSuccessfulBrokerUtc { get; set; } = DateTimeOffset.MinValue;
    }
    private sealed record LocalConnection(OfficeRealTimeBackplaneMember Member, HubCallerContext Context,
        LocalRoom Room, long AdmissionWatermark);
}

internal sealed record OfficeRealTimeSchedulerDiagnostics(long AuthorizationSweeps, long HeartbeatCalls,
    long PollCalls, int OccupiedRooms, int ActiveWorkers, int PendingOperations, long ExpiryAborts,
    long RedisScripts, long RedisArgumentBytes, long PubSubHintsReceived = 0, long PubSubHintBytesReceived = 0);
