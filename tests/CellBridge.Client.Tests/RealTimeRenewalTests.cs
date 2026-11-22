using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CellBridge.Client.Tests;

public sealed class RealTimeRenewalTests
{
    [Fact]
    public async Task TicketRenewsBeforeExpiryOverWebSocketUsingCallerTlsConfiguration()
    {
        const string path = "/shared/rtc-renewal.xlsx";
        var identity = new SubjectIdentity("client-tests:renewal", "renewal", "Renewal test");
        var actor = new CellBridgeActor(identity, CanCreate: true);
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=CellBridge Test", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddHours(1));

        var content = new InMemoryContentStore();
        var storage = new StorageProvider(new InMemoryDocumentRepository(content), content);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
        builder.Services.AddAuthentication();
        builder.Services.AddCellBridge(storage, requireDurability: false,
            configure: options => options.EnableOfficeRealTime = true);

        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(CellBridgeActor.SubjectClaim, identity.Subject),
                new Claim(ClaimTypes.Name, identity.Login),
                new Claim(CellBridgeActor.DisplayNameClaim, identity.DisplayName),
            ], "client-test"));
            return next(context);
        });
        app.UseAuthorization();
        app.MapCellBridge();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await app.StartAsync(timeout.Token);
        await app.Services.GetRequiredService<CellBridgeDocumentService>()
            .CreateAsync(path, OfficeDocumentTemplates.CreateExcelWorkbook(), actor);

        var origin = new Uri(app.Urls.Single());
        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, remote, _, _) => certificate.Equals(remote),
        });
        var transport = new ShortFirstTicket(new HttpCellBridgeTransport(http, origin));
        await using var session = await CellBridgeDocumentSession.OpenAsync(transport, new Uri(origin, path),
            new CellBridgeDocumentSessionOptions { LeaseRenewalInterval = TimeSpan.FromMinutes(1) }, timeout.Token);

        var renewed = await transport.Renewed.Task.WaitAsync(timeout.Token);
        Assert.True(renewed < transport.InitialExpiry);
    }

    [Fact]
    public async Task RejectsWebSocketUpgradeRedirectedToAnotherOrigin()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/signalr/negotiate", () => Results.Text(
            """{"TryWebSockets":true,"ProtocolVersion":"1.4","ConnectionToken":"connection"}""", "application/json"));
        app.MapGet("/signalr/connect", (HttpContext context) =>
            Results.Redirect(new UriBuilder("ws", "localhost", context.Connection.LocalPort, "/redirected").Uri.AbsoluteUri));
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.MapGet("/redirected", async (HttpContext context) =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            accepted.TrySetResult();
            await socket.SendAsync("""{"S":1}"""u8.ToArray().AsMemory(), WebSocketMessageType.Text, true,
                context.RequestAborted);
            await socket.ReceiveAsync(new byte[1024].AsMemory(), context.RequestAborted);
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.StartAsync(timeout.Token);
        var origin = new Uri(app.Urls.Single());
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var transport = new HttpCellBridgeTransport(http, origin);
        var target = new RealTimeConnectionTarget(origin, "ticket", null, Guid.NewGuid().ToString("B"));

        await using var channel = await transport.TryConnectRealTimeAsync(target, timeout.Token);
        Assert.Null(channel);
        await accepted.Task.WaitAsync(timeout.Token);
    }

    private sealed class ShortFirstTicket(ICellBridgeClientTransport inner) : ICellBridgeClientTransport
    {
        public TaskCompletionSource<DateTimeOffset> Renewed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset InitialExpiry { get; private set; }

        public async Task<CellStorageExecution> ExecuteAsync(CellStorageRequest request,
            CancellationToken cancellationToken = default)
        {
            var execution = await inner.ExecuteAsync(request, cancellationToken);
            var response = execution.Response.Responses.SelectMany(file => file.SubResponses)
                .SingleOrDefault(item => item.Type == SubRequestType.Properties);
            if (InitialExpiry == default && response is not null)
            {
                // Shorten the advertised lifetime without changing the signed ticket used by the server.
                InitialExpiry = DateTimeOffset.UtcNow.AddSeconds(6);
                var xml = XElement.Parse(Assert.IsType<string>(response.SubResponseDataXml));
                var ttl = Assert.Single(xml.Elements(), element =>
                    (string?)element.Attribute("id") == "DocumentAccessTokenTtl");
                ttl.SetAttributeValue("value", InitialExpiry.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
                response.SubResponseDataXml = xml.ToString(SaveOptions.DisableFormatting);
            }
            return execution;
        }

        public async Task<ICellBridgeRealTimeChannel?> TryConnectRealTimeAsync(RealTimeConnectionTarget target,
            CancellationToken cancellationToken = default)
        {
            var channel = Assert.IsAssignableFrom<ICellBridgeRealTimeChannel>(
                await inner.TryConnectRealTimeAsync(target, cancellationToken));
            return new ObserveRenewal(channel, Renewed);
        }
    }

    private sealed class ObserveRenewal(ICellBridgeRealTimeChannel inner, TaskCompletionSource<DateTimeOffset> renewed)
        : ICellBridgeRealTimeChannel
    {
        public IAsyncEnumerable<string> ReadNotificationsAsync(CancellationToken cancellationToken = default) =>
            inner.ReadNotificationsAsync(cancellationToken);

        public async Task UpdateClientInfoAsync(RealTimeConnectionTarget target, CancellationToken cancellationToken = default)
        {
            await inner.UpdateClientInfoAsync(target, cancellationToken);
            renewed.TrySetResult(DateTimeOffset.UtcNow);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
