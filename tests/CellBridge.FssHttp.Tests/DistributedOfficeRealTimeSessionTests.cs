using System.Reflection;
using System.Security.Claims;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace CellBridge.FssHttp.Tests;

public sealed partial class DistributedOfficeRealTimeSessionTests
{
    [Fact]
    public async Task Joining_recipient_skips_presence_covered_by_its_admission_snapshot_without_skipping_existing_peers()
    {
        var broker = new FakeBackplane();
        var clients = new FakeClients();
        await using var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(),
            new FakeHubContext(clients), broker);
        Guid resource = Guid.NewGuid();
        var sender = await Connect(store, resource, "writer");
        var existing = await Connect(store, resource, "existing");
        await broker.MutateAsync(sender.Member, broker.Epoch, "before-join", "presence",
            ClientKey(sender.Ticket.ClientId), "presenceUpdated", "{}", CancellationToken.None);
        var joining = await Connect(store, resource, "joining");

        await PollRoom(store, resource);

        Assert.Contains(clients.Calls, call => call.ConnectionId == existing.Context.ConnectionId && call.Method == "presenceUpdated");
        Assert.DoesNotContain(clients.Calls, call => call.ConnectionId == joining.Context.ConnectionId && call.Method == "presenceUpdated");
    }

    [Fact]
    public async Task Presence_snapshot_watermark_skips_older_queued_presence_for_that_recipient()
    {
        var broker = new FakeBackplane();
        var clients = new FakeClients();
        await using var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(),
            new FakeHubContext(clients), broker);
        Guid resource = Guid.NewGuid();
        var first = await Connect(store, resource, "writer");
        var recipient = await Connect(store, resource, "reader");
        string payload = "{\"text\":\"latest\"}";

        await broker.MutateAsync(first.Member, broker.Epoch, "presence-1", "presence",
            ClientKey(first.Ticket.ClientId), "presenceUpdated", payload, CancellationToken.None);
        var snapshot = await store.GetCompletePresenceStateAsync(recipient.Ticket, recipient.Context.ConnectionId,
            CancellationToken.None);
        Assert.Equal(payload, snapshot[ClientKey(first.Ticket.ClientId)]);

        await PollRoom(store, resource);

        Assert.DoesNotContain(clients.Calls, call => call.ConnectionId == recipient.Context.ConnectionId &&
            call.Method == "presenceUpdated");
    }

    [Fact]
    public async Task Presence_snapshot_watermark_does_not_suppress_later_typing_notification()
    {
        var broker = new FakeBackplane();
        var clients = new FakeClients();
        await using var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(),
            new FakeHubContext(clients), broker);
        Guid resource = Guid.NewGuid();
        var sender = await Connect(store, resource, "writer");
        var recipient = await Connect(store, resource, "reader");
        await store.GetCompletePresenceStateAsync(recipient.Ticket, recipient.Context.ConnectionId, CancellationToken.None);
        string payload = $$"""{"A":"{{sender.Ticket.ClientId:B}}","text":"typing"}""";
        await broker.MutateAsync(sender.Member, broker.Epoch, "typing-1", "notification", "",
            "updateNotification", $$"""{"channel":3,"payload":{{System.Text.Json.JsonSerializer.Serialize(payload)}}}""",
            CancellationToken.None);

        await PollRoom(store, resource);

        Assert.Contains(clients.Calls, call => call.ConnectionId == recipient.Context.ConnectionId &&
            call.Method == "updateNotification" && (int)call.Arguments[0]! == 3 && (string)call.Arguments[1]! == payload);
    }

    [Fact]
    public async Task Independent_room_deadline_aborts_sessions_when_all_four_workers_are_blocked()
    {
        var broker = new FakeBackplane { HoldEventReads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(),
            new FakeHubContext(new FakeClients()), broker);
        await store.StartAsync(CancellationToken.None);
        var connections = new List<Connected>();
        for (int i = 0; i < 4; i++) connections.Add(await Connect(store, Guid.NewGuid(), "blocked-" + i));

        try
        {
            await broker.FourReadsStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.WhenAll(connections.Select(x => WaitForAbortAsync(x.Context))).WaitAsync(TimeSpan.FromSeconds(8));

            Assert.All(connections, x => Assert.True(x.Context.Aborted));
            Assert.Equal(4, store.Diagnostics.ActiveWorkers); // Cancellation cannot free an I/O slot before its operation ends.
        }
        finally
        {
            broker.HoldEventReads.TrySetResult();
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task Missing_incarnation_during_heartbeat_does_not_disconnect_healthy_room_peers()
    {
        var broker = new FakeBackplane();
        await using var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(),
            new FakeHubContext(new FakeClients()), broker);
        Guid resource = Guid.NewGuid();
        var replaced = await Connect(store, resource, "replaced");
        var healthy = await Connect(store, resource, "healthy");
        await broker.RemoveAsync(replaced.Member, null, "replacement", CancellationToken.None);

        await store.StartAsync(CancellationToken.None);
        await WaitForAbortAsync(replaced.Context).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(healthy.Context.Aborted);
        Assert.Equal(1, store.Diagnostics.OccupiedRooms);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_requester_does_not_disconnect_other_local_room_members(bool publish)
    {
        var broker = new FakeBackplane();
        await using var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(),
            new FakeHubContext(new FakeClients()), broker);
        Guid resource = Guid.NewGuid();
        var stale = await Connect(store, resource, "stale");
        var healthy = await Connect(store, resource, "healthy");
        await broker.RemoveAsync(stale.Member, null, "replacement", CancellationToken.None);

        if (publish)
            Assert.False(await store.UpdatePresenceAsync(stale.Ticket, stale.Context.ConnectionId, "{}", CancellationToken.None));
        else
            Assert.Empty(await store.GetCompletePresenceStateAsync(stale.Ticket, stale.Context.ConnectionId, CancellationToken.None));

        Assert.True(stale.Context.Aborted);
        Assert.False(healthy.Context.Aborted);
    }

    [Fact]
    public async Task Broker_generation_change_fences_old_sessions_before_the_next_scheduler_tick()
    {
        var broker = new FakeBackplane();
        var clients = new FakeClients();
        await using var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(),
            new FakeHubContext(clients), broker);
        Guid resource = Guid.NewGuid();
        var sender = await Connect(store, resource, "writer");
        var peer = await Connect(store, resource, "reader");

        broker.Generation++;

        Assert.False(await store.UpdatePresenceAsync(sender.Ticket, sender.Context.ConnectionId, "{}", CancellationToken.None));
        Assert.Empty(await store.GetCompletePresenceStateAsync(peer.Ticket, peer.Context.ConnectionId, CancellationToken.None));
        Assert.Empty(clients.Calls);
    }

    [Fact]
    public async Task Sender_revocation_during_common_authorization_prevents_delivery_after_broker_observation()
    {
        var broker = new FakeBackplane();
        var clients = new FakeClients();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorizer = new AllowAllAuthorizer
        {
            Batch = async checks =>
            {
                entered.TrySetResult();
                await resume.Task;
                return checks.Select(check => check.Ticket.Actor.Identity.Subject != "writer").ToArray();
            }
        };
        await using var store = new DistributedOfficeRealTimeSessionStore(authorizer, new FakeHubContext(clients), broker);
        Guid resource = Guid.NewGuid();
        var sender = await Connect(store, resource, "writer");
        var peer = await Connect(store, resource, "reader");
        await broker.MutateAsync(sender.Member, broker.Epoch, "revoked", "presence",
            ClientKey(sender.Ticket.ClientId), "presenceUpdated", "{}", CancellationToken.None);

        var pending = PollRoom(store, resource);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { resume.TrySetResult(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Empty(clients.Calls);
        Assert.True(sender.Context.Aborted);
        Assert.False(peer.Context.Aborted);
    }

    private static async Task WaitForAbortAsync(FakeHubCallerContext context)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!context.Aborted) await Task.Delay(25, timeout.Token);
    }

    [Fact]
    public async Task Recipient_replaced_before_broker_observation_cannot_receive_a_queued_event()
    {
        var broker = new FakeBackplane();
        var clients = new FakeClients();
        await using var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(), new FakeHubContext(clients), broker);
        Guid resource = Guid.NewGuid();
        var sender = await Connect(store, resource, "writer");
        var stalePeer = await Connect(store, resource, "reader");
        await broker.MutateAsync(sender.Member, broker.Epoch, "queued", "presence", ClientKey(sender.Ticket.ClientId), "presenceUpdated", "{}", default);
        await broker.RemoveAsync(stalePeer.Member, null, "remote-replacement", default);

        await PollRoom(store, resource);

        Assert.Empty(clients.Calls);
        Assert.True(stalePeer.Context.Aborted);
        Assert.False(sender.Context.Aborted);
    }

    [Fact]
    public async Task Send_timeout_aborts_recipient_but_keeps_worker_slot_until_transport_finishes()
    {
        var broker = new FakeBackplane();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clients = new FakeClients { BeforeSend = () => { entered.TrySetResult(); return resume.Task; } };
        var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(), new FakeHubContext(clients), broker);
        Guid resource = Guid.NewGuid();
        var sender = await Connect(store, resource, "writer");
        var peer = await Connect(store, resource, "reader");
        await broker.MutateAsync(sender.Member, broker.Epoch, "stalled-send", "presence", ClientKey(sender.Ticket.ClientId), "presenceUpdated", "{}", default);
        await store.StartAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitForAbortAsync(peer.Context).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(store.Diagnostics.ActiveWorkers >= 1);
            Assert.False(resume.Task.IsCompleted);
        }
        finally
        {
            resume.TrySetResult();
            await store.DisposeAsync();
        }
    }

    private static async Task<Connected> Connect(DistributedOfficeRealTimeSessionStore store, Guid resource, string subject)
    {
        var ticket = new OfficeRealTimeTicket(resource, 1,
            new CellBridgeActor(new SubjectIdentity(subject, subject, subject)), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(2));
        var context = new FakeHubCallerContext(Guid.NewGuid().ToString("N"));
        Assert.True(await store.ConnectAsync(ticket, context, CancellationToken.None));
        var field = typeof(DistributedOfficeRealTimeSessionStore).GetField("_connections", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var connections = (System.Collections.IDictionary)field.GetValue(store)!;
        object local = connections[context.ConnectionId]!;
        var member = (OfficeRealTimeBackplaneMember)local.GetType().GetProperty("Member", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(local)!;
        return new(ticket, context, member);
    }

    private static async Task PollRoom(DistributedOfficeRealTimeSessionStore store, Guid resource)
    {
        var roomsField = typeof(DistributedOfficeRealTimeSessionStore).GetField("_rooms", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var rooms = (System.Collections.IDictionary)roomsField.GetValue(store)!;
        object localRoom = rooms[new OfficeRealTimeRoomKey(resource, 1)]!;
        var method = typeof(DistributedOfficeRealTimeSessionStore).GetMethod("ProcessEventsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var work = (Task<bool>)method.Invoke(store, [localRoom, CancellationToken.None])!;
        Assert.True(await work);
    }

    private sealed record Connected(OfficeRealTimeTicket Ticket, FakeHubCallerContext Context,
        OfficeRealTimeBackplaneMember Member);

    private sealed class AllowAllAuthorizer : IOfficeRealTimeAuthorizer
    {
        internal Func<IReadOnlyList<OfficeRealTimeTicketCheck>, Task<bool[]>>? Batch { get; init; }
        public ValueTask<bool> AuthorizeAsync(OfficeRealTimeTicket ticket, DocumentAccess access,
            bool requireOwnedClient, CancellationToken cancellationToken) => ValueTask.FromResult(true);
        public ValueTask<bool[]> AuthorizeTogetherAsync(IReadOnlyList<OfficeRealTimeTicketCheck> checks,
            CancellationToken cancellationToken) => Batch is { } batch ? new(batch(checks)) : ValueTask.FromResult(checks.Select(_ => true).ToArray());
    }

    private sealed class FakeBackplane : IOfficeRealTimeBackplane, IOfficeRealTimeBackplaneWakeSource
    {
        private readonly Dictionary<Guid, OfficeRealTimeBackplaneMember> _members = [];
        private readonly Dictionary<string, OfficeRealTimeBackplanePresence> _presence = new(StringComparer.Ordinal);
        private readonly List<OfficeRealTimeBackplaneEvent> _events = [];
        private long _sequence;
        internal string Epoch { get; } = Guid.NewGuid().ToString("N");
        internal TaskCompletionSource? HoldEventReads { get; set; }
        internal TaskCompletionSource? HoldConnect { get; set; }
        internal TaskCompletionSource ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FourReadsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int _readCalls;
        public long Generation { get; set; } = 1;
        public bool IsHealthy => true;
        public event Action<OfficeRealTimeRoomKey>? RoomChanged;
        internal void NotifyRoomChanged(OfficeRealTimeRoomKey room) => RoomChanged?.Invoke(room);

        public async Task<OfficeRealTimeBackplaneConnectResult> ConnectAsync(OfficeRealTimeBackplaneMember member,
            CancellationToken cancellationToken)
        {
            _members[member.Incarnation] = member;
            ConnectStarted.TrySetResult();
            if (HoldConnect is { } hold) await hold.Task;
            return new OfficeRealTimeBackplaneConnectResult(OfficeRealTimeBackplaneStatus.Ok, Snapshot(member.Room));
        }

        public Task<OfficeRealTimeBackplaneSnapshotResult> ReadSnapshotAsync(OfficeRealTimeRoomKey room,
            CancellationToken cancellationToken) => Task.FromResult(new OfficeRealTimeBackplaneSnapshotResult(
            OfficeRealTimeBackplaneStatus.Ok, Snapshot(room)));

        public async Task<OfficeRealTimeBackplaneEventsResult> ReadEventsAsync(OfficeRealTimeRoomKey room, string epoch,
            long afterSequence, int maxEvents, int maxBytes, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _readCalls) >= 4) FourReadsStarted.TrySetResult();
            if (HoldEventReads is { } hold) await hold.Task;
            var events = _events.Where(x => x.Sequence > afterSequence).Take(maxEvents).Select(item => item with
            {
                EncodedBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(item, new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }).Length
            }).ToArray();
            return new OfficeRealTimeBackplaneEventsResult(OfficeRealTimeBackplaneStatus.Ok, Epoch,
                _sequence, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), events, false, false);
        }

        public Task<OfficeRealTimeBackplaneMembersResult> ObserveMembersAsync(OfficeRealTimeRoomKey room,
            CancellationToken cancellationToken) => Task.FromResult(new OfficeRealTimeBackplaneMembersResult(
            OfficeRealTimeBackplaneStatus.Ok, Epoch, _members.Values.Where(x => x.Room == room).ToArray(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        public Task<OfficeRealTimeBackplaneMutationResult> MutateAsync(OfficeRealTimeBackplaneMember member,
            string epoch, string eventId, string kind, string clientKey, string method, string payloadJson,
            CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            long sequence = ++_sequence;
            if (kind == "presence") _presence[clientKey] = new(clientKey, member.Incarnation, payloadJson);
            _events.Add(new(sequence, eventId, Epoch, now, now.ToUnixTimeMilliseconds(), kind,
                kind == "presence" ? clientKey : ClientKey(member.Ticket.ClientId), member, method, payloadJson));
            return Task.FromResult(new OfficeRealTimeBackplaneMutationResult(OfficeRealTimeBackplaneStatus.Ok,
                Epoch, sequence, now.ToUnixTimeMilliseconds()));
        }

        public Task<OfficeRealTimeBackplaneHeartbeatResult> HeartbeatAsync(
            IReadOnlyList<OfficeRealTimeBackplaneMember> members, CancellationToken cancellationToken) =>
            Task.FromResult(new OfficeRealTimeBackplaneHeartbeatResult(OfficeRealTimeBackplaneStatus.Ok,
                members.Where(x => _members.ContainsKey(x.Incarnation)).Select(x => x.Incarnation).ToHashSet(),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        public Task<OfficeRealTimeBackplaneRemoveResult> RemoveAsync(OfficeRealTimeBackplaneMember member,
            string? presenceClientKey, string eventId, CancellationToken cancellationToken)
        {
            _members.Remove(member.Incarnation);
            return Task.FromResult(new OfficeRealTimeBackplaneRemoveResult(OfficeRealTimeBackplaneStatus.Ok,
                Epoch, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }

        private OfficeRealTimeBackplaneSnapshot Snapshot(OfficeRealTimeRoomKey room) => new(Epoch, _sequence,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _members.Values.Where(x => x.Room == room).ToArray(),
            _presence.Values.Where(x => _members.ContainsKey(x.Incarnation)).ToArray());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeHubContext(FakeClients clients) : IHubContext<OfficeRealTimeHub>
    {
        public IHubClients Clients => clients;
        public IGroupManager Groups { get; } = new FakeGroups();
    }

    private sealed class FakeClients : IHubClients
    {
        internal List<SentCall> Calls { get; } = [];
        internal Func<Task>? BeforeSend { get; init; }
        public IClientProxy Client(string connectionId) => new FakeProxy(connectionId, Calls, BeforeSend);
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new FakeProxy("many", Calls);
        public IClientProxy Group(string groupName) => new FakeProxy("group", Calls);
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new FakeProxy("group", Calls);
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => new FakeProxy("groups", Calls);
        public IClientProxy User(string userId) => new FakeProxy("user", Calls);
        public IClientProxy Users(IReadOnlyList<string> userIds) => new FakeProxy("users", Calls);
        public IClientProxy All => new FakeProxy("all", Calls);
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new FakeProxy("all-except", Calls);
    }
    private sealed record SentCall(string ConnectionId, string Method, object?[] Arguments);
    private sealed class FakeProxy(string id, List<SentCall> calls, Func<Task>? beforeSend = null) : IClientProxy
    {
        public async Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            if (beforeSend is not null) await beforeSend();
            lock (calls) calls.Add(new(id, method, args));
        }
    }
    private sealed class FakeGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class FakeHubCallerContext(string id) : HubCallerContext
    {
        public bool Aborted { get; private set; }
        public override string ConnectionId => id;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override IFeatureCollection Features { get; } = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        public override void Abort() => Aborted = true;
    }

    private static string ClientKey(Guid id) => id.ToString("B").ToLowerInvariant();
}
