using System.Net;
using System.Text;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CellBridge.FssHttp.Tests;

public sealed class OfficeDocumentDiscoveryTests
{
    private static readonly XNamespace Dav = "DAV:";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllpropReturnsAuthorizedPersistedFileMetadataWithCanonicalHref(bool mounted)
    {
        await using var fixture = await Fixture.Start(enabled: true, mounted);
        var head = (await fixture.Service.CreateAsync("/shared/annual report.docx", [1, 2, 3, 4, 5], TestActor.Value))!;
        using var client = fixture.Client("writer");
        using var response = await fixture.PropFind(client, head.Path, "<D:propfind xmlns:D='DAV:'><D:allprop/></D:propfind>");

        Assert.Equal((HttpStatusCode)207, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("private", response.Headers.CacheControl?.ToString());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var multistatus = Assert.IsType<XElement>(xml.Root);
        Assert.Equal(Dav + "multistatus", multistatus.Name);
        var resourceResponse = Assert.Single(multistatus.Elements(Dav + "response"));
        var href = Assert.Single(resourceResponse.Elements(Dav + "href")).Value;
        Assert.Equal("https://office.example" + (mounted ? "/mounted" : "") + "/shared/annual%20report.docx", href);

        var propstat = Assert.Single(resourceResponse.Elements(Dav + "propstat"));
        Assert.Equal("HTTP/1.1 200 OK", Assert.Single(propstat.Elements(Dav + "status")).Value);
        var properties = Assert.Single(propstat.Elements(Dav + "prop")).Elements().ToArray();
        Assert.Equal(new[] { "displayname", "getcontentlength", "getetag", "getlastmodified", "creationdate", "resourcetype", "ResourceId", "resourceId", "repl-uid", "resourcetag", "getcontenttype" },
            properties.Select(property => property.Name.LocalName));
        Assert.All(properties.Take(7), property => Assert.Equal(Dav, property.Name.Namespace));
        Assert.Equal("annual report.docx", properties[0].Value);
        Assert.Equal(head.Package.Length.ToString(), properties[1].Value);
        Assert.Equal(head.Etag, properties[2].Value);
        Assert.Equal(head.ModifiedUtc.ToUniversalTime().ToString("R"), properties[3].Value);
        Assert.Equal(head.CreatedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), properties[4].Value);
        Assert.Empty(properties[5].Elements());
        Assert.Equal($"{{{head.ResourceId:D}}}", properties[6].Value);
        Assert.Equal(XName.Get("resourceId", "http://schemas.microsoft.com/clouddocuments"), properties[7].Name);
        Assert.Equal(properties[6].Value, properties[7].Value);
        Assert.Equal(XName.Get("repl-uid", "http://schemas.microsoft.com/repl/"), properties[8].Name);
        Assert.Equal($"rid:{{{head.ResourceId:D}}}", properties[8].Value);
        Assert.Equal($"rt:{head.ResourceId:D}@{head.ContentVersion:D11}", properties[9].Value);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", properties[10].Value);
    }

    [Fact]
    public async Task ExplicitPropertySelectionGroupsUnknownPropertiesInNotFoundPropstat()
    {
        await using var fixture = await Fixture.Start();
        using var client = fixture.Client("writer");
        using var response = await fixture.PropFind(client, fixture.Head.Path,
            "<D:propfind xmlns:D='DAV:' xmlns:X='urn:test'><D:prop><D:getetag/><X:unsupported/></D:prop></D:propfind>");

        Assert.Equal((HttpStatusCode)207, response.StatusCode);
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var propstats = xml.Descendants(Dav + "propstat").ToArray();
        Assert.Equal(2, propstats.Length);
        Assert.Equal("HTTP/1.1 200 OK", propstats[0].Element(Dav + "status")?.Value);
        Assert.Equal(Dav + "getetag", Assert.Single(propstats[0].Element(Dav + "prop")!.Elements()).Name);
        Assert.Equal("HTTP/1.1 404 Not Found", propstats[1].Element(Dav + "status")?.Value);
        Assert.Equal(XName.Get("unsupported", "urn:test"), Assert.Single(propstats[1].Element(Dav + "prop")!.Elements()).Name);
    }

    [Fact]
    public async Task AnonymousAndDeniedFileMetadataAreNotRevealed()
    {
        await using var fixture = await Fixture.Start();
        using var anonymous = new HttpClient(fixture.Server.CreateHandler()) { BaseAddress = fixture.Server.BaseAddress };
        using (var response = await fixture.PropFind(anonymous, fixture.Head.Path, "<D:propfind xmlns:D='DAV:'><D:allprop/></D:propfind>"))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var denied = fixture.Client("other");
        using (var response = await fixture.PropFind(denied, fixture.Head.Path, "<D:propfind xmlns:D='DAV:'><D:allprop/></D:propfind>"))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using (var response = await fixture.PropFind(denied, "/shared/missing.docx", "<D:propfind xmlns:D='DAV:'><D:allprop/></D:propfind>"))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("<D:propfind xmlns:D='DAV:'><D:prop>")]
    [InlineData("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///secret'>]><D:propfind xmlns:D='DAV:'><D:allprop/></D:propfind>")]
    [InlineData("<D:propfind xmlns:D='DAV:'><D:allprop/><D:allprop/></D:propfind>")]
    [InlineData("<D:propfind xmlns:D='DAV:'><D:allprop><D:x/></D:allprop></D:propfind>")]
    public async Task MalformedAndUnsupportedXmlIsRejected(string body)
    {
        await using var fixture = await Fixture.Start();
        using var client = fixture.Client("writer");
        using var response = await fixture.PropFind(client, fixture.Head.Path, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ExcessiveBodyAndDepthAreRejected()
    {
        await using var fixture = await Fixture.Start();
        using var client = fixture.Client("writer");
        var oversized = "<D:propfind xmlns:D='DAV:'><D:allprop/></D:propfind>" + new string(' ', 16 * 1024);
        using (var body = await fixture.PropFind(client, fixture.Head.Path, oversized))
            Assert.Equal(HttpStatusCode.BadRequest, body.StatusCode);

        const string requestBody = "<D:propfind xmlns:D='DAV:'><D:allprop/></D:propfind>";
        foreach (var depth in new[] { null, "1", "infinity" })
        {
            using var response = await fixture.PropFind(client, fixture.Head.Path, requestBody, depth);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task DisabledExperimentDoesNotMapPropfind()
    {
        await using var fixture = await Fixture.Start(enabled: false);
        using var client = fixture.Client("writer");
        using var response = await fixture.PropFind(client, fixture.Head.Path,
            "<D:propfind xmlns:D='DAV:'><D:allprop/></D:propfind>");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.False(response.Headers.Contains("MS-Storage"));
        Assert.False(response.Headers.Contains("MS-Author-Via"));
    }

    private sealed class Fixture(WebApplication app, HttpClient client, DocumentHead head) : IAsyncDisposable
    {
        internal TestServer Server => app.GetTestServer();
        internal CellBridgeDocumentService Service => app.Services.GetRequiredService<CellBridgeDocumentService>();
        internal DocumentHead Head => head;
        internal HttpClient Client(string user) => TestActor.CreateClient(Server, user);

        internal Task<HttpResponseMessage> PropFind(HttpClient client, string path, string body, string? depth = "0")
        {
            var requestPath = path;
            if (!requestPath.StartsWith('/')) requestPath = "/" + requestPath;
            if (Mounted)
                requestPath = "/mounted" + requestPath;
            var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), requestPath)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/xml"),
            };
            if (depth is not null) request.Headers.TryAddWithoutValidation("Depth", depth);
            return client.SendAsync(request);
        }

        internal bool Mounted { get; init; }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }

        internal static async Task<Fixture> Start(bool enabled = true, bool mounted = false)
        {
            var content = new InMemoryContentStore();
            var provider = new StorageProvider(new InMemoryDocumentRepository(content), content);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
            TestActor.Register(builder.Services);
            builder.Services.AddCellBridge(provider, requireDurability: false, configure: options =>
            {
                options.EnableOfficeRealTime = enabled;
                options.PublicOrigin = "https://office.example";
            });
            var app = builder.Build();
            if (mounted) app.UsePathBase("/mounted");
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapCellBridge();
            var service = app.Services.GetRequiredService<CellBridgeDocumentService>();
            var head = (await service.CreateAsync("/shared/discovery.docx", [1, 2, 3], TestActor.Value))!;
            await app.StartAsync();
            return new Fixture(app, app.GetTestClient(), head) { Mounted = mounted };
        }
    }
}
