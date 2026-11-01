using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;

namespace CellBridge.AspNetCore;

/// <summary>Implements the Office-used ASP.NET SignalR 1.4 WebSocket subset over the shared RTC sessions.</summary>
internal sealed class OfficeClassicSignalR(IOfficeRealTimeSessionStore sessions,
    IOfficeRealTimeAuthorizer authorizer, TimeProvider time, IHostApplicationLifetime lifetime)
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(30);
    private const int MaximumMessageBytes = 64 * 1024;
    private const int MaximumNegotiationsPerParticipant = 2;
    private const int MaximumNegotiationsPerSubject = 16;
    private readonly object _negotiationLock = new();
    private readonly ConcurrentDictionary<string, PendingConnection> _pending = new(StringComparer.Ordinal);

    internal async Task<IResult> NegotiateAsync(HttpContext context)
    {
        if (!IsSupportedRequest(context, out var ticket, out var hubName) ||
            !await authorizer.AuthorizeAsync(ticket!, DocumentAccess.Read, true, context.RequestAborted).ConfigureAwait(false) ||
            OfficeRealTimeAccess.PublicBase(context) is not { } publicBase)
            return Results.NotFound();

        string connectionId = Guid.NewGuid().ToString("N");
        string connectionToken = WebEncoders.Base64UrlEncode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        lock (_negotiationLock)
        {
            RemoveExpiredTickets();
            if (_pending.Values.Count(entry => SameParticipant(entry.Ticket, ticket!)) >= MaximumNegotiationsPerParticipant ||
                _pending.Values.Count(entry => StringComparer.Ordinal.Equals(entry.Ticket.Actor.Identity.Subject,
                    ticket!.Actor.Identity.Subject)) >= MaximumNegotiationsPerSubject)
            {
                context.Response.Headers.RetryAfter = "30";
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }
            if (_pending.Count >= 2048) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var pending = new PendingConnection(ticket!, hubName!, connectionId, sessions, time);
            if (!_pending.TryAdd(connectionToken, pending)) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        return Results.Text(JsonSerializer.Serialize(new
        {
            Url = publicBase + "/_cellbridge/rtc/signalr",
            ConnectionToken = connectionToken,
            ConnectionId = connectionId,
            KeepAliveTimeout = KeepAliveInterval.TotalSeconds * 2,
            DisconnectTimeout = DisconnectTimeout.TotalSeconds,
            ConnectionTimeout = 110.0,
            TryWebSockets = true,
            ProtocolVersion = "1.4",
            TransportConnectTimeout = 5.0,
            LongPollDelay = 0.0,
        }), "application/json; charset=utf-8");
    }

    internal async Task<IResult> ConnectAsync(HttpContext context) =>
        await RunWebSocketAsync(context).ConfigureAwait(false);

    internal async Task<IResult> ReconnectAsync(HttpContext context) =>
        await RunWebSocketAsync(context).ConfigureAwait(false);

    internal IResult Start(HttpContext context)
    {
        if (!TryGetPending(context, out _, out _)) return Results.NotFound();
        return Results.Text(JsonSerializer.Serialize(new { Response = "started" }), "application/json; charset=utf-8");
    }

    internal async Task<IResult> AbortAsync(HttpContext context)
    {
        if (!TryGetPending(context, out PendingConnection? pending, out string? token)) return Results.NotFound();
        await pending!.Gate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            await pending.DisconnectAsync().ConfigureAwait(false);
            _pending.TryRemove(token!, out _);
            return Results.Ok();
        }
        finally { pending.Gate.Release(); }
    }

    private async Task<IResult> RunWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest ||
            !context.Request.Query.TryGetValue("transport", out var transport) ||
            transport.Count != 1 || !StringComparer.OrdinalIgnoreCase.Equals(transport[0], "webSockets") ||
            !TryGetPending(context, out PendingConnection? pending, out string? token))
            return Results.BadRequest();

        if (!TryTicket(context, out var ticket) || !SameTicket(pending!.Ticket, ticket!))
            return Results.NotFound();

        await pending.Gate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        bool gateHeld = true;
        try
        {
            if (pending.ExpiresUtc <= time.GetUtcNow() ||
                !_pending.TryGetValue(token!, out var registered) || !ReferenceEquals(registered, pending))
                return Results.NotFound();
            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            string cursor = "d-" + Guid.NewGuid().ToString("N") + "|i=0";
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
            using var sender = new ClassicSocketSender(socket, pending.HubName, cursor);
            string sessionConnectionId = pending.GetNextSessionConnectionId();
            var connection = new OfficeRealTimeConnection(sessionConnectionId, stopping.Token,
                sender.SendAsync, socket.Abort);

            Task? keepAlive = null;
            try
            {
                bool connected;
                try { connected = await sessions.ConnectAsync(pending.Ticket, connection, stopping.Token).ConfigureAwait(false); }
                catch (OfficeRealTimeBusyException)
                {
                    await sender.SendErrorAsync("The collaboration service is busy. Retry the connection.", stopping.Token).ConfigureAwait(false);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Retry later", CancellationToken.None).ConfigureAwait(false);
                    return Results.Empty;
                }
                if (!connected)
                {
                    await sender.SendErrorAsync("The collaboration session is not authorized.", stopping.Token).ConfigureAwait(false);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Not authorized", CancellationToken.None).ConfigureAwait(false);
                    return Results.Empty;
                }

                pending.Activate(connection);
                await sender.SendInitializationAsync(stopping.Token).ConfigureAwait(false);
                pending.Gate.Release();
                gateHeld = false;
                keepAlive = SendKeepAlivesAsync(sender, stopping.Token);
                await ReadMessagesAsync(socket, pending.Ticket, connection.ConnectionId, sender, stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (WebSocketException) { }
            catch (JsonException)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid SignalR message", CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Unsupported SignalR message", CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                stopping.Cancel();
                try { if (keepAlive is not null) await keepAlive.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                if (gateHeld) { pending.Gate.Release(); gateHeld = false; }
                await pending.DeactivateAsync(connection).ConfigureAwait(false);
            }
            return Results.Empty;
        }
        finally { if (gateHeld) pending.Gate.Release(); }
    }

    private async Task ReadMessagesAsync(WebSocket socket, OfficeRealTimeTicket ticket, string connectionId,
        ClassicSocketSender sender, CancellationToken cancellationToken)
    {
        byte[] chunk = new byte[8192];
        var buffered = new ArrayBufferWriter<byte>();
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult receive = await socket.ReceiveAsync(new ArraySegment<byte>(chunk), cancellationToken).ConfigureAwait(false);
            if (receive.MessageType == WebSocketMessageType.Close) return;
            if (receive.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("SignalR messages must use text frames.");
            for (int index = 0; index < receive.Count; index++)
            {
                if (buffered.WrittenCount >= MaximumMessageBytes)
                    throw new InvalidDataException("SignalR message exceeds the configured limit.");
                buffered.GetSpan(1)[0] = chunk[index];
                buffered.Advance(1);
            }
            if (!receive.EndOfMessage) continue;
            if (buffered.WrittenCount == 0) throw new InvalidDataException("Empty SignalR WebSocket messages are not supported.");
            await InvokeAsync(buffered.WrittenMemory.ToArray(), ticket, connectionId, sender, cancellationToken).ConfigureAwait(false);
            buffered = new ArrayBufferWriter<byte>();
        }
    }

    private async Task InvokeAsync(byte[] bytes, OfficeRealTimeTicket ticket, string connectionId,
        ClassicSocketSender sender, CancellationToken cancellationToken)
    {
        if (bytes.Length == 0) return;
        using JsonDocument message = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        JsonElement root = message.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("H", out JsonElement hubValue) || hubValue.ValueKind != JsonValueKind.String ||
            !StringComparer.OrdinalIgnoreCase.Equals(hubValue.GetString(), "realtimechannelhub") ||
            !root.TryGetProperty("M", out JsonElement methodValue) ||
            methodValue.ValueKind != JsonValueKind.String || !root.TryGetProperty("A", out JsonElement arguments) ||
            arguments.ValueKind != JsonValueKind.Array || !root.TryGetProperty("I", out JsonElement invocationId) ||
            invocationId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number) ||
            (invocationId.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(invocationId.GetString())))
            throw new InvalidDataException("Unsupported Office collaboration invocation.");
        string method = methodValue.GetString()!;
        object? result;
        try
        {
            result = method switch
            {
                "GetCompletePresenceState" when arguments.GetArrayLength() == 0 =>
                    await sessions.GetCompletePresenceStateAsync(ticket, connectionId, cancellationToken).ConfigureAwait(false),
                "UpdatePresence" when arguments.GetArrayLength() == 1 && arguments[0].ValueKind == JsonValueKind.String =>
                    await UpdatePresenceAsync(ticket, connectionId, arguments[0].GetString()!, cancellationToken).ConfigureAwait(false),
                "SendUpdateNotification" when arguments.GetArrayLength() == 2 && arguments[0].TryGetInt32(out int channel) &&
                    arguments[1].ValueKind == JsonValueKind.String =>
                    await SendNotificationAsync(ticket, connectionId, channel, arguments[1].GetString()!, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidDataException("Unsupported Office collaboration invocation."),
            };
            if (result is null) await sender.SendVoidResultAsync(invocationId, cancellationToken).ConfigureAwait(false);
            else await sender.SendResultAsync(invocationId, result, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            await sender.SendFailureAsync(invocationId, "The collaboration request was rejected.", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OfficeRealTimeBusyException or HubException)
        {
            await sender.SendFailureAsync(invocationId, "The collaboration request could not be completed.", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<object?> UpdatePresenceAsync(OfficeRealTimeTicket ticket, string connectionId,
        string payload, CancellationToken cancellationToken)
    {
        if (!await sessions.UpdatePresenceAsync(ticket, connectionId, payload, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Invalid presence update.");
        return null;
    }

    private async Task<object?> SendNotificationAsync(OfficeRealTimeTicket ticket, string connectionId,
        int channel, string payload, CancellationToken cancellationToken)
    {
        if (!await sessions.SendUpdateNotificationAsync(ticket, connectionId, channel, payload, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Invalid notification.");
        return null;
    }

    private async Task SendKeepAlivesAsync(ClassicSocketSender sender, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(KeepAliveInterval, time);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await sender.SendKeepAliveAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool TryGetPending(HttpContext context, out PendingConnection? pending, out string? token)
    {
        pending = null;
        token = null;
        if (!context.Request.Query.TryGetValue("connectionToken", out var tokens) || tokens.Count != 1 ||
            tokens[0] is not { Length: > 0 and <= 128 } value || !_pending.TryGetValue(value, out pending) ||
            pending.ExpiresUtc <= time.GetUtcNow() || !TryTicket(context, out var ticket) || !SameTicket(pending.Ticket, ticket!))
            return false;
        token = value;
        return true;
    }

    private static bool IsSupportedRequest(HttpContext context, out OfficeRealTimeTicket? ticket, out string? hubName)
    {
        hubName = null;
        if (!TryTicket(context, out ticket) || ticket is null ||
            !context.Request.Query.TryGetValue("clientProtocol", out var protocol) || protocol.Count != 1 || protocol[0] != "1.4" ||
            !context.Request.Query.TryGetValue("connectionData", out var connectionData) || connectionData.Count != 1 ||
            connectionData[0] is not { Length: > 0 and <= 1024 }) return false;
        try
        {
            using JsonDocument data = JsonDocument.Parse(connectionData[0]!);
            if (data.RootElement.ValueKind != JsonValueKind.Array || data.RootElement.GetArrayLength() != 1) return false;
            JsonElement hub = data.RootElement[0];
            if (hub.ValueKind != JsonValueKind.Object) return false;
            var names = hub.EnumerateObject().Where(property =>
                property.Name.Equals("name", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (names.Length != 1) return false;
            JsonElement name = names[0].Value;
            if (name.ValueKind != JsonValueKind.String || !StringComparer.OrdinalIgnoreCase.Equals(name.GetString(), "realtimechannelhub"))
                return false;
            hubName = name.GetString()!.ToLowerInvariant();
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryTicket(HttpContext context, out OfficeRealTimeTicket? ticket)
    {
        ticket = context.Items.TryGetValue(OfficeRealTimeRequest.TicketKey, out object? value)
            ? value as OfficeRealTimeTicket : null;
        return ticket is not null;
    }

    private static bool SameParticipant(OfficeRealTimeTicket left, OfficeRealTimeTicket right) =>
        left.ResourceId == right.ResourceId && left.LifecycleGeneration == right.LifecycleGeneration &&
        left.ClientId == right.ClientId &&
        StringComparer.Ordinal.Equals(left.Actor.Identity.Subject, right.Actor.Identity.Subject);

    private static bool SameTicket(OfficeRealTimeTicket left, OfficeRealTimeTicket right) =>
        SameParticipant(left, right) && left.ExpiresUtc == right.ExpiresUtc;

    private void RemoveExpiredTickets()
    {
        DateTimeOffset now = time.GetUtcNow();
        foreach (var entry in _pending)
        {
            if (entry.Value.ExpiresUtc > now || !entry.Value.Gate.Wait(0)) continue;
            try
            {
                // An admission already in progress can activate this entry.
                // Recheck under its gate before retiring the negotiation token.
                if (entry.Value.ExpiresUtc <= now) _pending.TryRemove(entry.Key, out _);
            }
            finally { entry.Value.Gate.Release(); }
        }
    }

    private sealed class PendingConnection(OfficeRealTimeTicket ticket, string hubName,
        string connectionId, IOfficeRealTimeSessionStore sessions, TimeProvider time)
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        private readonly object _sync = new();
        private OfficeRealTimeConnection? _active;
        private DateTimeOffset _inactiveExpiresUtc = time.GetUtcNow() + DisconnectTimeout;
        internal OfficeRealTimeTicket Ticket { get; } = ticket;
        internal string HubName { get; } = hubName;
        internal string ConnectionId { get; } = connectionId;
        internal DateTimeOffset ExpiresUtc
        {
            get
            {
                lock (_sync) return _active is null && _inactiveExpiresUtc < Ticket.ExpiresUtc
                    ? _inactiveExpiresUtc : Ticket.ExpiresUtc;
            }
        }

        internal string GetNextSessionConnectionId()
        {
            lock (_sync) return _active is null ? ConnectionId : Guid.NewGuid().ToString("N");
        }

        internal void Activate(OfficeRealTimeConnection connection)
        {
            lock (_sync) { _active = connection; }
        }

        internal async Task DeactivateAsync(OfficeRealTimeConnection connection)
        {
            await Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                bool current;
                lock (_sync)
                {
                    current = ReferenceEquals(_active, connection);
                    if (current)
                    {
                        _active = null;
                        _inactiveExpiresUtc = time.GetUtcNow() + DisconnectTimeout;
                    }
                }
                if (current) await sessions.DisconnectAsync(connection.ConnectionId, CancellationToken.None).ConfigureAwait(false);
            }
            finally { Gate.Release(); }
        }

        internal async Task DisconnectAsync()
        {
            OfficeRealTimeConnection? active;
            lock (_sync) { active = _active; _active = null; }
            active?.Abort();
            if (active is not null) await sessions.DisconnectAsync(active.ConnectionId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class ClassicSocketSender(WebSocket socket, string hubName, string cursor) : IDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sequence;
        internal async Task SendAsync(string method, object?[] arguments, CancellationToken cancellationToken)
        {
            await _initialized.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await SendFrameAsync(new { C = NextCursor(), M = new[] { new { H = hubName, M = method, A = arguments } } },
                cancellationToken).ConfigureAwait(false);
        }
        internal Task SendInitializationAsync(CancellationToken cancellationToken) =>
            SendInitializationCoreAsync(cancellationToken);
        private async Task SendInitializationCoreAsync(CancellationToken cancellationToken)
        {
            await SendFrameAsync(new { C = cursor, M = Array.Empty<object>(), S = 1 }, cancellationToken).ConfigureAwait(false);
            _initialized.TrySetResult();
        }
        internal Task SendKeepAliveAsync(CancellationToken cancellationToken) =>
            SendFrameAsync(new { }, cancellationToken);
        internal Task SendResultAsync(JsonElement id, object? result, CancellationToken cancellationToken) =>
            SendFrameAsync(new { I = id, R = result }, cancellationToken);
        internal Task SendVoidResultAsync(JsonElement id, CancellationToken cancellationToken) =>
            SendFrameAsync(new { I = id }, cancellationToken);
        internal Task SendFailureAsync(JsonElement id, string error, CancellationToken cancellationToken) =>
            SendFrameAsync(new { I = id, E = error }, cancellationToken);
        internal Task SendErrorAsync(string error, CancellationToken cancellationToken) =>
            SendFrameAsync(new { E = error }, cancellationToken);

        private string NextCursor() => "d-" + cursor[2..cursor.IndexOf('|')] + "|i=" + Interlocked.Increment(ref _sequence);

        private async Task SendFrameAsync<T>(T message, CancellationToken cancellationToken)
        {
            if (socket.State != WebSocketState.Open) throw new WebSocketException("The collaboration socket is closed.");
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(message);
            if (json.Length > MaximumMessageBytes) throw new WebSocketException("The collaboration message exceeds the configured limit.");
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await socket.SendAsync(json, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false); }
            finally { _sendLock.Release(); }
        }

        public void Dispose() => _sendLock.Dispose();
    }
}
