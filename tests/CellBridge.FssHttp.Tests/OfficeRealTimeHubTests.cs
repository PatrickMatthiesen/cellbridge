using System.Security.Claims;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace CellBridge.FssHttp.Tests;

public sealed class OfficeRealTimeHubTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Temporary_overload_preserves_connected_members_and_presence(bool snapshot)
    {
        Fixture f = new();
        var sender = await f.Connect(Guid.NewGuid(), 1, "sender");
        var peer = await f.Connect(sender.Ticket.ResourceId, 1, "peer");
        string payload = $$"""{"A":"{{sender.Ticket.ClientId:B}}"}""";
        Assert.True(await f.Store.UpdatePresenceAsync(sender.Ticket, sender.Context.ConnectionId,
            payload, CancellationToken.None));
        f.Authorizer.BeforeAuthorization = (_, _, _) => throw new OfficeRealTimeBusyException();
        if (snapshot)
            await Assert.ThrowsAsync<OfficeRealTimeBusyException>(() => f.Store.GetCompletePresenceStateAsync(
                peer.Ticket, peer.Context.ConnectionId, CancellationToken.None));
        else
            await Assert.ThrowsAsync<OfficeRealTimeBusyException>(() => f.Store.UpdatePresenceAsync(
                sender.Ticket, sender.Context.ConnectionId, payload, CancellationToken.None));
        Assert.False(sender.Context.Aborted);
        Assert.False(peer.Context.Aborted);
        f.Authorizer.BeforeAuthorization = null;
        var recovered = await f.Store.GetCompletePresenceStateAsync(peer.Ticket,
            peer.Context.ConnectionId, CancellationToken.None);
        Assert.Equal(payload, recovered[ClientKey(sender.Ticket.ClientId)]);
    }

    [Fact]
    public async Task Captured_native_excel_presence_with_empty_cid_uses_lowercase_ticket_key()
    {
        Fixture f = new();
        var sender = await f.Connect(Guid.NewGuid(), 1, "sender");
        var peer = await f.Connect(sender.Ticket.ResourceId, 1, "peer");
        string payload = $$"""
            {"Version":2,"ClientType":0,"PresenceType":0,"SelectedRange":[{"SheetName":"Sheet1","NamedObjectName":null,"FirstColumn":0,"FirstRow":0,"LastColumn":0,"LastRow":0}],"Object":[],"Cid":"","SupportsChat":null,"CollabMode":0,"OcsCapable":true}
            """;

        Assert.True(await f.Store.UpdatePresenceAsync(sender.Ticket, sender.Context.ConnectionId,
            payload, CancellationToken.None));

        var call = Assert.Single(f.Clients.Calls, x => x.ConnectionId == peer.Context.ConnectionId);
        Assert.Equal("presenceUpdated", call.Method);
        var sent = Assert.IsType<Dictionary<string, string>>(call.Arguments[0]);
        Assert.Equal(payload, Assert.Single(sent).Value);
        Assert.Equal(ClientKey(sender.Ticket.ClientId), Assert.Single(sent).Key);
        var snapshot = await f.Store.GetCompletePresenceStateAsync(peer.Ticket,
            peer.Context.ConnectionId, CancellationToken.None);
        Assert.Equal(payload, snapshot[ClientKey(sender.Ticket.ClientId)]);
    }

    [Fact]
    public async Task Native_excel_presence_accepts_a_nonempty_ticket_bound_cid()
    {
        Fixture f = new();
        var sender = await f.Connect(Guid.NewGuid(), 1, "sender");
        string payload = $$"""{"Cid":"{{sender.Ticket.ClientId:B}}"}""";

        Assert.True(await f.Store.UpdatePresenceAsync(sender.Ticket, sender.Context.ConnectionId,
            payload, CancellationToken.None));
    }

    [Fact]
    public async Task Presence_rejects_a_supplied_sender_that_does_not_match_the_ticket()
    {
        Fixture f = new();
        var sender = await f.Connect(Guid.NewGuid(), 1, "sender");
        var peer = await f.Connect(sender.Ticket.ResourceId, 1, "peer");

        foreach (string payload in new[] { "{\"A\":null}", "{\"A\":\"invalid\"}",
            $$"""{"A":"{{peer.Ticket.ClientId:B}}"}""", "{\"clientId\":null}",
            "{\"clientId\":\"invalid\"}", $$"""{"clientId":"{{peer.Ticket.ClientId:B}}"}""" })
            Assert.False(await f.Store.UpdatePresenceAsync(sender.Ticket, sender.Context.ConnectionId,
                payload, CancellationToken.None));

        Assert.False(await f.Store.UpdatePresenceAsync(sender.Ticket, sender.Context.ConnectionId,
            $$"""{"Cid":"{{peer.Ticket.ClientId:B}}"}""", CancellationToken.None));

        Assert.Empty(f.Clients.Calls);
        Assert.Empty(await f.Store.GetCompletePresenceStateAsync(peer.Ticket,
            peer.Context.ConnectionId, CancellationToken.None));
    }

    [Fact]
    public async Task Notification_reaches_authorized_peers_only_in_same_generation()
    {
        Fixture f = new();
        Guid resource = Guid.NewGuid();
        var sender = await f.Connect(resource, 1, "a");
        var peer = await f.Connect(resource, 1, "b");
        var otherGeneration = await f.Connect(resource, 2, "c");
        string payload = $$"""{"A":"{{sender.Ticket.ClientId:B}}","text":"typed"}""";

        Assert.True(await f.Store.SendUpdateNotificationAsync(sender.Ticket, sender.Context.ConnectionId,
            3, payload, CancellationToken.None));

        Assert.Single(f.Clients.Calls, x => x.ConnectionId == peer.Context.ConnectionId);
        Assert.DoesNotContain(f.Clients.Calls, x => x.ConnectionId == otherGeneration.Context.ConnectionId);
        Assert.DoesNotContain(f.Clients.Calls, x => x.ConnectionId == sender.Context.ConnectionId);
    }

    [Fact]
    public async Task Rejects_mismatched_sender_and_revoked_sender_or_receiver()
    {
        Fixture f = new();
        var sender = await f.Connect(Guid.NewGuid(), 1, "sender");
        var receiver = await f.Connect(sender.Ticket.ResourceId, 1, "receiver");
        string otherGuid = Guid.NewGuid().ToString("B");

        Assert.False(await f.Store.SendUpdateNotificationAsync(sender.Ticket, sender.Context.ConnectionId,
            3, $$"""{"A":"{{otherGuid}}"}""", CancellationToken.None));

        f.Authorizer.DeniedSubjects.Add("sender");
        Assert.False(await f.Store.SendUpdateNotificationAsync(sender.Ticket, sender.Context.ConnectionId,
            3, $$"""{"A":"{{sender.Ticket.ClientId:B}}"}""", CancellationToken.None));
        f.Authorizer.DeniedSubjects.Remove("sender");
        sender = await f.Connect(sender.Ticket.ResourceId, 1, "sender", sender.Ticket.ClientId);

        f.Authorizer.DeniedSubjects.Add("receiver");
        Assert.True(await f.Store.SendUpdateNotificationAsync(sender.Ticket, sender.Context.ConnectionId,
            3, $$"""{"A":"{{sender.Ticket.ClientId:B}}"}""", CancellationToken.None));
        Assert.DoesNotContain(f.Clients.Calls, x => x.ConnectionId == receiver.Context.ConnectionId);
        Assert.True(receiver.Context.Aborted);
    }

    [Fact]
    public async Task Native_latency_reports_are_authorized_locally_and_never_relayed()
    {
        Fixture f = new();
        var sender = await f.Connect(Guid.NewGuid(), 1, "sender");
        await f.Connect(sender.Ticket.ResourceId, 1, "peer");
        const string report = "{\"Id\":\"{1CD3F263-C528-40EB-923A-12E70A96D984}\",\"TD\":270}";
        Assert.True(await f.Store.SendUpdateNotificationAsync(sender.Ticket, sender.Context.ConnectionId,
            4, report, CancellationToken.None));
        Assert.Empty(f.Clients.Calls);
        foreach (var invalid in new[] { "{}", "{\"Id\":\"bad\",\"TD\":1}",
            "{\"Id\":\"{1CD3F263-C528-40EB-923A-12E70A96D984}\",\"TD\":\"270\"}",
            report.Replace("270", "-1"), report.Replace("270", "270,\"serverChangeToken\":1") })
            Assert.False(await f.Store.SendUpdateNotificationAsync(sender.Ticket, sender.Context.ConnectionId,
                4, invalid, CancellationToken.None));
        f.Authorizer.DeniedSubjects.Add("sender");
        Assert.False(await f.Store.SendUpdateNotificationAsync(sender.Ticket, sender.Context.ConnectionId,
            4, report, CancellationToken.None));
        f.Authorizer.DeniedSubjects.Remove("sender");
        await f.Store.DisconnectAsync(sender.Context.ConnectionId, CancellationToken.None);
        Assert.False(await f.Store.SendUpdateNotificationAsync(sender.Ticket, sender.Context.ConnectionId,
            4, report, CancellationToken.None));
    }

    [Fact]
    public async Task Enforces_payload_channel_and_connection_bounds()
    {
        Fixture f = new();
        var first = await f.Connect(Guid.NewGuid(), 1, "same-subject");
        for (int i = 0; i < 7; i++) await f.Connect(first.Ticket.ResourceId, 1, "same-subject");
        var overflow = await f.Connect(first.Ticket.ResourceId, 1, "same-subject", expectSuccess: false);

        Assert.False(overflow.IsConnected);
        Assert.False(await f.Store.SendUpdateNotificationAsync(first.Ticket, first.Context.ConnectionId,
            4, "{}", CancellationToken.None));
        Assert.False(await f.Store.SendUpdateNotificationAsync(first.Ticket, first.Context.ConnectionId,
            3, new string('x', 64 * 1024 + 1), CancellationToken.None));
        Assert.False(await f.Store.UpdatePresenceAsync(first.Ticket, first.Context.ConnectionId,
            "{\"A\":\"bad\",\"A\":\"duplicate\"}", CancellationToken.None));
    }

    [Fact]
    public async Task Enforces_per_document_and_global_connection_limits()
    {
        Fixture f = new();
        Guid firstResource = Guid.NewGuid();
        for (int i = 0; i < 32; i++) await f.Connect(firstResource, 1, "doc-" + i);
        Assert.False((await f.Connect(firstResource, 1, "doc-overflow", expectSuccess: false)).IsConnected);

        for (int i = 0; i < 96; i++) await f.Connect(Guid.NewGuid(), 1, "global-" + i);
        Assert.False((await f.Connect(Guid.NewGuid(), 1, "global-overflow", expectSuccess: false)).IsConnected);
    }

    [Fact]
    public async Task Reconnect_moves_presence_and_old_disconnect_cannot_remove_it()
    {
        Fixture f = new();
        Guid resource = Guid.NewGuid();
        var owner = await f.Connect(resource, 1, "owner");
        var peer = await f.Connect(resource, 1, "peer");
        string presence = $$"""{"A":"{{owner.Ticket.ClientId:B}}","state":"editing"}""";
        Assert.True(await f.Store.UpdatePresenceAsync(owner.Ticket, owner.Context.ConnectionId, presence, CancellationToken.None));

        var replacement = await f.Connect(resource, 1, "owner", owner.Ticket.ClientId);
        Assert.True(owner.Context.Aborted);
        await f.Store.DisconnectAsync(owner.Context.ConnectionId, CancellationToken.None);
        Dictionary<string, string> snapshot = await f.Store.GetCompletePresenceStateAsync(
            peer.Ticket, peer.Context.ConnectionId, CancellationToken.None);
        Assert.Equal(presence, snapshot[ClientKey(owner.Ticket.ClientId)]);

        await f.Store.DisconnectAsync(replacement.Context.ConnectionId, CancellationToken.None);
        Assert.Contains(f.Clients.Calls, call => call.ConnectionId == peer.Context.ConnectionId &&
            call.Method == "presenceUpdated" && ((string[])call.Arguments[1]!).Contains(ClientKey(owner.Ticket.ClientId)));
    }

    [Fact]
    public async Task Presence_snapshot_rechecks_requester_after_waiting_for_source_authority()
    {
        Fixture f = new();
        var requester = await f.Connect(Guid.NewGuid(), 1, "requester");
        var source = await f.Connect(requester.Ticket.ResourceId, 1, "source");
        Assert.True(await f.Store.UpdatePresenceAsync(source.Ticket, source.Context.ConnectionId,
            $$"""{"A":"{{source.Ticket.ClientId:B}}","state":"editing"}""", CancellationToken.None));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Authorizer.BeforeAuthorization = async (ticket, owned, cancellationToken) =>
        {
            if (ticket == source.Ticket && owned)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        };

        var pending = f.Store.GetCompletePresenceStateAsync(requester.Ticket,
            requester.Context.ConnectionId, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        f.Authorizer.DeniedSubjects.Add("requester");
        release.SetResult();

        Assert.Empty(await pending);
        Assert.True(requester.Context.Aborted);
    }

    [Fact]
    public async Task Presence_snapshot_excludes_source_disconnected_during_final_requester_check()
    {
        Fixture f = new();
        var requester = await f.Connect(Guid.NewGuid(), 1, "requester");
        var source = await f.Connect(requester.Ticket.ResourceId, 1, "source");
        Assert.True(await f.Store.UpdatePresenceAsync(source.Ticket, source.Context.ConnectionId,
            $$"""{"A":"{{source.Ticket.ClientId:B}}"}""", CancellationToken.None));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int requesterChecks = 0;
        f.Authorizer.BeforeAuthorization = async (ticket, owned, cancellationToken) =>
        {
            if (ticket == requester.Ticket && owned && ++requesterChecks == 3)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        };

        var pending = f.Store.GetCompletePresenceStateAsync(requester.Ticket,
            requester.Context.ConnectionId, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await f.Store.DisconnectAsync(source.Context.ConnectionId, new CancellationToken(canceled: true));
        }
        finally { release.SetResult(); }

        Assert.Empty(await pending);
        Assert.False(requester.Context.Aborted);
    }

    [Fact]
    public async Task Presence_snapshot_rechecks_common_authority_after_awaited_departure_delivery()
    {
        Fixture f = new();
        var requester = await f.Connect(Guid.NewGuid(), 1, "requester");
        var allowedSource = await f.Connect(requester.Ticket.ResourceId, 1, "allowed");
        var deniedSource = await f.Connect(requester.Ticket.ResourceId, 1, "denied");
        foreach (var source in new[] { allowedSource, deniedSource })
            Assert.True(await f.Store.UpdatePresenceAsync(source.Ticket, source.Context.ConnectionId,
                $$"""{"A":"{{source.Ticket.ClientId:B}}","state":"editing"}""", CancellationToken.None));
        f.Authorizer.DeniedSubjects.Add("denied");
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Clients.BeforeSend = async (connectionId, method, arguments, cancellationToken) =>
        {
            if (connectionId == requester.Context.ConnectionId && method == "presenceUpdated" &&
                ((string[])arguments[1]!).Contains(ClientKey(deniedSource.Ticket.ClientId)))
            {
                // The requester has passed departure-recipient authorization before this wait.
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        };

        var pending = f.Store.GetCompletePresenceStateAsync(requester.Ticket,
            requester.Context.ConnectionId, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        f.Authorizer.DeniedSubjects.Add("requester");
        release.SetResult();

        Assert.Empty(await pending);
        Assert.True(requester.Context.Aborted);
        Assert.True(deniedSource.Context.Aborted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delivery_rechecks_sender_write_access_and_expiry_after_recipient_authority_wait(bool expire)
    {
        Fixture f = new();
        var sender = await f.Connect(Guid.NewGuid(), 1, "sender");
        var peer = await f.Connect(sender.Ticket.ResourceId, 1, "peer", lifetime: TimeSpan.FromMinutes(30));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Authorizer.BeforeAuthorization = async (ticket, owned, cancellationToken) =>
        {
            if (ticket == peer.Ticket && !owned)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        };

        var pending = f.Store.SendUpdateNotificationAsync(sender.Ticket, sender.Context.ConnectionId,
            3, $$"""{"A":"{{sender.Ticket.ClientId:B}}","text":"typed"}""", CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (expire) f.Clock.Advance(TimeSpan.FromMinutes(6));
        else f.Authorizer.DeniedWriteSubjects.Add("sender");
        release.SetResult();

        Assert.False(await pending);
        Assert.True(sender.Context.Aborted);
        Assert.DoesNotContain(f.Clients.Calls, call => call.Method == "updateNotification");
    }

    [Fact]
    public async Task Delivery_rejects_recipient_revoked_while_final_sender_check_waits()
    {
        Fixture f = new();
        var sender = await f.Connect(Guid.NewGuid(), 1, "sender");
        var peer = await f.Connect(sender.Ticket.ResourceId, 1, "peer");
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int senderChecks = 0;
        f.Authorizer.BeforeAuthorization = async (ticket, owned, cancellationToken) =>
        {
            if (ticket == sender.Ticket && owned && ++senderChecks == 3)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        };

        var pending = f.Store.SendUpdateNotificationAsync(sender.Ticket, sender.Context.ConnectionId,
            3, $$"""{"A":"{{sender.Ticket.ClientId:B}}","text":"typed"}""", CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        f.Authorizer.DeniedSubjects.Add("peer");
        release.SetResult();

        Assert.True(await pending);
        Assert.True(peer.Context.Aborted);
        Assert.DoesNotContain(f.Clients.Calls, call => call.Method == "updateNotification");
    }

    [Fact]
    public async Task Canceled_disconnect_removes_state_while_delivery_holds_room_gate()
    {
        Fixture f = new();
        var sender = await f.Connect(Guid.NewGuid(), 1, "sender");
        var peer = await f.Connect(sender.Ticket.ResourceId, 1, "peer");
        for (int i = 0; i < 7; i++) await f.Connect(Guid.NewGuid(), 1, "sender");
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Authorizer.BeforeAuthorization = async (ticket, owned, cancellationToken) =>
        {
            if (ticket == peer.Ticket && !owned)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        };

        var pending = f.Store.UpdatePresenceAsync(sender.Ticket, sender.Context.ConnectionId,
            $$"""{"A":"{{sender.Ticket.ClientId:B}}"}""", CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Connected replacement;
        try
        {
            await f.Store.DisconnectAsync(sender.Context.ConnectionId, new CancellationToken(canceled: true));
            replacement = await f.Connect(Guid.NewGuid(), 1, "sender", expectSuccess: false);
        }
        finally
        {
            release.SetResult();
            await pending;
        }

        Assert.True(replacement.IsConnected);
        Assert.Empty(await f.Store.GetCompletePresenceStateAsync(peer.Ticket, peer.Context.ConnectionId,
            CancellationToken.None));
        Assert.DoesNotContain(f.Clients.Calls, call => call.Method == "presenceUpdated");
    }

    [Theory]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(128)]
    public async Task Admission_reclaims_expired_subject_document_and_global_slots(int count)
    {
        Fixture f = new();
        Guid resource = Guid.NewGuid();
        List<Connected> expired = [];
        for (int i = 0; i < count; i++)
            expired.Add(await f.Connect(count == 128 ? Guid.NewGuid() : resource, 1,
                count == 8 ? "owner" : "owner-" + i));
        var source = expired[0];
        Assert.True(await f.Store.UpdatePresenceAsync(source.Ticket, source.Context.ConnectionId,
            $$"""{"A":"{{source.Ticket.ClientId:B}}"}""", CancellationToken.None));

        f.Clock.Advance(TimeSpan.FromMinutes(6));
        var fresh = await f.Connect(count == 128 ? Guid.NewGuid() : resource, 1,
            count == 8 ? "owner" : "fresh", expectSuccess: false);

        Assert.True(fresh.IsConnected);
        Assert.All(expired, connection => Assert.True(connection.Context.Aborted));
        Assert.Empty(await f.Store.GetCompletePresenceStateAsync(fresh.Ticket, fresh.Context.ConnectionId,
            CancellationToken.None));
    }

    private sealed class Fixture
    {
        internal FakeAuthorizer Authorizer { get; } = new();
        internal FakeClients Clients { get; } = new();
        internal ManualClock Clock { get; } = new();
        internal OfficeRealTimeSessionStore Store { get; }

        internal Fixture() => Store = new(Authorizer, new FakeHubContext(Clients), Clock);

        internal async Task<Connected> Connect(Guid resource, long generation, string subject,
            Guid? clientId = null, bool expectSuccess = true, TimeSpan? lifetime = null)
        {
            OfficeRealTimeTicket ticket = new(resource, generation,
                new CellBridgeActor(new SubjectIdentity(subject, subject, subject)), clientId ?? Guid.NewGuid(),
                Clock.GetUtcNow().Add(lifetime ?? TimeSpan.FromMinutes(5)));
            FakeHubCallerContext context = new(Guid.NewGuid().ToString("N"));
            bool connected = await Store.ConnectAsync(ticket, context, CancellationToken.None);
            if (expectSuccess) Assert.True(connected);
            return new(ticket, context, connected);
        }
    }

    private sealed record Connected(OfficeRealTimeTicket Ticket, FakeHubCallerContext Context, bool IsConnected);

    private sealed class FakeAuthorizer : IOfficeRealTimeAuthorizer
    {
        internal HashSet<string> DeniedSubjects { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> DeniedWriteSubjects { get; } = new(StringComparer.Ordinal);
        internal Func<OfficeRealTimeTicket, bool, CancellationToken, Task>? BeforeAuthorization { get; set; }
        public async ValueTask<bool> AuthorizeAsync(OfficeRealTimeTicket ticket, DocumentAccess requiredAccess,
            bool requireOwnedClient, CancellationToken cancellationToken)
        {
            if (BeforeAuthorization is { } before) await before(ticket, requireOwnedClient, cancellationToken);
            return Allowed(ticket, requiredAccess);
        }

        public async ValueTask<bool[]> AuthorizeTogetherAsync(IReadOnlyList<OfficeRealTimeTicketCheck> checks,
            CancellationToken cancellationToken)
        {
            if (BeforeAuthorization is { } before)
                foreach (var check in checks) await before(check.Ticket, check.RequireOwnedClient, cancellationToken);
            // Model one permission snapshot acquired after all asynchronous admission work.
            return checks.Select(check => Allowed(check.Ticket, check.RequiredAccess)).ToArray();
        }

        private bool Allowed(OfficeRealTimeTicket ticket, DocumentAccess requiredAccess) =>
            !DeniedSubjects.Contains(ticket.Actor.Identity.Subject) &&
            (requiredAccess != DocumentAccess.Write || !DeniedWriteSubjects.Contains(ticket.Actor.Identity.Subject));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-30T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan elapsed) => now += elapsed;
    }

    private sealed class FakeHubContext(FakeClients clients) : IHubContext<OfficeRealTimeHub>
    {
        public IHubClients Clients => clients;
        public IGroupManager Groups { get; } = new FakeGroupManager();
    }

    private sealed class FakeClients : IHubClients
    {
        private readonly Dictionary<string, FakeClientProxy> _proxies = new(StringComparer.Ordinal);
        internal List<SentCall> Calls { get; } = [];
        internal Func<string, string, object?[], CancellationToken, Task>? BeforeSend { get; set; }
        public IClientProxy Client(string connectionId) => Proxy(connectionId);
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy("many:" + string.Join(',', connectionIds));
        public IClientProxy Group(string groupName) => Proxy("group:" + groupName);
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy("group-except:" + groupName);
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy("groups:" + string.Join(',', groupNames));
        public IClientProxy User(string userId) => Proxy("user:" + userId);
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy("users:" + string.Join(',', userIds));
        public IClientProxy All => Proxy("all");
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy("all-except");
        private FakeClientProxy Proxy(string id) => _proxies.TryGetValue(id, out FakeClientProxy? proxy)
            ? proxy : _proxies[id] = new FakeClientProxy(id, this);
    }

    private sealed class FakeClientProxy(string connectionId, FakeClients clients) : IClientProxy
    {
        public async Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            if (clients.BeforeSend is { } before) await before(connectionId, method, args, cancellationToken);
            clients.Calls.Add(new(connectionId, method, args));
        }
    }

    private sealed record SentCall(string ConnectionId, string Method, object?[] Arguments);

    private sealed class FakeGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeHubCallerContext(string connectionId) : HubCallerContext
    {
        private readonly Dictionary<object, object?> _items = [];
        public override string ConnectionId => connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items => _items;
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override IFeatureCollection Features => throw new NotImplementedException();
        public bool Aborted { get; private set; }
        public override void Abort() => Aborted = true;
    }

    private static string ClientKey(Guid clientId) => clientId.ToString("B").ToLowerInvariant();
}
