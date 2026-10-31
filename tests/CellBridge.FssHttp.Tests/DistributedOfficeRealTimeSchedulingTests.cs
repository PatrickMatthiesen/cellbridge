using CellBridge.AspNetCore;

namespace CellBridge.FssHttp.Tests;

public sealed partial class DistributedOfficeRealTimeSessionTests
{
    [Fact]
    public async Task Room_wake_hint_dispatches_from_the_log_without_waiting_for_fallback_poll()
    {
        var broker = new FakeBackplane();
        var clients = new FakeClients();
        await using var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(),
            new FakeHubContext(clients), broker);
        await store.StartAsync(CancellationToken.None);
        var sender = await Connect(store, Guid.NewGuid(), "wake-writer");
        var peer = await Connect(store, sender.Ticket.ResourceId, "wake-reader");
        var room = new OfficeRealTimeRoomKey(sender.Ticket.ResourceId, sender.Ticket.LifecycleGeneration);

        await broker.MutateAsync(sender.Member, broker.Epoch, "wake-event", "presence",
            ClientKey(sender.Ticket.ClientId), "presenceUpdated", "{}", CancellationToken.None);
        broker.NotifyRoomChanged(room);
        await WaitForAsync(() => clients.Calls.Any(x => x.ConnectionId == peer.Context.ConnectionId));

        Assert.Contains(clients.Calls, x => x.ConnectionId == peer.Context.ConnectionId && x.Method == "presenceUpdated");
    }

    [Fact]
    public async Task Lost_wake_hint_is_recovered_by_the_bounded_fallback_poll()
    {
        var broker = new FakeBackplane();
        var clients = new FakeClients();
        await using var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(),
            new FakeHubContext(clients), broker);
        await store.StartAsync(CancellationToken.None);
        var sender = await Connect(store, Guid.NewGuid(), "fallback-writer");
        var peer = await Connect(store, sender.Ticket.ResourceId, "fallback-reader");

        await broker.MutateAsync(sender.Member, broker.Epoch, "lost-wake", "presence",
            ClientKey(sender.Ticket.ClientId), "presenceUpdated", "{}", CancellationToken.None);
        await WaitForAsync(() => clients.Calls.Any(x => x.ConnectionId == peer.Context.ConnectionId));

        Assert.Contains(clients.Calls, x => x.ConnectionId == peer.Context.ConnectionId && x.Method == "presenceUpdated");
    }

    [Fact]
    public async Task Rotating_ready_queue_services_a_fifth_room_after_four_workers_release()
    {
        var broker = new FakeBackplane { HoldEventReads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(),
            new FakeHubContext(new FakeClients()), broker);
        var rooms = new List<Connected>();
        for (int i = 0; i < 5; i++) rooms.Add(await Connect(store, Guid.NewGuid(), "fair-room-" + i));
        await store.StartAsync(CancellationToken.None);

        await broker.FourReadsStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(4, store.Diagnostics.ActiveWorkers);
        broker.HoldEventReads.TrySetResult();
        await WaitForAsync(() => Volatile.Read(ref broker._readCalls) >= 5);

        Assert.InRange(store.Diagnostics.ActiveWorkers, 0, 4);
    }

    [Fact]
    public async Task Wake_storm_coalesces_to_bounded_room_work_while_worker_is_blocked()
    {
        var broker = new FakeBackplane { HoldEventReads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(),
            new FakeHubContext(new FakeClients()), broker);
        var connections = new List<Connected>();
        for (int i = 0; i < 4; i++) connections.Add(await Connect(store, Guid.NewGuid(), "wake-storm-" + i));
        await store.StartAsync(CancellationToken.None);
        await broker.FourReadsStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var connection = connections[0];
        var room = new OfficeRealTimeRoomKey(connection.Ticket.ResourceId, connection.Ticket.LifecycleGeneration);

        for (int i = 0; i < 1000; i++) broker.NotifyRoomChanged(room);

        Assert.InRange(store.Diagnostics.PendingOperations, 0, 16);
        broker.HoldEventReads.TrySetResult();
    }

    [Fact]
    public async Task Late_wake_after_stop_cannot_start_work_or_resurrect_a_room()
    {
        var broker = new FakeBackplane();
        var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(),
            new FakeHubContext(new FakeClients()), broker);
        var connection = await Connect(store, Guid.NewGuid(), "stopped");
        await store.StartAsync(CancellationToken.None);
        await store.StopAsync(CancellationToken.None);
        int reads = Volatile.Read(ref broker._readCalls);

        broker.NotifyRoomChanged(new(connection.Ticket.ResourceId, connection.Ticket.LifecycleGeneration));
        await Task.Delay(100);

        Assert.Equal(reads, Volatile.Read(ref broker._readCalls));
        Assert.Equal(0, store.Diagnostics.ActiveWorkers);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Stop_fences_queued_rooms_before_blocked_workers_complete()
    {
        var broker = new FakeBackplane { HoldEventReads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(),
            new FakeHubContext(new FakeClients()), broker);
        for (int i = 0; i < 5; i++) await Connect(store, Guid.NewGuid(), "stop-fence-" + i);
        await store.StartAsync(CancellationToken.None);

        await broker.FourReadsStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await store.StopAsync(CancellationToken.None);
        broker.HoldEventReads.TrySetResult();
        await WaitForAsync(() => store.Diagnostics.ActiveWorkers == 0);

        Assert.Equal(4, Volatile.Read(ref broker._readCalls));
        await store.DisposeAsync();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
