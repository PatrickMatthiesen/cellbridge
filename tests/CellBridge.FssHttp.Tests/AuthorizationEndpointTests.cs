using System.Net;
using System.Text;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.FssHttp.Tests;

public sealed class AuthorizationEndpointTests
{
    private static readonly XNamespace Protocol = CellStorageRequest.Namespace;

    [Theory]
    [InlineData("GET", "/shared/secret.bin")]
    [InlineData("HEAD", "/shared/secret.bin")]
    [InlineData("OPTIONS", "/shared")]
    [InlineData("POST", "/_vti_bin/cellstorage.svc")]
    [InlineData("POST", "/shared/secret.bin/_vti_bin/cellstorage.svc")]
    public async Task AnonymousRequestsAreChallengedBeforeBodyParsing(string method, string path)
    {
        await using var fixture = await Fixture.Start();
        using var request = new HttpRequestMessage(new(method), path) { Content = new StringContent("invalid SOAP") };
        using var result = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task ReadPermissionControlsBytesAndHeaders(string method)
    {
        await using var fixture = await Fixture.Start();
        foreach (var user in new[] { "reader", "other" })
        {
            using var request = new HttpRequestMessage(new(method), "/shared/secret.bin");
            request.Headers.Add("X-Test-User", user);
            using var result = await fixture.Client.SendAsync(request);
            Assert.Equal(user == "reader" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, result.StatusCode);
            if (user == "other") { Assert.Null(result.Headers.ETag); Assert.Null(result.Content.Headers.LastModified); }
            else if (method == "GET") Assert.Equal(new byte[] { 1, 2, 3 }, await result.Content.ReadAsByteArrayAsync());
        }
    }

    [Theory]
    [InlineData("writer", true, true)]
    [InlineData("reader", true, false)]
    [InlineData("other", false, false)]
    public async Task QueryAccessReportsIndependentPermissionsAndDoesNotLeakMetadata(string user, bool read, bool write)
    {
        await using var fixture = await Fixture.Start();
        var cell = new FsshttpbCellRequest();
        cell.SubRequests.Add(new(RequestTypes.QueryAccess) { RequestId = 7, Data = new QueryAccessSubRequestData() });
        var xml = await fixture.Send(user, new FssHttpSubRequest { Type = SubRequestType.Cell,
            SubRequestDataBinary = cell.ToByteArray(), SubRequestDataAttributes = { ["GetFileProps"] = "true" } });
        var data = xml.Descendants(Protocol + "SubResponseData").Single();
        var binary = FsshttpbResponse.Deserialize(new BinaryReaderEx(Convert.FromBase64String(data.Value)));
        var access = Assert.IsType<QueryAccessSubResponseData>(Assert.Single(binary.SubResponses).Data);
        Assert.Equal(read, access.ReadAccessError is null);
        Assert.Equal(write, access.WriteAccessError is null);
        if (!write) Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, access.WriteAccessError!.ErrorCode);
        var file = xml.Descendants(Protocol + "Response").Single();
        if (!read)
        {
            Assert.Null(file.Attribute("ResourceID"));
            Assert.Empty(data.Attributes());
            Assert.Equal("http://localhost/shared/supplied-only.bin", (string?)file.Attribute("Url"));
            Assert.Equal("FileUnauthorizedAccess", (string?)xml.Descendants(Protocol + "ResponseVersion").Single().Attribute("ErrorCode"));
        }
        else Assert.NotNull(file.Attribute("ResourceID"));
    }

    [Fact]
    public async Task WhoAmIUsesCallerWhileMetadataUsesPersistedAuthors()
    {
        await using var fixture = await Fixture.Start();
        var response = await fixture.Send("reader", new() { Type = SubRequestType.WhoAmI, SubRequestToken = 1 },
            new() { Type = SubRequestType.GetDocMetaInfo, SubRequestToken = 2 });
        var who = response.Descendants(Protocol + "SubResponse").First().Element(Protocol + "SubResponseData")!;
        Assert.Equal("test-reader", (string?)who.Attribute("UserLogin"));
        var props = response.Descendants().Where(p => p.Name.LocalName == "Property").ToDictionary(p => (string)p.Attribute("Key")!, p => (string)p.Attribute("Value")!);
        Assert.Equal("test-writer", props["vti_author"]);
        Assert.Equal("test-writer", props["vti_modifiedby"]);
    }

    [Fact]
    public async Task AnotherWriterCannotJoinOrLeaveAnotherUsersEditorSession()
    {
        await using var fixture = await Fixture.Start(otherCanWrite: true);
        var clientId = Guid.NewGuid();
        FssHttpSubRequest Request(string operation) => new()
        {
            Type = SubRequestType.EditorsTable, SubRequestToken = 1,
            SubRequestDataAttributes = { ["EditorsTableRequestType"] = operation, ["ClientID"] = clientId.ToString(),
                ["Timeout"] = "60", ["AsEditor"] = "true" },
        };
        Assert.Equal("Success", Error(await fixture.Send("writer", Request("JoinEditingSession"))));
        var before = await fixture.Provider.State.FindByResourceIdAsync(fixture.State.ResourceId);
        foreach (var operation in new[] { "JoinEditingSession", "RefreshEditingSession", "LeaveEditingSession" })
            Assert.Equal("FileUnauthorizedAccess", Error(await fixture.Send("other", Request(operation))));
        var after = await fixture.Provider.State.FindByResourceIdAsync(fixture.State.ResourceId);
        Assert.Equal(before, after);
        Assert.Equal(TestActor.Value.Identity, Assert.Single(after!.Editors).Owner);
    }

    [Theory]
    [InlineData("OnSuccess", "DependentOnlyOnSuccessRequestFailed")]
    [InlineData("OnFail", "Success")]
    [InlineData("OnExecute", "Success")]
    public async Task DenialPreservesDependencyRules(string dependency, string expected)
    {
        await using var fixture = await Fixture.Start();
        var response = await fixture.Send("reader",
            new() { Type = SubRequestType.ExclusiveLock, SubRequestToken = 1,
                SubRequestDataAttributes = { ["ExclusiveLockRequestType"] = "GetLock", ["ExclusiveLockID"] = Guid.NewGuid().ToString() } },
            new() { Type = SubRequestType.WhoAmI, SubRequestToken = 2, DependsOn = 1, DependencyType = dependency });
        var results = response.Descendants(Protocol + "SubResponse").ToArray();
        Assert.Equal("FileUnauthorizedAccess", (string?)results[0].Attribute("ErrorCode"));
        Assert.Equal(expected, (string?)results[1].Attribute("ErrorCode"));
    }

    [Theory]
    [InlineData("text/plain", null)]
    [InlineData("application/x-www-form-urlencoded", null)]
    [InlineData("text/xml", "https://attacker.example")]
    [InlineData("text/xml", "null")]
    public async Task BrowserSimpleAndCrossOriginPostsAreRejected(string contentType, string? origin)
    {
        await using var fixture = await Fixture.Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_vti_bin/cellstorage.svc")
        { Content = new StringContent("<fake/>", Encoding.UTF8, contentType) };
        request.Headers.Add("X-Test-User", "writer");
        if (origin is not null) request.Headers.Add("Origin", origin);
        using var result = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, result.StatusCode);
    }

    private static string? Error(XDocument xml) => (string?)xml.Descendants(Protocol + "SubResponse").Single().Attribute("ErrorCode");

    private sealed class Fixture(WebApplication app, StorageProvider provider, DocumentState state, HttpClient client) : IAsyncDisposable
    {
        public StorageProvider Provider => provider;
        public DocumentState State => state;
        public HttpClient Client => client;
        public static async Task<Fixture> Start(bool otherCanWrite = false)
        {
            var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            TestActor.Register(builder.Services);
            builder.Services.AddCellBridge(provider, requireDurability: false);
            var app = builder.Build();
            app.UseAuthentication(); app.UseAuthorization(); app.MapCellBridge();
            var service = app.Services.GetRequiredService<CellBridgeDocumentService>();
            var state = (await service.CreateAsync("/shared/secret.bin", [1, 2, 3], TestActor.Value))!;
            await provider.State.TransitionAsync(state.ResourceId, (current, _) => new StateTransition<bool>(current with
            { Security = current.Security with { Grants = current.Security.Grants.Add("tests:reader", DocumentAccess.Read)
                .Add("tests:other", otherCanWrite ? DocumentAccess.Read | DocumentAccess.Write : DocumentAccess.None) } }, true));
            await app.StartAsync();
            return new(app, provider, state, app.GetTestClient());
        }

        public async Task<XDocument> Send(string user, params FssHttpSubRequest[] requests)
        {
            XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
            var file = new XElement(Protocol + "Request", new XAttribute("Url", "http://localhost/shared/supplied-only.bin"), new XAttribute("RequestToken", 1),
                new XAttribute("UseResourceID", "true"), new XAttribute("ResourceID", state.ResourceId.ToString("N")));
            foreach (var r in requests)
            {
                var sub = new XElement(Protocol + "SubRequest", new XAttribute("Type", r.Type), new XAttribute("SubRequestToken", r.SubRequestToken ?? 1));
                if (r.DependsOn is { } depends) sub.Add(new XAttribute("DependsOn", depends), new XAttribute("DependencyType", r.DependencyType!));
                var data = new XElement(Protocol + "SubRequestData", r.SubRequestDataAttributes.Select(p => new XAttribute(p.Key, p.Value)));
                if (r.SubRequestDataBinary is { } binary) data.Add(Convert.ToBase64String(binary));
                sub.Add(data); file.Add(sub);
            }
            var envelope = new XElement(soap + "Envelope", new XElement(soap + "Body", new XElement(Protocol + "RequestCollection", file)));
            using var request = new HttpRequestMessage(HttpMethod.Post, "/_vti_bin/cellstorage.svc")
            { Content = new StringContent(envelope.ToString(), Encoding.UTF8, "text/xml") };
            request.Headers.Add("X-Test-User", user);
            using var result = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            return XDocument.Parse(await result.Content.ReadAsStringAsync());
        }

        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }
    }
}
