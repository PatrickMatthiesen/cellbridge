using CellBridge.AspNetCore;

namespace CellBridge.FssHttp.Tests;

public sealed partial class DistributedOfficeRealTimeSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admission_watermark_excludes_old_notifications_only_for_new_incarnation(bool reconnect)
    {
        var broker = new FakeBackplane();
        var clients = new FakeClients();
        await using var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(), new FakeHubContext(clients), broker);
        Guid resource = Guid.NewGuid();
        var sender = await Connect(store, resource, "writer");
        var existing = await Connect(store, resource, "existing");
        Connected? original = reconnect ? await Connect(store, resource, "reconnect") : null;
        await broker.MutateAsync(sender.Member, broker.Epoch, "before-admission", "notification", "",
            "updateNotification", "{\"channel\":3,\"payload\":\"before\"}", default);
        string joiningId;
        if (original is not null)
        {
            var replacement = new FakeHubCallerContext(Guid.NewGuid().ToString("N"));
            Assert.True(await store.ConnectAsync(original.Ticket, replacement, default));
            joiningId = replacement.ConnectionId;
            Assert.True(original.Context.Aborted);
        }
        else joiningId = (await Connect(store, resource, "joining")).Context.ConnectionId;

        await PollRoom(store, resource);

        Assert.Contains(clients.Calls, call => call.ConnectionId == existing.Context.ConnectionId && (string)call.Arguments[1]! == "before");
        Assert.DoesNotContain(clients.Calls, call => call.ConnectionId == joiningId);
        await broker.MutateAsync(sender.Member, broker.Epoch, "after-admission", "notification", "",
            "updateNotification", "{\"channel\":3,\"payload\":\"after\"}", default);
        await PollRoom(store, resource);
        Assert.Contains(clients.Calls, call => call.ConnectionId == joiningId && (string)call.Arguments[1]! == "after");
    }

    [Fact]
    public async Task Admission_watermark_is_not_advanced_by_a_later_presence_snapshot()
    {
        var broker = new FakeBackplane();
        var clients = new FakeClients();
        await using var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(), new FakeHubContext(clients), broker);
        Guid resource = Guid.NewGuid();
        var sender = await Connect(store, resource, "writer");
        var recipient = await Connect(store, resource, "reader");
        await broker.MutateAsync(sender.Member, broker.Epoch, "queued-typing", "notification", "",
            "updateNotification", "{\"channel\":3,\"payload\":\"typing\"}", default);
        await store.GetCompletePresenceStateAsync(recipient.Ticket, recipient.Context.ConnectionId, default);

        await PollRoom(store, resource);

        Assert.Contains(clients.Calls, call => call.ConnectionId == recipient.Context.ConnectionId && (string)call.Arguments[1]! == "typing");
    }
}
