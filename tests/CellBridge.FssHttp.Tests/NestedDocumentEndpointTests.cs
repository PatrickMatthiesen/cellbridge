using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.FssHttp.Tests;

public sealed class NestedDocumentEndpointTests
{
    private static readonly XNamespace Protocol = "http://schemas.microsoft.com/sharepoint/soap/";

    [Theory]
    [InlineData("/shared/report.docx", "/shared/report.docx")]
    [InlineData("/shared/team/a/report.docx", "/shared/team/a/report.docx")]
    [InlineData("/shared/team/b/report.docx", "/shared/team/b/report.docx")]
    [InlineData("/shared/team/a/deeper/one/more/report.docx", "/shared/team/a/deeper/one/more/report.docx")]
    [InlineData("/shared/team/a/space%20name/%E6%9D%B1%E4%BA%AC%23%25.docx", "/shared/team/a/space name/東京#%.docx")]
    public async Task NestedAndEncodedDownloadsResolveTheExactDocument(string target, string storedPath)
    {
        await using var fixture = await Fixture.Start();
        var expected = Encoding.UTF8.GetBytes(storedPath);
        await fixture.Create(storedPath, expected);
        using var result = await fixture.Send(HttpMethod.Get, target);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(expected, await result.Content.ReadAsByteArrayAsync());
        using var head = await fixture.Send(HttpMethod.Head, target);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(expected.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task DuplicateFileNamesInDifferentDirectoriesStayIndependent()
    {
        await using var fixture = await Fixture.Start();
        await fixture.Create("/shared/alpha/report.docx", [1]);
        await fixture.Create("/shared/beta/report.docx", [2]);
        using var first = await fixture.Send(HttpMethod.Get, "/shared/alpha/report.docx");
        using var second = await fixture.Send(HttpMethod.Get, "/shared/beta/report.docx");
        Assert.Equal(new byte[] { 1 }, await first.Content.ReadAsByteArrayAsync());
        Assert.Equal(new byte[] { 2 }, await second.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("/mount")]
    public async Task DirectoryOptionsWithOrWithoutTrailingSlashAdvertisesMountedLibrary(string mount)
    {
        await using var fixture = await Fixture.Start(mount);
        foreach (var suffix in new[] { "/shared", "/shared/", "/shared/teams/2026", "/shared/teams/2026/" })
        {
            using var result = await fixture.Send(HttpMethod.Options, mount + suffix);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            Assert.Equal("1.0", Assert.Single(result.Headers.GetValues("X-MSFSSHTTP")));
            Assert.Equal("http://localhost" + mount + "/shared/", Assert.Single(result.Headers.GetValues("X-MSGETWEBURL")));
        }
    }

    [Theory]
    [InlineData("/_vti_bin/cellstorage.svc")]
    [InlineData("/_vti_bin/cellstorage.svc/extra")]
    [InlineData("/shared/team/report.docx/_vti_bin/cellstorage.svc")]
    [InlineData("/shared/team/report.docx/_VTI_BIN/CELLSTORAGE.SVC/extra")]
    public async Task ProtocolRoutesAcceptValidNestedDocumentRequests(string protocolPath)
    {
        await using var fixture = await Fixture.Start();
        await fixture.Create("/shared/team/report.docx", [7, 8, 9]);
        using var result = await fixture.Soap(protocolPath, "/shared/team/report.docx", "GetDocMetaInfo");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var xml = XDocument.Parse(await result.Content.ReadAsStringAsync());
        Assert.Equal("Success", Error(xml));
        Assert.Equal("3", xml.Descendants().Where(x => x.Name.LocalName == "Property")
            .Single(x => (string?)x.Attribute("Key") == "vti_filesize").Attribute("Value")?.Value);
    }

    [Fact]
    public async Task MountedSoapUrlResolvesAndReturnsMountedCanonicalAndHistoryUrls()
    {
        await using var fixture = await Fixture.Start("/mount");
        await fixture.Create("/shared/deep/file.docx", [1, 2, 3, 4]);
        using var result = await fixture.Soap("/mount/shared/deep/file.docx/_vti_bin/cellstorage.svc",
            "http://localhost/mount/shared/deep/file.docx", "GetVersions");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var xml = XDocument.Parse(await result.Content.ReadAsStringAsync());
        Assert.Equal("Success", Error(xml));
        Assert.Equal("http://localhost/mount/shared/deep/file.docx",
            (string?)xml.Descendants(Protocol + "Response").Single().Attribute("Url"));
        var history = xml.Descendants().Single(x => x.Name.LocalName == "result").Attribute("url")?.Value;
        Assert.StartsWith("http://localhost/mount/_cellbridge/history/", history);
        using var historical = await fixture.Send(HttpMethod.Get, history!);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await historical.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task SuppliedResourceIdNeverFallsBackToItsUrl()
    {
        await using var fixture = await Fixture.Start();
        await fixture.Create("/shared/deep/file.docx", [1]);
        foreach (var id in new string?[] { Guid.NewGuid().ToString("D"), "not-a-guid", "", null })
        {
            using var result = await fixture.Soap("/shared/deep/file.docx/_vti_bin/cellstorage.svc",
                "/shared/deep/file.docx", "GetDocMetaInfo", useResourceId: true, resourceId: id);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            Assert.Equal("FileNotExistsOrCannotBeCreated", FileError(XDocument.Parse(await result.Content.ReadAsStringAsync())));
        }
    }

    [Theory]
    [InlineData("/shared/deep/../file.docx")]
    [InlineData("/shared/deep/%2E%2E/file.docx")]
    [InlineData("/shared/deep/%2Ffile.docx")]
    [InlineData("/shared/deep/%5Cfile.docx")]
    [InlineData("/shared//deep/file.docx")]
    [InlineData("/shared/deep/bad%escape.docx")]
    [InlineData("/shared/deep/bad%FF.docx")]
    public async Task InvalidSoapDocumentUrlsDoNotResolveOrExecute(string documentUrl)
    {
        await using var fixture = await Fixture.Start();
        await fixture.Create("/shared/deep/file.docx", [1, 2, 3]);
        using var result = await fixture.Soap("/_vti_bin/cellstorage.svc", documentUrl, "GetDocMetaInfo");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var xml = XDocument.Parse(await result.Content.ReadAsStringAsync());
        Assert.Equal("FileNotExistsOrCannotBeCreated", FileError(xml));
        Assert.Empty(xml.Descendants(Protocol + "SubResponse"));
    }

    [Theory]
    [InlineData("/shared/deep/literal%252Fname.docx", "/shared/deep/literal%2Fname.docx")]
    [InlineData("/shared/deep/space%20name.docx", "/shared/deep/space name.docx")]
    [InlineData("/shared/deep/%E6%9D%B1%E4%BA%AC%23%25.docx", "/shared/deep/東京#%.docx")]
    public async Task EscapedSoapDocumentUrlsResolveLiteralFileNames(string documentUrl, string storedPath)
    {
        await using var fixture = await Fixture.Start();
        var bytes = Encoding.UTF8.GetBytes(storedPath);
        await fixture.Create(storedPath, bytes);
        using var result = await fixture.Soap("/_vti_bin/cellstorage.svc", documentUrl, "GetDocMetaInfo");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var xml = XDocument.Parse(await result.Content.ReadAsStringAsync());
        Assert.Equal("Success", Error(xml));
        Assert.Equal(bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            xml.Descendants().Single(x => x.Name.LocalName == "Property" &&
                (string?)x.Attribute("Key") == "vti_filesize").Attribute("Value")?.Value);
        Assert.Equal("http://localhost" + documentUrl,
            (string?)xml.Descendants(Protocol + "Response").Single().Attribute("Url"));
    }

    [Fact]
    public async Task MovingDocumentRetiresOldDownloadButResourceIdStillResolves()
    {
        await using var fixture = await Fixture.Start();
        var state = await fixture.Create("/shared/old/place.docx", [5, 6]);
        await fixture.Service.MoveDocumentAsync(state.ResourceId, "/shared/new/deep/place.docx", TestActor.Value);
        using var oldDownload = await fixture.Send(HttpMethod.Get, "/shared/old/place.docx");
        Assert.Equal(HttpStatusCode.NotFound, oldDownload.StatusCode);
        using var newDownload = await fixture.Send(HttpMethod.Get, "/shared/new/deep/place.docx");
        Assert.Equal(new byte[] { 5, 6 }, await newDownload.Content.ReadAsByteArrayAsync());
        using var oldSoap = await fixture.Soap("/shared/old/place.docx/_vti_bin/cellstorage.svc",
            "/shared/old/place.docx", "GetDocMetaInfo", useResourceId: true, resourceId: state.ResourceId.ToString("D"));
        var resolved = XDocument.Parse(await oldSoap.Content.ReadAsStringAsync());
        Assert.Equal("Success", Error(resolved));
        Assert.Equal("http://localhost/shared/new/deep/place.docx",
            (string?)resolved.Descendants(Protocol + "Response").Single().Attribute("Url"));
        using var pathOnly = await fixture.Soap("/shared/old/place.docx/_vti_bin/cellstorage.svc",
            "/shared/old/place.docx", "GetDocMetaInfo");
        Assert.Equal("FileNotExistsOrCannotBeCreated", FileError(XDocument.Parse(await pathOnly.Content.ReadAsStringAsync())));
        await fixture.Provider.State.TransitionAsync(state.ResourceId, (current, _) =>
            new StateTransition<bool>(current with { Security = current.Security with
                { Owner = null, Grants = current.Security.Grants.SetItem("tests:writer", DocumentAccess.None) } }, true));
        using var revoked = await fixture.Soap("/shared/old/place.docx/_vti_bin/cellstorage.svc",
            "/shared/old/place.docx", "GetDocMetaInfo", useResourceId: true, resourceId: state.ResourceId.ToString("D"));
        Assert.Equal("FileUnauthorizedAccess", FileError(XDocument.Parse(await revoked.Content.ReadAsStringAsync())));
    }

    [Fact]
    public async Task RevokedReadPermissionDeniesNestedDownloadAndSoap()
    {
        await using var fixture = await Fixture.Start();
        var state = await fixture.Create("/shared/deep/private.docx", [1]);
        using var before = await fixture.Send(HttpMethod.Get, "/shared/deep/private.docx");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        await fixture.Provider.State.TransitionAsync(state.ResourceId, (current, _) =>
            new StateTransition<bool>(current with { Security = current.Security with
                { Owner = null, Grants = current.Security.Grants.SetItem("tests:writer", DocumentAccess.None) } }, true));
        using var download = await fixture.Send(HttpMethod.Get, "/shared/deep/private.docx");
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        using var soap = await fixture.Soap("/shared/deep/private.docx/_vti_bin/cellstorage.svc",
            "/shared/deep/private.docx", "GetDocMetaInfo");
        Assert.Equal("FileUnauthorizedAccess", FileError(XDocument.Parse(await soap.Content.ReadAsStringAsync())));
    }

    [Fact]
    public async Task HostGetRouteWinsWhileDirectoryDiscoveryAndOrdinaryPostRemainDeliberate()
    {
        await using var fixture = await Fixture.Start();
        using var host = await fixture.Send(HttpMethod.Get, "/shared/deep/health");
        Assert.Equal("host", await host.Content.ReadAsStringAsync());
        using var options = await fixture.Send(HttpMethod.Options, "/shared/deep/health");
        Assert.Equal("http://localhost/shared/", Assert.Single(options.Headers.GetValues("X-MSGETWEBURL")));
        using var post = await fixture.Send(HttpMethod.Post, "/shared/deep/health");
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }

    [Theory]
    [InlineData("/shared/deep/file.docx", 200)]
    [InlineData("/shared/deep/literal%252Fname.docx", 200)]
    [InlineData("/shared/deep/literal%2Fname.docx", 400)]
    [InlineData("/shared//deep/file.docx", 400)]
    [InlineData("/shared/deep/../file.docx", 400)]
    [InlineData("/shared/deep/%2E%2E/file.docx", 400)]
    [InlineData("/shared/deep/%5Cfile.docx", 400)]
    [InlineData("/shared/deep/bad%escape.docx", 400)]
    [InlineData("/shared/deep/bad%FF.docx", 400)]
    [InlineData("/shared/deep/file.docx//", 400)]
    [InlineData("/shared/deep/file.docx/_vti_bin/cellstorage.svc/%2F", 400)]
    [InlineData("/shared/deep/file.docx/_vti_bin/cellstorage.svc//extra", 400)]
    public async Task KestrelPreservesRawRequestTargetValidation(string target, int expected)
    {
        await using var fixture = await Fixture.Start(kestrel: true);
        await fixture.Create("/shared/deep/file.docx", [1]);
        await fixture.Create("/shared/deep/literal%2Fname.docx", [2]);
        var response = await fixture.Raw("GET", target);
        Assert.Equal(expected, RawStatus(response));
        if (expected == 200) Assert.Contains(target.Contains("literal", StringComparison.Ordinal) ? "\r\n\r\n\u0002" : "\r\n\r\n\u0001", response);
    }

    [Theory]
    [InlineData("/shared/deep/file.docx/_vti_bin/cellstorage.svc", 200)]
    [InlineData("/shared/deep/file.docx/_vti_bin/cellstorage.svc/%2F", 400)]
    [InlineData("/shared/deep/file.docx/_vti_bin/cellstorage.svc//extra", 400)]
    [InlineData("/_vti_bin/cellstorage.svc/%2E%2E/extra", 404)]
    [InlineData("/_vti_bin/cellstorage.svc/bad%escape", 400)]
    public async Task KestrelValidatesRawProtocolTargetsBeforeExecutingSoap(string target, int expected)
    {
        await using var fixture = await Fixture.Start(kestrel: true);
        await fixture.Create("/shared/deep/file.docx", [1]);
        var response = await fixture.Raw("POST", target, SoapBody(), "text/xml");
        Assert.Equal(expected, RawStatus(response));
        if (expected == 200) Assert.Contains("vti_filesize", response);
    }

    private static int RawStatus(string response) => int.Parse(response.Split("\r\n", 2)[0].Split(' ')[1]);
    private static string? Error(XDocument xml) => (string?)xml.Descendants(Protocol + "SubResponse").Single().Attribute("ErrorCode");
    private static string? FileError(XDocument xml) => (string?)xml.Descendants(Protocol + "Response").Single().Attribute("ErrorCode");
    private static string SoapBody() => """
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
          <s:Body>
            <RequestVersion xmlns="http://schemas.microsoft.com/sharepoint/soap/" Version="2" MinorVersion="2" />
            <RequestCollection xmlns="http://schemas.microsoft.com/sharepoint/soap/" CorrelationId="6B29FC40-CA47-1067-B31D-00DD010662DA">
              <Request Url="/shared/deep/file.docx" RequestToken="1">
                <SubRequest Type="GetDocMetaInfo" SubRequestToken="1" />
              </Request>
            </RequestCollection>
          </s:Body>
        </s:Envelope>
        """;

    private sealed class Fixture(WebApplication app, StorageProvider provider, HttpClient client, int port) : IAsyncDisposable
    {
        public StorageProvider Provider => provider;
        public CellBridgeDocumentService Service => app.Services.GetRequiredService<CellBridgeDocumentService>();

        public static async Task<Fixture> Start(string mount = "", bool kestrel = false)
        {
            var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
            var builder = WebApplication.CreateBuilder();
            if (kestrel) builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            else builder.WebHost.UseTestServer();
            TestActor.Register(builder.Services);
            builder.Services.AddCellBridge(provider, requireDurability: false);
            var app = builder.Build();
            if (mount.Length > 0) app.UsePathBase(mount);
            app.Use(async (context, next) =>
            {
                if (context.Request.Headers["X-Test-User"] == "writer")
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([
                        new(CellBridgeActor.SubjectClaim, "tests:writer"),
                        new(ClaimTypes.Name, "test-writer"),
                        new(CellBridgeActor.DisplayNameClaim, "Test writer"),
                        new(CellBridgeActor.CreateClaim, "true")], "test"));
                await next(context);
            });
            app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
            app.MapCellBridge();
            app.MapGet("/shared/deep/health", () => "host").RequireAuthorization();
            await app.StartAsync();
            var port = kestrel ? new Uri(app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single()).Port : 0;
            var client = kestrel ? new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") } : app.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Test-User", "writer");
            return new(app, provider, client, port);
        }

        public async Task<DocumentState> Create(string decodedPath, byte[] bytes) =>
            (await Service.CreateAsync(EncodePath(decodedPath), bytes, TestActor.Value))!;

        public Task<HttpResponseMessage> Send(HttpMethod method, string path) => client.SendAsync(new HttpRequestMessage(method, path));

        public Task<HttpResponseMessage> Soap(string target, string documentUrl, string operation,
            bool useResourceId = false, string? resourceId = null)
        {
            XNamespace envelope = "http://schemas.xmlsoap.org/soap/envelope/";
            var file = new XElement(Protocol + "Request", new XAttribute("Url", documentUrl), new XAttribute("RequestToken", 1),
                useResourceId ? new XAttribute("UseResourceID", "true") : null,
                resourceId is null ? null : new XAttribute("ResourceID", resourceId),
                new XElement(Protocol + "SubRequest", new XAttribute("Type", operation), new XAttribute("SubRequestToken", 1)));
            var xml = new XElement(envelope + "Envelope", new XElement(envelope + "Body",
                new XElement(Protocol + "RequestVersion", new XAttribute("Version", 2), new XAttribute("MinorVersion", 2)),
                new XElement(Protocol + "RequestCollection", new XAttribute("CorrelationId", Guid.NewGuid()), file)));
            var request = new HttpRequestMessage(HttpMethod.Post, target)
                { Content = new StringContent(xml.ToString(), Encoding.UTF8, "text/xml") };
            return client.SendAsync(request);
        }

        public async Task<string> Raw(string method, string target, string? body = null, string? contentType = null)
        {
            using var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, port);
            await using var stream = socket.GetStream();
            var payload = Encoding.UTF8.GetBytes(body ?? "");
            var headers = $"{method} {target} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nX-Test-User: writer\r\nConnection: close\r\n" +
                (body is null ? "" : $"Content-Type: {contentType}\r\nContent-Length: {payload.Length}\r\n") + "\r\n";
            var request = Encoding.ASCII.GetBytes(headers);
            await stream.WriteAsync(request);
            if (body is not null) await stream.WriteAsync(payload);
            using var output = new MemoryStream();
            await stream.CopyToAsync(output);
            return Encoding.Latin1.GetString(output.ToArray());
        }

        private static string EncodePath(string decodedPath) => string.Join('/', decodedPath.Split('/').Select(Uri.EscapeDataString));
        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }
    }
}
