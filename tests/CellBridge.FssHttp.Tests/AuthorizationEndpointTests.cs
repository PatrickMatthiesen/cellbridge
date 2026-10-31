using System.Net;
using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.FssHttp.Tests;

public sealed partial class AuthorizationEndpointTests
{
    private static readonly XNamespace Protocol = CellStorageRequest.Namespace;

    [Theory]
    [InlineData("writer", "/shared")]
    [InlineData("reader", "/shared")]
    [InlineData("writer", "/")]
    [InlineData("reader", "/")]
    [InlineData("writer", "/_vti_bin/cellstorage.svc/CellStorageService")]
    [InlineData("reader", "/shared/secret.bin/_vti_bin/cellstorage.svc/CellStorageService")]
    public async Task AuthenticatedDiscoveryAdvertisesTheSelectedProtocol(string user, string path)
    {
        await using var fixture = await Fixture.Start();
        using var client = fixture.CreateClient(user);
        using var request = new HttpRequestMessage(HttpMethod.Options, path);
        using var result = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("1.5", Assert.Single(result.Headers.GetValues("X-MSFSSHTTP")));
        Assert.Equal("1", Assert.Single(result.Headers.GetValues("MS-Storage")));
        Assert.Equal("CellBridge", Assert.Single(result.Headers.GetValues("X-MSNSERVER")));
    }

    [Theory]
    [InlineData("GET", "/shared/secret.bin")]
    [InlineData("HEAD", "/shared/secret.bin")]
    [InlineData("OPTIONS", "/shared")]
    [InlineData("OPTIONS", "/")]
    [InlineData("OPTIONS", "/_vti_bin/cellstorage.svc/CellStorageService")]
    [InlineData("OPTIONS", "/shared/secret.bin/_vti_bin/cellstorage.svc/CellStorageService")]
    [InlineData("GET", "/_cellbridge/history/settings")]
    [InlineData("POST", "/_vti_bin/cellstorage.svc")]
    [InlineData("POST", "/_vti_bin/sharedaccess.asmx")]
    [InlineData("POST", "/shared/secret.bin/_vti_bin/sharedaccess.asmx")]
    [InlineData("POST", "/shared/secret.bin/_vti_bin/cellstorage.svc")]
    public async Task AnonymousRequestsAreChallengedBeforeBodyParsing(string method, string path)
    {
        await using var fixture = await Fixture.Start();
        using var request = new HttpRequestMessage(new(method), path) { Content = new StringContent("invalid SOAP") };
        using var result = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task ConfiguredHistorySettingsDescribeThePublicationLimitWithoutOfferingMutation()
    {
        await using var fixture = await Fixture.Start(configure: options =>
            options.DocumentLibraryId = Guid.Parse("8c51b086-c566-4b12-b4d2-d37847129acc"));
        using var client = fixture.CreateClient("reader");
        using var result = await client.GetAsync("/_cellbridge/history/settings");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("text/html", result.Content.Headers.ContentType?.MediaType);
        Assert.True(result.Headers.CacheControl?.NoStore);
        var html = await result.Content.ReadAsStringAsync();
        Assert.Contains($"up to {fixture.Provider.Limits.MaxHistoryRevisions} saved revisions", html);
        Assert.Contains("does not automatically delete older revisions", html);
        Assert.DoesNotContain("<form", html);
    }

    [Fact]
    public async Task UnconfiguredCatalogDoesNotInventLibraryMetadata()
    {
        await using var fixture = await Fixture.Start();
        using var client = fixture.CreateClient("reader");
        using var settings = await client.GetAsync("/_cellbridge/history/settings");
        Assert.Equal(HttpStatusCode.NotFound, settings.StatusCode);
        var xml = await fixture.Send("reader", new FssHttpSubRequest { Type = SubRequestType.GetVersions });
        Assert.Equal("RequestNotSupported", Error(xml));
        Assert.DoesNotContain(xml.Descendants(), x => x.Name.LocalName == "list");
    }

    [Theory]
    [InlineData("GET", false)]
    [InlineData("HEAD", false)]
    [InlineData("GET", true)]
    [InlineData("HEAD", true)]
    public async Task ReadPermissionControlsBytesAndHeaders(string method, bool externalPolicy)
    {
        await using var fixture = await Fixture.Start(externalPolicy: externalPolicy);
        foreach (var user in new[] { "reader", "other" })
        {
            using var client = fixture.CreateClient(user);
            using var request = new HttpRequestMessage(new(method), "/shared/secret.bin");
            using var result = await client.SendAsync(request);
            Assert.Equal(user == "reader" ? HttpStatusCode.OK : HttpStatusCode.NotFound, result.StatusCode);
            if (user == "other") { Assert.Null(result.Headers.ETag); Assert.Null(result.Content.Headers.LastModified); }
            else if (method == "GET") Assert.Equal(new byte[] { 1, 2, 3 }, await result.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task ConcurrentClientsKeepTheirOwnIdentity()
    {
        await using var fixture = await Fixture.Start(configure: options =>
        {
            // Admit the test's ten concurrent requests per authenticated subject.
            options.MaxConcurrentRequests = 20;
            options.MaxConcurrentRequestsPerSubject = 10;
        });
        using var reader = fixture.CreateClient("reader");
        using var other = fixture.CreateClient("other");

        await Task.WhenAll(Enumerable.Range(0, 10).SelectMany(_ => new[]
        {
            Check(reader, HttpStatusCode.OK),
            Check(other, HttpStatusCode.NotFound),
            Check(fixture.Client, HttpStatusCode.Unauthorized),
        }));

        static async Task Check(HttpClient client, HttpStatusCode expected)
        {
            using var result = await client.GetAsync("/shared/secret.bin");
            Assert.Equal(expected, result.StatusCode);
        }
    }

    [Theory]
    [InlineData("writer", true, true, false)]
    [InlineData("reader", true, false, false)]
    [InlineData("other", false, false, false)]
    [InlineData("writer", true, true, true)]
    [InlineData("reader", true, false, true)]
    [InlineData("other", false, false, true)]
    public async Task QueryAccessReportsIndependentPermissionsAndDoesNotLeakMetadata(string user, bool read, bool write, bool externalPolicy)
    {
        await using var fixture = await Fixture.Start(externalPolicy: externalPolicy);
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
            Assert.Equal("FileUnauthorizedAccess", (string?)file.Attribute("ErrorCode"));
            Assert.NotNull(file.Attribute("ErrorMessage"));
        }
        else Assert.NotNull(file.Attribute("ResourceID"));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(" 1 ", true)]
    [InlineData(" true ", true)]
    public async Task GetFilePropsUsesXmlBooleanLexicalValues(string value, bool expected)
    {
        await using var fixture = await Fixture.Start();
        var cell = new FsshttpbCellRequest
        {
            SubRequests = { new(RequestTypes.QueryAccess) { RequestId = 1 } },
        };
        var xml = await fixture.Send("writer", new FssHttpSubRequest
        {
            Type = SubRequestType.Cell,
            SubRequestDataBinary = cell.ToByteArray(),
            SubRequestDataAttributes = { ["GetFileProps"] = value },
        });

        var data = xml.Descendants(Protocol + "SubResponseData").Single();
        Assert.Equal(expected, data.Attribute("Etag") is not null);
        Assert.Equal(expected, data.Attribute("CreateTime") is not null);
        Assert.Equal(expected, data.Attribute("LastModifiedTime") is not null);
    }

    [Fact]
    public async Task SoapExecutionAcceptsCollapsedSignedBinarySizeAndRejectsCollapsedMetadataCoalesce()
    {
        await using var fixture = await Fixture.Start();
        var put = new PutChangesSubRequestData { StorageIndex = new ExGuid(1, Guid.NewGuid()) };
        var cell = new FsshttpbCellRequest
        {
            SubRequests = { new(RequestTypes.PutChanges) { RequestId = 1, Data = put } },
            DataElementPackage = new DataElementPackage(),
        };
        byte[] binary = cell.ToByteArray();
        var before = await fixture.Provider.Documents.FindAsync(new DocumentIdentity.Resource(fixture.State.ResourceId));

        var xml = await fixture.Send("writer", new FssHttpSubRequest
        {
            Type = SubRequestType.Cell,
            SubRequestDataBinary = binary,
            SubRequestDataAttributes =
            {
                ["PartitionID"] = StoredDocument.MetadataPartitionId.ToString("D"),
                ["BinaryDataSize"] = $" +{binary.Length} ",
                ["Coalesce"] = " 1 ",
            },
        });

        var payload = Convert.FromBase64String(xml.Descendants(Protocol + "SubResponseData").Single().Value);
        var response = FsshttpbResponse.Deserialize(new BinaryReaderEx(payload));
        Assert.Equal((ulong)CellErrorCode.RequestNotSupported, Assert.Single(response.SubResponses).Error?.ErrorCode);
        Assert.Equal(before, await fixture.Provider.Documents.FindAsync(new DocumentIdentity.Resource(fixture.State.ResourceId)));
    }

    [Fact]
    public async Task EditorsExecutionAcceptsCollapsedBooleanAndSignedTimeout()
    {
        await using var fixture = await Fixture.Start();
        var xml = await fixture.Send("reader", new FssHttpSubRequest
        {
            Type = SubRequestType.EditorsTable,
            SubRequestDataAttributes =
            {
                ["EditorsTableRequestType"] = "JoinEditingSession",
                ["ClientID"] = Guid.NewGuid().ToString("D"),
                ["Timeout"] = " +60 ",
                ["AsEditor"] = " 0 ",
            },
        });

        Assert.Equal("Success", Error(xml));
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
    public async Task WhoAmIIncludesOnlyTrustedOptionalEmailAndSchemaSipAddress()
    {
        await using var fixture = await Fixture.Start();
        var response = await fixture.SendWithClaims("reader", [
            new Claim(ClaimTypes.Email, "reader@example.test"),
            new Claim(CellBridgeActor.SipAddressClaim, "reader@contoso.test"),
        ], new FssHttpSubRequest { Type = SubRequestType.WhoAmI, SubRequestToken = 1 });
        var who = response.Descendants(Protocol + "SubResponse").First().Element(Protocol + "SubResponseData")!;
        Assert.Equal("test-reader", (string?)who.Attribute("UserLogin"));
        Assert.Equal("reader@example.test", (string?)who.Attribute("UserEmailAddress"));
        Assert.Equal("reader@contoso.test", (string?)who.Attribute("UserSIPAddress"));
        Assert.Null(who.Attribute("SIPAddress"));

        var withoutMetadata = await fixture.Send("reader", new FssHttpSubRequest { Type = SubRequestType.WhoAmI, SubRequestToken = 1 });
        var bareWho = withoutMetadata.Descendants(Protocol + "SubResponse").First().Element(Protocol + "SubResponseData")!;
        Assert.Null(bareWho.Attribute("UserEmailAddress"));
        Assert.Null(bareWho.Attribute("UserSIPAddress"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnotherWriterCannotJoinOrLeaveAnotherUsersEditorSession(bool externalPolicy)
    {
        await using var fixture = await Fixture.Start(otherCanWrite: true, externalPolicy: externalPolicy);
        var clientId = Guid.NewGuid();
        FssHttpSubRequest Request(string operation) => new()
        {
            Type = SubRequestType.EditorsTable, SubRequestToken = 1,
            SubRequestDataAttributes = { ["EditorsTableRequestType"] = operation, ["ClientID"] = clientId.ToString(),
                ["Timeout"] = "60", ["AsEditor"] = "true" },
        };
        Assert.Equal("Success", Error(await fixture.Send("writer", Request("JoinEditingSession"))));
        var before = await fixture.Provider.Documents.ReadControlAsync(fixture.State.ResourceId);
        foreach (var operation in new[] { "JoinEditingSession", "RefreshEditingSession", "LeaveEditingSession" })
            Assert.Equal("FileUnauthorizedAccess", Error(await fixture.Send("other", Request(operation))));
        var after = await fixture.Provider.Documents.ReadControlAsync(fixture.State.ResourceId);
        Assert.Equal(before!.Head, after!.Head);
        Assert.Equal(before.Coordination, after.Coordination);
        Assert.Equal(before.Editors, after.Editors);
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
        using var client = fixture.CreateClient("writer");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_vti_bin/cellstorage.svc")
        { Content = new StringContent("<fake/>", Encoding.UTF8, contentType) };
        if (origin is not null) request.Headers.Add("Origin", origin);
        using var result = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, result.StatusCode);
    }

    private static string? Error(XDocument xml) => (string?)xml.Descendants(Protocol + "SubResponse").Single().Attribute("ErrorCode");

    [Theory]
    [InlineData("http://schemas.xmlsoap.org/soap/envelope/", "text/xml")]
    [InlineData("http://www.w3.org/2003/05/soap-envelope", "application/soap+xml")]
    public async Task SoapResponsesUseTheRequestedEnvelopeAndDirectProtocolBody(string soapNamespace, string mediaType)
    {
        await using var fixture = await Fixture.Start();
        using var client = fixture.CreateClient("writer");
        XNamespace soap = soapNamespace;
        var envelope = new XElement(soap + "Envelope", new XElement(soap + "Body",
            new XElement(Protocol + "RequestVersion", new XAttribute("Version", 2), new XAttribute("MinorVersion", 3)),
            new XElement(Protocol + "RequestCollection", new XAttribute("CorrelationId", Guid.NewGuid()),
                new XElement(Protocol + "Request", new XAttribute("Url", "http://localhost/shared/secret.bin"),
                    new XAttribute("RequestToken", 1),
                    new XElement(Protocol + "SubRequest", new XAttribute("Type", "WhoAmI"), new XAttribute("SubRequestToken", 1))))));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_vti_bin/cellstorage.svc")
        { Content = new StringContent(envelope.ToString(), Encoding.UTF8, mediaType) };
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(soap + "Envelope", xml.Root!.Name);
        Assert.Equal(new[] { Protocol + "ResponseVersion", Protocol + "ResponseCollection" },
            xml.Root.Element(soap + "Body")!.Elements().Select(element => element.Name));
        Assert.Equal("Success", Error(xml));
    }

    private sealed class Fixture(WebApplication app, StorageProvider provider, DocumentHead state, HttpClient client) : IAsyncDisposable
    {
        public StorageProvider Provider => provider;
        public DocumentHead State => state;
        public HttpClient Client => client;
        public HttpClient CreateClient(string user) => TestActor.CreateClient(app.GetTestServer(), user);
        public static async Task<Fixture> Start(bool otherCanWrite = false, bool externalPolicy = false, Action<CellBridgeOptions>? configure = null)
        {
            var provider = TestStorage.Memory();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            TestActor.Register(builder.Services);
            if (externalPolicy) builder.Services.AddSingleton<ICellBridgeAuthorizationPolicy>(new ExternalPolicy(otherCanWrite));
            builder.Services.AddCellBridge(provider, requireDurability: false, configure: configure);
            var app = builder.Build();
            app.UseAuthentication(); app.UseAuthorization(); app.MapCellBridge();
            var service = app.Services.GetRequiredService<CellBridgeDocumentService>();
            var state = (await service.CreateAsync("/shared/secret.bin", [1, 2, 3], TestActor.Value))!;
            if (!externalPolicy) await TestStorage.SetPermissionsAsync(provider, state.ResourceId,
                state.Security with { Grants = state.Security.Grants.Add("tests:reader", DocumentAccess.Read)
                    .Add("tests:other", otherCanWrite ? DocumentAccess.Read | DocumentAccess.Write : DocumentAccess.None) });
            await app.StartAsync();
            return new(app, provider, state, app.GetTestClient());
        }

        public async Task<XDocument> Send(string user, params FssHttpSubRequest[] requests)
            => await SendCore(user, [], requests);

        public Task<XDocument> SendWithClaims(string user, Claim[] claims, params FssHttpSubRequest[] requests) =>
            SendCore(user, claims, requests);

        private async Task<XDocument> SendCore(string user, Claim[] claims, FssHttpSubRequest[] requests)
        {
            XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
            var file = new XElement(Protocol + "Request", new XAttribute("Url", "http://localhost/shared/supplied-only.bin"), new XAttribute("RequestToken", 1),
                new XAttribute("UseResourceID", "true"), new XAttribute("ResourceID", state.ResourceId.ToString("N")));
            foreach (var r in requests)
            {
                var sub = new XElement(Protocol + "SubRequest", new XAttribute("Type", r.Type), new XAttribute("SubRequestToken", r.SubRequestToken ?? 1));
                if (r.DependsOn is { } depends) sub.Add(new XAttribute("DependsOn", depends), new XAttribute("DependencyType", r.DependencyType!));
                bool forbidsData = r.Type is SubRequestType.WhoAmI or SubRequestType.ServerTime or SubRequestType.GetDocMetaInfo or
                    SubRequestType.GetVersions or SubRequestType.LockStatus;
                if (!forbidsData)
                {
                    var data = new XElement(Protocol + "SubRequestData", r.SubRequestDataAttributes.Select(p => new XAttribute(p.Key, p.Value)));
                    if (r.SubRequestDataBinary is { } binary)
                    {
                        if (data.Attribute("BinaryDataSize") is null) data.Add(new XAttribute("BinaryDataSize", binary.Length));
                        data.Add(Convert.ToBase64String(binary));
                    }
                    sub.Add(data);
                }
                file.Add(sub);
            }
            var envelope = new XElement(soap + "Envelope", new XElement(soap + "Body",
                new XElement(Protocol + "RequestVersion", new XAttribute("Version", 2), new XAttribute("MinorVersion", 2)),
                new XElement(Protocol + "RequestCollection", new XAttribute("CorrelationId", Guid.NewGuid()), file)));
            using var request = new HttpRequestMessage(HttpMethod.Post, "/_vti_bin/cellstorage.svc")
            { Content = new StringContent(envelope.ToString(), Encoding.UTF8, "text/xml") };
            using var authenticatedClient = TestActor.CreateClient(app.GetTestServer(), user, claims);
            using var result = await authenticatedClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            return XDocument.Parse(await result.Content.ReadAsStringAsync());
        }

        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }
    }

    private sealed class ExternalPolicy(bool otherCanWrite) : ICellBridgeAuthorizationPolicy
    {
        public string PolicyDomain => "tests:endpoint-host";
        public DocumentAuthorizationBinding BindNewDocument(Guid resourceId) => new(PolicyDomain, 1, 1);
        public ICellBridgeAuthorizationSnapshot Resolve(DocumentAuthorizationView state) => new Snapshot(state.ResourceId, state.Security.AuthorizationPolicy!, otherCanWrite);
        private sealed record Snapshot(Guid ResourceId, DocumentAuthorizationBinding Binding, bool OtherCanWrite) : ICellBridgeAuthorizationSnapshot
        {
            public DocumentAccess Evaluate(string subject) => subject switch
            {
                "tests:writer" => DocumentAccess.Write,
                "tests:reader" => DocumentAccess.Read,
                "tests:other" when OtherCanWrite => DocumentAccess.Write,
                _ => DocumentAccess.None,
            };
        }
    }
}
