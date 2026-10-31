using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace CellBridge.AspNetCore;

internal sealed class OfficeRealTimeHub : Hub
{
    private const int MaximumCapturedRequestBytes = 64 * 1024;
    private readonly OfficeRealTimeSessionStore sessions;
    private readonly ProtocolCaptureStore captures;
    private readonly bool captureEnabled;

    public OfficeRealTimeHub(OfficeRealTimeSessionStore sessions, ProtocolCaptureStore captures, CellBridgeOptions options)
    {
        this.sessions = sessions;
        this.captures = captures;
        captureEnabled = options.CaptureDirectory is not null;
    }

    public override async Task OnConnectedAsync()
    {
        OfficeRealTimeTicket? ticket = GetTicket();
        if (ticket is null || !await sessions.ConnectAsync(ticket, Context, Context.ConnectionAborted).ConfigureAwait(false))
        {
            Context.Abort();
            return;
        }
        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await sessions.DisconnectAsync(Context.ConnectionId, CancellationToken.None).ConfigureAwait(false);
        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    public Task<Dictionary<string, string>> GetCompletePresenceState() =>
        sessions.GetCompletePresenceStateAsync(GetRequiredTicket(), Context.ConnectionId, Context.ConnectionAborted);

    public Task<object?> UpdatePresence(string presence) => UpdatePresenceCoreAsync(presence);

    public Task<object?> SendUpdateNotification(int channel, string payload) => SendUpdateCoreAsync(channel, payload);

    private async Task<object?> UpdatePresenceCoreAsync(string presence)
    {
        OfficeRealTimeTicket ticket = GetRequiredTicket();
        await CaptureRequestAsync("UpdatePresence", null, presence).ConfigureAwait(false);
        if (!await sessions.UpdatePresenceAsync(ticket, Context.ConnectionId, presence, Context.ConnectionAborted)
            .ConfigureAwait(false)) throw new HubException("The presence update was rejected.");
        return null;
    }

    private async Task<object?> SendUpdateCoreAsync(int channel, string payload)
    {
        OfficeRealTimeTicket ticket = GetRequiredTicket();
        await CaptureRequestAsync("SendUpdateNotification", channel, payload).ConfigureAwait(false);
        if (!await sessions.SendUpdateNotificationAsync(ticket, Context.ConnectionId, channel, payload, Context.ConnectionAborted)
            .ConfigureAwait(false)) throw new HubException("The update notification was rejected.");
        return null;
    }

    private async Task CaptureRequestAsync(string operation, int? channel, string payload)
    {
        if (!captureEnabled || payload.Length > MaximumCapturedRequestBytes ||
            Encoding.UTF8.GetByteCount(payload) > MaximumCapturedRequestBytes) return;
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(new
        {
            operation,
            channel,
            payload,
        });
        if (content.Length > MaximumCapturedRequestBytes) return;

        CancellationToken requestAborted = Context.ConnectionAborted;
        try
        {
            await captures.WriteAsync(Guid.NewGuid().ToString("N"), $".rtc.{operation}.request.json", content, requestAborted)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            // Capture is diagnostic only. The session operation below keeps its
            // existing cancellation behavior and receives the same connection token.
        }
    }

    private OfficeRealTimeTicket? GetTicket() =>
        Context.GetHttpContext()?.Items.TryGetValue(OfficeRealTimeRequest.TicketKey, out object? value) == true
            ? value as OfficeRealTimeTicket : null;

    private OfficeRealTimeTicket GetRequiredTicket() => GetTicket() ?? throw new HubException("The real-time ticket is unavailable.");
}
