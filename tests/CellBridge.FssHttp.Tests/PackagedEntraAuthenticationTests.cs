using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using CellBridge.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CellBridge.FssHttp.Tests;

/// <summary>Consumer integration without the sample authentication project or its account database.</summary>
public sealed class PackagedEntraAuthenticationTests
{
    private const string CookieScheme = "Consumer.Session";
    private const string Origin = "https://office.example.test";
    private static readonly Guid TenantId = Guid.Parse("2853e6e3-a865-44cc-9c37-33537ea6dfe5");
    private static readonly Guid ClientId = Guid.Parse("45e3e565-1b8e-47b3-b3cf-3ea99d582e54");
    private static readonly Guid ObjectId = Guid.Parse("e70a36cc-9540-48c2-90f4-96c67cafce2f");
    private static readonly string Issuer = $"https://login.microsoftonline.com/{TenantId:D}/v2.0";

    [Fact]
    public async Task ConsumerCanFollowAudienceChangesWithoutRebuildingItsHost()
    {
        var audience = new MutableAudience();
        await using var fixture = await Fixture.CreateAsync(audience: audience);
        using var home = await fixture.ExchangeAsync(fixture.Token());
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        using var personalDenied = await fixture.ExchangeAsync(fixture.Token("personal-account"));
        Assert.Equal(HttpStatusCode.Unauthorized, personalDenied.StatusCode);
        audience.Value = CellBridgeEntraAccountAudience.OrganizationsAndPersonal;
        using var personalAllowed = await fixture.ExchangeAsync(fixture.Token("personal-account"));
        Assert.Equal(HttpStatusCode.OK, personalAllowed.StatusCode);
        audience.Value = CellBridgeEntraAccountAudience.Personal;
        using var organizationDenied = await fixture.ExchangeAsync(fixture.Token());
        Assert.Equal(HttpStatusCode.Unauthorized, organizationDenied.StatusCode);
    }

    private sealed class MutableAudience : ICellBridgeEntraAudienceProvider
    {
        public CellBridgeEntraAccountAudience Value { get; set; } = CellBridgeEntraAccountAudience.SingleTenant;
        public ValueTask<CellBridgeEntraAudienceSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CellBridgeEntraAudienceSnapshot(Value, TenantId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task NativeRegistrationNeedsNoBrowserSecretAndPreservesHostDefaultScheme()
    {
        await using var fixture = await Fixture.CreateAsync();
        var schemes = fixture.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.Equal(CookieScheme, (await schemes.GetDefaultAuthenticateSchemeAsync())!.Name);
        Assert.Equal(CookieScheme, (await schemes.GetDefaultChallengeSchemeAsync())!.Name);

        using var exchange = await fixture.ExchangeAsync(fixture.Token());
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        Assert.NotNull(fixture.Mapping.LastIdentity);
        Assert.Equal(EntraAuthentication.LoginProvider, fixture.Mapping.LastIdentity.Provider);
        Assert.Equal(EntraAuthentication.ProviderKey(TenantId, ObjectId), fixture.Mapping.LastIdentity.ProviderKey);
        Assert.Equal(TenantId, fixture.Mapping.LastIdentity.TenantId);
        Assert.Equal(ObjectId, fixture.Mapping.LastIdentity.ObjectId);
        var ticket = fixture.ReadTicket(exchange);
        Assert.Equal("consumer:editor", ticket.Principal.FindFirstValue(CellBridgeActor.SubjectClaim));
        Assert.Equal("Host display name", ticket.Principal.FindFirstValue(CellBridgeActor.DisplayNameClaim));
        Assert.False(ticket.Principal.HasClaim(CellBridgeActor.CreateClaim, "true"));
    }

    [Fact]
    public async Task CookieExchangeFollowedByRepeatedCapabilityDiscoveryDoesNotRestartAuthentication()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var exchange = await fixture.ExchangeAsync(fixture.Token());
        var cookie = CookieHeader(exchange);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, "/_vti_bin/cellstorage.svc");
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            request.Headers.TryAddWithoutValidation("X-MS-CookieUri-Requested", "t");
            using var capabilities = await fixture.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
            Assert.False(capabilities.Headers.Contains("Set-Cookie"));
        }

        using var anonymous = new HttpRequestMessage(HttpMethod.Options, "/_vti_bin/cellstorage.svc");
        anonymous.Headers.TryAddWithoutValidation("X-MS-CookieUri-Requested", "t");
        using var discovery = await fixture.Client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        Assert.Contains(discovery.Headers.WwwAuthenticate, challenge =>
            challenge.Scheme == "Bearer" && challenge.Parameter!.Contains("cookie_uri=", StringComparison.Ordinal));

        using var forced = new HttpRequestMessage(HttpMethod.Options, "/_vti_bin/cellstorage.svc");
        forced.Headers.TryAddWithoutValidation("Cookie", cookie);
        forced.Headers.TryAddWithoutValidation("X-IDCRL_OPTIONS", "force-auth-challenge");
        forced.Headers.TryAddWithoutValidation("IgnoreCookieAuthentication", "t");
        using var challengeResponse = await fixture.Client.SendAsync(forced);
        Assert.Equal(HttpStatusCode.Unauthorized, challengeResponse.StatusCode);
        Assert.Contains(challengeResponse.Headers.WwwAuthenticate, challenge => challenge.Scheme == "Bearer");
    }

    [Theory]
    [InlineData("Bearer invalid-token")]
    [InlineData("Basic invalid-credentials")]
    public async Task InvalidSuppliedCredentialsCannotUseAnExistingCookie(string authorization)
    {
        await using var fixture = await Fixture.CreateAsync(forceRenewal: true);
        using var exchange = await fixture.ExchangeAsync(fixture.Token());
        var cookie = CookieHeader(exchange);

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Options, "/_vti_bin/cellstorage.svc"),
            (HttpMethod.Post, EntraAuthentication.NativeCookiePath),
        })
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            using var response = await fixture.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("missing-subject")]
    [InlineData("unauthenticated")]
    public async Task ExchangeRequiresAnAuthenticatedMappedHostSubject(string mode)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Mapping.Mode = mode;
        using var response = await fixture.ExchangeAsync(fixture.Token());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("wrong-audience")]
    [InlineData("wrong-tenant")]
    [InlineData("personal-account")]
    [InlineData("wrong-scope")]
    [InlineData("wrong-client")]
    [InlineData("expired")]
    public async Task InvalidTokensNeverReachTheAccountMapper(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var response = await fixture.ExchangeAsync(fixture.Token(change));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(fixture.Mapping.LastIdentity);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task NativeSessionExpiresWithTheTokenAndCannotRenewThroughHostEvents()
    {
        await using var fixture = await Fixture.CreateAsync(forceRenewal: true);
        var expiry = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds());
        using var exchange = await fixture.ExchangeAsync(fixture.Token(expires: expiry));
        var ticket = fixture.ReadTicket(exchange);
        Assert.Equal(expiry, ticket.Properties.ExpiresUtc);
        Assert.False(ticket.Properties.AllowRefresh);
        var setCookie = Assert.Single(exchange.Headers.GetValues("Set-Cookie"));
        Assert.Contains("samesite=none", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/host-account");
        request.Headers.TryAddWithoutValidation("Cookie", CookieHeader(exchange));
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal("consumer:editor", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ExchangeAlsoCapsSessionAtTheConfiguredCookieLifetime()
    {
        await using var fixture = await Fixture.CreateAsync(cookieLifetime: TimeSpan.FromMinutes(1));
        var before = DateTimeOffset.UtcNow;
        using var exchange = await fixture.ExchangeAsync(fixture.Token(expires: before.AddHours(1)));
        var ticket = fixture.ReadTicket(exchange);
        Assert.InRange(ticket.Properties.ExpiresUtc!.Value, before.AddSeconds(59), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task HostSigningInCannotExtendTokenExpiryOrWeakenNativeCookieAttributes()
    {
        await using var fixture = await Fixture.CreateAsync(extendOnSigningIn: true);
        var expiry = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds());
        using var exchange = await fixture.ExchangeAsync(fixture.Token(expires: expiry));
        var ticket = fixture.ReadTicket(exchange);
        Assert.Equal(expiry, ticket.Properties.ExpiresUtc);
        Assert.False(ticket.Properties.AllowRefresh);
        var cookie = Assert.Single(exchange.Headers.GetValues("Set-Cookie"));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=none", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("max-age=", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("duplicate-subject")]
    [InlineData("multiple-identities")]
    public async Task AmbiguousMappedPrincipalsCannotEstablishASession(string mode)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Mapping.Mode = mode;
        using var response = await fixture.ExchangeAsync(fixture.Token());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("missing-origin")]
    [InlineData("http-origin")]
    [InlineData("invalid-tenant")]
    [InlineData("common")]
    [InlineData("organizations")]
    [InlineData("consumers")]
    [InlineData("invalid-client")]
    public void InvalidProviderConfigurationIsRejectedDuringRegistration(string problem)
    {
        var services = new ServiceCollection();
        Assert.ThrowsAny<ArgumentException>(() => services.AddCellBridge(TestStorage.Memory(), requireDurability: false,
            configure: options => options.PublicOrigin = problem switch
            {
                "missing-origin" => null,
                "http-origin" => "http://office.example.test",
                _ => Origin,
            }, configureHosting: host => host.Authentication(auth =>
            {
                auth.UseMapper<HostMapper>(CookieScheme);
                auth.UseEntra(options =>
                {
                    options.TenantId = problem switch
                    {
                        "invalid-tenant" => "invalid",
                        "common" or "organizations" or "consumers" => problem,
                        _ => TenantId.ToString("D"),
                    };
                    options.ClientId = problem == "invalid-client" ? "invalid" : ClientId.ToString("D");
                });
            })));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("secure")]
    [InlineData("domain")]
    [InlineData("events-type")]
    public async Task InvalidHostCookiesFailAtStartupWithoutBeingReconfigured(string problem)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new MappingState());
        builder.Services.AddAuthentication(CookieScheme).AddCookie(CookieScheme, cookie =>
        {
            cookie.Cookie.Name = problem == "name" ? "HostSession" : EntraAuthentication.SessionCookieName;
            cookie.Cookie.Path = "/";
            cookie.Cookie.SecurePolicy = problem == "secure" ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
            if (problem == "domain") cookie.Cookie.Domain = "example.test";
            if (problem == "events-type") cookie.EventsType = typeof(CookieAuthenticationEvents);
        });
        builder.Services.AddCellBridge(TestStorage.Memory(), requireDurability: false,
            configure: options => options.PublicOrigin = Origin,
            configureHosting: host => host.Authentication(auth =>
            {
                auth.UseMapper<HostMapper>(CookieScheme);
                auth.UseEntra(options =>
                {
                    options.TenantId = TenantId.ToString("D");
                    options.ClientId = ClientId.ToString("D");
                });
            }));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCellBridge();
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
    }

    [Fact]
    public async Task CrossOriginExchangeCannotIssueASession()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, EntraAuthentication.NativeCookiePath);
        request.Headers.Authorization = new("Bearer", fixture.Token());
        request.Headers.TryAddWithoutValidation("Origin", "https://attacker.example.test");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(fixture.Mapping.LastIdentity);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task MappingCellBridgeWithoutEntraDoesNotExposeTheCookieExchange()
    {
        await using var fixture = await Fixture.CreateAsync(enableEntra: false);
        using var response = await fixture.ExchangeAsync("invalid-token");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string CookieHeader(HttpResponseMessage response) =>
        Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0];

    private sealed class MappingState
    {
        public string Mode { get; set; } = "accept";
        public ValidatedExternalIdentity? LastIdentity { get; set; }
    }

    private sealed class HostMapper(MappingState state) : ICellBridgeExternalIdentityMapper
    {
        public ValueTask<ClaimsPrincipal?> MapAsync(ValidatedExternalIdentity identity, CancellationToken cancellationToken)
        {
            state.LastIdentity = identity;
            if (state.Mode == "reject") return ValueTask.FromResult<ClaimsPrincipal?>(null);
            var claims = new List<Claim> { new(CellBridgeActor.DisplayNameClaim, "Host display name") };
            if (state.Mode != "missing-subject") claims.Add(new(CellBridgeActor.SubjectClaim, "consumer:editor"));
            if (state.Mode == "duplicate-subject") claims.Add(new(CellBridgeActor.SubjectClaim, "consumer:another"));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims,
                state.Mode == "unauthenticated" ? null : "HostAccounts"));
            if (state.Mode == "multiple-identities") principal.AddIdentity(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "another") }, "AnotherAccount"));
            return ValueTask.FromResult<ClaimsPrincipal?>(principal);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly RSA _key;
        public IServiceProvider Services => _app.Services;
        public HttpClient Client { get; }
        public MappingState Mapping { get; }

        private Fixture(WebApplication app, RSA key, MappingState mapping)
        {
            _app = app;
            _key = key;
            Mapping = mapping;
            Client = app.GetTestClient();
            Client.BaseAddress = new Uri(Origin);
        }

        public static async Task<Fixture> CreateAsync(bool forceRenewal = false, TimeSpan? cookieLifetime = null,
            bool enableEntra = true, bool extendOnSigningIn = false, MutableAudience? audience = null)
        {
            var key = RSA.Create(2048);
            var metadata = new OpenIdConnectConfiguration
            {
                Issuer = audience is null ? Issuer : "https://login.microsoftonline.com/{tenantid}/v2.0",
            };
            metadata.SigningKeys.Add(new RsaSecurityKey(key) { KeyId = "consumer-signing-key" });
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var mapping = new MappingState();
            builder.Services.AddSingleton(mapping);
            if (audience is not null) builder.Services.AddSingleton<ICellBridgeEntraAudienceProvider>(audience);
            builder.Services.AddAuthentication(CookieScheme).AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = EntraAuthentication.SessionCookieName;
                options.Cookie.Path = "/";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = cookieLifetime ?? TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.Events.OnSigningIn = context =>
                {
                    if (extendOnSigningIn)
                    {
                        context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1);
                        context.Properties.AllowRefresh = true;
                        context.Properties.Items.Clear();
                        context.CookieOptions.Secure = false;
                        context.CookieOptions.HttpOnly = false;
                        context.CookieOptions.Domain = "example.test";
                        context.CookieOptions.MaxAge = TimeSpan.FromDays(1);
                    }
                    return Task.CompletedTask;
                };
                options.Events.OnValidatePrincipal = context =>
                {
                    if (forceRenewal) context.ShouldRenew = true;
                    return Task.CompletedTask;
                };
            });
            builder.Services.AddCellBridge(TestStorage.Memory(), requireDurability: false,
                configure: options =>
                {
                    options.PublicOrigin = Origin;
                    options.EnableOfficeRealTime = false;
                },
                configureHosting: enableEntra ? host => host.Authentication(auth =>
                {
                    auth.UseMapper<HostMapper>(CookieScheme);
                    auth.UseEntra(options =>
                    {
                        options.TenantId = TenantId.ToString("D");
                        options.ClientId = ClientId.ToString("D");
                        options.DiscoverAccountTypes = audience is not null;
                        options.ClientSecret = audience is not null ? "test-only-discovery-secret" : null;
                    });
                }) : null);
            if (enableEntra) builder.Services.PostConfigure<JwtBearerOptions>(EntraAuthentication.BearerScheme, options =>
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata));
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapCellBridge();
            app.MapGet("/host-account", (HttpContext context) => context.User.FindFirstValue(CellBridgeActor.SubjectClaim))
                .RequireAuthorization();
            await app.StartAsync();
            return new Fixture(app, key, mapping);
        }

        public string Token(string change = "", DateTimeOffset? expires = null)
        {
            var now = DateTime.UtcNow;
            // This models token validation only, not personal-account acquisition by Office.
            var tenant = change == "personal-account" ? "9188040d-6c67-4c5b-b112-36a304b66dad"
                : change == "wrong-tenant" ? Guid.NewGuid().ToString("D") : TenantId.ToString("D");
            var issuer = change == "personal-account" ? $"https://login.microsoftonline.com/{tenant}/v2.0" : Issuer;
            var claims = new[]
            {
                new Claim("tid", tenant),
                new Claim("ver", "2.0"),
                new Claim("oid", ObjectId.ToString("D")),
                new Claim("scp", change == "wrong-scope" ? "profile" : EntraAuthentication.AccessScope),
                new Claim("azp", change == "wrong-client" ? Guid.NewGuid().ToString("D") : EntraAuthentication.NativeOfficeClientId),
                new Claim(CellBridgeActor.SubjectClaim, "attacker:forged"),
                new Claim(CellBridgeActor.CreateClaim, "true"),
            };
            var token = new JwtSecurityToken(issuer, change == "wrong-audience" ? "another-api" : ClientId.ToString("D"),
                claims, change == "expired" ? now.AddMinutes(-20) : now.AddMinutes(-1),
                change == "expired" ? now.AddMinutes(-10) : expires?.UtcDateTime ?? now.AddMinutes(5),
                new SigningCredentials(new RsaSecurityKey(_key) { KeyId = "consumer-signing-key" }, SecurityAlgorithms.RsaSha256));
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public async Task<HttpResponseMessage> ExchangeAsync(string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, EntraAuthentication.NativeCookiePath);
            request.Headers.Authorization = new("Bearer", token);
            return await Client.SendAsync(request);
        }

        public AuthenticationTicket ReadTicket(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var options = Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieScheme);
            return Assert.IsType<AuthenticationTicket>(options.TicketDataFormat.Unprotect(CookieHeader(response).Split('=', 2)[1]));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            _key.Dispose();
        }
    }
}
