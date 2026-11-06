using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.WebUtilities;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace CellBridge.FssHttp.Tests;

public sealed class OfficeRealTimeEndpointTests
{
    private const string PublicOrigin = "https://office.example";
    private const string Mount = "/mounted";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RtcLimitsDoNotReplaceTheHostsOtherHubSettings(bool hostRegisteredFirst)
    {
        var builder = WebApplication.CreateBuilder();
        void RegisterHostHub() => builder.Services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 1024 * 1024;
            options.MaximumParallelInvocationsPerClient = 8;
            options.EnableDetailedErrors = true;
        }).AddHubOptions<UnrelatedHub>(_ => { });
        if (hostRegisteredFirst) RegisterHostHub();
        builder.Services.AddCellBridge(TestStorage.Memory(), requireDurability: false);
        if (!hostRegisteredFirst) RegisterHostHub();
        await using var app = builder.Build();

        var other = app.Services.GetRequiredService<IOptions<HubOptions<UnrelatedHub>>>().Value;
        Assert.Equal(1024 * 1024, other.MaximumReceiveMessageSize);
        Assert.Equal(8, other.MaximumParallelInvocationsPerClient);
        Assert.True(other.EnableDetailedErrors);
        var rtc = app.Services.GetRequiredService<IOptions<HubOptions<OfficeRealTimeHub>>>().Value;
        Assert.Equal(64 * 1024, rtc.MaximumReceiveMessageSize);
        Assert.Equal(1, rtc.MaximumParallelInvocationsPerClient);
        Assert.False(rtc.EnableDetailedErrors);
    }

    private sealed class UnrelatedHub : Hub { }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultRtcPreservesTheHostsImplicitCookieScheme(bool cookieRegisteredFirst)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (cookieRegisteredFirst) builder.Services.AddAuthentication().AddCookie("host");
        builder.Services.AddCellBridge(TestStorage.Memory(), requireDurability: false);
        if (!cookieRegisteredFirst) builder.Services.AddAuthentication().AddCookie("host");
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCellBridge();
        app.MapGet("/sign-in", async (HttpContext context) =>
        {
            await context.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "host-user")], "host")));
            return Results.Ok();
        });
        app.MapGet("/protected", (HttpContext context) => context.User.Identity!.Name!).RequireAuthorization();
        await app.StartAsync();
        var schemes = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.Equal("host", (await schemes.GetDefaultAuthenticateSchemeAsync())!.Name);
        Assert.Equal("host", (await schemes.GetDefaultChallengeSchemeAsync())!.Name);
        using var client = app.GetTestClient();
        using var signedIn = await client.GetAsync("/sign-in");
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", signedIn.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        Assert.Equal("host-user", await client.GetStringAsync("/protected"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AuthoringDiscoveryFollowsRtcOption(bool enabled, bool mounted)
    {
        await using var fixture = await Fixture.Start(enabled, mounted);
        using var client = TestActor.CreateClient(fixture.Server, "writer");
        foreach (var path in new[] { "/", "/shared/", "/_vti_bin/cellstorage.svc/CellStorageService",
            "/shared/probe.xlsx/_vti_bin/cellstorage.svc/CellStorageService" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, (mounted ? Mount : "") + path);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("1.5", Assert.Single(response.Headers.GetValues("X-MSFSSHTTP")));
            if (enabled)
            {
                Assert.Equal("1", Assert.Single(response.Headers.GetValues("MS-Storage")));
                Assert.Equal("DAV", Assert.Single(response.Headers.GetValues("MS-Author-Via")));
                Assert.Equal("CellBridge", Assert.Single(response.Headers.GetValues("X-MSNSERVER")));
                Assert.False(response.Headers.Contains("X-MSGETWEBURL"));
            }
            else
            {
                Assert.False(response.Headers.Contains("MS-Storage"));
                Assert.False(response.Headers.Contains("MS-Author-Via"));
                Assert.False(response.Headers.Contains("X-MSNSERVER"));
            }
            Assert.False(response.Headers.Contains("DAV"));
            var methods = path.Contains("cellstorage.svc", StringComparison.Ordinal) ? "OPTIONS,POST"
                : enabled ? "OPTIONS,GET,HEAD,POST,PROPFIND" : "OPTIONS,GET,HEAD,POST";
            Assert.Equal(methods, Assert.Single(response.Headers.GetValues("Public")));
            Assert.Equal(methods.Split(','), response.Content.Headers.Allow);
        }
    }

    [Fact]
    public async Task FindSessionAcceptsFreshOwnedTicketAndReturnsNoStoreDiscovery()
    {
        await using var fixture = await Fixture.Start(mounted: true);
        Guid client = await fixture.CreateCoauthorLease();
        var properties = await fixture.Properties(mounted: true);

        using var response = await fixture.GetFindSession(properties["DocumentAccessToken"], client, mounted: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("private", response.Headers.CacheControl?.ToString());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("enabled").GetBoolean());
        Assert.True(json.RootElement.GetProperty("netcore").GetBoolean());
        Assert.Equal(PublicOrigin + Mount + "/_cellbridge/rtc/signalr", json.RootElement.GetProperty("url").GetString());
        Assert.Equal("environment=CellBridge", json.RootElement.GetProperty("connectionString").GetString());
        Assert.Contains(Mount + "/_cellbridge/rtc/", properties["RealtimeTypingEndpointUrl"]);
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("name")]
    public async Task ClassicSignalRNegotiatesAndRunsTheObservedOfficeWebSocketMethods(string nameProperty)
    {
        await using var fixture = await Fixture.Start();
        Guid clientId = await fixture.CreateCoauthorLease();
        string signature = (await fixture.Properties())["DocumentAccessToken"];
        var negotiationQuery = new Dictionary<string, string?>
        {
            ["clientProtocol"] = "1.4",
            ["connectionData"] = "[{\"" + nameProperty + "\":\"RealtimeChannelHub\"}]",
            ["qs"] = signature,
            ["ClientId"] = clientId.ToString("D"),
        };
        string path = QueryHelpers.AddQueryString("/_cellbridge/rtc/signalr/signalr/negotiate", negotiationQuery);
        using var client = TestActor.CreateClient(fixture.Server, "writer");
        using var negotiate = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
        using JsonDocument negotiation = JsonDocument.Parse(await negotiate.Content.ReadAsStringAsync());
        JsonElement negotiated = negotiation.RootElement;
        Assert.Equal("1.4", negotiated.GetProperty("ProtocolVersion").GetString());
        Assert.True(negotiated.GetProperty("TryWebSockets").GetBoolean());
        Assert.Equal(PublicOrigin + "/_cellbridge/rtc/signalr", negotiated.GetProperty("Url").GetString());
        string connectionToken = negotiated.GetProperty("ConnectionToken").GetString()!;

        var connectQuery = new Dictionary<string, string?>
        {
            ["transport"] = "webSockets",
            ["connectionToken"] = connectionToken,
            ["qs"] = signature,
            ["ClientId"] = clientId.ToString("D"),
        };
        var websocketClient = fixture.Server.CreateWebSocketClient();
        UriBuilder websocketBase = new(fixture.Server.BaseAddress) { Scheme = "ws" };
        Uri websocketUri = new(websocketBase.Uri, QueryHelpers.AddQueryString(
            "/_cellbridge/rtc/signalr/signalr/connect", connectQuery));
        using WebSocket socket = await websocketClient.ConnectAsync(websocketUri, CancellationToken.None);

        JsonElement initialize = await ReadClassicFrameAsync(socket);
        Assert.Equal(1, initialize.GetProperty("S").GetInt32());
        await SendClassicFrameAsync(socket, """{"H":"RealtimeChannelHub","M":"GetCompletePresenceState","A":[],"I":"1"}""");
        JsonElement presence = await ReadClassicFrameAsync(socket);
        Assert.Equal("1", presence.GetProperty("I").GetString());
        Assert.Empty(presence.GetProperty("R").EnumerateObject());

        await SendClassicFrameAsync(socket, """{"H":"RealtimeChannelHub","M":"UpdatePresence","A":["{}"],"I":"2"}""");
        JsonElement update = await ReadClassicFrameAsync(socket);
        Assert.Equal("2", update.GetProperty("I").GetString());
        Assert.False(update.TryGetProperty("R", out _));
        Assert.False(update.TryGetProperty("E", out _));
        await SendClassicFrameAsync(socket, """{"H":"RealtimeChannelHub","M":"SendUpdateNotification","A":[2,"{}"],"I":"3"}""");
        JsonElement channelTwo = await ReadClassicFrameAsync(socket);
        Assert.Equal("3", channelTwo.GetProperty("I").GetString());
        Assert.False(channelTwo.TryGetProperty("E", out _));

        foreach (object[] notification in ExcelRealTimePayloads.Notifications())
        {
            string invocation = JsonSerializer.Serialize(new
            {
                H = "RealtimeChannelHub", M = "SendUpdateNotification", I = "native-excel",
                A = notification,
            });
            await SendClassicFrameAsync(socket, invocation);
            JsonElement reply = await ReadClassicFrameAsync(socket);
            Assert.Equal("native-excel", reply.GetProperty("I").GetString());
            Assert.False(reply.TryGetProperty("E", out _));
        }

        // SignalR reconnect can arrive while the prior physical socket is still open.
        // The session store replaces its connection, and stale cleanup must not remove
        // the new connection or its presence.
        var reconnectQuery = new Dictionary<string, string?>
        {
            ["transport"] = "webSockets", ["connectionToken"] = connectionToken,
            ["qs"] = signature, ["ClientId"] = clientId.ToString("D"),
        };
        Uri reconnectUri = new(websocketBase.Uri, QueryHelpers.AddQueryString(
            "/_cellbridge/rtc/signalr/signalr/reconnect", reconnectQuery));
        using WebSocket reconnected = await websocketClient.ConnectAsync(reconnectUri, CancellationToken.None);
        JsonElement reconnectInitialization = await ReadClassicFrameAsync(reconnected);
        Assert.Equal(1, reconnectInitialization.GetProperty("S").GetInt32());
        await SendClassicFrameAsync(reconnected,
            """{"H":"RealtimeChannelHub","M":"GetCompletePresenceState","A":[],"I":"4"}""");
        JsonElement reconnectPresence = await ReadClassicFrameAsync(reconnected);
        Assert.Equal("4", reconnectPresence.GetProperty("I").GetString());
        Assert.Equal("{}", reconnectPresence.GetProperty("R").GetProperty("{" + clientId.ToString("D").ToLowerInvariant() + "}").GetString());
        var auxiliaryQuery = new Dictionary<string, string?>
        {
            ["connectionToken"] = connectionToken, ["qs"] = signature, ["ClientId"] = clientId.ToString("D"),
        };
        using var start = await client.GetAsync(QueryHelpers.AddQueryString("/_cellbridge/rtc/signalr/signalr/start", auxiliaryQuery));
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        using (JsonDocument startBody = JsonDocument.Parse(await start.Content.ReadAsStringAsync()))
            Assert.Equal("started", startBody.RootElement.GetProperty("Response").GetString());
        using var abort = await client.GetAsync(QueryHelpers.AddQueryString("/_cellbridge/rtc/signalr/signalr/abort", auxiliaryQuery));
        Assert.Equal(HttpStatusCode.OK, abort.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClassicSignalRWorksOnKestrelWithoutHostWebSocketSetup(bool stopWhileConnected)
    {
        var provider = TestStorage.Memory();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        TestActor.Register(builder.Services);
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddCellBridge(provider, requireDurability: false);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCellBridge();
        await app.StartAsync();
        string origin = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        var service = app.Services.GetRequiredService<CellBridgeDocumentService>();
        var head = (await service.CreateAsync("/shared/classic-signalr.docx", MinimalDocx.Create(), TestActor.Value))!;
        Guid clientId = await CreateCoauthorLeaseAsync(service, head.ResourceId);
        var properties = await app.Services.GetRequiredService<IOfficeRealTimePropertiesProvider>()
            .GetPropertiesAsync(head.ResourceId, TestActor.Value, origin, CancellationToken.None);
        var query = new Dictionary<string, string?>
        {
            ["clientProtocol"] = "1.4", ["connectionData"] = "[{\"Name\":\"RealtimeChannelHub\"}]",
            ["qs"] = properties["DocumentAccessToken"], ["ClientId"] = clientId.ToString("D"),
        };
        using var http = new HttpClient { BaseAddress = new Uri(origin) };
        using var response = await http.GetAsync(QueryHelpers.AddQueryString(
            "/_cellbridge/rtc/signalr/signalr/negotiate", query));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument negotiation = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var connectQuery = new Dictionary<string, string?>
        {
            ["transport"] = "webSockets", ["connectionToken"] = negotiation.RootElement.GetProperty("ConnectionToken").GetString(),
            ["qs"] = properties["DocumentAccessToken"], ["ClientId"] = clientId.ToString("D"),
        };
        UriBuilder websocketOrigin = new(new Uri(origin)) { Scheme = "ws" };
        Uri websocketUri = new(websocketOrigin.Uri, QueryHelpers.AddQueryString(
            "/_cellbridge/rtc/signalr/signalr/connect", connectQuery));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(websocketUri, CancellationToken.None);
        JsonElement initialization = await ReadClassicFrameAsync(socket);
        Assert.Equal(1, initialization.GetProperty("S").GetInt32());
        await SendClassicFrameAsync(socket, """{"H":"RealtimeChannelHub","M":"GetCompletePresenceState","A":[],"I":"1"}""");
        JsonElement presence = await ReadClassicFrameAsync(socket);
        Assert.Equal("1", presence.GetProperty("I").GetString());
        if (stopWhileConnected)
            await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        else
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test complete", CancellationToken.None);
    }

    private static async Task<Guid> CreateCoauthorLeaseAsync(CellBridgeDocumentService service, Guid resourceId)
    {
        Guid clientId = Guid.NewGuid();
        string exclusiveId = Guid.NewGuid().ToString("D");
        string schemaId = Guid.NewGuid().ToString("D");
        var getLock = new FssHttpSubRequest { Type = SubRequestType.ExclusiveLock,
            SubRequestDataAttributes = { ["ExclusiveLockRequestType"] = "GetLock", ["ExclusiveLockID"] = exclusiveId,
                ["ClientID"] = clientId.ToString("D"), ["Timeout"] = "3600" } };
        Assert.Null((await service.ExecuteControlAsync(resourceId, getLock, TestActor.Value, CancellationToken.None)).ErrorCode);
        var convert = new FssHttpSubRequest { Type = SubRequestType.ExclusiveLock,
            SubRequestDataAttributes = { ["ExclusiveLockRequestType"] = "ConvertToSchemaJoinCoauth",
                ["ExclusiveLockID"] = exclusiveId, ["SchemaLockID"] = schemaId,
                ["ClientID"] = clientId.ToString("D"), ["Timeout"] = "3600" } };
        Assert.Null((await service.ExecuteControlAsync(resourceId, convert, TestActor.Value, CancellationToken.None)).ErrorCode);
        return clientId;
    }

    private static async Task SendClassicFrameAsync(WebSocket socket, string json)
    {
        byte[] text = Encoding.UTF8.GetBytes(json);
        int split = Math.Max(1, text.Length / 2);
        await socket.SendAsync(new ArraySegment<byte>(text, 0, split), WebSocketMessageType.Text, false, CancellationToken.None);
        await socket.SendAsync(new ArraySegment<byte>(text, split, text.Length - split), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<JsonElement> ReadClassicFrameAsync(WebSocket socket)
    {
        byte[] buffer = new byte[64 * 1024];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);
        Assert.NotEmpty(buffer.AsSpan(0, result.Count).ToArray());
        string frame = Encoding.UTF8.GetString(buffer, 0, result.Count);
        Assert.DoesNotContain('\u001e', frame);
        using JsonDocument json = JsonDocument.Parse(frame);
        return json.RootElement.Clone();
    }

    [Fact]
    public async Task TemporaryAdmissionPressureDoesNotBecomeAnAuthorizationDenial()
    {
        await using var fixture = await Fixture.Start(configure: options =>
        {
            options.MaxConcurrentRequests = 1;
            options.MaxConcurrentRequestsPerSubject = 1;
            options.MaxQueuedRequests = 0;
            options.MaxQueuedRequestsPerSubject = 0;
        });
        Guid client = await fixture.CreateCoauthorLease();
        var signature = (await fixture.Properties())["DocumentAccessToken"];
        var limiter = fixture.App.Services.GetRequiredService<ProtocolRequestAdmission>();
        using (var held = (await limiter.AcquireAsync(TestActor.Value.Identity.Subject)).Lease!)
        {
            using var overloaded = await fixture.GetFindSession(signature, client);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, overloaded.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), overloaded.Headers.RetryAfter?.Delta);
            var authorizer = fixture.App.Services.GetRequiredService<IOfficeRealTimeAuthorizer>();
            var ticket = new OfficeRealTimeTicket(fixture.ResourceId, fixture.Head.LifecycleGeneration,
                TestActor.Value, client, fixture.Clock.GetUtcNow().AddMinutes(5));
            await Assert.ThrowsAsync<OfficeRealTimeBusyException>(() =>
                authorizer.AuthorizeTogetherAsync([new(ticket, DocumentAccess.Read, true)], CancellationToken.None).AsTask());
        }
        using var recovered = await fixture.GetFindSession(signature, client);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task RtcAuthorizationWaitsForShortSubjectBursts()
    {
        await using var fixture = await Fixture.Start(configure: options =>
        {
            options.MaxConcurrentRequests = 1;
            options.MaxConcurrentRequestsPerSubject = 1;
        });
        Guid client = await fixture.CreateCoauthorLease();
        var limiter = fixture.App.Services.GetRequiredService<ProtocolRequestAdmission>();
        var held = (await limiter.AcquireAsync(TestActor.Value.Identity.Subject)).Lease!;
        try
        {
            var authorizer = fixture.App.Services.GetRequiredService<IOfficeRealTimeAuthorizer>();
            var ticket = new OfficeRealTimeTicket(fixture.ResourceId, fixture.Head.LifecycleGeneration,
                TestActor.Value, client, fixture.Clock.GetUtcNow().AddMinutes(5));
            var pending = authorizer.AuthorizeAsync(ticket, DocumentAccess.Read, true, CancellationToken.None).AsTask();
            Assert.False(pending.IsCompleted);
            held.Dispose();
            Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { held.Dispose(); }
    }

    [Fact]
    public async Task FindSessionRejectsTamperedAndSourceMismatchedSignatures()
    {
        await using var fixture = await Fixture.Start();
        Guid client = await fixture.CreateCoauthorLease();
        var signature = (await fixture.Properties())["DocumentAccessToken"];

        var tampered = ReplaceQueryValue(signature, "access_token", "invalid-ticket");
        using var invalid = await fixture.GetFindSession(tampered, client);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);

        var mismatchedSource = ReplaceQueryValue(signature, "WopiSrc", PublicOrigin + "/different.docx");
        using var mismatched = await fixture.GetFindSession(mismatchedSource, client);
        Assert.Equal(HttpStatusCode.Unauthorized, mismatched.StatusCode);
    }

    [Fact]
    public async Task FindSessionRejectsExpiredTicketAndDifferentClientsOrResources()
    {
        await using var fixture = await Fixture.Start();
        Guid client = await fixture.CreateCoauthorLease();
        var signature = (await fixture.Properties())["DocumentAccessToken"];
        fixture.Clock.Advance(TimeSpan.FromMinutes(21));
        using (var expired = await fixture.GetFindSession(signature, client))
            Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);

        fixture.Clock.Reset();
        var renewed = (await fixture.Properties())["DocumentAccessToken"];
        using (var wrongClient = await fixture.GetFindSession(renewed, Guid.NewGuid()))
            Assert.Equal(HttpStatusCode.NotFound, wrongClient.StatusCode);

        Guid siblingResource = await fixture.CreateSiblingDocument();
        Guid otherClient = await fixture.CreateCoauthorLease(resourceId: siblingResource);
        using var wrongResource = await fixture.GetFindSession(renewed, otherClient);
        Assert.Equal(HttpStatusCode.NotFound, wrongResource.StatusCode);
    }

    [Fact]
    public async Task TicketAdvertisesItsLifetimeExpiresAndCanBeReacquired()
    {
        await using var fixture = await Fixture.Start();
        Guid client = await fixture.CreateCoauthorLease();
        var properties = await fixture.Properties();
        Assert.Equal(fixture.Clock.GetUtcNow().AddMinutes(20).ToUnixTimeMilliseconds().ToString(),
            properties["DocumentAccessTokenTtl"]);
        using (var current = await fixture.GetFindSession(properties["DocumentAccessToken"], client))
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        fixture.Clock.Advance(TimeSpan.FromMinutes(20));
        using (var expired = await fixture.GetFindSession(properties["DocumentAccessToken"], client))
            Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        var renewed = await fixture.Properties();
        using var fresh = await fixture.GetFindSession(renewed["DocumentAccessToken"], client);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task FindSessionHidesRevokedAndDeletedDocumentTickets()
    {
        await using var fixture = await Fixture.Start();
        Guid client = await fixture.CreateCoauthorLease();
        var signature = (await fixture.Properties())["DocumentAccessToken"];

        await TestStorage.SetPermissionsAsync(fixture.Provider, fixture.ResourceId,
            fixture.Head.Security with { Owner = "tests:other" });
        using (var revoked = await fixture.GetFindSession(signature, client))
            Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);

        // Deleted resources remain known internally, but an old ticket must no longer authorize.
        var control = (await fixture.Provider.Documents.ReadControlAsync(fixture.ResourceId))!;
        Assert.True((await fixture.Provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(
            fixture.ResourceId, control.Head.LifecycleGeneration, control.Head.StateVersion))).Applied);
        using var deleted = await fixture.GetFindSession(signature, client);
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
    }

    [Fact]
    public async Task FindSessionRejectsOldLifecycleTicketAfterResourceRecreation()
    {
        await using var fixture = await Fixture.Start();
        Guid client = await fixture.CreateCoauthorLease();
        var signature = (await fixture.Properties())["DocumentAccessToken"];
        var control = (await fixture.Provider.Documents.ReadControlAsync(fixture.ResourceId))!;
        Assert.True((await fixture.Provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(
            fixture.ResourceId, control.Head.LifecycleGeneration, control.Head.StateVersion))).Applied);
        var retired = (await fixture.Provider.Documents.FindAsync(new DocumentIdentity.Resource(fixture.ResourceId, IncludeDeleted: true)))!;
        var replacement = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), retired.Path,
            MinimalDocx.Create("replacement"), retired.Security, fixture.Provider.Content, fixture.Provider.Limits,
            lifecycleGeneration: retired.LifecycleGeneration + 1);
        Assert.True((await fixture.Provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(
            fixture.ResourceId, retired.LifecycleGeneration, retired.StateVersion, replacement))).Applied);
        await fixture.CreateCoauthorLease(replacement.Head.ResourceId, client);

        using var response = await fixture.GetFindSession(signature, client);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TicketCannotAuthenticateOrdinaryDocumentOrSoapRoutesAndAnonymousNegotiateIsChallenged()
    {
        await using var fixture = await Fixture.Start();
        Guid clientId = await fixture.CreateCoauthorLease();
        var signature = (await fixture.Properties())["DocumentAccessToken"];
        var ordinaryQuery = QueryHelpers.AddQueryString("", new Dictionary<string, string?>
        {
            ["qs"] = signature,
            ["ClientId"] = clientId.ToString("D"),
        });

        using var anonymous = new HttpClient(fixture.Server.CreateHandler()) { BaseAddress = fixture.Server.BaseAddress };
        using (var file = await anonymous.GetAsync("/shared/rtc.docx" + ordinaryQuery))
            Assert.Equal(HttpStatusCode.Unauthorized, file.StatusCode);
        using (var soap = await anonymous.PostAsync("/_vti_bin/cellstorage.svc" + ordinaryQuery,
            new StringContent("<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Body /></s:Envelope>")))
            Assert.Equal(HttpStatusCode.Unauthorized, soap.StatusCode);
        using (var negotiate = await anonymous.PostAsync("/_cellbridge/rtc/rtchub/negotiate?negotiateVersion=1", null))
            Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
        using (var classicNegotiate = await anonymous.GetAsync(
            "/_cellbridge/rtc/signalr/signalr/negotiate?clientProtocol=1.4&connectionData=%5B%7B%22name%22%3A%22realtimechannelhub%22%7D%5D"))
            Assert.Equal(HttpStatusCode.Unauthorized, classicNegotiate.StatusCode);

        var authorizedNegotiatePath = QueryHelpers.AddQueryString("/_cellbridge/rtc/rtchub/negotiate",
            new Dictionary<string, string?> { ["negotiateVersion"] = "1", ["qs"] = signature,
                ["ClientId"] = clientId.ToString("D") });
        using var authorized = await fixture.Client.PostAsync(authorizedNegotiatePath, null);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);

        var classicPath = QueryHelpers.AddQueryString("/_cellbridge/rtc/signalr/signalr/negotiate",
            new Dictionary<string, string?> { ["clientProtocol"] = "1.4",
                ["connectionData"] = "[{\"name\":\"realtimechannelhub\"}]", ["qs"] = signature,
                ["ClientId"] = clientId.ToString("D") });
        using var classicAuthorized = await fixture.Client.GetAsync(classicPath);
        Assert.Equal(HttpStatusCode.OK, classicAuthorized.StatusCode);
    }

    [Fact]
    public async Task DisabledFeatureRegistersNoTicketProviderOrRtcEndpoint()
    {
        await using var fixture = await Fixture.Start(enabled: false);
        Assert.Null(fixture.App.Services.GetService<IOfficeRealTimePropertiesProvider>());
        using var response = await fixture.Client.GetAsync("/_cellbridge/rtc/FindSession");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task RtcRejectsAuthoritiesWithoutSharedSnapshotsOnlyWhenEnabled(bool enabled, bool legacyEvaluator)
    {
        var content = new InMemoryContentStore();
        var provider = new StorageProvider(new InMemoryDocumentRepository(content), content);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        TestActor.Register(builder.Services);
        builder.Services.AddCellBridge(provider, requireDurability: false, configure: options =>
        {
            options.EnableOfficeRealTime = enabled;
            options.PublicOrigin = PublicOrigin;
        });
        // Late registrations must also be checked when the endpoints are mapped.
        if (legacyEvaluator) builder.Services.AddSingleton<ICellBridgeAccessEvaluator, StoredDocumentAccessEvaluator>();
        else builder.Services.AddSingleton<IDocumentAuthorizationProvider, OrdinaryAuthority>();
        await using var app = builder.Build();

        if (enabled) Assert.Throws<NotSupportedException>(() => app.MapCellBridge());
        else app.MapCellBridge();
    }

    [Fact]
    public async Task SharedAuthorizationRejectsMixedResourcesAndExpiredRecipientTickets()
    {
        await using var fixture = await Fixture.Start();
        Guid client = await fixture.CreateCoauthorLease();
        var authorizer = fixture.App.Services.GetRequiredService<IOfficeRealTimeAuthorizer>();
        var sender = new OfficeRealTimeTicket(fixture.ResourceId, fixture.Head.LifecycleGeneration,
            TestActor.Value, client, fixture.Clock.GetUtcNow().AddMinutes(30));
        var recipient = sender with { ExpiresUtc = fixture.Clock.GetUtcNow().AddMinutes(1) };

        var bothAllowed = await authorizer.AuthorizeTogetherAsync([
            new(sender, DocumentAccess.Write, true), new(recipient, DocumentAccess.Read, false)
        ], CancellationToken.None);
        Assert.Equal([true, true], bothAllowed);
        var mixedResources = await authorizer.AuthorizeTogetherAsync([
            new(sender, DocumentAccess.Write, true), new(recipient with { ResourceId = Guid.NewGuid() }, DocumentAccess.Read, false)
        ], CancellationToken.None);
        Assert.Equal([false, false], mixedResources);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        var expiredRecipient = await authorizer.AuthorizeTogetherAsync([
            new(sender, DocumentAccess.Write, true), new(recipient, DocumentAccess.Read, false)
        ], CancellationToken.None);
        Assert.Equal([true, false], expiredRecipient);
    }

    private sealed class OrdinaryAuthority : IDocumentAuthorizationProvider
    {
        public string PolicyDomain => "tests:rtc-ordinary";
        public ValueTask<DocumentAuthorizationDecision> AuthorizeAsync(DocumentAuthorizationRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new DocumentAuthorizationDecision(true));
    }

    private static string ReplaceQueryValue(string query, string key, string value)
    {
        var parsed = QueryHelpers.ParseQuery("?" + query).ToDictionary(item => item.Key, item => (string?)item.Value.ToString());
        parsed[key] = value;
        return QueryString.Create(parsed).Value![1..];
    }

    private sealed class Fixture(WebApplication app, StorageProvider provider, HttpClient client,
        ManualClock clock, Guid resourceId, DocumentHead head) : IAsyncDisposable
    {
        internal WebApplication App => app;
        internal TestServer Server => app.GetTestServer();
        internal StorageProvider Provider => provider;
        internal HttpClient Client => client;
        internal ManualClock Clock => clock;
        internal Guid ResourceId => resourceId;
        internal DocumentHead Head => head;

        internal async Task<Guid> CreateCoauthorLease(Guid? resourceId = null, Guid? clientId = null)
        {
            var target = resourceId ?? ResourceId;
            var service = App.Services.GetRequiredService<CellBridgeDocumentService>();
            var actor = TestActor.Value;
            clientId ??= Guid.NewGuid();
            var exclusiveId = Guid.NewGuid().ToString("D");
            var schemaId = Guid.NewGuid().ToString("D");
            var getLock = new FssHttpSubRequest { Type = SubRequestType.ExclusiveLock,
                SubRequestDataAttributes = { ["ExclusiveLockRequestType"] = "GetLock", ["ExclusiveLockID"] = exclusiveId,
                    ["ClientID"] = clientId.Value.ToString("D"), ["Timeout"] = "3600" } };
            var acquired = await service.ExecuteControlAsync(target, getLock, actor, CancellationToken.None);
            Assert.Null(acquired.ErrorCode);

            var convert = new FssHttpSubRequest { Type = SubRequestType.ExclusiveLock,
                SubRequestDataAttributes = { ["ExclusiveLockRequestType"] = "ConvertToSchemaJoinCoauth",
                    ["ExclusiveLockID"] = exclusiveId, ["SchemaLockID"] = schemaId,
                    ["ClientID"] = clientId.Value.ToString("D"), ["Timeout"] = "3600" } };
            var joined = await service.ExecuteControlAsync(target, convert, actor, CancellationToken.None);
            Assert.Null(joined.ErrorCode);
            return clientId.Value;
        }

        internal async Task<Guid> CreateSiblingDocument()
        {
            var service = App.Services.GetRequiredService<CellBridgeDocumentService>();
            var sibling = (await service.CreateAsync("/shared/rtc-sibling.docx", MinimalDocx.Create(), TestActor.Value))!;
            return sibling.ResourceId;
        }

        internal Task<IReadOnlyDictionary<string, string>> Properties(bool mounted = false) =>
            App.Services.GetRequiredService<IOfficeRealTimePropertiesProvider>().GetPropertiesAsync(
                ResourceId, TestActor.Value, PublicOrigin + (mounted ? Mount : ""),
                CancellationToken.None).AsTask();

        internal Task<HttpResponseMessage> GetFindSession(string signature, Guid clientId, bool mounted = false)
        {
            var path = (mounted ? Mount : "") + "/_cellbridge/rtc/FindSession";
            var target = QueryHelpers.AddQueryString(path, new Dictionary<string, string?>
            {
                ["qs"] = signature,
                ["ClientId"] = clientId.ToString("D"),
            });
            return Client.GetAsync(target);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
        }

        internal static async Task<Fixture> Start(bool? enabled = null, bool mounted = false,
            Action<CellBridgeOptions>? configure = null)
        {
            var clock = new ManualClock();
            var content = new InMemoryContentStore();
            var provider = new StorageProvider(new InMemoryDocumentRepository(content, clock: clock), content);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
            TestActor.Register(builder.Services);
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddCellBridge(provider, requireDurability: false, configure: options =>
            {
                if (enabled is { } rtcEnabled) options.EnableOfficeRealTime = rtcEnabled;
                options.PublicOrigin = PublicOrigin;
                configure?.Invoke(options);
            });
            var app = builder.Build();
            if (mounted) app.UsePathBase(Mount);
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapCellBridge();
            var service = app.Services.GetRequiredService<CellBridgeDocumentService>();
            var head = (await service.CreateAsync("/shared/rtc.docx", MinimalDocx.Create(), TestActor.Value))!;
            await app.StartAsync();
            return new(app, provider, app.GetTestClient(), clock, head.ResourceId, head);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-30T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan elapsed) => now += elapsed;
        internal void Reset() => now = DateTimeOffset.Parse("2026-10-30T12:00:00Z");
    }
}
