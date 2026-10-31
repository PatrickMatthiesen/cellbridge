using CellBridge.AspNetCore;

namespace CellBridge.FssHttp.Tests;

public sealed partial class DistributedOfficeRealTimeSessionTests
{
    [Theory]
    [InlineData(1, 14_000)]
    [InlineData(5, 10_000)]
    public async Task Unicode_events_within_encoded_record_and_batch_limits_keep_the_room_connected(int eventCount, int characters)
    {
        var broker = new FakeBackplane();
        var clients = new FakeClients();
        await using var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(), new FakeHubContext(clients), broker);
        Guid resource = Guid.NewGuid();
        var sender = await Connect(store, resource, "writer");
        var recipient = await Connect(store, resource, "reader");
        string text = new('漢', characters);
        for (int index = 0; index < eventCount; index++)
            await broker.MutateAsync(sender.Member, broker.Epoch, "unicode-" + index, "presence", ClientKey(sender.Ticket.ClientId),
                "presenceUpdated", $$"""{"text":"{{text}}","index":{{index}}}""", default);

        await PollRoom(store, resource);

        Assert.Equal(eventCount, clients.Calls.Count);
        Assert.False(sender.Context.Aborted);
        Assert.False(recipient.Context.Aborted);
    }
}
