using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Builder;
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

    private static async Task<JsonElement> ReceiveJsonAsync(WebSocket socket)
    {
        var (result, buffer) = await ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        using JsonDocument json = JsonDocument.Parse(buffer);
        return json.RootElement.Clone();
    }

    private static async Task<(WebSocketReceiveResult Result, byte[] Buffer)> ReceiveAsync(WebSocket socket)
    {
        byte[] buffer = new byte[4096];
        WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        return (result, buffer.AsMemory(0, result.Count).ToArray());
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
            builder.Services.AddSingleton<IOfficeRealTimeAuthorizer>(new AllowAllAuthorizer());
            var app = builder.Build();
            var clock = (ManualClock)app.Services.GetRequiredService<TimeProvider>();
            var fixture = new Fixture(app, sessions, clock);
            OfficeClassicSignalR classic = new(sessions, app.Services.GetRequiredService<IOfficeRealTimeAuthorizer>(),
                clock, app.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>());
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
            $"{ticket.ResourceId:N}:{ticket.LifecycleGeneration}:{ticket.Actor.Identity.Subject}:{ticket.ClientId:N}";

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
        internal TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        public Task<Dictionary<string, string>> GetCompletePresenceStateAsync(OfficeRealTimeTicket ticket,
            string connectionId, CancellationToken cancellationToken) => Task.FromResult(new Dictionary<string, string>());

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
