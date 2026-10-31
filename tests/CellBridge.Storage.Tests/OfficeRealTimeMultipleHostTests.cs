using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using CellBridge.TestInfrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CellBridge.Storage.Tests;

/// <summary>Real Kestrel instances, protocol ticket issuance, PostgreSQL state and Redis RTC.</summary>
public sealed partial class OfficeRealTimeMultipleHostTests
{
    private const string PublicOrigin = "https://rtc.integration.test";
    private static readonly XNamespace Protocol = "http://schemas.microsoft.com/sharepoint/soap/";
    private static readonly CellBridgeActor ReaderActor = new(new("tests:reader", "test-reader", "Test reader"));

    [Fact]
    public async Task Oversized_notification_is_rejected_without_disconnecting_healthy_collaborators()
    {
        await using var fixture = await Fixture.StartAsync();
        var file = await fixture.CreateDocumentAsync();
        Guid senderClient = await fixture.JoinCoauthoringAsync(file.Path);
        Guid peerClient = await fixture.JoinCoauthoringAsync(file.Path);
        await using var sender = await fixture.ConnectAsync(fixture.First,
            await fixture.GetDocumentTicketAsync(fixture.First, file.Path), senderClient, utf8Json: true);
        await using var peer = await fixture.ConnectAsync(fixture.First,
            await fixture.GetDocumentTicketAsync(fixture.First, file.Path), peerClient);
        await peer.Connection.InvokeAsync<Dictionary<string, string>>("GetCompletePresenceState");
        string oversized = $$"""{"A":"{{senderClient:B}}","text":"{{new string('漢', 14_000)}}"}""";
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() =>
            sender.Connection.InvokeAsync("SendUpdateNotification", 3, oversized));
        Assert.False(sender.Closed.Task.IsCompleted);
        Assert.False(peer.Closed.Task.IsCompleted);
        await peer.Connection.InvokeAsync<Dictionary<string, string>>("GetCompletePresenceState");
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Connection.On<int, string>("updateNotification", (_, payload) => received.TrySetResult(payload));
        string accepted = $$"""{"A":"{{senderClient:B}}","text":"accepted"}""";
        await sender.Connection.InvokeAsync("SendUpdateNotification", 3, accepted);
        Assert.Equal(accepted, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Large_unicode_presence_reaches_the_other_host_without_disconnecting_the_room()
    {
        await using var fixture = await Fixture.StartAsync();
        var file = await fixture.CreateDocumentAsync();
        Guid senderClient = await fixture.JoinCoauthoringAsync(file.Path);
        Guid peerClient = await fixture.JoinCoauthoringAsync(file.Path);
        await using var sender = await fixture.ConnectAsync(fixture.First,
            await fixture.GetDocumentTicketAsync(fixture.First, file.Path), senderClient, utf8Json: true);
        await using var peer = await fixture.ConnectAsync(fixture.Second,
            await fixture.GetDocumentTicketAsync(fixture.Second, file.Path), peerClient);
        await peer.Connection.InvokeAsync<Dictionary<string, string>>("GetCompletePresenceState");
        var received = System.Threading.Channels.Channel.CreateUnbounded<string>();
        string senderKey = senderClient.ToString("B").ToLowerInvariant();
        peer.Connection.On<Dictionary<string, string>, string[]>("presenceUpdated", (values, _) =>
        {
            if (values.TryGetValue(senderKey, out var value)) received.Writer.TryWrite(value);
        });
        string[] texts = [new string('漢', 14_000), new string('ش', 14_000), string.Concat(Enumerable.Repeat("😀", 5_000)), "still connected"];
        foreach (string text in texts)
        {
            string presence = $$"""{"A":"{{senderClient:B}}","text":"{{text}}"}""";
            Assert.True(Encoding.UTF8.GetByteCount(presence) < 64 * 1024);
            await sender.Connection.InvokeAsync("UpdatePresence", presence).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(presence, await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(sender.Closed.Task.IsCompleted);
            Assert.False(peer.Closed.Task.IsCompleted);
        }
        var snapshot = await peer.Connection.InvokeAsync<Dictionary<string, string>>("GetCompletePresenceState");
        Assert.Contains("still connected", snapshot[senderKey]);
    }

    [Fact]
    public async Task Two_kestrel_instances_relay_authorized_signalr_traffic_and_reconnect()
    {
        await using var fixture = await Fixture.StartAsync();
        await Task.Delay(250);
        Assert.Equal(0, fixture.First.Scheduler.Diagnostics.PollCalls);
        Assert.Equal(0, fixture.First.Scheduler.Diagnostics.AuthorizationSweeps);
        var file = await fixture.CreateDocumentAsync();
        Guid senderClient = await fixture.JoinCoauthoringAsync(file.Path);
        Guid peerClient = await fixture.JoinCoauthoringAsync(file.Path);
        string senderTicket = await fixture.GetDocumentTicketAsync(fixture.First, file.Path);
        string peerTicket = await fixture.GetDocumentTicketAsync(fixture.Second, file.Path);

        await using var sender = await fixture.ConnectAsync(fixture.First, senderTicket, senderClient);
        string initialPresence = $$"""{"A":"{{senderClient:B}}","text":"before-peer-join"}""";
        await sender.Connection.InvokeAsync("UpdatePresence", initialPresence).WaitAsync(TimeSpan.FromSeconds(10));
        await using var peer = await fixture.ConnectAsync(fixture.Second, peerTicket, peerClient);
        var initialSnapshot = await peer.Connection.InvokeAsync<Dictionary<string, string>>("GetCompletePresenceState")
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(initialPresence, initialSnapshot[senderClient.ToString("B").ToLowerInvariant()]);
        var subsequentSnapshot = await peer.Connection.InvokeAsync<Dictionary<string, string>>("GetCompletePresenceState")
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(initialPresence, subsequentSnapshot[senderClient.ToString("B").ToLowerInvariant()]);
        var received = new TaskCompletionSource<(Dictionary<string, string> Values, string[] Removed)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var departed = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Connection.On<Dictionary<string, string>, string[]>("presenceUpdated",
            (values, removed) =>
            {
                received.TrySetResult((values, removed));
                if (removed.Length != 0) departed.TrySetResult(removed);
            });

        string presence = $$"""{"A":"{{senderClient:B}}","text":"cross-host-next"}""";
        await sender.Connection.InvokeAsync("UpdatePresence", presence).WaitAsync(TimeSpan.FromSeconds(10));
        var senderDiagnostics = fixture.First.Scheduler.Diagnostics;
        var peerDiagnostics = fixture.Second.Scheduler.Diagnostics;
        (Dictionary<string, string> Values, string[] Removed) delivered;
        try
        {
            delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            Dictionary<string, string>? peerState = null;
            string snapshotFailure = "none";
            try
            {
                peerState = await peer.Connection.InvokeAsync<Dictionary<string, string>>("GetCompletePresenceState")
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception error) { snapshotFailure = error.GetType().Name; }
            string redisState = await fixture.RedisRoomDiagnosticsAsync();
            string closeReason = peer.Closed.Task.IsCompleted ? await peer.Closed.Task ?? "clean" : "pending";
            string snapshotKeys = peerState is null ? "" : string.Join(',', peerState.Keys);
            throw new InvalidOperationException(
                $"Cross-host callback timed out. senderDiagnosticsBefore={senderDiagnostics}; peerDiagnosticsBefore={peerDiagnostics}; " +
                $"senderDiagnosticsAfter={fixture.First.Scheduler.Diagnostics}; peerDiagnosticsAfter={fixture.Second.Scheduler.Diagnostics}; " +
                $"peerState={peer.Connection.State}; peerClosed={peer.Closed.Task.IsCompleted}; peerClosedReason={closeReason}; " +
                $"snapshotFailure={snapshotFailure}; snapshotEntryCount={peerState?.Count.ToString() ?? "unknown"}; " +
                $"snapshotKeys=[{snapshotKeys}]; redisState={redisState}.");
        }
        Assert.Equal(presence, delivered.Values[senderClient.ToString("B").ToLowerInvariant()]);
        Assert.Empty(delivered.Removed);

        var state = await peer.Connection.InvokeAsync<Dictionary<string, string>>("GetCompletePresenceState")
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(presence, state[senderClient.ToString("B").ToLowerInvariant()]);

        await sender.Connection.StopAsync();
        Assert.Contains(senderClient.ToString("B").ToLowerInvariant(),
            await departed.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        // A fresh protocol ticket reconnects on the other Kestrel instance.
        string renewed = await fixture.GetDocumentTicketAsync(fixture.Second, file.Path);
        await using var reconnected = await fixture.ConnectAsync(fixture.Second, renewed, senderClient);
        await reconnected.Connection.InvokeAsync<Dictionary<string, string>>("GetCompletePresenceState")
            .WaitAsync(TimeSpan.FromSeconds(10));

        // Stop one independent Kestrel instance. The remaining instance still relays the room.
        await fixture.First.App.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var afterHostStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Connection.On<Dictionary<string, string>, string[]>("presenceUpdated", (_, _) => afterHostStop.TrySetResult());
        await reconnected.Connection.InvokeAsync("UpdatePresence", $$"""{"A":"{{senderClient:B}}","host":"remaining"}""");
        await afterHostStop.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(fixture.First.Scheduler.Diagnostics.PollCalls > 0);
        Assert.True(fixture.Second.Scheduler.Diagnostics.PollCalls > 0);
        Console.WriteLine("RTC instances=2 observedDelivery=1 reconnect=1");
    }

    [Fact]
    public async Task Ticket_expiry_closes_the_socket_and_protocol_renewal_reconnects()
    {
        await using var fixture = await Fixture.StartAsync();
        var file = await fixture.CreateDocumentAsync();
        Guid client = await fixture.JoinCoauthoringAsync(file.Path);
        string ticket = await fixture.GetDocumentTicketAsync(fixture.First, file.Path);
        await using var peer = await fixture.ConnectAsync(fixture.First, ticket, client);
        await peer.Connection.InvokeAsync<Dictionary<string, string>>("GetCompletePresenceState")
            .WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Clock.Advance(TimeSpan.FromMinutes(21));
        await peer.Closed.Task.WaitAsync(TimeSpan.FromSeconds(12));
        Assert.True(fixture.First.Scheduler.Diagnostics.ExpiryAborts > 0,
            $"Socket closed without a scheduler expiry abort. diagnostics={fixture.First.Scheduler.Diagnostics}; clock={fixture.Clock.GetUtcNow():O}");

        string renewedTicket = await fixture.GetDocumentTicketAsync(fixture.Second, file.Path);
        await using var renewed = await fixture.ConnectAsync(fixture.Second, renewedTicket, client);
        await renewed.Connection.InvokeAsync<Dictionary<string, string>>("GetCompletePresenceState")
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Missing_redis_room_state_aborts_existing_sockets_without_replaying_old_events()
    {
        await using var fixture = await Fixture.StartAsync();
        var file = await fixture.CreateDocumentAsync();
        Guid senderClient = await fixture.JoinCoauthoringAsync(file.Path);
        Guid peerClient = await fixture.JoinCoauthoringAsync(file.Path);
        await using var sender = await fixture.ConnectAsync(fixture.First,
            await fixture.GetDocumentTicketAsync(fixture.First, file.Path), senderClient);
        await using var peer = await fixture.ConnectAsync(fixture.Second,
            await fixture.GetDocumentTicketAsync(fixture.Second, file.Path), peerClient);
        await sender.Connection.InvokeAsync("UpdatePresence", $$"""{"A":"{{senderClient:B}}","old":true}""");

        await fixture.ClearRedisAsync();
        await Task.WhenAll(sender.Closed.Task, peer.Closed.Task).WaitAsync(TimeSpan.FromSeconds(12));

        // Lost epoch state cannot be refreshed by heartbeats. A fresh protocol ticket gets a new room.
        string fresh = await fixture.GetDocumentTicketAsync(fixture.Second, file.Path);
        await using var reconnect = await fixture.ConnectAsync(fixture.Second, fresh, peerClient);
        var state = await reconnect.Connection.InvokeAsync<Dictionary<string, string>>("GetCompletePresenceState")
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(state);
    }

    [Fact]
    public async Task Redis_connection_interruption_aborts_sessions_and_allows_fresh_reconnect_after_recovery()
    {
        await using var fixture = await Fixture.StartAsync();
        var file = await fixture.CreateDocumentAsync();
        Guid firstClient = await fixture.JoinCoauthoringAsync(file.Path);
        Guid secondClient = await fixture.JoinCoauthoringAsync(file.Path);
        string firstTicket = await fixture.GetDocumentTicketAsync(fixture.First, file.Path);
        string secondTicket = await fixture.GetDocumentTicketAsync(fixture.Second, file.Path);
        await using var first = await fixture.ConnectAsync(fixture.First, firstTicket, firstClient);
        await using var second = await fixture.ConnectAsync(fixture.Second, secondTicket, secondClient);
        // SignalR StartAsync can finish before server-side admission. Establish
        // both admitted memberships before interrupting their broker connections.
        await first.Connection.InvokeAsync<Dictionary<string, string>>("GetCompletePresenceState");
        await second.Connection.InvokeAsync<Dictionary<string, string>>("GetCompletePresenceState");
        long firstGeneration = fixture.First.App.Services.GetRequiredService<IOfficeRealTimeBackplane>().Generation;
        long secondGeneration = fixture.Second.App.Services.GetRequiredService<IOfficeRealTimeBackplane>().Generation;

        using (var admin = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(fixture.RedisConnection))
        {
            var server = admin.GetServer(admin.GetEndPoints()[0]);
            var killed = await server.ExecuteAsync("CLIENT", "KILL", "TYPE", "normal");
            Assert.True((long)killed >= 2, "Redis did not terminate both Kestrel backplane connections.");
        }

        await Task.WhenAll(first.Closed.Task, second.Closed.Task).WaitAsync(TimeSpan.FromSeconds(15));
        await WaitUntilAsync(() => fixture.First.App.Services.GetRequiredService<IOfficeRealTimeBackplane>().IsHealthy &&
            fixture.Second.App.Services.GetRequiredService<IOfficeRealTimeBackplane>().IsHealthy &&
            fixture.First.Scheduler.Diagnostics.OccupiedRooms == 0 && fixture.Second.Scheduler.Diagnostics.OccupiedRooms == 0,
            TimeSpan.FromSeconds(15));
        Assert.True(fixture.First.App.Services.GetRequiredService<IOfficeRealTimeBackplane>().Generation > firstGeneration);
        Assert.True(fixture.Second.App.Services.GetRequiredService<IOfficeRealTimeBackplane>().Generation > secondGeneration);

        string fresh = await fixture.GetDocumentTicketAsync(fixture.Second, file.Path);
        await using var reconnect = await fixture.ConnectAsync(fixture.Second, fresh, secondClient);
        Assert.Empty(await reconnect.Connection.InvokeAsync<Dictionary<string, string>>("GetCompletePresenceState")
            .WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Stopped_host_with_lost_cleanup_is_reaped_after_membership_lease_expires()
    {
        await using var fixture = await Fixture.StartAsync(distributedHosts: 2, faultableFirstHost: true);
        var file = await fixture.CreateDocumentAsync();
        Guid abandonedClient = await fixture.JoinCoauthoringAsync(file.Path);
        Guid firstSurvivorClient = await fixture.JoinCoauthoringAsync(file.Path);
        Guid secondSurvivorClient = await fixture.JoinCoauthoringAsync(file.Path);
        await using var abandoned = await fixture.ConnectAsync(fixture.First,
            await fixture.GetDocumentTicketAsync(fixture.First, file.Path), abandonedClient);
        await using var firstSurvivor = await fixture.ConnectAsync(fixture.Second,
            await fixture.GetDocumentTicketAsync(fixture.Second, file.Path), firstSurvivorClient);
        await using var secondSurvivor = await fixture.ConnectAsync(fixture.Second,
            await fixture.GetDocumentTicketAsync(fixture.Second, file.Path), secondSurvivorClient);
        var departure = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        secondSurvivor.Connection.On<Dictionary<string, string>, string[]>("presenceUpdated",
            (_, removed) =>
            {
                if (removed.Contains(abandonedClient.ToString("B").ToLowerInvariant(), StringComparer.Ordinal))
                    departure.TrySetResult(removed);
            });
        await abandoned.Connection.InvokeAsync("UpdatePresence", $$"""{"A":"{{abandonedClient:B}}","host":"ending"}""")
            .WaitAsync(TimeSpan.FromSeconds(10));

        fixture.First.FaultableBackplane!.SuppressRemovals = true;
        await fixture.First.App.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await departure.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var snapshot = await firstSurvivor.Connection.InvokeAsync<Dictionary<string, string>>("GetCompletePresenceState")
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(abandonedClient.ToString("B").ToLowerInvariant(), snapshot.Keys);
    }

    [Fact]
    public async Task Authorization_denies_connect_rejects_write_and_closes_revoked_sessions()
    {
        await using var fixture = await Fixture.StartAsync();
        var file = await fixture.CreateDocumentAsync();
        Guid senderClient = await fixture.JoinCoauthoringAsync(file.Path);
        Guid readerClient = Guid.NewGuid();
        string senderTicket = await fixture.GetDocumentTicketAsync(fixture.First, file.Path);
        var provider = fixture.First.App.Services.GetRequiredService<CellBridgeDocumentService>().Provider;
        var control = (await provider.Documents.ReadControlAsync(file.ResourceId))!;

        await TestStorage.SetPermissionsAsync(provider, file.ResourceId, control.Head.Security with
        {
            Grants = control.Head.Security.Grants.SetItem(ReaderActor.Identity.Subject, DocumentAccess.Read)
        });
        await fixture.JoinReadOnlyAsync(fixture.Second, file.Path, readerClient);
        string readerTicket = await fixture.GetDocumentTicketAsync(fixture.Second, file.Path, "reader");

        control = (await provider.Documents.ReadControlAsync(file.ResourceId))!;
        await TestStorage.SetPermissionsAsync(provider, file.ResourceId, control.Head.Security with
        {
            Grants = control.Head.Security.Grants.Remove(ReaderActor.Identity.Subject)
        });
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.ConnectAsync(fixture.Second, readerTicket, readerClient));

        await using var sender = await fixture.ConnectAsync(fixture.First, senderTicket, senderClient);
        control = (await provider.Documents.ReadControlAsync(file.ResourceId))!;
        await TestStorage.SetPermissionsAsync(provider, file.ResourceId, control.Head.Security with
        {
            Grants = control.Head.Security.Grants.SetItem(ReaderActor.Identity.Subject, DocumentAccess.Read)
        });
        readerClient = Guid.NewGuid();
        await fixture.JoinReadOnlyAsync(fixture.Second, file.Path, readerClient);
        readerTicket = await fixture.GetDocumentTicketAsync(fixture.Second, file.Path, "reader");
        await using var reader = await fixture.ConnectAsync(fixture.Second, readerTicket, readerClient);
        var presenceReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Connection.On<Dictionary<string, string>, string[]>("presenceUpdated", (_, _) => presenceReceived.TrySetResult());
        await sender.Connection.InvokeAsync("UpdatePresence", $$"""{"A":"{{senderClient:B}}","access":"read"}""")
            .WaitAsync(TimeSpan.FromSeconds(10));
        await presenceReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var presence = await reader.Connection.InvokeAsync<Dictionary<string, string>>("GetCompletePresenceState")
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(senderClient.ToString("B").ToLowerInvariant(), presence.Keys);

        var writeFailure = await Record.ExceptionAsync(() =>
            reader.Connection.InvokeAsync("SendUpdateNotification", 3,
                $$"""{"A":"{{readerClient:D}}","id":1,"payload":"denied"}"""));
        Assert.True(writeFailure is Microsoft.AspNetCore.SignalR.HubException or TaskCanceledException,
            $"Read-only write unexpectedly completed or failed with {writeFailure?.GetType().Name ?? "no exception"}.");
        await reader.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Guid revokedClient = Guid.NewGuid();
        await fixture.JoinReadOnlyAsync(fixture.Second, file.Path, revokedClient);
        string revokedTicket = await fixture.GetDocumentTicketAsync(fixture.Second, file.Path, "reader");
        await using var revokedReader = await fixture.ConnectAsync(fixture.Second, revokedTicket, revokedClient);
        control = (await provider.Documents.ReadControlAsync(file.ResourceId))!;
        await TestStorage.SetPermissionsAsync(provider, file.ResourceId, control.Head.Security with
        {
            Grants = control.Head.Security.Grants.Remove(ReaderActor.Identity.Subject)
        });
        await revokedReader.Closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Subject_connection_limit_applies_across_rooms_and_hosts()
    {
        await using var fixture = await Fixture.StartAsync();
        var firstFile = await fixture.CreateDocumentAsync("/shared/subject-a.docx");
        var secondFile = await fixture.CreateDocumentAsync("/shared/subject-b.docx");
        var connections = new List<SignalRPeer>();
        try
        {
            for (int index = 0; index < 8; index++)
            {
                string document = index % 2 == 0 ? firstFile.Path : secondFile.Path;
                Host host = index % 2 == 0 ? fixture.First : fixture.Second;
                Guid client = await fixture.JoinCoauthoringAsync(document);
                string ticket = await fixture.GetDocumentTicketAsync(host, document);
                connections.Add(await fixture.ConnectAsync(host, ticket, client));
            }

            Guid rejectedClient = await fixture.JoinCoauthoringAsync(firstFile.Path);
            string rejectedTicket = await fixture.GetDocumentTicketAsync(fixture.Second, firstFile.Path);
            SignalRPeer? ninth = null;
            try { ninth = await fixture.ConnectAsync(fixture.Second, rejectedTicket, rejectedClient); }
            catch (Exception) { /* Hub admission closes the transport once the cluster subject limit is reached. */ }
            if (ninth is not null)
            {
                await ninth.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await ninth.DisposeAsync();
            }
            Assert.Equal(8, connections.Count);
        }
        finally
        {
            foreach (var connection in connections) await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task Bounded_load_compares_local_and_distributed_hot_and_multiple_rooms()
    {
        await using var fixture = await Fixture.StartAsync(distributedHosts: 3, includeLocalHost: true);
        var localHot = await fixture.CreateDocumentAsync("/shared/hot-local.docx");
        var oneHostHot = await fixture.CreateDocumentAsync("/shared/hot-one-distributed.docx");
        var twoHostHot = await fixture.CreateDocumentAsync("/shared/hot-two-distributed.docx");
        var threeHostHot = await fixture.CreateDocumentAsync("/shared/hot-three-distributed.docx");
        var roomA = await fixture.CreateDocumentAsync("/shared/room-a.docx");
        var roomB = await fixture.CreateDocumentAsync("/shared/room-b.docx");
        using var databaseActivities = new DatabaseActivityCounter();
        await Task.Delay(500);
        long idleDatabaseActivities = databaseActivities.Count;
        var idleScheduler = new[] { fixture.First, fixture.Second, fixture.Third }
            .Select(host => host.Scheduler.Diagnostics).ToArray();
        var runs = new List<LoadResult>();
        string? runFailure = null;
        ConcurrentBackendCounters? concurrentBackend = null;
        async Task<LoadResult> CaptureLoadAsync(string name, Func<Task<LoadResult>> action)
        {
            try { return await action(); }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref runFailure, error.GetType().Name, null);
                return LoadResult.Failure(name, error.GetType().Name);
            }
        }

        runs.Add(await CaptureLoadAsync("single-local-hot", () =>
            RunLoadAsync(fixture, databaseActivities, fixture.Local!, [fixture.Local!, fixture.Local!], localHot.Path, 80, "single-local-hot")));
        runs.Add(await CaptureLoadAsync("single-distributed-hot", () =>
            RunLoadAsync(fixture, databaseActivities, fixture.First, [fixture.First, fixture.First], oneHostHot.Path, 80, "single-distributed-hot")));
        runs.Add(await CaptureLoadAsync("distributed-two-host-hot", () =>
            RunLoadAsync(fixture, databaseActivities, fixture.First, [fixture.First, fixture.Second], twoHostHot.Path, 80, "distributed-two-host-hot")));
        runs.Add(await CaptureLoadAsync("distributed-three-host-hot", () =>
            RunLoadAsync(fixture, databaseActivities, fixture.First, [fixture.Second, fixture.Third], threeHostHot.Path, 80, "distributed-three-host-hot")));
        long concurrentDbStart = databaseActivities.Count;
        var concurrentRedisStart = fixture.RedisWorkCounts;
        var concurrent = await Task.WhenAll(
            CaptureLoadAsync("concurrent-room-a", () =>
                RunLoadAsync(fixture, databaseActivities, fixture.First, [fixture.Second], roomA.Path, 80, "concurrent-room-a", measureBackend: false)),
            CaptureLoadAsync("concurrent-room-b", () =>
                RunLoadAsync(fixture, databaseActivities, fixture.Second, [fixture.Third], roomB.Path, 80, "concurrent-room-b", measureBackend: false)));
        runs.AddRange(concurrent);
        var concurrentRedisEnd = fixture.RedisWorkCounts;
        concurrentBackend = new(databaseActivities.Count - concurrentDbStart,
            concurrentRedisEnd.Scripts - concurrentRedisStart.Scripts,
            concurrentRedisEnd.ArgumentBytes - concurrentRedisStart.ArgumentBytes);

        string artifact = Path.GetFullPath(Path.Combine(RepositoryRoot(), "artifacts/testing/multi-host-rtc",
            $"load-comparison-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, System.Text.Json.JsonSerializer.Serialize(new
        {
            runId = Path.GetFileNameWithoutExtension(artifact),
            generatedUtc = DateTimeOffset.UtcNow,
            framework = RuntimeInformation.FrameworkDescription,
            operatingSystem = RuntimeInformation.OSDescription,
            processorCount = Environment.ProcessorCount,
            environment = "Windows Kestrel instances in one test process; isolated Aspire PostgreSQL and Redis",
            office = "not launched; simulated SignalR protocol traffic",
            warmupOperationsPerScenario = 5,
            measuredOperationsPerScenario = 80,
            targetRatePerRoomHz = 20,
            runFailure,
            idleWindowMilliseconds = 500,
            idleNpgsqlActivities = idleDatabaseActivities,
            idleSchedulerDiagnostics = idleScheduler,
            concurrentBackend,
            scenarios = runs,
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(runs));
        Console.WriteLine("Artifact: " + artifact);

        Assert.Null(runFailure);
        Assert.All(runs, run =>
        {
            Assert.Equal(5, run.WarmupAccepted);
            Assert.Equal(80, run.Accepted);
            Assert.Equal(0, run.Rejected);
            Assert.Equal(0, run.Errors);
            Assert.Equal(80 * run.RecipientCount, run.Delivered);
            Assert.Equal(0, run.Missing);
            Assert.Equal(0, run.Duplicate);
        });
        Assert.All(idleScheduler, value =>
        {
            Assert.Equal(0, value.PollCalls);
            Assert.Equal(0, value.HeartbeatCalls);
            Assert.Equal(0, value.AuthorizationSweeps);
            Assert.Equal(0, value.OccupiedRooms);
        });
    }

    [Fact]
    public async Task Bounded_load_rotates_rates_and_exercises_more_rooms_than_dispatch_workers()
    {
        await using var fixture = await Fixture.StartAsync(distributedHosts: 3);
        using var databaseActivities = new DatabaseActivityCounter();
        var runs = new List<LoadResult>();
        string? runFailure = null;

        async Task CaptureAsync(string name, Func<Task<LoadResult>> action)
        {
            try { runs.Add(await action()); }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref runFailure, error.GetType().Name, null);
                runs.Add(LoadResult.Failure(name, error.GetType().Name));
            }
        }

        // Each topology and rate runs twice. Reverse the topology order in the
        // second pass to reduce fixed-order warmup and phase bias.
        for (int pass = 0; pass < 2; pass++)
        {
            int[] rates = pass == 0 ? [10, 40] : [40, 10];
            foreach (int rate in rates)
            {
                string[] topologies = pass == 0 ? ["one-host", "three-host"] : ["three-host", "one-host"];
                foreach (string topology in topologies)
                {
                    string path = $"/shared/rotated-{pass}-{rate}-{topology}.docx";
                    await fixture.CreateDocumentAsync(path, readerAccess: true);
                    Host[] recipients = topology == "one-host" ? [fixture.First, fixture.First] : [fixture.Second, fixture.Third];
                    string name = $"rotated-{pass}-{rate}hz-{topology}";
                    await CaptureAsync(name, () => RunLoadAsync(fixture, databaseActivities, fixture.First,
                        recipients, path, 30, name, targetHertz: rate, recipientActor: "reader"));
                }
            }
        }

        // Six active rooms exceed the four dispatch workers on the sender host.
        // Writer and reader tickets use separate subjects, each below the
        // namespace-wide eight-room subject limit.
        var rooms = new List<(string Path, Host Sender, Host Recipient)>();
        for (int index = 0; index < 6; index++)
        {
            string path = $"/shared/fairness-{index}.docx";
            await fixture.CreateDocumentAsync(path, readerAccess: true);
            rooms.Add((path, fixture.First, index % 2 == 0 ? fixture.Second : fixture.Third));
        }

        var beforeDiagnostics = new[] { fixture.First, fixture.Second, fixture.Third }
            .Select(host => host.Scheduler.Diagnostics).ToArray();
        long databaseStart = databaseActivities.Count;
        var redisStart = fixture.RedisWorkCounts;
        var fairRuns = await Task.WhenAll(rooms.Select(async (room, index) =>
        {
            string name = $"six-room-fairness-{index}";
            try
            {
                return await RunLoadAsync(fixture, databaseActivities, room.Sender, [room.Recipient],
                    room.Path, 24, name, measureBackend: false, targetHertz: 40, recipientActor: "reader");
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref runFailure, error.GetType().Name, null);
                return LoadResult.Failure(name, error.GetType().Name);
            }
        }));
        var redisEnd = fixture.RedisWorkCounts;
        var afterDiagnostics = new[] { fixture.First, fixture.Second, fixture.Third }
            .Select(host => host.Scheduler.Diagnostics).ToArray();

        string artifact = Path.GetFullPath(Path.Combine(RepositoryRoot(), "artifacts/testing/multi-host-rtc",
            $"load-rotated-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, System.Text.Json.JsonSerializer.Serialize(new
        {
            runId = Path.GetFileNameWithoutExtension(artifact),
            generatedUtc = DateTimeOffset.UtcNow,
            framework = RuntimeInformation.FrameworkDescription,
            operatingSystem = RuntimeInformation.OSDescription,
            processorCount = Environment.ProcessorCount,
            environment = "Windows Kestrel instances in one test process; isolated Aspire PostgreSQL and Redis",
            office = "not launched; simulated SOAP ticket issuance and SignalR traffic",
            design = "two rotated passes at 10Hz and 40Hz, then six concurrent rooms on three Kestrel instances",
            perScenarioWarmupOperations = 5,
            rotatedMeasuredOperationsPerScenario = 30,
            fairnessMeasuredOperationsPerRoom = 24,
            runFailure,
            fairnessWindowDatabaseActivitiesIncludingRoomSetupAndWarmup = databaseActivities.Count - databaseStart,
            fairnessWindowRedisScriptsIncludingRoomSetupAndWarmup = redisEnd.Scripts - redisStart.Scripts,
            fairnessWindowRedisArgumentBytesIncludingRoomSetupAndWarmup = redisEnd.ArgumentBytes - redisStart.ArgumentBytes,
            fairnessWindowPubSubHintsIncludingRoomSetupAndWarmup = afterDiagnostics.Sum(value => value.PubSubHintsReceived) - beforeDiagnostics.Sum(value => value.PubSubHintsReceived),
            fairnessWindowPubSubLogicalUtf8BytesIncludingRoomSetupAndWarmup = afterDiagnostics.Sum(value => value.PubSubHintBytesReceived) - beforeDiagnostics.Sum(value => value.PubSubHintBytesReceived),
            schedulerBefore = beforeDiagnostics,
            schedulerAfter = afterDiagnostics,
            rotatedScenarios = runs,
            sixRoomScenarios = fairRuns,
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { rotated = runs, sixRooms = fairRuns }));
        Console.WriteLine("Artifact: " + artifact);

        Assert.Null(runFailure);
        Assert.Equal(8, runs.Count);
        Assert.All(runs, run =>
        {
            Assert.Equal(5, run.WarmupAccepted);
            Assert.Equal(30, run.Accepted);
            Assert.Equal(0, run.Rejected);
            Assert.Equal(0, run.Errors);
            Assert.Equal(30 * run.RecipientCount, run.Delivered);
            Assert.Equal(0, run.Missing);
            Assert.Equal(0, run.Duplicate);
        });
        Assert.Equal(6, fairRuns.Length);
        Assert.All(fairRuns, run =>
        {
            Assert.Equal(5, run.WarmupAccepted);
            Assert.Equal(24, run.Accepted);
            Assert.Equal(0, run.Rejected);
            Assert.Equal(0, run.Errors);
            Assert.Equal(24, run.Delivered);
            Assert.Equal(0, run.Missing);
            Assert.Equal(0, run.Duplicate);
        });
    }

    private static async Task<LoadResult> RunLoadAsync(Fixture fixture, DatabaseActivityCounter databaseActivities,
        Host senderHost, Host[] recipientHosts, string path, int count, string scenario, bool measureBackend = true,
        int targetHertz = 20, string recipientActor = "writer")
    {
        Guid senderClient = await fixture.JoinCoauthoringAsync(path);
        Guid[] recipientClients = [];
        string senderTicket = await fixture.GetDocumentTicketAsync(senderHost, path);
        var recipientTickets = new List<string>();
        foreach (var host in recipientHosts)
        {
            Guid client;
            if (recipientActor == "reader")
            {
                client = Guid.NewGuid();
                await fixture.JoinReadOnlyAsync(host, path, client);
            }
            else client = await fixture.JoinCoauthoringAsync(path);
            recipientClients = [.. recipientClients, client];
            recipientTickets.Add(await fixture.GetDocumentTicketAsync(host, path, recipientActor));
        }
        await using var sender = await fixture.ConnectAsync(senderHost, senderTicket, senderClient);
        var recipients = new List<SignalRPeer>();
        foreach (int index in Enumerable.Range(0, recipientHosts.Length))
            recipients.Add(await fixture.ConnectAsync(recipientHosts[index], recipientTickets[index], recipientClients[index]));
        var starts = new ConcurrentDictionary<int, long>();
        var seen = new ConcurrentDictionary<(int Recipient, int Id), int>();
        var latencies = new ConcurrentBag<double>();
        int warmupDeliveries = 0;
        int measuredDeliveries = 0;
        var warmupComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var measuredComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUpdate(int recipient, string payload)
        {
            using var json = System.Text.Json.JsonDocument.Parse(payload);
            int id = json.RootElement.GetProperty("id").GetInt32();
            if (id < 0)
            {
                if (Interlocked.Increment(ref warmupDeliveries) == 5 * recipientHosts.Length) warmupComplete.TrySetResult();
                return;
            }
            if (starts.TryGetValue(id, out long start)) latencies.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            seen.AddOrUpdate((recipient, id), 1, (_, value) => value + 1);
            if (Interlocked.Increment(ref measuredDeliveries) >= count * recipientHosts.Length) measuredComplete.TrySetResult();
        }
        for (int index = 0; index < recipients.Count; index++)
        {
            int recipient = index;
            recipients[index].Connection.On<int, string>("updateNotification", (_, payload) => OnUpdate(recipient, payload));
        }
        int warmupAccepted = 0;
        int accepted = 0;
        int rejected = 0;
        int errorCount = 0;
        var errors = new HashSet<string>(StringComparer.Ordinal);
        long payloadBytesMin = long.MaxValue;
        long payloadBytesMax = 0;
        string Payload(int id) => $$"""{"A":"{{senderClient:D}}","id":{{id}},"p":"01234567890123456789012345678901"}""";
        for (int id = -5; id < 0; id++)
        {
            try
            {
                await sender.Connection.InvokeAsync("SendUpdateNotification", 3, Payload(id)).WaitAsync(TimeSpan.FromSeconds(5));
                warmupAccepted++;
            }
            catch (Exception error) { errorCount++; errors.Add(error.GetType().Name); }
        }
        await warmupComplete.Task.WaitAsync(TimeSpan.FromSeconds(15));
        TimeSpan period = TimeSpan.FromSeconds(1d / targetHertz);
        long databaseStart = databaseActivities.Count;
        var redisStart = fixture.RedisWorkCounts;
        using var benchmarkProcess = Process.GetCurrentProcess();
        benchmarkProcess.Refresh();
        TimeSpan processCpuStart = benchmarkProcess.TotalProcessorTime;
        long measurementStart = Stopwatch.GetTimestamp();
        for (int id = 0; id < count; id++)
        {
            var due = TimeSpan.FromTicks(period.Ticks * id);
            var elapsed = Stopwatch.GetElapsedTime(measurementStart);
            if (elapsed < due) await Task.Delay(due - elapsed);
            string payload = Payload(id);
            long bytes = Encoding.UTF8.GetByteCount(payload);
            payloadBytesMin = Math.Min(payloadBytesMin, bytes);
            payloadBytesMax = Math.Max(payloadBytesMax, bytes);
            starts[id] = Stopwatch.GetTimestamp();
            try
            {
                await sender.Connection.InvokeAsync("SendUpdateNotification", 3, payload).WaitAsync(TimeSpan.FromSeconds(5));
                accepted++;
            }
            catch (Exception error) { rejected++; errorCount++; errors.Add(error.GetType().Name); }
        }
        try { await measuredComplete.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException) { errorCount++; errors.Add(nameof(TimeoutException)); }
        double duration = Stopwatch.GetElapsedTime(measurementStart).TotalMilliseconds;
        long databaseOperations = measureBackend ? databaseActivities.Count - databaseStart : -1;
        var redisEnd = fixture.RedisWorkCounts;
        long redisScripts = measureBackend ? redisEnd.Scripts - redisStart.Scripts : -1;
        long redisArgumentBytes = measureBackend ? redisEnd.ArgumentBytes - redisStart.ArgumentBytes : -1;
        benchmarkProcess.Refresh();
        double processCpuMilliseconds = measureBackend
            ? (benchmarkProcess.TotalProcessorTime - processCpuStart).TotalMilliseconds : -1;
        long processWorkingSetBytes = measureBackend ? benchmarkProcess.WorkingSet64 : -1;
        double[] ordered = latencies.Order().ToArray();
        int uniqueDeliveries = seen.Count;
        int expectedDeliveries = accepted * recipientHosts.Length;
        int activeHosts = recipientHosts.Append(senderHost).Distinct().Count();
        var result = new LoadResult(scenario, activeHosts, recipientHosts.Length, warmupAccepted, accepted, rejected, errorCount,
            string.Join(',', errors), measuredDeliveries, uniqueDeliveries, Math.Max(0, expectedDeliveries - uniqueDeliveries),
            seen.Values.Sum(value => Math.Max(0, value - 1)), Math.Round(accepted / Math.Max(duration / 1000, .001), 2),
            Quantile(ordered, .50), Quantile(ordered, .95), Quantile(ordered, .99), Math.Round(duration, 2),
            targetHertz, payloadBytesMin == long.MaxValue ? 0 : payloadBytesMin, payloadBytesMax,
            databaseOperations, redisScripts, redisArgumentBytes, Math.Round(processCpuMilliseconds, 2),
            processWorkingSetBytes,
            measureBackend ? redisEnd.Hints - redisStart.Hints : -1,
            measureBackend ? redisEnd.HintBytes - redisStart.HintBytes : -1);
        foreach (var recipient in recipients) await recipient.DisposeAsync();
        return result;
    }

    private static double Quantile(double[] data, double percentile) => data.Length == 0 ? 0 :
        Math.Round(data[Math.Clamp((int)Math.Ceiling(data.Length * percentile) - 1, 0, data.Length - 1)], 3);

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CellBridge.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the CellBridge repository root for benchmark artifacts.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (!predicate())
        {
            if (deadline.Elapsed >= timeout) throw new TimeoutException("Condition was not reached before the test deadline.");
            await Task.Delay(50);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly IDistributedApplicationTestingBuilder builder;
        private readonly DistributedApplication application;
        private readonly string postgresConnection;
        private readonly string redisConnection;
        private readonly IDataProtectionProvider protection = DataProtectionProvider.Create("cellbridge-rtc-multi-host-tests");
        private readonly List<Host> hosts = [];
        private Host? local;
        private readonly MutableClock clock = new();
        private readonly string roomNamespace = "rtc-tests-" + Guid.NewGuid().ToString("N");
        private readonly ConcurrentDictionary<string, string> coauthorSchemas = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim coauthorGate = new(1, 1);
        private Guid resourceId;
        private string path = "/shared/multihost.docx";

        internal Host First => hosts[0];
        internal Host Second => hosts[1];
        internal Host Third => hosts[2];
        internal Host? Local => local;
        internal MutableClock Clock => clock;
        internal string RedisConnection => redisConnection;
        internal (long Scripts, long ArgumentBytes, long Hints, long HintBytes) RedisWorkCounts =>
            (hosts.Sum(host => host.Scheduler.Diagnostics.RedisScripts),
             hosts.Sum(host => host.Scheduler.Diagnostics.RedisArgumentBytes),
             hosts.Sum(host => host.Scheduler.Diagnostics.PubSubHintsReceived),
             hosts.Sum(host => host.Scheduler.Diagnostics.PubSubHintBytesReceived));

        private Fixture(IDistributedApplicationTestingBuilder builder, DistributedApplication application,
            string postgresConnection, string redisConnection)
        {
            this.builder = builder;
            this.application = application;
            this.postgresConnection = postgresConnection;
            this.redisConnection = redisConnection;
        }

        internal static async Task<Fixture> StartAsync(int distributedHosts = 2, bool includeLocalHost = false,
            bool faultableFirstHost = false)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var builder = await PostgreSqlEnvironment.CreateBuilderAsync(timeout.Token);
            DistributedApplication? application = null;
            try
            {
                builder.Resources.Remove(builder.Resources.Single(resource => resource.Name == "webdemo"));
                builder.AddContainer("rtc-redis", "redis", "7.4-alpine")
                    .WithArgs("--save", "", "--appendonly", "no", "--maxmemory-policy", "noeviction")
                    .WithEndpoint(targetPort: 6379, name: "tcp", scheme: "tcp");
                application = await builder.BuildAsync(timeout.Token);
                await application.StartAsync(timeout.Token);
                await application.ResourceNotifications.WaitForResourceAsync("storage-init", KnownResourceStates.Finished, timeout.Token);
                if (!application.ResourceNotifications.TryGetCurrentState("storage-init", out var initialized) ||
                    initialized.Snapshot.ExitCode != 0)
                    throw new InvalidOperationException("The isolated PostgreSQL schema initialization failed.");
                await application.ResourceNotifications.WaitForResourceAsync("rtc-redis", KnownResourceStates.Running, timeout.Token);
                string database = await application.GetConnectionStringAsync("cellbridge", timeout.Token)
                    ?? throw new InvalidOperationException("Aspire did not provide the isolated document database.");
                var endpoint = application.GetEndpoint("rtc-redis", "tcp");
                var redis = $"{endpoint.Host}:{endpoint.Port},abortConnect=false,allowAdmin=true";
                using (var redisProbe = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(redis))
                    await redisProbe.GetDatabase().PingAsync().WaitAsync(TimeSpan.FromSeconds(10));
                var result = new Fixture(builder, application, database, redis);
                for (int i = 0; i < distributedHosts; i++)
                    await result.StartHostAsync(distributed: true, faultable: faultableFirstHost && i == 0);
                if (includeLocalHost) result.local = await result.StartHostAsync(distributed: false);
                return result;
            }
            catch
            {
                try { if (application is not null) await application.DisposeAsync(); }
                finally { await builder.DisposeAsync(); }
                throw;
            }
        }

        private async Task<Host> StartHostAsync(bool distributed, bool faultable = false)
        {
            var dataSource = NpgsqlDataSource.Create(postgresConnection);
            var limits = new StorageLimits();
            var content = new PostgreSqlContentStore(dataSource, limits.MaxObjectBytes);
            var provider = new StorageProvider(new PostgreSqlStateStore(dataSource, limits, content), content, limits);
            var hostBuilder = WebApplication.CreateBuilder();
            hostBuilder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            hostBuilder.Logging.ClearProviders();
            TestActor.Register(hostBuilder.Services);
            hostBuilder.Services.AddSingleton<TimeProvider>(clock);
            hostBuilder.Services.AddSingleton(dataSource);
            hostBuilder.Services.AddSingleton(protection);
            hostBuilder.Services.AddDataProtection();
            hostBuilder.Services.AddCellBridge(provider, requireDurability: true, multipleInstances: distributed, configure: options =>
            {
                if (distributed)
                {
                    options.OfficeRealTimeRedisConnectionString = redisConnection;
                    options.OfficeRealTimeRedisNamespace = roomNamespace;
                }
                options.PublicOrigin = PublicOrigin;
            });
            if (faultable)
            {
                var registration = hostBuilder.Services.Last(service => service.ServiceType == typeof(IOfficeRealTimeBackplane));
                hostBuilder.Services.Remove(registration);
                hostBuilder.Services.AddSingleton<RedisOfficeRealTimeBackplane>();
                hostBuilder.Services.AddSingleton<FaultableBackplane>();
                hostBuilder.Services.AddSingleton<IOfficeRealTimeBackplane>(services => services.GetRequiredService<FaultableBackplane>());
            }
            var app = hostBuilder.Build();
            app.Use(async (context, next) =>
            {
                string user = context.Request.Headers["X-Test-User"].ToString();
                if (user == "writer")
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([
                        new(CellBridgeActor.SubjectClaim, TestActor.Value.Identity.Subject),
                        new(ClaimTypes.Name, TestActor.Value.Identity.Login),
                        new(CellBridgeActor.DisplayNameClaim, TestActor.Value.Identity.DisplayName),
                        new(CellBridgeActor.CreateClaim, "true")], "test"));
                else if (user == "reader")
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([
                        new(CellBridgeActor.SubjectClaim, ReaderActor.Identity.Subject),
                        new(ClaimTypes.Name, ReaderActor.Identity.Login),
                        new(CellBridgeActor.DisplayNameClaim, ReaderActor.Identity.DisplayName)], "test"));
                await next(context);
            });
            app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.MapCellBridge();
            await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var client = new HttpClient { BaseAddress = new Uri(address) };
            client.DefaultRequestHeaders.Add("X-Test-User", "writer");
            var scheduler = app.Services.GetService<DistributedOfficeRealTimeSessionStore>();
            var host = new Host(this, app, client, scheduler, dataSource, app.Services.GetService<FaultableBackplane>());
            if (distributed) hosts.Add(host);
            return host;
        }

        internal async Task ClearRedisAsync()
        {
            using var redis = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(redisConnection);
            await redis.GetServer(redis.GetEndPoints()[0]).FlushDatabaseAsync();
        }

        internal async Task<string> RedisRoomDiagnosticsAsync()
        {
            using var redis = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(redisConnection);
            var server = redis.GetServer(redis.GetEndPoints()[0]);
            var db = redis.GetDatabase();
            var results = new List<string>();
            foreach (var meta in server.Keys(pattern: "cellbridge:rtc:*:room:*:meta"))
            {
                string room = meta.ToString()[..^5];
                var sequence = await db.HashGetAsync(meta, "seq");
                var memberCount = await db.HashLengthAsync(room + ":members");
                var presenceCount = await db.HashLengthAsync(room + ":presence");
                var eventCount = await db.ListLengthAsync(room + ":events");
                results.Add($"seq={sequence};members={memberCount};presence={presenceCount};events={eventCount}");
            }
            return string.Join('|', results);
        }

        internal async Task<(Guid ResourceId, string Path)> CreateDocumentAsync(string? documentPath = null, bool readerAccess = false)
        {
            path = documentPath ?? path;
            var service = First.App.Services.GetRequiredService<CellBridgeDocumentService>();
            var head = await service.CreateAsync(path, MinimalDocx.Create(), TestActor.Value)
                ?? throw new InvalidOperationException("Could not create isolated test document.");
            if (readerAccess)
                await TestStorage.SetPermissionsAsync(service.Provider, head.ResourceId, head.Security with
                {
                    Grants = head.Security.Grants.SetItem(ReaderActor.Identity.Subject, DocumentAccess.Read)
                });
            resourceId = head.ResourceId;
            return (resourceId, path);
        }

        internal async Task<Guid> JoinCoauthoringAsync(string documentPath)
        {
            await coauthorGate.WaitAsync();
            try
            {
            Guid client = Guid.NewGuid();
            if (coauthorSchemas.TryGetValue(documentPath, out var sharedSchema))
            {
                await SoapAsync(First, documentPath, new XElement(Protocol + "SubRequest",
                    new XAttribute("Type", "Coauth"), new XAttribute("SubRequestToken", 1),
                    new XElement(Protocol + "SubRequestData", new XAttribute("CoauthRequestType", "JoinCoauthoring"),
                        new XAttribute("ClientID", client), new XAttribute("SchemaLockID", sharedSchema),
                        new XAttribute("Timeout", "3600"))));
                return client;
            }
            Guid exclusive = Guid.NewGuid();
            Guid schema = Guid.NewGuid();
            await SoapAsync(First, documentPath, new XElement(Protocol + "SubRequest",
                new XAttribute("Type", "ExclusiveLock"), new XAttribute("SubRequestToken", 1),
                new XElement(Protocol + "SubRequestData", new XAttribute("ExclusiveLockRequestType", "GetLock"),
                    new XAttribute("ExclusiveLockID", exclusive), new XAttribute("ClientID", client), new XAttribute("Timeout", "3600"))));
            await SoapAsync(First, documentPath, new XElement(Protocol + "SubRequest",
                new XAttribute("Type", "ExclusiveLock"), new XAttribute("SubRequestToken", 1),
                new XElement(Protocol + "SubRequestData", new XAttribute("ExclusiveLockRequestType", "ConvertToSchemaJoinCoauth"),
                    new XAttribute("ExclusiveLockID", exclusive), new XAttribute("SchemaLockID", schema),
                    new XAttribute("ClientID", client), new XAttribute("Timeout", "3600"))));
            Assert.True(coauthorSchemas.TryAdd(documentPath, schema.ToString("D")));
            return client;
            }
            finally { coauthorGate.Release(); }
        }

        internal async Task<string> GetDocumentTicketAsync(Host host, string documentPath, string actor = "writer")
        {
            var enumerate = await SoapAsync(host, documentPath, new XElement(Protocol + "SubRequest",
                new XAttribute("Type", "Properties"), new XAttribute("SubRequestToken", 1),
                new XElement(Protocol + "SubRequestData", new XAttribute("Properties", "PropertyEnumerate"))), actor);
            string[] ids = enumerate.Descendants().Where(element => element.Name.LocalName == "PropertyId")
                .Select(element => (string?)element.Attribute("id")).Where(id => id is not null).Cast<string>().ToArray();
            Assert.Contains("DocumentAccessToken", ids);
            var get = await SoapAsync(host, documentPath, new XElement(Protocol + "SubRequest",
                new XAttribute("Type", "Properties"), new XAttribute("SubRequestToken", 1),
                new XElement(Protocol + "SubRequestData", new XAttribute("Properties", "PropertyGet"),
                    new XElement(Protocol + "PropertyIds",
                    ids.Select(id => new XElement(Protocol + "PropertyId", new XAttribute("id", id)))))), actor);
            var token = get.Descendants().SingleOrDefault(element =>
                element.Name.LocalName == "PropertyValue" && (string?)element.Attribute("id") == "DocumentAccessToken");
            if (token is null)
            {
                string? error = (string?)get.Descendants(Protocol + "SubResponse").SingleOrDefault()?.Attribute("ErrorCode");
                string returnedIds = string.Join(",", get.Descendants().Where(element => element.Name.LocalName == "PropertyValue")
                    .Select(element => (string?)element.Attribute("id")));
                throw new InvalidOperationException($"Properties.PropertyGet returned no ticket; ErrorCode={error ?? "<none>"}, returned property ids=[{returnedIds}].");
            }
            return token
                .Attribute("value")!.Value;
        }

        internal async Task JoinReadOnlyAsync(Host host, string documentPath, Guid clientId)
        {
            await SoapAsync(host, documentPath, new XElement(Protocol + "SubRequest",
                new XAttribute("Type", "EditorsTable"), new XAttribute("SubRequestToken", 1),
                new XElement(Protocol + "SubRequestData", new XAttribute("EditorsTableRequestType", "JoinEditingSession"),
                    new XAttribute("ClientID", clientId), new XAttribute("Timeout", "3600"), new XAttribute("AsEditor", "false"))),
                "reader");
        }

        private static async Task<XDocument> SoapAsync(Host host, string documentPath, XElement subRequest,
            string actor = "writer")
        {
            var envelope = XNamespace.Get("http://schemas.xmlsoap.org/soap/envelope/");
            var body = new XElement(envelope + "Envelope", new XElement(envelope + "Body",
                new XElement(Protocol + "RequestVersion", new XAttribute("Version", 2), new XAttribute("MinorVersion", 2)),
                new XElement(Protocol + "RequestCollection", new XAttribute("CorrelationId", Guid.NewGuid()),
                    new XElement(Protocol + "Request", new XAttribute("Url", documentPath),
                        new XAttribute("RequestToken", 1), subRequest))));
            using var actorClient = actor == "writer" ? null : new HttpClient { BaseAddress = host.Http.BaseAddress };
            var client = actorClient ?? host.Http;
            if (actorClient is not null) actorClient.DefaultRequestHeaders.Add("X-Test-User", actor);
            using var response = await client.PostAsync("/_vti_bin/cellstorage.svc/CellStorageService",
                new StringContent(body.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml"));
            response.EnsureSuccessStatusCode();
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
            var failure = xml.Descendants(Protocol + "SubResponse").SingleOrDefault();
            if ((string?)failure?.Attribute("ErrorCode") is { Length: > 0 } error && error != "Success")
                throw new InvalidOperationException("Simulated protocol request failed: " + error);
            return xml;
        }

        internal async Task<SignalRPeer> ConnectAsync(Host host, string ticket, Guid clientId, bool utf8Json = false)
        {
            using var find = await host.Http.GetAsync("/_cellbridge/rtc/FindSession?qs=" + Uri.EscapeDataString(ticket) +
                "&ClientId=" + clientId.ToString("D"));
            find.EnsureSuccessStatusCode();
            string path = $"/_cellbridge/rtc/rtchub?qs={Uri.EscapeDataString(ticket)}&ClientId={clientId:D}";
            var uriBuilder = new UriBuilder(new Uri(host.Http.BaseAddress!, path)) { Scheme = "ws" };
            var connectionBuilder = new HubConnectionBuilder().WithUrl(uriBuilder.Uri);
            // Keep the Unicode regression's invocation within the existing
            // 64 KiB wire-message limit. Production transport limits stay unchanged.
            if (utf8Json) connectionBuilder.AddJsonProtocol(options => options.PayloadSerializerOptions.Encoder =
                System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping);
            var connection = connectionBuilder.Build();
            var closed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Closed += error => { closed.TrySetResult(error?.GetType().Name); return Task.CompletedTask; };
            await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            return new(connection, closed);
        }

        public async ValueTask DisposeAsync()
        {
            if (local is not null) await local.DisposeAsync();
            foreach (var host in hosts) await host.DisposeAsync();
            await application.DisposeAsync();
            await builder.DisposeAsync();
        }
    }

    private sealed class Host(Fixture fixture, WebApplication app, HttpClient http, DistributedOfficeRealTimeSessionStore? scheduler,
        NpgsqlDataSource dataSource, FaultableBackplane? faultableBackplane)
        : IAsyncDisposable
    {
        internal WebApplication App => app;
        internal Fixture Fixture => fixture;
        internal HttpClient Http => http;
        internal DistributedOfficeRealTimeSessionStore Scheduler => scheduler ?? throw new InvalidOperationException("Local RTC has no distributed scheduler.");
        internal FaultableBackplane? FaultableBackplane => faultableBackplane;
        public async ValueTask DisposeAsync() { http.Dispose(); await app.DisposeAsync(); await dataSource.DisposeAsync(); }
    }

    private sealed class FaultableBackplane(RedisOfficeRealTimeBackplane inner) : IOfficeRealTimeBackplane
    {
        internal bool SuppressRemovals { get; set; }
        public long Generation => inner.Generation;
        public bool IsHealthy => inner.IsHealthy;
        public Task<OfficeRealTimeBackplaneConnectResult> ConnectAsync(OfficeRealTimeBackplaneMember member, CancellationToken token) => inner.ConnectAsync(member, token);
        public Task<OfficeRealTimeBackplaneSnapshotResult> ReadSnapshotAsync(OfficeRealTimeRoomKey room, CancellationToken token) => inner.ReadSnapshotAsync(room, token);
        public Task<OfficeRealTimeBackplaneEventsResult> ReadEventsAsync(OfficeRealTimeRoomKey room, string epoch, long sequence, int events, int bytes, CancellationToken token) => inner.ReadEventsAsync(room, epoch, sequence, events, bytes, token);
        public Task<OfficeRealTimeBackplaneMembersResult> ObserveMembersAsync(OfficeRealTimeRoomKey room, CancellationToken token) => inner.ObserveMembersAsync(room, token);
        public Task<OfficeRealTimeBackplaneMutationResult> MutateAsync(OfficeRealTimeBackplaneMember member, string epoch, string eventId, string kind, string key, string method, string payload, CancellationToken token) => inner.MutateAsync(member, epoch, eventId, kind, key, method, payload, token);
        public Task<OfficeRealTimeBackplaneHeartbeatResult> HeartbeatAsync(IReadOnlyList<OfficeRealTimeBackplaneMember> members, CancellationToken token) => inner.HeartbeatAsync(members, token);
        public Task<OfficeRealTimeBackplaneRemoveResult> RemoveAsync(OfficeRealTimeBackplaneMember member, string? key, string eventId, CancellationToken token) =>
            SuppressRemovals ? Task.FromResult(new OfficeRealTimeBackplaneRemoveResult(OfficeRealTimeBackplaneStatus.Ok, null, null, 0)) :
                inner.RemoveAsync(member, key, eventId, token);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed record SignalRPeer(HubConnection Connection, TaskCompletionSource<string?> Closed) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
    }

    private sealed record LoadResult(string Scenario, int ActiveHosts, int RecipientCount, int WarmupAccepted, int Accepted,
        int Rejected, int Errors, string ErrorTypes, int Delivered, int UniqueDeliveries, int Missing, int Duplicate,
        double OperationsPerSecond, double P50Milliseconds, double P95Milliseconds, double P99Milliseconds,
        double DurationMilliseconds, int TargetRateHz, long PayloadBytesMinimum, long PayloadBytesMaximum,
        long DatabaseActivities, long RedisScripts, long RedisArgumentBytes, double ProcessCpuMilliseconds,
        long ProcessWorkingSetBytes, long PubSubHintsReceived = -1, long PubSubLogicalUtf8Bytes = -1)
    {
        internal static LoadResult Failure(string scenario, string errorType) => new(scenario, 0, 0, 0, 0, 0, 1,
            errorType, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -1, -1, -1, -1, -1);
    }

    private sealed record ConcurrentBackendCounters(long DatabaseActivities, long RedisScripts, long RedisArgumentBytes);

    private sealed class DatabaseActivityCounter : IDisposable
    {
        private readonly ActivityListener listener;
        private long count;
        internal long Count => Interlocked.Read(ref count);

        internal DatabaseActivityCounter()
        {
            listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Npgsql",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = _ => Interlocked.Increment(ref count),
            };
            ActivitySource.AddActivityListener(listener);
        }

        public void Dispose() => listener.Dispose();
    }

    private sealed class MutableClock : TimeProvider
    {
        private long offsetTicks;
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow().AddTicks(Interlocked.Read(ref offsetTicks));
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref offsetTicks, elapsed.Ticks);
    }
}
