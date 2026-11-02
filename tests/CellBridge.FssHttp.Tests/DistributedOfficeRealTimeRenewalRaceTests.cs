using System.Reflection;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;

namespace CellBridge.FssHttp.Tests;

public sealed partial class DistributedOfficeRealTimeSessionTests
{
    [Fact]
    public async Task Presence_snapshot_does_not_remove_a_remote_ticket_renewed_during_authorization()
    {
        var clock = new RenewalClock(DateTimeOffset.UtcNow);
        var broker = new FakeBackplane();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int batches = 0;
        var authorizer = new AllowAllAuthorizer
        {
            Batch = async checks =>
            {
                if (Interlocked.Increment(ref batches) == 1)
                {
                    entered.TrySetResult();
                    await resume.Task;
                }
                return checks.Select(check => check.Ticket.ExpiresUtc > clock.GetUtcNow()).ToArray();
            }
        };
        await using var senderStore = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(),
            new FakeHubContext(new FakeClients()), broker, clock);
        await using var receiverStore = new DistributedOfficeRealTimeSessionStore(authorizer,
            new FakeHubContext(new FakeClients()), broker, clock);
        var sender = await Connect(senderStore, Guid.NewGuid(), "writer");
        var receiver = await Connect(receiverStore, sender.Ticket.ResourceId, "reader");
        var receiverRenewed = receiver.Ticket with { ExpiresUtc = receiver.Ticket.ExpiresUtc.AddMinutes(5) };
        Assert.True(await receiverStore.RenewTicketAsync(receiver.Ticket, receiverRenewed, receiver.Context.ConnectionId, default));
        Assert.True(await senderStore.UpdatePresenceAsync(sender.Ticket, sender.Context.ConnectionId, "{}", default));
        var snapshot = receiverStore.GetCompletePresenceStateAsync(receiverRenewed, receiver.Context.ConnectionId, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var renewed = sender.Ticket with { ExpiresUtc = sender.Ticket.ExpiresUtc.AddMinutes(5) };
            Assert.True(await senderStore.RenewTicketAsync(sender.Ticket, renewed, sender.Context.ConnectionId, default));
            clock.Now = sender.Ticket.ExpiresUtc.AddSeconds(1);
            resume.TrySetResult();
            Assert.DoesNotContain(ClientKey(sender.Ticket.ClientId), await snapshot.WaitAsync(TimeSpan.FromSeconds(3)));

            var members = await broker.ObserveMembersAsync(sender.Member.Room, default);
            Assert.Contains(members.Members, member => member.Incarnation == sender.Member.Incarnation && member.Ticket == renewed);
            var next = await receiverStore.GetCompletePresenceStateAsync(receiverRenewed, receiver.Context.ConnectionId, default);
            Assert.Equal("{}", next[ClientKey(sender.Ticket.ClientId)]);
            Assert.False(sender.Context.Aborted);
            Assert.False(receiver.Context.Aborted);
        }
        finally { resume.TrySetResult(); }
    }

    [Fact]
    public async Task Authorization_sweep_rechecks_a_ticket_renewed_during_authorization()
    {
        var clock = new RenewalClock(DateTimeOffset.UtcNow);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int batches = 0;
        var authorizer = new AllowAllAuthorizer
        {
            Batch = async checks =>
            {
                if (Interlocked.Increment(ref batches) == 1)
                {
                    entered.TrySetResult();
                    await resume.Task;
                }
                return checks.Select(check => check.Ticket.ExpiresUtc > clock.GetUtcNow()).ToArray();
            }
        };
        var broker = new FakeBackplane();
        await using var store = new DistributedOfficeRealTimeSessionStore(authorizer,
            new FakeHubContext(new FakeClients()), broker, clock);
        var sender = await Connect(store, Guid.NewGuid(), "writer");
        Assert.True(await store.UpdatePresenceAsync(sender.Ticket, sender.Context.ConnectionId, "{}", default));
        var sweep = InvokeRoomWork(store, sender.Ticket.ResourceId, "AuthorizeRoomAsync");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var renewed = sender.Ticket with { ExpiresUtc = sender.Ticket.ExpiresUtc.AddMinutes(5) };
            Assert.True(await store.RenewTicketAsync(sender.Ticket, renewed, sender.Context.ConnectionId, default));
            clock.Now = sender.Ticket.ExpiresUtc.AddSeconds(1);
            resume.TrySetResult();
            await sweep.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.False(sender.Context.Aborted);
            Assert.True(batches >= 2);
            var presence = await store.GetCompletePresenceStateAsync(renewed, sender.Context.ConnectionId, default);
            Assert.Equal("{}", presence[ClientKey(sender.Ticket.ClientId)]);
        }
        finally { resume.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cross_host_event_uses_the_current_sender_ticket(bool renewDuringAuthorization)
    {
        var clock = new RenewalClock(DateTimeOffset.UtcNow);
        var broker = new FakeBackplane();
        var received = new FakeClients();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int batches = 0;
        var authorizer = new AllowAllAuthorizer
        {
            Batch = async checks =>
            {
                if (renewDuringAuthorization && Interlocked.Increment(ref batches) == 1)
                {
                    entered.TrySetResult();
                    await resume.Task;
                }
                return checks.Select(check => check.Ticket.ExpiresUtc > clock.GetUtcNow()).ToArray();
            }
        };
        await using var senderStore = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(),
            new FakeHubContext(new FakeClients()), broker, clock);
        await using var receiverStore = new DistributedOfficeRealTimeSessionStore(authorizer,
            new FakeHubContext(received), broker, clock);
        var sender = await Connect(senderStore, Guid.NewGuid(), "writer");
        var receiver = await Connect(receiverStore, sender.Ticket.ResourceId, "reader");
        var receiverRenewed = receiver.Ticket with { ExpiresUtc = receiver.Ticket.ExpiresUtc.AddMinutes(5) };
        Assert.True(await receiverStore.RenewTicketAsync(receiver.Ticket, receiverRenewed, receiver.Context.ConnectionId, default));
        Assert.True(await senderStore.SendUpdateNotificationAsync(sender.Ticket, sender.Context.ConnectionId, 1, "", default));
        Task? poll = null;
        try
        {
            if (renewDuringAuthorization)
            {
                poll = PollRoom(receiverStore, sender.Ticket.ResourceId);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            var renewed = sender.Ticket with { ExpiresUtc = sender.Ticket.ExpiresUtc.AddMinutes(5) };
            Assert.True(await senderStore.RenewTicketAsync(sender.Ticket, renewed, sender.Context.ConnectionId, default));
            clock.Now = sender.Ticket.ExpiresUtc.AddSeconds(1);
            resume.TrySetResult();
            await (poll ?? PollRoom(receiverStore, sender.Ticket.ResourceId)).WaitAsync(TimeSpan.FromSeconds(3));

            var delivery = Assert.Single(received.Calls, call => call.Method == "updateNotification");
            Assert.Equal("", delivery.Arguments[1]);
            Assert.False(sender.Context.Aborted);
            Assert.False(receiver.Context.Aborted);
            var cleanup = typeof(DistributedOfficeRealTimeSessionStore).GetMethod("DrainCleanupAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)cleanup.Invoke(receiverStore, [CancellationToken.None])!;
            Assert.Contains((await broker.ObserveMembersAsync(sender.Member.Room, default)).Members,
                member => member.Incarnation == sender.Member.Incarnation && member.Ticket == renewed);
        }
        finally { resume.TrySetResult(); }
    }

    private static Task InvokeRoomWork(DistributedOfficeRealTimeSessionStore store, Guid resource, string methodName)
    {
        var rooms = (System.Collections.IDictionary)typeof(DistributedOfficeRealTimeSessionStore)
            .GetField("_rooms", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        var room = rooms[new OfficeRealTimeRoomKey(resource, 1)]!;
        var method = typeof(DistributedOfficeRealTimeSessionStore).GetMethod(methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(store, [room, CancellationToken.None])!;
    }

    private sealed class RenewalClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
