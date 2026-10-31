using System.Text;
using System.Text.Json;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace CellBridge.AspNetCore;

internal sealed class OfficeRealTimeSessionStore
{
    private readonly IOfficeRealTimeAuthorizer authorizer;
    private readonly IHubContext<OfficeRealTimeHub> hubContext;
    private readonly TimeProvider time;
    private const int MaxConnections = 128;
    private const int MaxDocumentConnections = 32;
    private const int MaxSubjectConnections = 8;
    private const int MaxPayloadBytes = 64 * 1024;
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(5);
    private static readonly HashSet<string> ChangeTokenFields = new(StringComparer.Ordinal)
    {
        "ChangeToken", "ClientId", "CorrelationId", "EventCommitTimestampUtc", "EventReason",
        "EventRequestId", "EventSourceApp", "IsVerificationEvent", "PartIds", "Properties",
        "Source", "SubscriptionId", "SyncToken"
    };

    private readonly object _sync = new();
    private readonly Dictionary<RoomKey, Room> _rooms = [];
    private readonly Dictionary<string, Connection> _connections = new(StringComparer.Ordinal);

    public OfficeRealTimeSessionStore(IOfficeRealTimeAuthorizer authorizer,
        IHubContext<OfficeRealTimeHub> hubContext, TimeProvider? timeProvider = null)
    {
        this.authorizer = authorizer;
        this.hubContext = hubContext;
        time = timeProvider ?? TimeProvider.System;
    }

    internal async Task<bool> ConnectAsync(OfficeRealTimeTicket ticket, HubCallerContext context,
        CancellationToken cancellationToken)
    {
        using var operationDeadline = CreateDeadline(cancellationToken);
        cancellationToken = operationDeadline.Token;
        if (IsExpired(ticket) ||
            !await authorizer.AuthorizeAsync(ticket, DocumentAccess.Read, true, cancellationToken).ConfigureAwait(false))
            return false;

        await PruneExpiredConnectionsAsync(cancellationToken).ConfigureAwait(false);

        RoomKey key = new(ticket.ResourceId, ticket.LifecycleGeneration);
        Room room;
        lock (_sync)
        {
            if (!_rooms.TryGetValue(key, out room!)) _rooms.Add(key, room = new Room(key));
            room.Leases++;
        }

        bool entered = false;
        try
        {
            await room.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            Connection? replaced = null;
            lock (_sync)
            {
                if (IsExpired(ticket)) return false;
                string clientKey = ClientKey(ticket.ClientId);
                if (room.Clients.TryGetValue(clientKey, out string? currentId) &&
                    _connections.TryGetValue(currentId, out Connection? current))
                {
                    if (!StringComparer.Ordinal.Equals(current.Ticket.Actor.Identity.Subject, ticket.Actor.Identity.Subject))
                        return false;
                    replaced = current;
                    RemoveConnectionLocked(current);
                }

                int total = _connections.Count;
                int documentCount = room.Connections.Count;
                int subjectCount = _connections.Values.Count(x =>
                    StringComparer.Ordinal.Equals(x.Ticket.Actor.Identity.Subject, ticket.Actor.Identity.Subject));
                if (total >= MaxConnections || documentCount >= MaxDocumentConnections || subjectCount >= MaxSubjectConnections)
                {
                    if (replaced is not null) RestoreConnectionLocked(room, replaced);
                    return false;
                }

                Connection connection = new(ticket, context, room, key);
                _connections.Add(context.ConnectionId, connection);
                room.Connections.Add(context.ConnectionId);
                room.Clients[clientKey] = context.ConnectionId;
                if (replaced is not null && room.Presence.TryGetValue(clientKey, out Presence? existingPresence) &&
                    StringComparer.Ordinal.Equals(existingPresence.ConnectionId, replaced.Context.ConnectionId))
                    room.Presence[clientKey] = existingPresence with { ConnectionId = context.ConnectionId };
            }
            replaced?.Context.Abort();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            if (entered) room.Gate.Release();
            ReleaseRoomLease(key, room);
        }
    }

    internal async Task<Dictionary<string, string>> GetCompletePresenceStateAsync(
        OfficeRealTimeTicket ticket, string connectionId, CancellationToken cancellationToken)
    {
        using var operationDeadline = CreateDeadline(cancellationToken);
        cancellationToken = operationDeadline.Token;
        if (!await AuthorizeSenderAsync(ticket, connectionId, DocumentAccess.Read, cancellationToken).ConfigureAwait(false))
            return [];
        Room? room = GetRoomForConnection(connectionId, ticket);
        if (room is null) return [];
        if (!await EnterRoomAsync(room, cancellationToken).ConfigureAwait(false)) return [];
        try
        {
            if (!await AuthorizeCurrentSenderInRoomAsync(ticket, connectionId, room, DocumentAccess.Read,
                cancellationToken).ConfigureAwait(false)) return [];
            List<(string Key, string Payload, Connection Source)> snapshot;
            lock (_sync)
            {
                snapshot = room.Presence.Select(x =>
                    (x.Key, x.Value.Payload, _connections.GetValueOrDefault(x.Value.ConnectionId)))
                    .Where(x => x.Item3 is not null)
                    .Select(x => (x.Key, x.Payload, x.Item3!)).ToList();
            }

            while (true)
            {
                var checks = new List<OfficeRealTimeTicketCheck> { new(ticket, DocumentAccess.Read, true) };
                checks.AddRange(snapshot.Select(entry => new OfficeRealTimeTicketCheck(entry.Source.Ticket,
                    DocumentAccess.Read, true)));
                var allowed = await authorizer.AuthorizeTogetherAsync(checks, cancellationToken).ConfigureAwait(false);
                if (allowed.Length != checks.Count || !allowed[0])
                {
                    Connection? requester;
                    lock (_sync) _connections.TryGetValue(connectionId, out requester);
                    if (requester is not null) await RemoveDeniedAsync(requester, cancellationToken).ConfigureAwait(false);
                    return [];
                }
                bool cleaned = false;
                for (int i = 0; i < snapshot.Count; i++)
                {
                    var entry = snapshot[i];
                    if (!allowed[i + 1] || IsExpired(entry.Source.Ticket))
                    {
                        await RemoveDeniedAsync(entry.Source, cancellationToken).ConfigureAwait(false);
                        cleaned = true;
                    }
                }
                if (cleaned)
                {
                    // Cleanup can await recipient authorization and network delivery. Remove the
                    // departed sources and obtain a new common permission snapshot afterward.
                    // Each pass removes at least one source, so the room bound also bounds retries.
                    lock (_sync) snapshot.RemoveAll(entry => !IsCurrentConnectionLocked(entry.Source));
                    continue;
                }
                lock (_sync)
                {
                    if (IsExpired(ticket) || !_connections.TryGetValue(connectionId, out var requester) ||
                        requester.Ticket != ticket) return [];
                    Dictionary<string, string> result = new(StringComparer.Ordinal);
                    foreach (var entry in snapshot)
                        if (!IsExpired(entry.Source.Ticket) && IsCurrentConnectionLocked(entry.Source) &&
                            room.Presence.TryGetValue(entry.Key, out var presence) &&
                            presence.ConnectionId == entry.Source.Context.ConnectionId)
                            result[entry.Key] = entry.Payload;
                    return result;
                }
            }
        }
        finally { ExitRoom(room); }
    }

    internal async Task<bool> UpdatePresenceAsync(OfficeRealTimeTicket ticket, string connectionId,
        string payload, CancellationToken cancellationToken)
    {
        using var operationDeadline = CreateDeadline(cancellationToken);
        cancellationToken = operationDeadline.Token;
        if (!TryValidatePayload(payload, out JsonDocument? document) || document is null) return false;
        using (document)
        {
            // Native presence carries application state without an A field. The
            // authenticated connection owns the dictionary key, not its payload.
            if (!IdentityFieldMatches(document.RootElement, "A", ticket.ClientId) ||
                !IdentityFieldMatches(document.RootElement, "clientId", ticket.ClientId) ||
                !IdentityFieldMatches(document.RootElement, "Cid", ticket.ClientId, allowEmpty: true)) return false;
        }
        return await RelayAsync(ticket, connectionId, DocumentAccess.Read, "presenceUpdated",
            [new Dictionary<string, string> { [ClientKey(ticket.ClientId)] = payload }, Array.Empty<string>()],
            (room, _) =>
            {
                lock (_sync)
                {
                    if (!_connections.TryGetValue(connectionId, out var current) || current.Ticket != ticket)
                        return Task.FromResult(false);
                    room.Presence[ClientKey(ticket.ClientId)] = new(connectionId, payload);
                }
                return Task.FromResult(true);
            }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> SendUpdateNotificationAsync(OfficeRealTimeTicket ticket, string connectionId,
        int channel, string payload, CancellationToken cancellationToken)
    {
        using var operationDeadline = CreateDeadline(cancellationToken);
        cancellationToken = operationDeadline.Token;
        if ((!IsClientChannel(channel) && channel != 4) || !TryValidatePayload(payload, out JsonDocument? document) || document is null)
            return false;
        using (document)
        {
            JsonElement root = document.RootElement;
            // Native Excel reports receive latency on channel 4. This is local
            // telemetry, not a server-change notification to relay to peers.
            if (channel == 4)
            {
                if (root.EnumerateObject().Count() != 2 ||
                    !root.TryGetProperty("Id", out var reportId) || reportId.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(reportId.GetString(), out _) ||
                    !root.TryGetProperty("TD", out var delay) || delay.ValueKind != JsonValueKind.Number ||
                    !delay.TryGetInt32(out var milliseconds) || milliseconds < 0)
                    return false;
                if (!await AuthorizeAsync(ticket, DocumentAccess.Read, true, cancellationToken).ConfigureAwait(false))
                    return false;
                lock (_sync)
                    return !IsExpired(ticket) && _connections.TryGetValue(connectionId, out var connection) &&
                        connection.Ticket == ticket;
            }
            if (channel == 1)
            {
                if (!IsValidChannelOne(root)) return false;
            }
            else if (root.TryGetProperty("A", out JsonElement senderValue))
            {
                if (senderValue.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(senderValue.GetString(), out Guid populatedSender) || populatedSender != ticket.ClientId) return false;
            }
            if (channel == 3 && (!TryGetSender(root, out Guid sender) || sender != ticket.ClientId)) return false;
            if (channel == 8 && !root.TryGetProperty("SL2", out _)) return false;
        }

        return await RelayAsync(ticket, connectionId, DocumentAccess.Write, "updateNotification",
            [channel, payload], (_, _) => Task.FromResult(true), cancellationToken).ConfigureAwait(false);
    }

    internal async Task DisconnectAsync(string connectionId, CancellationToken cancellationToken)
    {
        using var operationDeadline = CreateDeadline(cancellationToken);
        cancellationToken = operationDeadline.Token;
        Departure departure;
        lock (_sync)
        {
            if (!_connections.TryGetValue(connectionId, out var connection)) return;
            departure = RemoveForDepartureLocked(connection);
        }
        await AnnounceDepartureAsync(departure, cancellationToken).ConfigureAwait(false);
    }

    private async Task PruneExpiredConnectionsAsync(CancellationToken cancellationToken)
    {
        Departure[] departures;
        lock (_sync)
            departures = _connections.Values.Where(connection => IsExpired(connection.Ticket)).ToArray()
                .Select(RemoveForDepartureLocked).ToArray();
        foreach (var departure in departures) departure.Connection.Context.Abort();
        int next = 0;
        try
        {
            while (next < departures.Length)
                await AnnounceDepartureAsync(departures[next++], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            while (next < departures.Length)
            {
                var connection = departures[next++].Connection;
                ReleaseRoomLease(connection.Key, connection.Room);
            }
        }
    }

    // All local state is removed before waiting for the room gate or network delivery.
    private Departure RemoveForDepartureLocked(Connection connection)
    {
        connection.Room.Leases++;
        string key = ClientKey(connection.Ticket.ClientId);
        bool removedPresence = connection.Room.Presence.TryGetValue(key, out var presence) &&
            StringComparer.Ordinal.Equals(presence.ConnectionId, connection.Context.ConnectionId);
        RemoveConnectionLocked(connection);
        if (removedPresence) connection.Room.Presence.Remove(key);
        return new(connection, removedPresence);
    }

    private async Task AnnounceDepartureAsync(Departure departure, CancellationToken cancellationToken)
    {
        var connection = departure.Connection;
        if (!departure.RemovedPresence)
        {
            ReleaseRoomLease(connection.Key, connection.Room);
            return;
        }
        if (!await EnterRoomAsync(connection.Room, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            string clientKey = ClientKey(connection.Ticket.ClientId);
            lock (_sync)
                if (connection.Room.Presence.ContainsKey(clientKey)) return;
            await DeliverAsync(connection.Room, "presenceUpdated",
                [new Dictionary<string, string>(), new[] { clientKey }], null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { ExitRoom(connection.Room); }
    }

    private async Task<bool> RelayAsync(OfficeRealTimeTicket ticket, string connectionId,
        DocumentAccess access, string method, object?[] args,
        Func<Room, CancellationToken, Task<bool>> beforeDelivery, CancellationToken cancellationToken)
    {
        if (!await AuthorizeSenderAsync(ticket, connectionId, access, cancellationToken).ConfigureAwait(false)) return false;
        Room? room = GetRoomForConnection(connectionId, ticket);
        if (room is null || !await EnterRoomAsync(room, cancellationToken).ConfigureAwait(false)) return false;
        try
        {
            if (!await AuthorizeCurrentSenderInRoomAsync(ticket, connectionId, room, access,
                cancellationToken).ConfigureAwait(false)) return false;
            if (!await beforeDelivery(room, cancellationToken).ConfigureAwait(false)) return false;
            return await DeliverAsync(room, method, args, connectionId, cancellationToken, access).ConfigureAwait(false);
        }
        finally { ExitRoom(room); }
    }

    private async Task<bool> DeliverAsync(Room room, string method, object?[] args, string? excludedConnectionId,
        CancellationToken cancellationToken, DocumentAccess senderAccess = DocumentAccess.Read)
    {
        string[] recipients;
        lock (_sync) recipients = room.Connections.ToArray();
        foreach (string id in recipients)
        {
            if (StringComparer.Ordinal.Equals(id, excludedConnectionId)) continue;
            Connection? recipient;
            lock (_sync) _connections.TryGetValue(id, out recipient);
            if (recipient is null) continue;
            Connection? sender = null;
            bool recipientAllowed;
            if (excludedConnectionId is not null)
            {
                lock (_sync) _connections.TryGetValue(excludedConnectionId, out sender);
                if (sender is null) return false;
                var allowed = await authorizer.AuthorizeTogetherAsync([
                    new(sender.Ticket, senderAccess, true), new(recipient.Ticket, DocumentAccess.Read, false)
                ], cancellationToken).ConfigureAwait(false);
                if (allowed.Length != 2 || !allowed[0] || IsExpired(sender.Ticket))
                {
                    await RemoveDeniedAsync(sender, cancellationToken).ConfigureAwait(false);
                    return false;
                }
                recipientAllowed = allowed[1] && !IsExpired(recipient.Ticket);
            }
            else
                recipientAllowed = !IsExpired(recipient.Ticket) &&
                    await AuthorizeAsync(recipient.Ticket, DocumentAccess.Read, false, cancellationToken).ConfigureAwait(false) &&
                    !IsExpired(recipient.Ticket);
            if (!recipientAllowed)
            {
                await RemoveDeniedAsync(recipient, cancellationToken).ConfigureAwait(false);
                continue;
            }
            lock (_sync)
            {
                if (!IsCurrentConnectionLocked(recipient) || IsExpired(recipient.Ticket)) continue;
                if (sender is not null && (!IsCurrentConnectionLocked(sender) || IsExpired(sender.Ticket))) return false;
            }

            try
            {
                await hubContext.Clients.Client(id).SendCoreAsync(method, args, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
        return true;
    }

    private async Task RemoveDeniedAsync(Connection connection, CancellationToken cancellationToken)
    {
        bool removedPresence = false;
        lock (_sync)
        {
            if (!_connections.TryGetValue(connection.Context.ConnectionId, out Connection? current) || !ReferenceEquals(current, connection))
                return;
            string key = ClientKey(connection.Ticket.ClientId);
            removedPresence = connection.Room.Presence.TryGetValue(key, out Presence? presence) &&
                StringComparer.Ordinal.Equals(presence.ConnectionId, connection.Context.ConnectionId);
            RemoveConnectionLocked(connection);
            if (removedPresence) connection.Room.Presence.Remove(key);
        }
        connection.Context.Abort();
        if (removedPresence)
            await DeliverAsync(connection.Room, "presenceUpdated",
                [new Dictionary<string, string>(), new[] { ClientKey(connection.Ticket.ClientId) }], null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> AuthorizeSenderAsync(OfficeRealTimeTicket ticket, string connectionId,
        DocumentAccess access, CancellationToken cancellationToken)
    {
        bool authorized = !IsExpired(ticket) &&
            await AuthorizeAsync(ticket, access, true, cancellationToken).ConfigureAwait(false) && !IsExpired(ticket);
        Connection? connection;
        lock (_sync)
            _connections.TryGetValue(connectionId, out connection);
        if (!authorized)
        {
            if (connection is not null && connection.Ticket == ticket)
                await DenyConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            return false;
        }
        return connection is not null && connection.Ticket == ticket;
    }

    private async Task<bool> AuthorizeCurrentSenderInRoomAsync(OfficeRealTimeTicket ticket, string connectionId,
        Room room, DocumentAccess access, CancellationToken cancellationToken)
    {
        Connection? connection;
        lock (_sync)
        {
            _connections.TryGetValue(connectionId, out connection);
            if (connection is null || connection.Ticket != ticket || !ReferenceEquals(connection.Room, room)) return false;
        }
        bool authorized = !IsExpired(ticket) &&
            await AuthorizeAsync(ticket, access, true, cancellationToken).ConfigureAwait(false) && !IsExpired(ticket);
        if (!authorized)
        {
            await RemoveDeniedAsync(connection, cancellationToken).ConfigureAwait(false);
            return false;
        }
        lock (_sync) return IsCurrentConnectionLocked(connection);
    }

    private async Task DenyConnectionAsync(Connection connection, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (!_connections.TryGetValue(connection.Context.ConnectionId, out Connection? current) || !ReferenceEquals(current, connection)) return;
            connection.Room.Leases++;
        }
        if (!await EnterRoomAsync(connection.Room, cancellationToken).ConfigureAwait(false)) return;
        try { await RemoveDeniedAsync(connection, cancellationToken).ConfigureAwait(false); }
        finally { ExitRoom(connection.Room); }
    }

    private ValueTask<bool> AuthorizeAsync(OfficeRealTimeTicket ticket, DocumentAccess access,
        bool requireOwnedClient, CancellationToken cancellationToken) =>
        authorizer.AuthorizeAsync(ticket, access, requireOwnedClient, cancellationToken);

    private Room? GetRoomForConnection(string connectionId, OfficeRealTimeTicket ticket)
    {
        lock (_sync)
        {
            if (!_connections.TryGetValue(connectionId, out Connection? connection) || connection.Ticket != ticket) return null;
            connection.Room.Leases++;
            return connection.Room;
        }
    }

    private async Task<bool> EnterRoomAsync(Room room, CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CreateDeadline(cancellationToken);
            await room.Gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { ReleaseRoomLease(room.Key, room); return false; }
    }

    private void ExitRoom(Room room)
    {
        room.Gate.Release();
        ReleaseRoomLease(room.Key, room);
    }

    private void ReleaseRoomLease(RoomKey key, Room room)
    {
        lock (_sync)
        {
            room.Leases--;
            if (room.Leases == 0 && room.Connections.Count == 0 && _rooms.TryGetValue(key, out Room? current) && ReferenceEquals(current, room))
                _rooms.Remove(key);
        }
    }

    private void RemoveConnectionLocked(Connection connection)
    {
        _connections.Remove(connection.Context.ConnectionId);
        connection.Room.Connections.Remove(connection.Context.ConnectionId);
        string key = ClientKey(connection.Ticket.ClientId);
        if (connection.Room.Clients.TryGetValue(key, out string? id) && StringComparer.Ordinal.Equals(id, connection.Context.ConnectionId))
            connection.Room.Clients.Remove(key);
    }

    private void RestoreConnectionLocked(Room room, Connection connection)
    {
        _connections[connection.Context.ConnectionId] = connection;
        room.Connections.Add(connection.Context.ConnectionId);
        room.Clients[ClientKey(connection.Ticket.ClientId)] = connection.Context.ConnectionId;
    }

    private bool IsCurrentConnectionLocked(Connection connection) =>
        _connections.TryGetValue(connection.Context.ConnectionId, out var current) && ReferenceEquals(current, connection);

    private bool IsExpired(OfficeRealTimeTicket ticket) => ticket.ExpiresUtc <= time.GetUtcNow();
    private static string ClientKey(Guid clientId) => clientId.ToString("B").ToLowerInvariant();
    private static CancellationTokenSource CreateDeadline(CancellationToken token)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(token);
        source.CancelAfter(DispatchTimeout);
        return source;
    }

    private static bool IsClientChannel(int channel) => channel is 1 or 3 or 8 or 14 or 18;

    private static bool IsValidChannelOne(JsonElement root)
    {
        HashSet<string> allowed = new(StringComparer.Ordinal) { "HQ", "Id", "NT", "WEB" };
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (ChangeTokenFields.Contains(property.Name) || !allowed.Contains(property.Name)) return false;
        }
        return root.EnumerateObject().Any();
    }

    private static bool TryGetSender(JsonElement root, out Guid sender)
    {
        sender = default;
        return root.TryGetProperty("A", out JsonElement value) && value.ValueKind == JsonValueKind.String &&
               Guid.TryParse(value.GetString(), out sender);
    }

    private static bool IdentityFieldMatches(JsonElement root, string fieldName, Guid clientId, bool allowEmpty = false)
    {
        if (!root.TryGetProperty(fieldName, out JsonElement value)) return true;
        if (value.ValueKind != JsonValueKind.String) return false;
        string? text = value.GetString();
        if (allowEmpty && string.IsNullOrEmpty(text)) return true;
        return Guid.TryParse(text, out Guid suppliedClient) && suppliedClient == clientId;
    }

    private static bool TryValidatePayload(string? payload, out JsonDocument? document)
    {
        document = null;
        if (payload is null) return false;
        if (Encoding.UTF8.GetByteCount(payload) > MaxPayloadBytes) return false;
        try
        {
            JsonDocument parsed = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow });
            if (parsed.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateProperty(parsed.RootElement))
            {
                parsed.Dispose();
                return false;
            }
            document = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool HasDuplicateProperty(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperty(property.Value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement value in element.EnumerateArray()) if (HasDuplicateProperty(value)) return true;
        }
        return false;
    }

    private sealed class Room
    {
        internal Room(RoomKey key) => Key = key;
        internal RoomKey Key { get; }
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal HashSet<string> Connections { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, string> Clients { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, Presence> Presence { get; } = new(StringComparer.Ordinal);
        internal int Leases { get; set; }
    }

    private readonly record struct RoomKey(Guid ResourceId, long Generation);
    private sealed record Connection(OfficeRealTimeTicket Ticket, HubCallerContext Context, Room Room, RoomKey Key);
    private sealed record Presence(string ConnectionId, string Payload);
    private sealed record Departure(Connection Connection, bool RemovedPresence);
}
