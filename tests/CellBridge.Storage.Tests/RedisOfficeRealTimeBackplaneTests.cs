using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.TestInfrastructure;
using StackExchange.Redis;

namespace CellBridge.Storage.Tests;

// These test the trusted coordination adapter directly. They do not qualify
// ticket issuance or document authorization; the Kestrel tests exercise those.
public sealed class RedisOfficeRealTimeBackplaneTests
{
    [Fact]
    public async Task Event_byte_counts_match_the_stored_unicode_and_escaped_json()
    {
        await using var f = await Fixture.Start();
        var member = Member(new(Guid.NewGuid(), 1), "writer");
        string epoch = (await f.Broker.ConnectAsync(member, default)).Snapshot!.Epoch;
        string[] payloads =
        [
            $$"""{"text":"{{new string('漢', 14_000)}}"}""",
            $$"""{"text":"{{new string('ش', 14_000)}}"}""",
            $$"""{"text":"{{string.Concat(Enumerable.Repeat("😀", 7_000))}}"}""",
            "{\"text\":\"quoted \\\"text\\\" and backslash \\\\ and newline\\n\"}"
        ];
        foreach (int index in Enumerable.Range(0, payloads.Length))
            Assert.Equal(OfficeRealTimeBackplaneStatus.Ok,
                (await f.Broker.MutateAsync(member, epoch, "encoding-" + index, "presence", ClientKey(member),
                    "presenceUpdated", payloads[index], default)).Status);

        var stored = await f.Database.ListRangeAsync(f.RoomPrefix(member.Room) + ":events");
        var read = await f.Broker.ReadEventsAsync(member.Room, epoch, 0, 32, 256 * 1024, default);
        Assert.Equal(OfficeRealTimeBackplaneStatus.Ok, read.Status);
        Assert.Equal(payloads.Length, read.Events.Count);
        for (int index = 0; index < read.Events.Count; index++)
        {
            Assert.Equal(payloads[index], read.Events[index].PayloadJson);
            Assert.Equal(Encoding.UTF8.GetByteCount(stored[index].ToString()), read.Events[index].EncodedBytes);
            Assert.InRange(read.Events[index].EncodedBytes, 1, 80 * 1024);
        }
        Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(read.Events[0]).Length > 80 * 1024);
        Assert.True(read.Events.Sum(item => item.EncodedBytes) < 256 * 1024);
    }

    [Fact]
    public async Task Room_admission_and_replacement_preserve_limits_and_fence_stale_cleanup()
    {
        await using var f = await Fixture.Start();
        var room = new OfficeRealTimeRoomKey(Guid.NewGuid(), 1);
        var members = Enumerable.Range(0, 32).Select(i => Member(room, "subject-" + i)).ToArray();
        foreach (var member in members)
            Assert.Equal(OfficeRealTimeBackplaneStatus.Ok, (await f.Broker.ConnectAsync(member, default)).Status);
        Assert.Equal(OfficeRealTimeBackplaneStatus.Denied,
            (await f.Broker.ConnectAsync(Member(room, "overflow"), default)).Status);
        var original = members[0];
        var imposter = Member(room, "other") with { Ticket = original.Ticket with { Actor = new(new("other", "other", "other")) } };
        Assert.Equal(OfficeRealTimeBackplaneStatus.Denied, (await f.Broker.ConnectAsync(imposter, default)).Status);
        var replacement = original with { Incarnation = Guid.NewGuid(), ConnectionId = Guid.NewGuid().ToString("N"), HostId = Guid.NewGuid() };
        var admitted = await f.Broker.ConnectAsync(replacement, default);
        Assert.Equal(OfficeRealTimeBackplaneStatus.Ok, admitted.Status);
        Assert.Equal(32, admitted.Snapshot!.Members.Count);
        Assert.DoesNotContain(admitted.Snapshot.Members, member => member.Incarnation == original.Incarnation);
        Assert.Equal(OfficeRealTimeBackplaneStatus.Missing,
            (await f.Broker.RemoveAsync(original, ClientKey(original), "late-disconnect", default)).Status);
        var heartbeat = await f.Broker.HeartbeatAsync([original, replacement], default);
        Assert.Equal(OfficeRealTimeBackplaneStatus.Ok, heartbeat.Status);
        Assert.DoesNotContain(original.Incarnation, heartbeat.CurrentIncarnations);
        Assert.Contains(replacement.Incarnation, heartbeat.CurrentIncarnations);
    }

    [Fact]
    public async Task Subject_limit_spans_rooms_and_abandoned_memberships_cannot_be_renewed_after_expiry()
    {
        await using var f = await Fixture.Start();
        var members = Enumerable.Range(0, 8).Select(_ => Member(new(Guid.NewGuid(), 1), "same-subject")).ToArray();
        foreach (var member in members)
            Assert.Equal(OfficeRealTimeBackplaneStatus.Ok, (await f.Broker.ConnectAsync(member, default)).Status);
        var ninth = Member(new(Guid.NewGuid(), 1), "same-subject");
        Assert.Equal(OfficeRealTimeBackplaneStatus.Denied, (await f.Broker.ConnectAsync(ninth, default)).Status);

        // No host heartbeat runs in this adapter-only fixture. Simulate abandoned
        // host membership by allowing the real Redis-time lease to expire.
        await Task.Delay(TimeSpan.FromSeconds(11));
        var expired = await f.Broker.HeartbeatAsync([members[0]], default);
        Assert.Empty(expired.CurrentIncarnations);
        Assert.Equal(OfficeRealTimeBackplaneStatus.Ok, (await f.Broker.ConnectAsync(ninth, default)).Status);
    }

    [Fact]
    public async Task Rolling_log_stays_bounded_and_detects_a_lagging_cursor()
    {
        await using var f = await Fixture.Start();
        var member = Member(new(Guid.NewGuid(), 1), "writer");
        var admitted = await f.Broker.ConnectAsync(member, default);
        string epoch = admitted.Snapshot!.Epoch;
        for (int i = 1; i <= 300; i++)
        {
            var result = await f.Broker.MutateAsync(member, epoch, "event-" + i, "notification", ClientKey(member),
                "updateNotification", "{\"channel\":3,\"payload\":\"{}\"}", default);
            Assert.Equal(OfficeRealTimeBackplaneStatus.Ok, result.Status);
            Assert.Equal(i, result.Sequence);
            if (i % 50 == 0)
                Assert.Contains(member.Incarnation, (await f.Broker.HeartbeatAsync([member], default)).CurrentIncarnations);
        }
        var lagging = await f.Broker.ReadEventsAsync(member.Room, epoch, 0, 32, 256 * 1024, default);
        Assert.True(lagging.HasGap);
        var current = await f.Broker.ReadEventsAsync(member.Room, epoch, 299, 32, 256 * 1024, default);
        Assert.False(current.HasGap);
        Assert.Equal(300, Assert.Single(current.Events).Sequence);
        Assert.Equal(256, await f.Database.ListLengthAsync(f.RoomPrefix(member.Room) + ":events"));
    }

    [Fact]
    public async Task Cleanup_budget_preserves_epoch_and_membership_until_departure_can_be_appended()
    {
        await using var f = await Fixture.Start();
        var member = Member(new(Guid.NewGuid(), 1), "writer");
        string epoch = (await f.Broker.ConnectAsync(member, default)).Snapshot!.Epoch;
        for (int i = 0; i < 40; i++)
            Assert.Equal(OfficeRealTimeBackplaneStatus.Ok,
                (await f.Broker.MutateAsync(member, epoch, "presence-" + i, "presence", ClientKey(member), "presenceUpdated", "{}", default)).Status);
        string prefix = f.RoomPrefix(member.Room);
        // Make the log old without slowing the test by thirty seconds. The keys
        // belong exclusively to this disposable adapter fixture.
        await f.Database.ScriptEvaluateAsync("local n=redis.call('LLEN',KEYS[1]); local bytes=0; for i=0,n-1 do local e=cjson.decode(redis.call('LINDEX',KEYS[1],i)); e.redisTimeMilliseconds=0; local raw=cjson.encode(e); redis.call('LSET',KEYS[1],i,raw); bytes=bytes+#raw end; redis.call('HSET',KEYS[2],'bytes',bytes)", [prefix + ":events", prefix + ":meta"]);
        var first = await f.Broker.RemoveAsync(member, ClientKey(member), "departure", default);
        Assert.Equal(OfficeRealTimeBackplaneStatus.Busy, first.Status);
        Assert.Equal(8, await f.Database.ListLengthAsync(prefix + ":events"));
        Assert.Equal(epoch, (string?)await f.Database.HashGetAsync(prefix + ":meta", "epoch"));
        Assert.Equal(1, await f.Database.HashLengthAsync(prefix + ":members"));
        var second = await f.Broker.RemoveAsync(member, ClientKey(member), "departure", default);
        Assert.Equal(OfficeRealTimeBackplaneStatus.Ok, second.Status);
        var read = await f.Broker.ReadEventsAsync(member.Room, epoch, 40, 32, 256 * 1024, default);
        Assert.False(read.HasGap);
        Assert.Equal("departure", Assert.Single(read.Events).Kind);
    }

    [Fact]
    public async Task Rolling_log_accounts_for_serialized_bytes_before_reaching_the_event_count_limit()
    {
        await using var f = await Fixture.Start();
        var member = Member(new(Guid.NewGuid(), 1), "writer");
        string epoch = (await f.Broker.ConnectAsync(member, default)).Snapshot!.Epoch;
        string payload = "{\"channel\":3,\"payload\":\"" + new string('x', 32 * 1024) + "\"}";
        for (int i = 0; i < 150; i++)
        {
            var result = await f.Broker.MutateAsync(member, epoch, "large-" + i, "notification", ClientKey(member),
                "updateNotification", payload, default);
            Assert.Equal(OfficeRealTimeBackplaneStatus.Ok, result.Status);
            if (i % 25 == 0) await f.Broker.HeartbeatAsync([member], default);
        }
        string prefix = f.RoomPrefix(member.Room);
        var events = await f.Database.ListRangeAsync(prefix + ":events");
        Assert.True(events.Length < 150);
        long actualBytes = events.Sum(value => (long)Encoding.UTF8.GetByteCount(value.ToString()));
        Assert.InRange(actualBytes, 1, 4 * 1024 * 1024);
        Assert.Equal(actualBytes, (long)await f.Database.HashGetAsync(prefix + ":meta", "bytes"));
        Assert.True((await f.Broker.ReadEventsAsync(member.Room, epoch, 0, 32, 256 * 1024, default)).HasGap);
    }

    [Fact]
    public async Task Missing_room_metadata_requires_a_fresh_epoch_and_does_not_adopt_old_members()
    {
        await using var f = await Fixture.Start();
        var member = Member(new(Guid.NewGuid(), 1), "writer");
        string epoch = (await f.Broker.ConnectAsync(member, default)).Snapshot!.Epoch;
        await f.Database.KeyDeleteAsync(f.RoomPrefix(member.Room) + ":meta");
        Assert.Equal(OfficeRealTimeBackplaneStatus.Missing,
            (await f.Broker.HeartbeatAsync([member], default)).Status);
        var replacement = Member(member.Room, "writer");
        Assert.Equal(OfficeRealTimeBackplaneStatus.Reset, (await f.Broker.ConnectAsync(replacement, default)).Status);
        var fresh = await f.Broker.ConnectAsync(replacement, default);
        Assert.Equal(OfficeRealTimeBackplaneStatus.Ok, fresh.Status);
        Assert.NotEqual(epoch, fresh.Snapshot!.Epoch);
        Assert.Equal(replacement.Incarnation, Assert.Single(fresh.Snapshot.Members).Incarnation);
    }

    [Fact]
    public async Task Room_wake_hints_follow_committed_presence_and_departure_events()
    {
        await using var f = await Fixture.Start();
        var room = new OfficeRealTimeRoomKey(Guid.NewGuid(), 1);
        var member = Member(room, "writer");
        var hints = new System.Collections.Concurrent.ConcurrentQueue<OfficeRealTimeRoomKey>();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Broker.RoomChanged += changed =>
        {
            hints.Enqueue(changed);
            arrived.TrySetResult();
        };

        string epoch = (await f.Broker.ConnectAsync(member, default)).Snapshot!.Epoch;
        Assert.Equal(OfficeRealTimeBackplaneStatus.Ok,
            (await f.Broker.MutateAsync(member, epoch, "wake-presence", "presence", ClientKey(member), "presenceUpdated", "{}", default)).Status);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(room, Assert.Single(hints));

        Assert.Equal(OfficeRealTimeBackplaneStatus.Ok,
            (await f.Broker.RemoveAsync(member, ClientKey(member), "wake-departure", default)).Status);
        await EventuallyAsync(() => hints.Count == 2, TimeSpan.FromSeconds(3));
        Assert.All(hints, hint => Assert.Equal(room, hint));
        Assert.Equal(2, f.Broker.WakeHintsReceived);
        Assert.True(f.Broker.WakeHintBytesReceived > 0);
    }

    [Fact]
    public async Task Lease_pruning_emits_advisory_wake_after_it_appends_a_departure()
    {
        await using var f = await Fixture.Start();
        var room = new OfficeRealTimeRoomKey(Guid.NewGuid(), 1);
        var member = Member(room, "writer");
        var arrived = new TaskCompletionSource<OfficeRealTimeRoomKey>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Broker.RoomChanged += changed => arrived.TrySetResult(changed);
        string epoch = (await f.Broker.ConnectAsync(member, default)).Snapshot!.Epoch;
        Assert.Equal(OfficeRealTimeBackplaneStatus.Ok,
            (await f.Broker.MutateAsync(member, epoch, "presence-before-expiry", "presence", ClientKey(member), "presenceUpdated", "{}", default)).Status);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await f.Database.SortedSetAddAsync(f.RoomPrefix(room) + ":expiry", member.ConnectionId, 0);
        var snapshot = await f.Broker.ReadSnapshotAsync(room, default);
        Assert.Equal(OfficeRealTimeBackplaneStatus.Ok, snapshot.Status);
        Assert.Equal(room, await arrived.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        var events = await f.Broker.ReadEventsAsync(room, epoch, 1, 32, 256 * 1024, default);
        Assert.Equal("departure", Assert.Single(events.Events).Kind);
    }

    [Fact]
    public async Task Wake_subscription_ignores_other_namespaces_and_noncanonical_channels()
    {
        await using var f = await Fixture.Start();
        var received = new TaskCompletionSource<OfficeRealTimeRoomKey>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Broker.RoomChanged += room => received.TrySetResult(room);
        var invalidResource = Guid.Parse("abcdefab-cdef-abcd-efab-cdefabcdefab");
        string prefix = f.RoomPrefix(new(invalidResource, 1));
        string uppercase = prefix.Replace(invalidResource.ToString("N"), invalidResource.ToString("N").ToUpperInvariant(), StringComparison.Ordinal);
        await f.Database.PublishAsync(RedisChannel.Literal("cellbridge:rtc:{other}:room:00000000000000000000000000000000:1:meta:wake"), "1");
        await f.Database.PublishAsync(RedisChannel.Literal(prefix + ":meta:wake"), "bad");
        await f.Database.PublishAsync(RedisChannel.Literal(uppercase + ":meta:wake"), "1");
        await f.Database.PublishAsync(RedisChannel.Literal(prefix.Replace(invalidResource.ToString("N"), "not-a-guid", StringComparison.Ordinal) + ":meta:wake"), "1");
        await f.Database.PublishAsync(RedisChannel.Literal(prefix[..^1] + "01:meta:wake"), "1");

        await Task.Delay(250);
        Assert.False(received.Task.IsCompleted);
        var actual = new OfficeRealTimeRoomKey(Guid.NewGuid(), 1);
        var member = Member(actual, "writer");
        string epoch = (await f.Broker.ConnectAsync(member, default)).Snapshot!.Epoch;
        await f.Broker.MutateAsync(member, epoch, "valid-wake", "notification", ClientKey(member), "updateNotification", "{}", default);
        Assert.Equal(actual, await received.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Losing_only_the_pubsub_connection_advances_the_broker_generation()
    {
        await using var f = await Fixture.Start();
        var baseline = f.Broker.Generation;
        await f.Database.ExecuteAsync("CLIENT", "KILL", "TYPE", "pubsub");
        await EventuallyAsync(() => f.Broker.Generation > baseline, TimeSpan.FromSeconds(5));
        // Recovery may be faster than this assertion. The generation change is
        // the fence that makes existing rooms reconnect even after recovery.
        Assert.True(f.Broker.Generation > baseline);
    }

    private static async Task EventuallyAsync(Func<bool> predicate, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!predicate()) await Task.Delay(25, cancellation.Token);
    }

    private static OfficeRealTimeBackplaneMember Member(OfficeRealTimeRoomKey room, string subject) =>
        new(room, Guid.NewGuid().ToString("N"), Guid.NewGuid(), Guid.NewGuid(),
            new(room.ResourceId, room.LifecycleGeneration, new(new(subject, subject, subject)), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(20)));
    private static string ClientKey(OfficeRealTimeBackplaneMember member) => member.Ticket.ClientId.ToString("B").ToLowerInvariant();

    private sealed class Fixture(IDistributedApplicationTestingBuilder builder, DistributedApplication application,
        IConnectionMultiplexer connection, RedisOfficeRealTimeBackplane broker, string roomNamespace) : IAsyncDisposable
    {
        internal RedisOfficeRealTimeBackplane Broker => broker;
        internal IDatabase Database => connection.GetDatabase();
        internal string RoomPrefix(OfficeRealTimeRoomKey room)
        {
            string tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(roomNamespace)))[..24];
            return $"cellbridge:rtc:{{{tag}}}:room:{room.ResourceId:N}:{room.LifecycleGeneration}";
        }

        internal static async Task<Fixture> Start()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var builder = await PostgreSqlEnvironment.CreateBuilderAsync(timeout.Token);
            builder.Resources.Clear();
            builder.AddContainer("rtc-coordination", "redis", "7.4-alpine")
                .WithArgs("--save", "", "--appendonly", "no", "--maxmemory-policy", "noeviction")
                .WithEndpoint(targetPort: 6379, name: "tcp", scheme: "tcp");
            DistributedApplication? application = null;
            IConnectionMultiplexer? connection = null;
            RedisOfficeRealTimeBackplane? broker = null;
            try
            {
                application = await builder.BuildAsync(timeout.Token);
                await application.StartAsync(timeout.Token);
                await application.ResourceNotifications.WaitForResourceAsync("rtc-coordination", KnownResourceStates.Running, timeout.Token);
                var endpoint = application.GetEndpoint("rtc-coordination", "tcp");
                string configuration = $"{endpoint.Host}:{endpoint.Port}";
                connection = await ConnectionMultiplexer.ConnectAsync(configuration + ",allowAdmin=true");
                string roomNamespace = "adapter-tests-" + Guid.NewGuid().ToString("N");
                broker = new(new CellBridgeOptions { OfficeRealTimeRedisConnectionString = configuration, OfficeRealTimeRedisNamespace = roomNamespace });
                return new(builder, application, connection, broker, roomNamespace);
            }
            catch
            {
                if (broker is not null) await broker.DisposeAsync();
                connection?.Dispose();
                if (application is not null) await application.DisposeAsync();
                await builder.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await broker.DisposeAsync();
            connection.Dispose();
            await application.DisposeAsync();
            await builder.DisposeAsync();
        }
    }
}
