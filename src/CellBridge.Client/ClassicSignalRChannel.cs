using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace CellBridge.Client;

internal sealed class ClassicSignalRChannel : ICellBridgeRealTimeChannel
{
    private const string Hub = "RealtimeChannelHub";
    private const int MaximumFrameBytes = 1024 * 1024;

    private readonly ClientWebSocket _socket;
    private readonly RealTimeConnectionTarget _target;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _pending = new();
    private readonly Channel<string> _notifications = Channel.CreateBounded<string>(new BoundedChannelOptions(32)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleWriter = true,
    });
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _receive;
    private long _invocationId;
    private int _disposed;

    private ClassicSignalRChannel(ClientWebSocket socket, RealTimeConnectionTarget target)
    {
        _socket = socket;
        _target = target;
        _receive = ReceiveAsync();
    }

    internal static async Task<ICellBridgeRealTimeChannel?> TryConnectAsync(HttpClient client, Uri origin,
        RealTimeConnectionTarget target, CancellationToken cancellationToken)
    {
        var endpoint = target.Endpoint;
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != origin.Scheme || endpoint.IdnHost != origin.IdnHost ||
            endpoint.Port != origin.Port || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            return null;

        // The Properties endpoint reports the RTC base. Some hosts report the classic route directly.
        string basePath = endpoint.AbsolutePath.TrimEnd('/');
        if (!basePath.EndsWith("/signalr", StringComparison.OrdinalIgnoreCase))
            basePath += "/signalr";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        try
        {
            var negotiate = Route(endpoint, basePath, "negotiate",
                ("clientProtocol", "1.4"), ("connectionData", """[{"name":"RealtimeChannelHub"}]"""),
                ("qs", target.DocumentAccessToken), ("ClientId", target.ClientId));
            using var response = await client.GetAsync(negotiate, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            await response.Content.LoadIntoBufferAsync(64 * 1024, ct).ConfigureAwait(false);
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("TryWebSockets", out var supported) || supported.ValueKind != JsonValueKind.True ||
                !json.RootElement.TryGetProperty("ProtocolVersion", out var protocol) ||
                protocol.ValueKind != JsonValueKind.String || protocol.GetString() != "1.4" ||
                !json.RootElement.TryGetProperty("ConnectionToken", out var token) ||
                token.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(token.GetString()))
                return null;

            var connect = Route(endpoint, basePath, "connect",
                ("transport", "webSockets"), ("connectionToken", token.GetString()!),
                ("qs", target.DocumentAccessToken), ("ClientId", target.ClientId));
            var websocket = new UriBuilder(connect) { Scheme = connect.Scheme == "https" ? "wss" : "ws" };
            var socket = new ClientWebSocket();
            ClassicSignalRChannel? channel = null;
            try
            {
                // The RTC ticket authenticates the socket even when the SOAP client uses cookies or bearer auth.
                await socket.ConnectAsync(websocket.Uri, ct).ConfigureAwait(false);
                channel = new(socket, target);
                await channel._initialized.Task.WaitAsync(ct).ConfigureAwait(false);
                return channel;
            }
            catch
            {
                if (channel is null)
                    socket.Dispose();
                else
                    await channel.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is HttpRequestException or WebSocketException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            return null;
        }
    }

    public IAsyncEnumerable<string> ReadNotificationsAsync(CancellationToken cancellationToken = default) =>
        _notifications.Reader.ReadAllAsync(cancellationToken);

    public async Task UpdateClientInfoAsync(RealTimeConnectionTarget target, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (target.Endpoint != _target.Endpoint || !StringComparer.OrdinalIgnoreCase.Equals(target.ClientId, _target.ClientId))
            throw new ArgumentException("RTC ticket renewal requires the same endpoint and client ID.", nameof(target));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var id = Interlocked.Increment(ref _invocationId).ToString(CultureInfo.InvariantCulture);
        var acknowledgment = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.TryAdd(id, acknowledgment);
        try
        {
            if (_receive.IsCompleted)
                throw new IOException("The classic SignalR connection closed.");

            var ticket = JsonSerializer.Serialize(new { qs = target.DocumentAccessToken });
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { H = Hub, M = "UpdateClientInfo", A = new[] { ticket }, I = id });
            await _send.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            finally
            {
                _send.Release();
            }
            await acknowledgment.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (WebSocketException error)
        {
            throw new IOException("The classic SignalR connection could not renew its ticket.", error);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[8192];
        using var frame = new MemoryStream();
        Exception? failure = null;
        try
        {
            while (true)
            {
                var part = await _socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                if (part.MessageType == WebSocketMessageType.Close)
                    break;
                if (part.MessageType != WebSocketMessageType.Text || frame.Length + part.Count > MaximumFrameBytes)
                    throw new InvalidDataException("The server sent an invalid classic SignalR message.");

                frame.Write(buffer, 0, part.Count);
                if (!part.EndOfMessage)
                    continue;
                using var json = JsonDocument.Parse(frame.GetBuffer().AsMemory(0, (int)frame.Length));
                frame.SetLength(0);
                Dispatch(json.RootElement);
            }
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
            // Abort can dispose the socket between successive receive operations.
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            if (Volatile.Read(ref _disposed) == 0)
                failure = ex;
        }
        finally
        {
            var closed = new IOException("The classic SignalR connection closed.", failure);
            _initialized.TrySetException(closed);
            foreach (var pending in _pending.Values)
                pending.TrySetException(closed);
            _notifications.Writer.TryComplete(failure is null ? null : closed);
        }
    }

    private void Dispatch(JsonElement frame)
    {
        if (frame.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The server sent an invalid classic SignalR frame.");

        bool hasError = frame.TryGetProperty("E", out _);
        if (frame.TryGetProperty("I", out var id) && id.ValueKind == JsonValueKind.String)
        {
            if (_pending.TryGetValue(id.GetString()!, out var acknowledgment))
            {
                if (hasError)
                    acknowledgment.TrySetException(new IOException("The server rejected the RTC ticket renewal."));
                else
                    acknowledgment.TrySetResult();
            }
            return;
        }

        if (hasError)
            throw new IOException("The server rejected the classic SignalR connection.");
        if (frame.TryGetProperty("S", out var started) && started.ValueKind == JsonValueKind.Number &&
            started.TryGetInt32(out var state) && state == 1)
            _initialized.TrySetResult();

        if (frame.TryGetProperty("M", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var notification in messages.EnumerateArray())
                _notifications.Writer.TryWrite(notification.GetRawText());
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _socket.Abort();
        await _receive.ConfigureAwait(false);
        _socket.Dispose();
    }

    private static Uri Route(Uri endpoint, string basePath, string operation, params (string Key, string Value)[] fields)
    {
        var uri = new UriBuilder(endpoint)
        {
            Path = basePath + "/" + operation,
            Query = string.Join('&', fields.Select(field => field.Key + "=" + Uri.EscapeDataString(field.Value))),
        };
        return uri.Uri;
    }
}
