using CellBridge.AspNetCore;

namespace CellBridge.FssHttp.Tests;

public sealed partial class DistributedOfficeRealTimeSessionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Replacement_departure_removes_old_presence_without_erasing_new_presence(bool publishReplacement, bool snapshotFirst)
    {
        var broker = new FakeBackplane();
        var clients = new FakeClients();
        await using var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(), new FakeHubContext(clients), broker);
        Guid resource = Guid.NewGuid();
        var original = await Connect(store, resource, "writer");
        var peer = await Connect(store, resource, "peer");
        string key = ClientKey(original.Ticket.ClientId);
        await broker.MutateAsync(original.Member, broker.Epoch, "original", "presence", key, "presenceUpdated", "{\"version\":1}", default);
        await PollRoom(store, resource);
        clients.Calls.Clear();
        // Model ConnectScript's atomic replacement: append the old departure,
        // remove its incarnation, then admit a new incarnation with no presence.
        await broker.MutateAsync(original.Member, broker.Epoch, "departure", "departure", key, "presenceUpdated", "{}", default);
        await broker.RemoveAsync(original.Member, key, "replace", default);
        var replacement = new FakeHubCallerContext(Guid.NewGuid().ToString("N"));
        Assert.True(await store.ConnectAsync(original.Ticket, replacement, default));
        if (publishReplacement)
            Assert.True(await store.UpdatePresenceAsync(original.Ticket, replacement.ConnectionId, "{\"version\":2}", default));
        if (snapshotFirst)
        {
            var snapshot = await store.GetCompletePresenceStateAsync(peer.Ticket, peer.Context.ConnectionId, default);
            Assert.Equal("{\"version\":2}", snapshot[key]);
        }

        await PollRoom(store, resource);

        var calls = clients.Calls.Where(call => call.ConnectionId == peer.Context.ConnectionId).ToArray();
        if (snapshotFirst) Assert.Empty(calls);
        else
        {
            Assert.Equal(new[] { key }, (string[])calls[0].Arguments[1]!);
            if (publishReplacement)
            {
                Assert.Equal(2, calls.Length);
                Assert.Equal("{\"version\":2}", ((Dictionary<string, string>)calls[1].Arguments[0]!)[key]);
            }
            else Assert.Single(calls);
        }
    }
}
