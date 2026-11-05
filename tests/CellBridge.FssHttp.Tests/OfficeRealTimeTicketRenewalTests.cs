using System.Collections;
using System.Reflection;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;

namespace CellBridge.FssHttp.Tests;

public sealed class OfficeRealTimeTicketRenewalTests
{
    [Fact]
    public async Task Local_renewal_preserves_connection_and_presence_without_relaying_departure()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new OfficeRealTimeSessionStore(new AllowAllAuthorizer(), null!, clock);
        Guid resource = Guid.NewGuid();
        Guid senderId = Guid.NewGuid();
        OfficeRealTimeTicket senderTicket = Ticket(resource, senderId, "renewing-sender", clock.GetUtcNow());
        OfficeRealTimeTicket peerTicket = Ticket(resource, Guid.NewGuid(), "renewal-peer", clock.GetUtcNow());
        var peerMessages = new List<string>();
        int senderAborts = 0;
        var sender = new OfficeRealTimeConnection("sender", CancellationToken.None,
            (_, _, _) => Task.CompletedTask, () => senderAborts++);
        var peer = new OfficeRealTimeConnection("peer", CancellationToken.None,
            (method, _, _) => { peerMessages.Add(method); return Task.CompletedTask; }, () => { });

        Assert.True(await store.ConnectAsync(senderTicket, sender, CancellationToken.None));
        Assert.True(await store.ConnectAsync(peerTicket, peer, CancellationToken.None));
        string beforePayload = $$"""{"A":"{{senderId:D}}","text":"before"}""";
        Assert.True(await store.UpdatePresenceAsync(senderTicket, sender.ConnectionId, beforePayload, CancellationToken.None));
        int peerMessagesBeforeRenewal = peerMessages.Count;

        clock.Advance(TimeSpan.FromMinutes(1));
        OfficeRealTimeTicket renewedTicket = senderTicket with { ExpiresUtc = clock.GetUtcNow().AddMinutes(20) };
        Assert.True(await store.RenewTicketAsync(senderTicket, renewedTicket, sender.ConnectionId, CancellationToken.None));

        Assert.Equal(peerMessagesBeforeRenewal, peerMessages.Count);
        Assert.Equal(0, senderAborts);
        var snapshot = await store.GetCompletePresenceStateAsync(renewedTicket, sender.ConnectionId, CancellationToken.None);
        Assert.Equal(beforePayload, snapshot[senderId.ToString("B").ToLowerInvariant()]);
        Assert.Empty(await store.GetCompletePresenceStateAsync(senderTicket, sender.ConnectionId, CancellationToken.None));
        Assert.False(await store.UpdatePresenceAsync(senderTicket, sender.ConnectionId, beforePayload, CancellationToken.None));

        string afterPayload = $$"""{"A":"{{senderId:D}}","text":"after"}""";
        Assert.True(await store.UpdatePresenceAsync(renewedTicket, sender.ConnectionId, afterPayload, CancellationToken.None));
        Assert.Contains("presenceUpdated", peerMessages);
        Assert.Equal(0, senderAborts);
    }

    [Fact]
    public async Task Local_renewal_does_not_resurrect_a_ticket_that_expires_while_waiting_for_the_room()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new OfficeRealTimeSessionStore(new AllowAllAuthorizer(), null!, clock);
        OfficeRealTimeTicket current = Ticket(Guid.NewGuid(), Guid.NewGuid(), "expiring-sender", clock.GetUtcNow());
        var connection = new OfficeRealTimeConnection("expiring", CancellationToken.None,
            (_, _, _) => Task.CompletedTask, () => { });
        Assert.True(await store.ConnectAsync(current, connection, CancellationToken.None));

        var connections = (IDictionary)typeof(OfficeRealTimeSessionStore)
            .GetField("_connections", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        object local = connections[connection.ConnectionId]!;
        object room = local.GetType().GetProperty("Room", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(local)!;
        var gate = (SemaphoreSlim)room.GetType().GetProperty("Gate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(room)!;
        await gate.WaitAsync();
        try
        {
            OfficeRealTimeTicket renewed = current with { ExpiresUtc = current.ExpiresUtc.AddMinutes(20) };
            Task<bool> renewal = store.RenewTicketAsync(current, renewed, connection.ConnectionId, CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(20));
            gate.Release();
            Assert.False(await renewal);
        }
        finally
        {
            if (gate.CurrentCount == 0) gate.Release();
        }
    }

    private static OfficeRealTimeTicket Ticket(Guid resource, Guid clientId, string subject, DateTimeOffset now) =>
        new(resource, 1, new CellBridgeActor(new SubjectIdentity(subject, subject, subject)), clientId,
            now.AddMinutes(20));

    private sealed class AllowAllAuthorizer : IOfficeRealTimeAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(OfficeRealTimeTicket ticket, DocumentAccess requiredAccess,
            bool requireOwnedClient, CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public ValueTask<bool[]> AuthorizeTogetherAsync(IReadOnlyList<OfficeRealTimeTicketCheck> checks,
            CancellationToken cancellationToken) => ValueTask.FromResult(checks.Select(_ => true).ToArray());
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
