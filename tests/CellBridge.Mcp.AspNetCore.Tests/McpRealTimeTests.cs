using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Client;
using CellBridge.FssHttp;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.WebUtilities;
using static CellBridge.Mcp.AspNetCore.Tests.McpWorkbookTests;

namespace CellBridge.Mcp.AspNetCore.Tests;

public sealed class McpRealTimeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InProcessConnectorReceivesOfficePresenceAndUpdatesWhenEnabled(bool enableRealTime)
    {
        await using var host = await McpTestHost.StartAsync(enableRealTime);
        var document = await host.CreateWorkbookAsync();
        await host.SetPeerAccessAsync(document.ResourceId, DocumentAccess.Read | DocumentAccess.Write);
        using var owner = host.CreateHttp("owner");
        using var peer = host.CreateHttp("peer");

        var mcp = new McpHttpSession(owner);
        await mcp.InitializeAsync();
        var opened = await OpenAsync(mcp, new { resourceId = document.ResourceId });
        var workbookHandle = Handle(opened);

        var writerTransport = new RtcTransport(host.CreateTransport(peer));
        var readerTransport = new RtcTransport(host.CreateTransport(owner), host.CreateRealTimeConnector(McpTestHost.Owner));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var writer = await CellBridgeDocumentSession.OpenAsync(writerTransport, host.WorkbookUrl,
            new CellBridgeDocumentSessionOptions { Mode = CellBridgeDocumentSessionMode.EagerWriter }, deadline.Token);
        await using var reader = await CellBridgeDocumentSession.OpenAsync(readerTransport, host.WorkbookUrl,
            cancellationToken: deadline.Token);
        await using var notifications = reader.ReadNotificationsAsync(deadline.Token).GetAsyncEnumerator();

        if (enableRealTime)
        {
            var target = Assert.IsType<RealTimeConnectionTarget>(writerTransport.Target);
            using var office = await OpenOfficeSocketAsync(peer, target, deadline.Token);
            Assert.Equal(1, (await ReceiveAsync(office, deadline.Token)).GetProperty("S").GetInt32());
            await SendAsync(office, "UpdatePresence", ["{}"], deadline.Token);
            var presence = await ReadNotificationAsync(notifications, "presenceUpdated");
            Assert.Equal("{}", presence[0].GetProperty(writer.ClientId.ToString("B")).GetString());

            await SendAsync(office, "SendUpdateNotification", [1, ""], deadline.Token);
            var update = await ReadNotificationAsync(notifications, "updateNotification");
            Assert.Equal(1, update[0].GetInt32());
            Assert.Equal("", update[1].GetString());
        }
        else
        {
            Assert.Null(writerTransport.Target);
            Assert.Null(readerTransport.Target);
            Assert.False(await notifications.MoveNextAsync());
        }

        var synchronized = await SuccessAsync(mcp, "sync_workbook", new { workbookHandle });
        Assert.Equal(Revision(opened), Revision(synchronized));
        await SuccessAsync(mcp, "close_workbook", new { workbookHandle });
    }

    private static async Task<ClientWebSocket> OpenOfficeSocketAsync(HttpClient peer, RealTimeConnectionTarget target,
        CancellationToken cancellationToken)
    {
        var common = new Dictionary<string, string?>
        {
            ["qs"] = target.DocumentAccessToken,
            ["ClientId"] = target.ClientId,
        };
        var negotiate = QueryHelpers.AddQueryString(new Uri(target.Endpoint, "signalr/negotiate").AbsoluteUri,
            new Dictionary<string, string?>(common)
            {
                ["clientProtocol"] = "1.4",
                ["connectionData"] = """[{"name":"RealtimeChannelHub"}]""",
            });
        var response = await peer.GetFromJsonAsync<JsonElement>(negotiate, cancellationToken);
        var connect = QueryHelpers.AddQueryString(new Uri(target.Endpoint, "signalr/connect").AbsoluteUri,
            new Dictionary<string, string?>(common)
            {
                ["transport"] = "webSockets",
                ["connectionToken"] = response.GetProperty("ConnectionToken").GetString(),
            });
        var endpoint = new UriBuilder(connect) { Scheme = "ws" };
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(endpoint.Uri, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static Task SendAsync(ClientWebSocket socket, string method, object[] arguments, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.SerializeToUtf8Bytes(new { H = "RealtimeChannelHub", M = method, A = arguments, I = method });
        return socket.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        WebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            Assert.Equal(WebSocketMessageType.Text, frame.MessageType);
            message.Write(buffer, 0, frame.Count);
        }
        while (!frame.EndOfMessage);
        using var document = JsonDocument.Parse(message.ToArray());
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> ReadNotificationAsync(IAsyncEnumerator<string> notifications, string method)
    {
        while (await notifications.MoveNextAsync())
        {
            using var message = JsonDocument.Parse(notifications.Current);
            if (message.RootElement.GetProperty("M").GetString() == method)
                return message.RootElement.GetProperty("A").Clone();
        }
        throw new InvalidOperationException($"RTC closed before the {method} notification.");
    }

    private sealed class RtcTransport(HttpCellBridgeTransport transport, ICellBridgeRealTimeConnector? connector = null)
        : ICellBridgeClientTransport
    {
        public RealTimeConnectionTarget? Target { get; private set; }

        public Task<CellStorageExecution> ExecuteAsync(CellStorageRequest request, CancellationToken cancellationToken = default) =>
            transport.ExecuteAsync(request, cancellationToken);

        public Task<ICellBridgeRealTimeChannel?> TryConnectRealTimeAsync(RealTimeConnectionTarget target,
            CancellationToken cancellationToken = default)
        {
            Target = target;
            return connector?.TryConnectRealTimeAsync(target, cancellationToken) ?? Task.FromResult<ICellBridgeRealTimeChannel?>(null);
        }
    }
}
