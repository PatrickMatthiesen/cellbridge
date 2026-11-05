using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.FssHttp.Tests;

public sealed class OfficeClassicSignalRLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_reconnect_does_not_replace_the_active_socket(bool busy)
    {
        var sessions = new FakeSessionStore();
        sessions.ConnectResults.Enqueue(ConnectResult.Accepted);
        sessions.ConnectResults.Enqueue(busy ? ConnectResult.Busy : ConnectResult.Rejected);
        await using var fixture = await Fixture.Start(sessions);
        OfficeRealTimeTicket ticket = fixture.CreateTicket("reconnect");
        string connectionToken = await fixture.NegotiateAsync(ticket);
        var query = fixture.TransportQuery(ticket, connectionToken);

        using WebSocket first = await fixture.ConnectAsync("/connect", query);
        Assert.Equal(1, (await ReceiveJsonAsync(first)).GetProperty("S").GetInt32());
        OfficeRealTimeConnection firstConnection = Assert.Single(sessions.Connections);

        using WebSocket rejected = await fixture.ConnectAsync("/reconnect", query);
        JsonElement error = await ReceiveJsonAsync(rejected);
        Assert.True(error.TryGetProperty("E", out _));
        Assert.Equal(WebSocketMessageType.Close, (await ReceiveAsync(rejected)).Result.MessageType);
        Assert.Equal(WebSocketState.Open, first.State);

        await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test complete", CancellationToken.None);
        await sessions.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Contains(firstConnection.ConnectionId, sessions.DisconnectedIds);
        Assert.Equal(new[] { firstConnection.ConnectionId }, sessions.DisconnectedIds);
    }

    [Fact]
    public async Task Negotiation_slots_are_per_participant_and_expire_after_thirty_seconds()
    {
        await using var fixture = await Fixture.Start(new FakeSessionStore());
        OfficeRealTimeTicket ticket = fixture.CreateTicket("participant-a");

        HttpResponseMessage[] sameParticipant = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => fixture.NegotiateResponseAsync(ticket)));
        Assert.Equal(2, sameParticipant.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(2, sameParticipant.Count(response => response.StatusCode == HttpStatusCode.TooManyRequests));
        string unusedToken = await ReadConnectionTokenAsync(
            sameParticipant.First(response => response.StatusCode == HttpStatusCode.OK));

        // A participant cannot evade the subject quota by minting new ClientIds.
        string sameSubject = ticket.Actor.Identity.Subject;
        for (int index = 0; index < 14; index++)
        {
            OfficeRealTimeTicket anotherClient = fixture.CreateTicket("client-" + index, ticket.ResourceId,
                sameSubject, Guid.NewGuid());
            using var response = await fixture.NegotiateResponseAsync(anotherClient);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        OfficeRealTimeTicket overSubjectLimit = fixture.CreateTicket("client-over-limit", ticket.ResourceId,
            sameSubject, Guid.NewGuid());
        using (var blockedSubject = await fixture.NegotiateResponseAsync(overSubjectLimit))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, blockedSubject.StatusCode);
            Assert.Equal("30", Assert.Single(blockedSubject.Headers.GetValues("Retry-After")));
        }

        OfficeRealTimeTicket anotherParticipant = fixture.CreateTicket("participant-b", ticket.ResourceId);
        using (var separate = await fixture.NegotiateResponseAsync(anotherParticipant))
            Assert.Equal(HttpStatusCode.OK, separate.StatusCode);

        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        using (var expiredStart = await fixture.StartResponseAsync(ticket, unusedToken))
            Assert.Equal(HttpStatusCode.NotFound, expiredStart.StatusCode);
        using (var afterExpiry = await fixture.NegotiateResponseAsync(ticket))
            Assert.Equal(HttpStatusCode.OK, afterExpiry.StatusCode);
        foreach (HttpResponseMessage response in sameParticipant) response.Dispose();
    }

    [Fact]
    public async Task Active_negotiation_survives_thirty_seconds_then_disconnected_entry_expires()
    {
        var sessions = new FakeSessionStore();
        await using var fixture = await Fixture.Start(sessions);
        OfficeRealTimeTicket ticket = fixture.CreateTicket("active");
        string connectionToken = await fixture.NegotiateAsync(ticket);
        using WebSocket socket = await fixture.ConnectAsync("/connect", fixture.TransportQuery(ticket, connectionToken));
        Assert.Equal(1, (await ReceiveJsonAsync(socket)).GetProperty("S").GetInt32());

        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        using (var activeStart = await fixture.StartResponseAsync(ticket, connectionToken))
            Assert.Equal(HttpStatusCode.OK, activeStart.StatusCode);

        OfficeRealTimeConnection connection = Assert.Single(sessions.Connections);
        socket.Abort();
        await sessions.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(connection.ConnectionId, sessions.DisconnectedIds);
        using (var reconnectWindow = await fixture.StartResponseAsync(ticket, connectionToken))
            Assert.Equal(HttpStatusCode.OK, reconnectWindow.StatusCode);

        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        using (var expiredStart = await fixture.StartResponseAsync(ticket, connectionToken))
            Assert.Equal(HttpStatusCode.NotFound, expiredStart.StatusCode);
    }

    [Fact]
    public async Task Update_client_info_renews_the_ticket_without_replacing_the_socket()
    {
        var sessions = new FakeSessionStore();
        await using var fixture = await Fixture.Start(sessions);
        OfficeRealTimeTicket current = fixture.CreateTicket("ticket-renewal");
        string connectionToken = await fixture.NegotiateAsync(current);
        using WebSocket socket = await fixture.ConnectAsync("/connect", fixture.TransportQuery(current, connectionToken));
        Assert.Equal(1, (await ReceiveJsonAsync(socket)).GetProperty("S").GetInt32());

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        OfficeRealTimeTicket renewed = current with { ExpiresUtc = fixture.Clock.GetUtcNow().AddMinutes(20) };
        string querySignature = fixture.CreateRenewalSignature(renewed);
        string invocation = JsonSerializer.Serialize(new
        {
            H = "RealtimeChannelHub", M = "UpdateClientInfo", A = new[] { JsonSerializer.Serialize(new { qs = querySignature }) }, I = "renew-ticket",
        });
        await socket.SendAsync(Encoding.UTF8.GetBytes(invocation), WebSocketMessageType.Text, true, CancellationToken.None);
        JsonElement response = await ReceiveJsonAsync(socket);

        Assert.Equal("renew-ticket", response.GetProperty("I").GetString());
        Assert.False(response.TryGetProperty("E", out _));
        var renewal = Assert.Single(sessions.Renewals);
        Assert.Equal(current, renewal.Current);
        Assert.Equal(renewed, renewal.Renewed);
        Assert.Equal(Assert.Single(sessions.Connections).ConnectionId, renewal.ConnectionId);
        Assert.Empty(sessions.DisconnectedIds);
        Assert.Equal(WebSocketState.Open, socket.State);

        fixture.RegisterTicket(renewed);
        using var renewedStart = await fixture.StartResponseAsync(renewed, connectionToken);
        Assert.Equal(HttpStatusCode.OK, renewedStart.StatusCode);
        using var staleStart = await fixture.StartResponseAsync(current, connectionToken);
        Assert.Equal(HttpStatusCode.NotFound, staleStart.StatusCode);

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "finished", CancellationToken.None);
        await sessions.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(Assert.Single(sessions.Connections).ConnectionId, sessions.DisconnectedIds);
    }

    [Fact]
    public async Task Update_client_info_rejects_a_ticket_for_another_scope_or_expiry()
    {
        var sessions = new FakeSessionStore();
        await using var fixture = await Fixture.Start(sessions);
        OfficeRealTimeTicket current = fixture.CreateTicket("ticket-renewal-rejected");
        string connectionToken = await fixture.NegotiateAsync(current);
        using WebSocket socket = await fixture.ConnectAsync("/connect", fixture.TransportQuery(current, connectionToken));
        Assert.Equal(1, (await ReceiveJsonAsync(socket)).GetProperty("S").GetInt32());
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        OfficeRealTimeTicket renewed = current with { ExpiresUtc = fixture.Clock.GetUtcNow().AddMinutes(20) };

        var wrongSubject = renewed with
        {
            Actor = renewed.Actor with
            {
                Identity = new SubjectIdentity("other-subject", renewed.Actor.Identity.Login, renewed.Actor.Identity.DisplayName),
            },
        };
        var wrongAccessLimit = renewed with
        {
            Actor = renewed.Actor with { AccessLimit = new DocumentAccessLimit(renewed.ResourceId, DocumentAccess.Read) },
        };
        var wrongTickets = new[]
        {
            renewed with { ResourceId = Guid.NewGuid() },
            renewed with { LifecycleGeneration = renewed.LifecycleGeneration + 1 },
            wrongSubject,
            wrongAccessLimit,
        };
        foreach (OfficeRealTimeTicket wrongTicket in wrongTickets)
        {
            await SendUpdateClientInfoAsync(socket, fixture.CreateRenewalSignature(wrongTicket), "reject-scope");
            JsonElement rejected = await ReceiveJsonAsync(socket);
            Assert.Equal("reject-scope", rejected.GetProperty("I").GetString());
            Assert.True(rejected.TryGetProperty("E", out _));
        }

        await SendUpdateClientInfoAsync(socket,
            fixture.CreateRenewalSignature(renewed, renewed.ExpiresUtc.ToUnixTimeMilliseconds() - 1), "reject-ttl");
        JsonElement badTtl = await ReceiveJsonAsync(socket);
        Assert.Equal("reject-ttl", badTtl.GetProperty("I").GetString());
        Assert.True(badTtl.TryGetProperty("E", out _));
        Assert.Empty(sessions.Renewals);
        Assert.Empty(sessions.DisconnectedIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_keepalive_removes_session_and_starts_negotiation_expiry(bool unexpectedFailure)
    {
        var sessions = new FakeSessionStore();
        await using var fixture = await Fixture.Start(sessions);
        OfficeRealTimeTicket ticket = fixture.CreateTicket("failed-keepalive");
        string token = await fixture.NegotiateAsync(ticket);
        using var socket = new FailedKeepAliveSocket(unexpectedFailure);
        Task<IResult> connection = fixture.RunSocketAsync(ticket, token, socket);

        await socket.KeepAliveFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            await connection.WaitAsync(TimeSpan.FromSeconds(3));
        });

        Assert.Equal(Assert.Single(sessions.Connections).ConnectionId, Assert.Single(sessions.DisconnectedIds));
        if (unexpectedFailure) Assert.IsType<IOException>(failure);
        else Assert.Null(failure);

        using (var reconnectWindow = await fixture.StartResponseAsync(ticket, token))
            Assert.Equal(HttpStatusCode.OK, reconnectWindow.StatusCode);
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        using (var expired = await fixture.StartResponseAsync(ticket, token))
            Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
    }

    [Theory]
    [InlineData(2, 40 * 1024, false)]
    [InlineData(32, 64 * 1024, true)]
    public async Task Complete_presence_snapshot_can_exceed_the_incoming_message_limit(
        int participants, int payloadBytes, bool escapeHeavy)
    {
        var sessions = new FakeSessionStore();
        await using var fixture = await Fixture.Start(sessions);
        OfficeRealTimeTicket ticket = fixture.CreateTicket("presence-snapshot");
        string payload = "{\"text\":\"" + new string(escapeHeavy ? '<' : 'x', payloadBytes - 11) + "\"}";
        for (int index = 0; index < participants; index++)
            sessions.Presence[(index == 0 ? ticket.ClientId : Guid.NewGuid()).ToString("B")] = payload;
        string token = await fixture.NegotiateAsync(ticket);
        using WebSocket socket = await fixture.ConnectAsync("/connect", fixture.TransportQuery(ticket, token));
        Assert.Equal(1, (await ReceiveJsonAsync(socket)).GetProperty("S").GetInt32());

        await socket.SendAsync(Encoding.UTF8.GetBytes(
            """{"H":"RealtimeChannelHub","M":"GetCompletePresenceState","A":[],"I":"snapshot"}"""),
            WebSocketMessageType.Text, true, CancellationToken.None);
        JsonElement result = await ReceiveJsonAsync(socket);

        Assert.Equal("snapshot", result.GetProperty("I").GetString());
        var presence = result.GetProperty("R");
        Assert.Equal(participants, presence.EnumerateObject().Count());
        foreach (var entry in sessions.Presence)
            Assert.Equal(entry.Value, presence.GetProperty(entry.Key).GetString());

        await socket.SendAsync(Encoding.UTF8.GetBytes(
            """{"H":"RealtimeChannelHub","M":"UpdatePresence","A":["{}"],"I":"still-connected"}"""),
            WebSocketMessageType.Text, true, CancellationToken.None);
        Assert.Equal("still-connected", (await ReceiveJsonAsync(socket)).GetProperty("I").GetString());
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test complete", CancellationToken.None);
        await sessions.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task<JsonElement> ReceiveJsonAsync(WebSocket socket)
    {
        var (result, buffer) = await ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        using JsonDocument json = JsonDocument.Parse(buffer);
        return json.RootElement.Clone();
    }

    private static Task SendUpdateClientInfoAsync(WebSocket socket, string signature, string invocationId)
    {
        string invocation = JsonSerializer.Serialize(new
        {
            H = "RealtimeChannelHub", M = "UpdateClientInfo", A = new[] { JsonSerializer.Serialize(new { qs = signature }) }, I = invocationId,
        });
        return socket.SendAsync(Encoding.UTF8.GetBytes(invocation), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<(WebSocketReceiveResult Result, byte[] Buffer)> ReceiveAsync(WebSocket socket)
    {
        byte[] buffer = new byte[4096];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (result, message.ToArray());
    }

    private static async Task<string> ReadConnectionTokenAsync(HttpResponseMessage response)
    {
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("ConnectionToken").GetString()!;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentDictionary<string, OfficeRealTimeTicket> _tickets = new(StringComparer.Ordinal);
        private HttpClient? _client;
        private OfficeClassicSignalR _classic = null!;

        private Fixture(WebApplication app, FakeSessionStore sessions, ManualClock clock)
        {
            _app = app;
            Clock = clock;
        }

        internal ManualClock Clock { get; }

        internal static async Task<Fixture> Start(FakeSessionStore sessions)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<TimeProvider>(new ManualClock());
            builder.Services.AddSingleton(new CellBridgeOptions());
            builder.Services.AddDataProtection();
            builder.Services.AddSingleton<IOfficeRealTimeAuthorizer>(new AllowAllAuthorizer());
            var app = builder.Build();
            var clock = (ManualClock)app.Services.GetRequiredService<TimeProvider>();
            var fixture = new Fixture(app, sessions, clock);
            OfficeRealTimeAccess access = new(null!, app.Services.GetRequiredService<IDataProtectionProvider>(),
                clock, null!);
            OfficeClassicSignalR classic = new(sessions, app.Services.GetRequiredService<IOfficeRealTimeAuthorizer>(),
                access, clock, app.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>());
            fixture._classic = classic;
            app.UseWebSockets();
            app.MapGet("/negotiate", async context =>
            {
                if (!fixture.TrySetTicket(context)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
                await (await classic.NegotiateAsync(context)).ExecuteAsync(context);
            });
            app.MapGet("/connect", async context =>
            {
                if (!fixture.TrySetTicket(context)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
                await (await classic.ConnectAsync(context)).ExecuteAsync(context);
            });
            app.MapGet("/reconnect", async context =>
            {
                if (!fixture.TrySetTicket(context)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
                await (await classic.ReconnectAsync(context)).ExecuteAsync(context);
            });
            app.MapGet("/start", async context =>
            {
                if (!fixture.TrySetTicket(context)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
                await classic.Start(context).ExecuteAsync(context);
            });
            await app.StartAsync();
            fixture._client = app.GetTestClient();
            return fixture;
        }

        internal OfficeRealTimeTicket CreateTicket(string participant, Guid? resourceId = null,
            string? subjectOverride = null, Guid? clientIdOverride = null)
        {
            Guid clientId = clientIdOverride ?? Guid.NewGuid();
            string subject = subjectOverride ?? "test-user:" + participant;
            var ticket = new OfficeRealTimeTicket(resourceId ?? Guid.NewGuid(), 1,
                new CellBridgeActor(new SubjectIdentity(subject, subject, participant)), clientId,
                Clock.GetUtcNow().AddMinutes(20));
            _tickets[Signature(ticket)] = ticket;
            return ticket;
        }

        internal void RegisterTicket(OfficeRealTimeTicket ticket) => _tickets[Signature(ticket)] = ticket;

        internal string CreateRenewalSignature(OfficeRealTimeTicket ticket, long? ttlOverride = null)
        {
            const string publicBase = "http://localhost";
            string source = $"{publicBase}/shared/renewal-{ticket.ResourceId:N}.docx";
            var data = new
            {
                ResourceId = ticket.ResourceId,
                LifecycleGeneration = ticket.LifecycleGeneration,
                Identity = ticket.Actor.Identity,
                AccessLimit = ticket.Actor.AccessLimit,
                ExpiresUnixMilliseconds = ticket.ExpiresUtc.ToUnixTimeMilliseconds(),
                Source = source,
            };
            string protectedToken = _app.Services.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("CellBridge.OfficeRealTime.DocumentAccess.v1", publicBase)
                .Protect(JsonSerializer.Serialize(data));
            return QueryString.Create(new Dictionary<string, string?>
            {
                ["WopiSrc"] = source,
                ["access_token"] = protectedToken,
                ["access_token_ttl"] = (ttlOverride ?? ticket.ExpiresUtc.ToUnixTimeMilliseconds())
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
            }).Value![1..];
        }

        internal async Task<string> NegotiateAsync(OfficeRealTimeTicket ticket)
        {
            using HttpResponseMessage response = await NegotiateResponseAsync(ticket);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await ReadConnectionTokenAsync(response);
        }

        internal Task<HttpResponseMessage> NegotiateResponseAsync(OfficeRealTimeTicket ticket) =>
            (_client ?? throw new InvalidOperationException("The test fixture has not started."))
                .GetAsync(QueryHelpers.AddQueryString("/negotiate", TicketQuery(ticket)));

        internal Task<HttpResponseMessage> StartResponseAsync(OfficeRealTimeTicket ticket, string connectionToken)
        {
            var query = TicketQuery(ticket);
            query["connectionToken"] = connectionToken;
            return (_client ?? throw new InvalidOperationException("The test fixture has not started."))
                .GetAsync(QueryHelpers.AddQueryString("/start", query));
        }

        internal Dictionary<string, string?> TransportQuery(OfficeRealTimeTicket ticket, string connectionToken)
        {
            var query = TicketQuery(ticket);
            query["transport"] = "webSockets";
            query["connectionToken"] = connectionToken;
            return query;
        }

        internal async Task<WebSocket> ConnectAsync(string path, Dictionary<string, string?> query)
        {
            var client = _app.GetTestServer().CreateWebSocketClient();
            return await client.ConnectAsync(new Uri("ws://localhost" + QueryHelpers.AddQueryString(path, query)),
                CancellationToken.None);
        }

        internal Task<IResult> RunSocketAsync(OfficeRealTimeTicket ticket, string token, WebSocket socket)
        {
            var context = new DefaultHttpContext { RequestServices = _app.Services };
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("localhost");
            context.Request.QueryString = new QueryString(QueryHelpers.AddQueryString("", TransportQuery(ticket, token)));
            context.Items[OfficeRealTimeRequest.TicketKey] = ticket;
            context.Features.Set<IHttpWebSocketFeature>(new SocketFeature(socket));
            return _classic.ConnectAsync(context);
        }

        private Dictionary<string, string?> TicketQuery(OfficeRealTimeTicket ticket) => new()
        {
            ["clientProtocol"] = "1.4",
            ["connectionData"] = "[{\"name\":\"realtimechannelhub\"}]",
            ["qs"] = Signature(ticket),
            ["ClientId"] = ticket.ClientId.ToString("D"),
        };

        private bool TrySetTicket(HttpContext context)
        {
            string? signature = context.Request.Query["qs"].ToString();
            if (!_tickets.TryGetValue(signature, out OfficeRealTimeTicket? ticket)) return false;
            context.Items[OfficeRealTimeRequest.TicketKey] = ticket;
            return true;
        }

        private static string Signature(OfficeRealTimeTicket ticket) =>
            $"{ticket.ResourceId:N}:{ticket.LifecycleGeneration}:{ticket.Actor.Identity.Subject}:{ticket.ClientId:N}:{ticket.ExpiresUtc.ToUnixTimeMilliseconds()}";

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            _client?.Dispose();
            await _app.DisposeAsync();
        }
    }

    private enum ConnectResult { Accepted, Rejected, Busy }

    private sealed class SocketFeature(WebSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(socket);
    }

    private sealed class FailedKeepAliveSocket(bool unexpectedFailure) : WebSocket
    {
        private readonly TaskCompletionSource<WebSocketReceiveResult> _receive = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource KeepAliveFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() => _receive.TrySetResult(new(0, WebSocketMessageType.Close, true));
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            _receive.Task.WaitAsync(cancellationToken);
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken)
        {
            if (!buffer.AsSpan().SequenceEqual("{}"u8)) return Task.CompletedTask;
            _receive.TrySetResult(new(0, WebSocketMessageType.Close, true));
            KeepAliveFailed.TrySetResult();
            return Task.FromException(unexpectedFailure
                ? new IOException("Injected unexpected keepalive failure.")
                : new WebSocketException("Injected keepalive transport failure."));
        }
    }

    private sealed class FakeSessionStore : IOfficeRealTimeSessionStore
    {
        internal ConcurrentQueue<ConnectResult> ConnectResults { get; } = new();
        internal ConcurrentQueue<OfficeRealTimeConnection> Connections { get; } = new();
        internal ConcurrentQueue<string> DisconnectedIds { get; } = new();
        internal ConcurrentQueue<(OfficeRealTimeTicket Current, OfficeRealTimeTicket Renewed, string ConnectionId)> Renewals { get; } = new();
        internal TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Dictionary<string, string> Presence { get; } = [];

        public Task<bool> ConnectAsync(OfficeRealTimeTicket ticket, OfficeRealTimeConnection connection,
            CancellationToken cancellationToken)
        {
            Connections.Enqueue(connection);
            var result = ConnectResults.TryDequeue(out ConnectResult queued) ? queued : ConnectResult.Accepted;
            return result switch
            {
                ConnectResult.Accepted => Task.FromResult(true),
                ConnectResult.Rejected => Task.FromResult(false),
                ConnectResult.Busy => Task.FromException<bool>(new OfficeRealTimeBusyException()),
                _ => throw new InvalidOperationException(),
            };
        }

        public Task DisconnectAsync(string connectionId, CancellationToken cancellationToken)
        {
            DisconnectedIds.Enqueue(connectionId);
            Disconnected.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<bool> RenewTicketAsync(OfficeRealTimeTicket currentTicket, OfficeRealTimeTicket renewedTicket,
            string connectionId, CancellationToken cancellationToken)
        {
            Renewals.Enqueue((currentTicket, renewedTicket, connectionId));
            return Task.FromResult(true);
        }

        public Task<Dictionary<string, string>> GetCompletePresenceStateAsync(OfficeRealTimeTicket ticket,
            string connectionId, CancellationToken cancellationToken) => Task.FromResult(new Dictionary<string, string>(Presence));

        public Task<bool> UpdatePresenceAsync(OfficeRealTimeTicket ticket, string connectionId, string payload,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> SendUpdateNotificationAsync(OfficeRealTimeTicket ticket, string connectionId, int channel,
            string payload, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class AllowAllAuthorizer : IOfficeRealTimeAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(OfficeRealTimeTicket ticket, DocumentAccess requiredAccess,
            bool requireOwnedClient, CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public ValueTask<bool[]> AuthorizeTogetherAsync(IReadOnlyList<OfficeRealTimeTicketCheck> checks,
            CancellationToken cancellationToken) => ValueTask.FromResult(checks.Select(_ => true).ToArray());
    }

    internal sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-11-02T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
