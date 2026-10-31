using CellBridge.AspNetCore;

namespace CellBridge.FssHttp.Tests;

public sealed partial class DistributedOfficeRealTimeSessionTests
{
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
