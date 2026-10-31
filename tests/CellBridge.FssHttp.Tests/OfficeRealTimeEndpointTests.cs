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
using Microsoft.AspNetCore.WebUtilities;

namespace CellBridge.FssHttp.Tests;

public sealed class OfficeRealTimeEndpointTests
{
    private const string PublicOrigin = "https://office.example";
    private const string Mount = "/mounted";

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
        Assert.Equal(PublicOrigin + Mount + "/_cellbridge/rtc/signalr", json.RootElement.GetProperty("url").GetString());
        Assert.Equal("environment=CellBridge", json.RootElement.GetProperty("connectionString").GetString());
        Assert.Contains(Mount + "/_cellbridge/rtc/", properties["RealtimeTypingEndpointUrl"]);
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
    public async Task TwoMinuteTicketAdvertisesItsLifetimeExpiresAndCanBeReacquired()
    {
        await using var fixture = await Fixture.Start(configure: options =>
            options.OfficeRealTimeTokenLifetime = TimeSpan.FromMinutes(2));
        Guid client = await fixture.CreateCoauthorLease();
        var properties = await fixture.Properties();
        Assert.Equal(fixture.Clock.GetUtcNow().AddMinutes(2).ToUnixTimeMilliseconds().ToString(),
            properties["DocumentAccessTokenTtl"]);
        using (var current = await fixture.GetFindSession(properties["DocumentAccessToken"], client))
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
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

        var authorizedNegotiatePath = QueryHelpers.AddQueryString("/_cellbridge/rtc/rtchub/negotiate",
            new Dictionary<string, string?> { ["negotiateVersion"] = "1", ["qs"] = signature,
                ["ClientId"] = clientId.ToString("D") });
        using var authorized = await fixture.Client.PostAsync(authorizedNegotiatePath, null);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
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
