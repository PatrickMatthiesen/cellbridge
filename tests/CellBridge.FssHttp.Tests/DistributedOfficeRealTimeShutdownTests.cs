using System.Collections.Concurrent;
using System.Reflection;
using CellBridge.AspNetCore;

namespace CellBridge.FssHttp.Tests;

public sealed partial class DistributedOfficeRealTimeSessionTests
{
    [Fact]
    public async Task Redis_admission_completing_after_stop_cannot_register_a_local_connection()
    {
        var broker = new FakeBackplane { HoldConnect = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(), new FakeHubContext(new FakeClients()), broker);
        await store.StartAsync(default);
        var ticket = new OfficeRealTimeTicket(Guid.NewGuid(), 1, new(new("late", "late", "late")), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
        var context = new FakeHubCallerContext("late-admission");
        var admission = store.ConnectAsync(ticket, context, default);
        await broker.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await store.StopAsync(default);
        broker.HoldConnect.TrySetResult();

        Assert.False(await admission.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, store.Diagnostics.OccupiedRooms);
    }

    [Fact]
    public async Task Disposal_tracks_a_pump_blocked_at_its_admission_lock()
    {
        var store = new DistributedOfficeRealTimeSessionStore(new AllowAllAuthorizer(), new FakeHubContext(new FakeClients()), new FakeBackplane());
        await store.StartAsync(default);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object sync = typeof(DistributedOfficeRealTimeSessionStore).GetField("_sync", flags)!.GetValue(store)!;
        var workers = (SemaphoreSlim)typeof(DistributedOfficeRealTimeSessionStore).GetField("_workers", flags)!.GetValue(store)!;
        var running = (ConcurrentDictionary<Task, byte>)typeof(DistributedOfficeRealTimeSessionStore).GetField("_running", flags)!.GetValue(store)!;
        Task disposal;
        lock (sync)
        {
            typeof(DistributedOfficeRealTimeSessionStore).GetMethod("SchedulePump", flags)!.Invoke(store, null);
            Assert.True(SpinWait.SpinUntil(() => workers.CurrentCount < 4, TimeSpan.FromSeconds(2)));
            Assert.NotEmpty(running);
            disposal = store.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
        }
        await disposal.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
