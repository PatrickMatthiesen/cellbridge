using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;

namespace CellBridge.FssHttp.Tests;

public sealed partial class DistributedOfficeRealTimeSessionTests
{
    [Theory]
    [MemberData(nameof(ExcelRealTimePayloads.InvalidNotifications), MemberType = typeof(ExcelRealTimePayloads))]
    public async Task Distributed_native_envelopes_retain_json_and_identity_limits(int channel, string payload)
    {
        var clients = new FakeClients();
        await using var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(),
            new FakeHubContext(clients), new FakeBackplane());
        var sender = await Connect(store, Guid.NewGuid(), "sender");
        await Connect(store, sender.Ticket.ResourceId, "peer");
        Assert.False(await store.SendUpdateNotificationAsync(sender.Ticket, sender.Context.ConnectionId,
            channel, payload, CancellationToken.None));
        await PollRoom(store, sender.Ticket.ResourceId);
        Assert.Empty(clients.Calls);
    }

    [Theory]
    [MemberData(nameof(ExcelRealTimePayloads.Notifications), MemberType = typeof(ExcelRealTimePayloads))]
    public async Task Native_excel_families_cross_distributed_core_and_classic_connections(int channel, string payload)
    {
        var broker = new FakeBackplane();
        var clients = new FakeClients();
        await using var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(),
            new FakeHubContext(clients), broker);
        Guid resource = Guid.NewGuid();
        var core = await Connect(store, resource, "core");
        await Connect(store, Guid.NewGuid(), "other-document");
        var nativeTicket = new OfficeRealTimeTicket(resource, 1,
            new CellBridgeActor(new SubjectIdentity("native", "native", "Native")),
            Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(2));
        var nativeCalls = new List<(string Method, object?[] Arguments)>();
        var native = new OfficeRealTimeConnection("classic", CancellationToken.None,
            (method, arguments, _) => { nativeCalls.Add((method, arguments)); return Task.CompletedTask; }, () => { });
        Assert.True(await store.ConnectAsync(nativeTicket, native, CancellationToken.None));
        var otherGeneration = nativeTicket with { LifecycleGeneration = 2, ClientId = Guid.NewGuid() };
        var isolatedCalls = new List<string>();
        Assert.True(await store.ConnectAsync(otherGeneration, new OfficeRealTimeConnection("other-generation", CancellationToken.None,
            (method, _, _) => { isolatedCalls.Add(method); return Task.CompletedTask; }, () => { }), CancellationToken.None));

        Assert.True(await store.SendUpdateNotificationAsync(core.Ticket, core.Context.ConnectionId,
            channel, payload, CancellationToken.None));
        await PollRoom(store, resource);
        var nativeDelivery = Assert.Single(nativeCalls);
        Assert.Equal("updateNotification", nativeDelivery.Method);
        Assert.Equal(new object?[] { channel, payload }, nativeDelivery.Arguments);
        Assert.Empty(clients.Calls);
        Assert.Empty(isolatedCalls);
        nativeCalls.Clear();

        Assert.True(await store.SendUpdateNotificationAsync(nativeTicket, native.ConnectionId,
            channel, payload, CancellationToken.None));
        await PollRoom(store, resource);
        var coreDelivery = Assert.Single(clients.Calls);
        Assert.Equal(core.Context.ConnectionId, coreDelivery.ConnectionId);
        Assert.Equal("updateNotification", coreDelivery.Method);
        Assert.Equal(new object?[] { channel, payload }, coreDelivery.Arguments);
        Assert.Empty(nativeCalls);
        Assert.Empty(isolatedCalls);
        clients.Calls.Clear();

        Assert.False(await store.SendUpdateNotificationAsync(nativeTicket, native.ConnectionId,
            channel, ExcelRealTimePayloads.WithProperty(payload, "A", core.Ticket.ClientId), CancellationToken.None));
        Assert.False(await store.SendUpdateNotificationAsync(nativeTicket, native.ConnectionId,
            channel == 3 ? 8 : 3, payload, CancellationToken.None));
        Assert.False(await store.SendUpdateNotificationAsync(nativeTicket, native.ConnectionId,
            4, payload, CancellationToken.None));
        await PollRoom(store, resource);
        Assert.Empty(clients.Calls);
        Assert.Empty(nativeCalls);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public async Task Colocated_recipients_share_fresh_authorization_in_groups_of_at_most_four(int recipientCount)
    {
        var broker = new FakeBackplane();
        var batches = new List<int>();
        var authorizer = new AllowAllAuthorizer
        {
            Batch = checks => { batches.Add(checks.Count); return Task.FromResult(checks.Select(_ => true).ToArray()); }
        };
        var clients = new FakeClients();
        await using var store = new DistributedOfficeRealTimeSessionStore(authorizer, new FakeHubContext(clients), broker);
        Guid resource = Guid.NewGuid();
        var sender = await Connect(store, resource, "writer");
        for (int index = 0; index < recipientCount; index++) await Connect(store, resource, "reader-" + index);
        batches.Clear();
        await broker.MutateAsync(sender.Member, broker.Epoch, "batch", "presence", ClientKey(sender.Ticket.ClientId), "presenceUpdated", "{}", default);

        await PollRoom(store, resource);

        Assert.Equal(recipientCount == 2 ? new[] { 3 } : new[] { 5, 2 }, batches);
        Assert.Equal(recipientCount, clients.Calls.Count);
    }

    [Fact]
    public async Task Denied_recipient_does_not_suppress_authorized_recipient_in_same_batch()
    {
        var broker = new FakeBackplane();
        bool deny = false;
        var authorizer = new AllowAllAuthorizer
        {
            Batch = checks => Task.FromResult(checks.Select(check => !deny || check.Ticket.Actor.Identity.Subject != "denied").ToArray())
        };
        var clients = new FakeClients();
        await using var store = new DistributedOfficeRealTimeSessionStore(authorizer, new FakeHubContext(clients), broker);
        Guid resource = Guid.NewGuid();
        var sender = await Connect(store, resource, "writer");
        var denied = await Connect(store, resource, "denied");
        var allowed = await Connect(store, resource, "allowed");
        deny = true;
        await broker.MutateAsync(sender.Member, broker.Epoch, "deny", "presence", ClientKey(sender.Ticket.ClientId), "presenceUpdated", "{}", default);

        await PollRoom(store, resource);

        Assert.True(denied.Context.Aborted);
        Assert.False(allowed.Context.Aborted);
        Assert.Equal(allowed.Context.ConnectionId, Assert.Single(clients.Calls).ConnectionId);
    }

    [Fact]
    public async Task Every_send_starts_and_times_out_independently_before_ignoring_transport_finishes()
    {
        var broker = new FakeBackplane();
        int starts = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clients = new FakeClients { BeforeSend = () => { if (Interlocked.Increment(ref starts) == 2) bothStarted.TrySetResult(); return release.Task; } };
        await using var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(), new FakeHubContext(clients), broker);
        Guid resource = Guid.NewGuid();
        var sender = await Connect(store, resource, "writer");
        var first = await Connect(store, resource, "first");
        var second = await Connect(store, resource, "second");
        await broker.MutateAsync(sender.Member, broker.Epoch, "blocked", "presence", ClientKey(sender.Ticket.ClientId), "presenceUpdated", "{}", default);
        var dispatch = PollRoom(store, resource);
        try
        {
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.WhenAll(WaitForAbortAsync(first.Context), WaitForAbortAsync(second.Context)).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(dispatch.IsCompleted);
        }
        finally { release.TrySetResult(); await dispatch.WaitAsync(TimeSpan.FromSeconds(3)); }
    }
}
