using System.Net;
using System.Text;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage;

namespace CellBridge.FssHttp.Tests;

public sealed partial class AuthorizationEndpointTests
{
    private static readonly XNamespace SharedAccess = "http://schemas.microsoft.com/sharepoint/soap/";
    private static readonly XNamespace Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Soap12 = "http://www.w3.org/2003/05/soap-envelope";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsOnlyClientReportsPendingTransitionUntilAcknowledged(bool externalPolicy)
    {
        await using var fixture = await Fixture.Start(otherCanWrite: true, externalPolicy: externalPolicy);
        Assert.True(await IsOnlyClient(fixture, "writer", fixture.State.ResourceId));

        var schemaLockId = Guid.NewGuid();
        var writerClientId = Guid.NewGuid();
        var otherClientId = Guid.NewGuid();
        Assert.Equal("Success", Error(await fixture.Send("writer", CoauthRequest("JoinCoauthoring", writerClientId, schemaLockId))));
        Assert.True(await IsOnlyClient(fixture, "writer", fixture.State.ResourceId));
        Assert.Equal("Success", Error(await fixture.Send("other", CoauthRequest("JoinCoauthoring", otherClientId, schemaLockId))));

        Assert.False(await IsOnlyClient(fixture, "writer", fixture.State.ResourceId));
        Assert.Equal("Success", Error(await fixture.Send("writer", CoauthRequest("MarkTransitionComplete", writerClientId, schemaLockId))));
        Assert.True(await IsOnlyClient(fixture, "writer", fixture.State.ResourceId));

        using var reader = await SendIsOnlyClient(fixture, "reader", fixture.State.ResourceId);
        Assert.Equal(HttpStatusCode.OK, reader.StatusCode);
        Assert.True(ReadOnlyClientResult(await reader.Content.ReadAsStringAsync(), Soap11));
    }

    [Fact]
    public async Task IsOnlyClientHidesUnknownAndDeniedResourceIdentities()
    {
        await using var fixture = await Fixture.Start();
        using var unknown = await SendIsOnlyClient(fixture, "reader", Guid.NewGuid());
        using var denied = await SendIsOnlyClient(fixture, "other", fixture.State.ResourceId);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.DoesNotContain("IsOnlyClientResult", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokingStoredReaderGrantRemovesIsOnlyClientAccess()
    {
        await using var fixture = await Fixture.Start();
        Assert.True(await IsOnlyClient(fixture, "reader", fixture.State.ResourceId));

        await TestStorage.SetPermissionsAsync(fixture.Provider, fixture.State.ResourceId,
            fixture.State.Security with { Grants = fixture.State.Security.Grants.Remove("tests:reader") });

        using var result = await SendIsOnlyClient(fixture, "reader", fixture.State.ResourceId);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task IsOnlyClientAcceptsSoap12()
    {
        await using var fixture = await Fixture.Start();
        using var result = await SendIsOnlyClient(fixture, "reader", fixture.State.ResourceId, soap12: true);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.True(ReadOnlyClientResult(await result.Content.ReadAsStringAsync(), Soap12));
    }

    [Fact]
    public async Task IsOnlyClientRejectsMalformedAndMismatchedSoapRequests()
    {
        await using var fixture = await Fixture.Start();
        var resourceId = fixture.State.ResourceId;
        var invalidRequests = new (string Body, string? Action)[]
        {
            ("<broken", null),
            ($"<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><s:Envelope xmlns:s=\"{Soap11}\"><s:Body><IsOnlyClient xmlns=\"{SharedAccess}\"><id>&e;</id></IsOnlyClient></s:Body></s:Envelope>", null),
            ($"<s:Envelope xmlns:s=\"{Soap11}\"><s:Body><IsOnlyClient xmlns=\"{SharedAccess}\"><id>{resourceId:D}</id></IsOnlyClient><IsOnlyClient xmlns=\"{SharedAccess}\"><id>{resourceId:D}</id></IsOnlyClient></s:Body></s:Envelope>", null),
            ($"<s:Envelope xmlns:s=\"{Soap11}\"><s:Body><Other xmlns=\"{SharedAccess}\"><id>{resourceId:D}</id></Other></s:Body></s:Envelope>", null),
            ($"<s:Envelope xmlns:s=\"{Soap11}\"><s:Body><IsOnlyClient xmlns=\"urn:unexpected\"><id>{resourceId:D}</id></IsOnlyClient></s:Body></s:Envelope>", null),
            ($"<s:Envelope xmlns:s=\"{Soap11}\"><s:Body><IsOnlyClient xmlns=\"{SharedAccess}\"><id>{resourceId:D}</id><id>{resourceId:D}</id></IsOnlyClient></s:Body></s:Envelope>", null),
            ($"<s:Envelope xmlns:s=\"{Soap11}\"><s:Body><IsOnlyClient xmlns=\"{SharedAccess}\"><id>not-a-guid</id></IsOnlyClient></s:Body></s:Envelope>", null),
            (Soap11Request(resourceId), "\"http://schemas.microsoft.com/sharepoint/soap/Other\""),
        };

        foreach (var (body, action) in invalidRequests)
        {
            using var result = await SendRawIsOnlyClient(fixture, "reader", body, action);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }
    }

    [Fact]
    public async Task IsOnlyClientHonorsASmallerHostRequestLimit()
    {
        await using var fixture = await Fixture.Start(configure: options => options.MaxRequestBytes = 100);
        using var result = await SendIsOnlyClient(fixture, "reader", fixture.State.ResourceId);
        Assert.Equal((HttpStatusCode)413, result.StatusCode);
    }

    [Fact]
    public async Task IsOnlyClientRejectsChunkedBodiesOverSixteenKibibytes()
    {
        await using var fixture = await Fixture.Start();
        using var client = fixture.CreateClient("reader");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_vti_bin/sharedaccess.asmx")
        {
            Content = new NonSeekableContent(Encoding.UTF8.GetBytes(Soap11Request(fixture.State.ResourceId) + new string(' ', 16 * 1024))),
        };
        request.Content.Headers.ContentType = new("text/xml") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation("SOAPAction", "\"http://schemas.microsoft.com/sharepoint/soap/IsOnlyClient\"");
        using var result = await client.SendAsync(request);

        Assert.Equal((HttpStatusCode)413, result.StatusCode);
    }

    private static FssHttpSubRequest CoauthRequest(string operation, Guid clientId, Guid schemaLockId) => new()
    {
        Type = SubRequestType.Coauth,
        SubRequestToken = 1,
        SubRequestDataAttributes =
        {
            ["CoauthRequestType"] = operation,
            ["ClientID"] = clientId.ToString("D"),
            ["SchemaLockID"] = schemaLockId.ToString("D"),
            ["Timeout"] = "3600",
        },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DocumentRelativeSharedAccessUsesTheSameAuthorizationAndTransitionState(bool soap12)
    {
        await using var fixture = await Fixture.Start();
        var path = "/shared/secret.bin/_vti_bin/sharedaccess.asmx";
        using var allowed = await SendRawIsOnlyClient(fixture, "reader", SoapRequest(fixture.State.ResourceId, soap12), null, soap12, path);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.True(ReadOnlyClientResult(await allowed.Content.ReadAsStringAsync(), soap12 ? Soap12 : Soap11));
        using var denied = await SendRawIsOnlyClient(fixture, "other", SoapRequest(fixture.State.ResourceId, soap12), null, soap12, path);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    private static async Task<bool> IsOnlyClient(Fixture fixture, string user, Guid resourceId)
    {
        using var result = await SendIsOnlyClient(fixture, user, resourceId);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        return ReadOnlyClientResult(await result.Content.ReadAsStringAsync(), Soap11);
    }

    private static Task<HttpResponseMessage> SendIsOnlyClient(Fixture fixture, string user, Guid resourceId, bool soap12 = false) =>
        SendRawIsOnlyClient(fixture, user, SoapRequest(resourceId, soap12), action: null, soap12);

    private static async Task<HttpResponseMessage> SendRawIsOnlyClient(Fixture fixture, string user, string body, string? action, bool soap12 = false,
        string path = "/_vti_bin/sharedaccess.asmx")
    {
        using var client = fixture.CreateClient(user);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, soap12 ? "application/soap+xml" : "text/xml"),
        };
        if (action is not null) request.Headers.TryAddWithoutValidation("SOAPAction", action);
        else if (!soap12) request.Headers.TryAddWithoutValidation("SOAPAction", "\"http://schemas.microsoft.com/sharepoint/soap/IsOnlyClient\"");
        if (soap12) request.Content.Headers.ContentType!.Parameters.Add(new("action", "\"http://schemas.microsoft.com/sharepoint/soap/IsOnlyClient\""));
        return await client.SendAsync(request);
    }

    private static string SoapRequest(Guid resourceId, bool soap12 = false)
    {
        XNamespace envelope = soap12 ? Soap12 : Soap11;
        var root = new XElement(envelope + "Envelope",
            new XElement(envelope + "Body",
                new XElement(SharedAccess + "IsOnlyClient",
                    new XElement(SharedAccess + "id", resourceId.ToString("D")))));
        return new XDocument(root).ToString(SaveOptions.DisableFormatting);
    }

    private static string Soap11Request(Guid resourceId) => SoapRequest(resourceId);

    private static bool ReadOnlyClientResult(string body, XNamespace envelope)
    {
        var xml = XDocument.Parse(body);
        var response = Assert.Single(xml.Descendants(SharedAccess + "IsOnlyClientResponse"));
        Assert.Equal(envelope + "Envelope", xml.Root!.Name);
        return bool.Parse(Assert.Single(response.Elements(SharedAccess + "IsOnlyClientResult")).Value);
    }

    private sealed class NonSeekableContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
