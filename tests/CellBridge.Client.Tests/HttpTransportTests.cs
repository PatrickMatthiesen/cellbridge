using System.Net;
using System.Text;
using CellBridge.FssHttp;

namespace CellBridge.Client.Tests;

public sealed class HttpTransportTests
{
    private static readonly Uri Origin = new("https://client.test/");

    [Fact]
    public async Task PasswordLoginUsesTheApplicationMount()
    {
        var paths = new List<string>();
        using var http = new HttpClient(new Respond(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            var response = path switch
            {
                "/demo/auth/login" when request.Method == HttpMethod.Get => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""<input name="__RequestVerificationToken" value="token">"""),
                },
                "/demo/auth/login" => new HttpResponseMessage(HttpStatusCode.Found),
                "/demo/auth/complete" => new HttpResponseMessage(HttpStatusCode.OK),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            if (response.StatusCode == HttpStatusCode.Found)
                response.Headers.Location = new Uri("/demo/auth/complete", UriKind.Relative);
            response.RequestMessage = request;
            return response;
        }));

        await WebDemoLogin.SignInAsync(http, Origin, "reader", "test", applicationPathBase: "/demo");

        Assert.Equal(["/demo/auth/login", "/demo/auth/login", "/demo/auth/complete"], paths);
    }

    [Fact]
    public async Task NonSuccessSoapFaultPreservesTheServerReason()
    {
        using var http = new HttpClient(new Respond(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""
                <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
                  <s:Body><s:Fault><faultcode>s:Server</faultcode><faultstring>Try again</faultstring></s:Fault></s:Body>
                </s:Envelope>
                """, Encoding.UTF8, "text/xml"),
        }));
        var execution = await new HttpCellBridgeTransport(http, Origin).ExecuteAsync(WhoAmI());

        var fault = Assert.IsType<CellStorageSoapFault>(execution.Response.Fault);
        Assert.Equal("s:Server", fault.Code);
        Assert.Equal("Try again", fault.Reason);
    }

    [Fact]
    public async Task TransportBoundsResponseBuffering()
    {
        using var http = new HttpClient(new Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 129), Encoding.UTF8, "text/xml"),
        }));
        var transport = new HttpCellBridgeTransport(http, Origin, maxResponseBytes: 128);

        await Assert.ThrowsAsync<HttpRequestException>(() => transport.ExecuteAsync(WhoAmI()));
    }

    [Fact]
    public async Task RealTimeTicketStaysOnTheConfiguredOrigin()
    {
        using var http = new HttpClient(new Respond(_ => throw new InvalidOperationException("Unexpected RTC request.")));
        var transport = new HttpCellBridgeTransport(http, Origin);
        var target = new RealTimeConnectionTarget(new Uri("https://other.test/_cellbridge/rtc/"), "ticket", null,
            Guid.NewGuid().ToString("B"));

        Assert.Null(await transport.TryConnectRealTimeAsync(target));
    }

    private static CellStorageRequest WhoAmI() => new()
    {
        CorrelationId = Guid.NewGuid(),
        Requests =
        [
            new FssHttpRequest
            {
                Url = new Uri(Origin, "/shared/document.xlsx").AbsoluteUri,
                RequestToken = 1,
                SubRequests = [new() { Type = SubRequestType.WhoAmI, SubRequestToken = 1 }],
            },
        ],
    };

    private sealed class Respond(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
