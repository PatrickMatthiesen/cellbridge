using System.IdentityModel.Tokens.Jwt;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using CellBridge.AspNetCore;
using CellBridge.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CellBridge.FssHttp.Tests;

public sealed class EntraAuthenticationTests
{
    private static readonly Guid TenantId = Guid.Parse("a7654a10-9200-4e1d-a21c-693b8b390f72");
    private static readonly Guid ClientId = Guid.Parse("fb4da779-7d29-45c5-859a-2cb0e7583cc3");
    private static readonly Guid ObjectId = Guid.Parse("b725e35c-6850-4b60-92f8-2aa1d48ea291");
    private static readonly Guid OtherTenantId = Guid.Parse("8f4053cc-bb16-4a55-9f3a-47ef7fa5d8e2");
    private static readonly string Issuer = $"https://login.microsoftonline.com/{TenantId:D}/v2.0";

    [Fact]
    public async Task SignedOfficeBearerMapsOnlyLinkedLocalIdentityAndUsesBearerChallenge()
    {
        await using var fixture = await Fixture.CreateAsync();

        using var response = await fixture.Client.SendAsync(new BearerToken(fixture.CreateToken()).Request());

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var identity = await response.Content.ReadFromJsonAsync<IdentityResult>();
        Assert.NotNull(identity);
        Assert.Equal("local:" + fixture.User.Id, identity.Subject);
        Assert.Equal("Database display name", identity.DisplayName);
        Assert.True(identity.CanCreate);

        using var missing = await fixture.Client.GetAsync("/office");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Contains("Bearer realm=", missing.Headers.WwwAuthenticate.Single().ToString(), StringComparison.Ordinal);
        Assert.Equal("no-store", missing.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer")]
    public async Task ProtocolDiscoveryChallengesUntilOfficeSuppliesAToken(string? authorization)
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var path in new[] { "/", "/shared/", "/_vti_bin/cellstorage.svc" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, path);
            if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
            using var challenge = await fixture.Client.SendAsync(request);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, challenge.StatusCode);
            Assert.Contains("Bearer realm=", challenge.Headers.WwwAuthenticate.Single().ToString(), StringComparison.Ordinal);
            Assert.Contains("cookie_uri=\"https://office.example.test/_api/SP.OAuth.NativeClient/Authenticate\"",
                challenge.Headers.WwwAuthenticate.Single().ToString(), StringComparison.Ordinal);
            Assert.False(challenge.Headers.Contains("X-MSFSSHTTP"));

            using var authenticated = new HttpRequestMessage(HttpMethod.Options, path);
            authenticated.Headers.Authorization = new("Bearer", fixture.CreateToken());
            using var discovery = await fixture.Client.SendAsync(authenticated);
            Assert.Equal(System.Net.HttpStatusCode.OK, discovery.StatusCode);
            Assert.Equal("1.5", discovery.Headers.GetValues("X-MSFSSHTTP").Single());
        }
    }

    [Theory]
    [InlineData("wrong-tenant")]
    [InlineData("wrong-audience")]
    [InlineData("wrong-issuer")]
    [InlineData("expired")]
    [InlineData("wrong-scope")]
    [InlineData("wrong-client")]
    [InlineData("wrong-signing-key")]
    public async Task BearerRejectsInvalidTokenProperties(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var response = await fixture.Client.SendAsync(new BearerToken(fixture.CreateToken(change)).Request());
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("disabled")]
    [InlineData("locked")]
    [InlineData("two-factor")]
    public async Task BearerRequiresAnEnabledUnrestrictedLinkedLocalAccount(string state)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (state == "unknown")
            await fixture.RemoveLoginAsync();
        else
        {
            if (state == "disabled") fixture.User.Enabled = false;
            if (state == "locked")
            {
                fixture.User.LockoutEnabled = true;
                fixture.User.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(10);
            }
            if (state == "two-factor") fixture.User.TwoFactorEnabled = true;
            await fixture.UpdateUserAsync();
        }

        using var response = await fixture.Client.SendAsync(new BearerToken(fixture.CreateToken()).Request());
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OfficeSchemeUsesCookieForAbsentOrEmptyBearerAndNeverFallsBackFromAnInvalidToken()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(IdentityConstants.ApplicationScheme,
            fixture.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value.DefaultScheme);

        var cookie = await fixture.CreateCookieAsync();
        using var cookieOnly = await fixture.SendOfficeRequestAsync(HttpMethod.Get, "/office", cookie);
        Assert.Equal(System.Net.HttpStatusCode.OK, cookieOnly.StatusCode);

        using var emptyBearer = await fixture.SendOfficeRequestAsync(HttpMethod.Get, "/office", cookie, "  bEaReR   ");
        Assert.Equal(System.Net.HttpStatusCode.OK, emptyBearer.StatusCode);

        using var invalidBearer = await fixture.SendOfficeRequestAsync(HttpMethod.Get, "/office", cookie, "Bearer invalid-token");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, invalidBearer.StatusCode);

        using var otherScheme = await fixture.SendOfficeRequestAsync(HttpMethod.Get, "/office", cookie, "Basic credentials");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, otherScheme.StatusCode);

        using var malformedBearer = await fixture.SendOfficeRequestAsync(HttpMethod.Get, "/office", cookie, "Bearer\tinvalid-token");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, malformedBearer.StatusCode);

        using var multiple = new HttpRequestMessage(HttpMethod.Get, "/office");
        multiple.Headers.TryAddWithoutValidation("Cookie", EntraAuthentication.SessionCookieName + "=" + cookie);
        multiple.Headers.TryAddWithoutValidation("Authorization", new[] { "Bearer invalid-token", "Bearer " + fixture.CreateToken() });
        using var multipleResponse = await fixture.Client.SendAsync(multiple);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, multipleResponse.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer")]
    [InlineData("  bEaReR   ")]
    public async Task NativeCookieExchangeBootstrapsCookieForProtocolOptions(string? authorization)
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = fixture.CreateToken();
        using var exchange = await fixture.ExchangeCookieAsync(token);

        Assert.Equal(System.Net.HttpStatusCode.OK, exchange.StatusCode);
        var cookie = Assert.Single(exchange.Headers.GetValues("Set-Cookie"));
        AssertCookieAttributes(cookie);
        var cookieValue = CookieValue(cookie);

        using var options = await fixture.SendOfficeRequestAsync(HttpMethod.Options, "/shared/", cookieValue,
            authorization);
        Assert.Equal(System.Net.HttpStatusCode.OK, options.StatusCode);
        Assert.Equal("1.5", options.Headers.GetValues("X-MSFSSHTTP").Single());
    }

    [Fact]
    public async Task OrdinaryBearerRequestDoesNotIssueSessionCookie()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var response = await fixture.Client.SendAsync(new BearerToken(fixture.CreateToken()).Request());

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task NativeCookieExchangeRequiresBearerAndCannotUseAnExistingCookie()
    {
        await using var fixture = await Fixture.CreateAsync();
        var cookie = await fixture.CreateCookieAsync();

        using var missing = await fixture.ExchangeCookieAsync(null);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.False(missing.Headers.Contains("Set-Cookie"));

        using var cookieOnly = await fixture.ExchangeCookieAsync(null, cookie: cookie);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, cookieOnly.StatusCode);
        Assert.False(cookieOnly.Headers.Contains("Set-Cookie"));

        using var invalid = await fixture.ExchangeCookieAsync("not-a-valid-token", cookie: cookie);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.False(invalid.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task EmptyBearerNativeCookieProbeReturnsChallengeWithoutIssuingCookie()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var response = await fixture.ExchangeCookieAsync(null, "  bEaReR   ");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Contains(response.Headers.WwwAuthenticate, value => value.ToString().Contains("cookie_uri=\"https://office.example.test/_api/SP.OAuth.NativeClient/Authenticate\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(3600, 60)]
    [InlineData(60, 3600)]
    public async Task NativeCookieTicketIsPersistentAndExpiresAtTheEarlierLimit(int cookieSpanSeconds, int tokenLifetimeSeconds)
    {
        await using var fixture = await Fixture.CreateAsync(TimeSpan.FromSeconds(cookieSpanSeconds));
        var token = fixture.CreateToken(lifetime: TimeSpan.FromSeconds(tokenLifetimeSeconds));
        var tokenExpiry = new DateTimeOffset(new JwtSecurityTokenHandler().ReadJwtToken(token).ValidTo, TimeSpan.Zero);
        using var response = await fixture.ExchangeCookieAsync(token);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        var protectedValue = CookieValue(cookie);
        var options = fixture.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var ticket = Assert.IsType<AuthenticationTicket>(options.TicketDataFormat.Unprotect(protectedValue));
        Assert.False(ticket.Properties.AllowRefresh);
        Assert.True(ticket.Properties.IsPersistent);
        Assert.NotNull(ticket.Properties.IssuedUtc);
        Assert.NotNull(ticket.Properties.ExpiresUtc);
        Assert.True(ticket.Properties.ExpiresUtc <= tokenExpiry);
        Assert.True(ticket.Properties.ExpiresUtc <= ticket.Properties.IssuedUtc + TimeSpan.FromSeconds(cookieSpanSeconds));
        Assert.True(ticket.Properties.ExpiresUtc > DateTimeOffset.UtcNow);
        var cookieExpiry = CookieExpiry(cookie);
        Assert.True(cookieExpiry <= tokenExpiry.AddSeconds(1));
        Assert.True(cookieExpiry <= ticket.Properties.IssuedUtc.Value.AddSeconds(cookieSpanSeconds + 1));
        Assert.True(cookieExpiry > DateTimeOffset.UtcNow);
        AssertCookieAttributes(cookie);
    }

    [Fact]
    public async Task MountedNativeCookieChallengeUsesConfiguredOriginInsteadOfRequestHost()
    {
        await using var fixture = await Fixture.CreateAsync(pathBase: "/mounted");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/mounted" + EntraAuthentication.NativeCookiePath);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer");
        request.Headers.Host = "attacker.example.test";
        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, value => value.ToString().Contains(
            "cookie_uri=\"https://office.example.test/mounted" + EntraAuthentication.NativeCookiePath + "\"",
            StringComparison.Ordinal));
        Assert.DoesNotContain(response.Headers.WwwAuthenticate, value => value.ToString().Contains("attacker.example.test", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NativeCookieExchangeIsPostOnly()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var response = await fixture.Client.GetAsync(EntraAuthentication.NativeCookiePath);

        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("disabled")]
    [InlineData("locked")]
    [InlineData("two-factor")]
    public async Task NativeCookieExchangeRejectsUnlinkedOrRestrictedLocalAccounts(string state)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (state == "unknown")
            await fixture.RemoveLoginAsync();
        else
        {
            if (state == "disabled") fixture.User.Enabled = false;
            if (state == "locked")
            {
                fixture.User.LockoutEnabled = true;
                fixture.User.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(10);
            }
            if (state == "two-factor") fixture.User.TwoFactorEnabled = true;
            await fixture.UpdateUserAsync();
        }

        using var response = await fixture.ExchangeCookieAsync(fixture.CreateToken());
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("tampered")]
    public async Task ExpiredOrTamperedSessionCookieCannotAuthorizeOffice(string kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var cookie = await fixture.CreateCookieAsync(expiry: kind == "expired"
            ? DateTimeOffset.UtcNow.AddMinutes(-5)
            : DateTimeOffset.UtcNow.AddHours(1));
        if (kind == "tampered") cookie = cookie[..^1] + (cookie[^1] == 'A' ? 'B' : 'A');

        using var response = await fixture.SendOfficeRequestAsync(HttpMethod.Get, "/office", cookie);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticationDiagnosticsDoNotLogBearerOrSessionCookieValues()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = fixture.CreateToken();
        using var response = await fixture.ExchangeCookieAsync(token);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var cookie = CookieValue(Assert.Single(response.Headers.GetValues("Set-Cookie")));

        Assert.Contains(fixture.LogMessages, value => value.Contains("credential bearer", StringComparison.OrdinalIgnoreCase) &&
            value.Contains("session cookie present False", StringComparison.OrdinalIgnoreCase) &&
            value.Contains("scheme CellBridge.Entra.Bearer", StringComparison.Ordinal) &&
            value.Contains("outcome succeeded", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fixture.LogMessages, value => value.Contains("session Set-Cookie issued True", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fixture.LogMessages, value => value.Contains(token, StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.LogMessages, value => value.Contains(cookie, StringComparison.Ordinal));
    }

    [Fact]
    public async Task NativeCookieValidationDoesNotRenewTheSessionWhenHostRequestsRenewal()
    {
        await using var fixture = await Fixture.CreateAsync(forceCookieRenewal: true);
        using var exchange = await fixture.ExchangeCookieAsync(fixture.CreateToken());
        Assert.Equal(System.Net.HttpStatusCode.OK, exchange.StatusCode);
        var cookie = CookieValue(Assert.Single(exchange.Headers.GetValues("Set-Cookie")));

        using var validation = await fixture.SendOfficeRequestAsync(HttpMethod.Get, "/office", cookie);

        Assert.Equal(System.Net.HttpStatusCode.OK, validation.StatusCode);
        Assert.False(validation.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task InvalidExchangeDoesNotRenewAnExistingCookieWhenHostRequestsRenewal()
    {
        await using var fixture = await Fixture.CreateAsync(forceCookieRenewal: true);
        var cookie = await fixture.CreateCookieAsync();

        using var exchange = await fixture.ExchangeCookieAsync("not-a-valid-token", cookie: cookie);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, exchange.StatusCode);
        Assert.False(exchange.Headers.Contains("Set-Cookie"));
    }

    private static void AssertCookieAttributes(string setCookie)
    {
        Assert.StartsWith(EntraAuthentication.SessionCookieName + "=", setCookie, StringComparison.Ordinal);
        Assert.Contains("; path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("; domain=", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; expires=", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    private static string CookieValue(string setCookie) =>
        setCookie.Split(';', 2)[0].Split('=', 2)[1];

    private static DateTimeOffset CookieExpiry(string setCookie)
    {
        var expires = setCookie.Split(';').Single(value => value.TrimStart().StartsWith("expires=", StringComparison.OrdinalIgnoreCase));
        Assert.True(DateTimeOffset.TryParse(expires[(expires.IndexOf('=') + 1)..].Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed));
        return parsed;
    }

    [Fact]
    public async Task BrowserOidcUsesCodePkceAndMapsCallbackToLinkedLocalAccount()
    {
        await using var fixture = await Fixture.CreateAsync();
        var options = fixture.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(EntraAuthentication.OidcScheme);
        Assert.Equal("code", options.ResponseType);
        Assert.True(options.UsePkce);
        Assert.False(options.SaveTokens);
        Assert.Equal("/signin-oidc", options.CallbackPath);
        Assert.Equal(IdentityConstants.ApplicationScheme, options.SignInScheme);
        Assert.Equal("local:" + fixture.User.Id, await fixture.InvokeBrowserCallbackAsync(options));
    }

    private sealed record IdentityResult(string? Subject, string? DisplayName, bool CanCreate);

    private sealed record BearerToken(string Value)
    {
        public HttpRequestMessage Request()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/office");
            request.Headers.Authorization = new("Bearer", Value);
            return request;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly RSA _signingKey;
        private readonly RSA _otherSigningKey;
        private readonly WebApplication _app;
        private readonly CapturingLoggerProvider _loggerProvider;
        public IServiceProvider Services => _app.Services;
        public HttpClient Client { get; }
        public CellBridgeUser User { get; }
        public IReadOnlyList<string> LogMessages => _loggerProvider.Messages;

        private Fixture(WebApplication app, RSA signingKey, RSA otherSigningKey, CellBridgeUser user,
            CapturingLoggerProvider loggerProvider)
        {
            _app = app;
            Client = app.GetTestClient();
            _signingKey = signingKey;
            _otherSigningKey = otherSigningKey;
            User = user;
            _loggerProvider = loggerProvider;
        }

        public static async Task<Fixture> CreateAsync(TimeSpan? cookieSpan = null, string? pathBase = null,
            bool forceCookieRenewal = false)
        {
            var key = RSA.Create(2048);
            var otherKey = RSA.Create(2048);
            var databaseName = "entra-auth-" + Guid.NewGuid().ToString("N");
            var keyId = "entra-test-key";
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Entra:Enabled"] = "true",
                ["Authentication:Entra:TenantId"] = TenantId.ToString("D"),
                ["Authentication:Entra:ClientId"] = ClientId.ToString("D"),
                ["Authentication:Entra:ClientSecret"] = "test-only-secret",
            }).Build();
            var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
            metadata.SigningKeys.Add(new RsaSecurityKey(key) { KeyId = keyId });
            var manager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
            var user = new CellBridgeUser
            {
                UserName = "linked-local-user",
                Email = "local@example.test",
                DisplayName = "Database display name",
                CanCreate = true,
                Enabled = true,
                EmailConfirmed = true,
            };

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var services = builder.Services;
            var loggerProvider = new CapturingLoggerProvider();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(loggerProvider);
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
            services.AddSingleton(new AuthenticationOrigin("https://office.example.test"));
                    services.AddSingleton<IConfiguration>(configuration);
                    services.AddDbContext<AuthenticationDatabase>(options =>
                        options.UseInMemoryDatabase(databaseName));
                    services.AddIdentityCore<CellBridgeUser>(options => options.Lockout.AllowedForNewUsers = true)
                        .AddEntityFrameworkStores<AuthenticationDatabase>()
                        .AddSignInManager()
                        .AddClaimsPrincipalFactory<CellBridgeClaimsFactory>();
                    services.AddAuthentication(options => options.DefaultScheme = IdentityConstants.ApplicationScheme)
                        .AddCookie(IdentityConstants.ApplicationScheme, options =>
                        {
                            options.Cookie.Name = EntraAuthentication.SessionCookieName;
                            options.Cookie.Path = "/";
                            options.Cookie.HttpOnly = true;
                            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                            options.ExpireTimeSpan = cookieSpan ?? TimeSpan.FromHours(8);
                            options.SlidingExpiration = true;
                            options.Events.OnValidatePrincipal = context =>
                            {
                                if (forceCookieRenewal) context.ShouldRenew = true;
                                return Task.CompletedTask;
                            };
                        });
                    EntraAuthentication.AddCellBridgeEntraAuthentication(services, configuration);
                    services.AddCellBridge(TestStorage.Memory(), requireDurability: false,
                        configure: options => options.EnableOfficeRealTime = false);
                    services.AddCellBridgeOfficeFormsAuthentication(IdentityConstants.ApplicationScheme,
                        options => options.ProtocolAuthenticationScheme = EntraAuthentication.OfficeScheme);
                    services.AddAuthorization();
                    services.AddRouting();
                    services.PostConfigure<JwtBearerOptions>(EntraAuthentication.BearerScheme, options =>
                    {
                        options.ConfigurationManager = manager;
                        options.TokenValidationParameters.ValidIssuer = Issuer;
                        options.TokenValidationParameters.ValidAudience = ClientId.ToString("D");
                    });
            var app = builder.Build();
            if (pathBase is not null) app.UsePathBase(pathBase);
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapCellBridge();
            EntraAuthentication.MapNativeCookieExchange(app);
            app.MapGet("/office", async context =>
            {
                var result = new IdentityResult(
                    context.User.FindFirstValue(CellBridgeActor.SubjectClaim),
                    context.User.FindFirstValue(CellBridgeActor.DisplayNameClaim),
                    context.User.HasClaim(CellBridgeActor.CreateClaim, "true"));
                await context.Response.WriteAsJsonAsync(result);
            }).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = EntraAuthentication.OfficeScheme });
            await app.StartAsync();

            using (var scope = app.Services.CreateScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<AuthenticationDatabase>();
                await database.Database.EnsureCreatedAsync();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<CellBridgeUser>>();
                var create = await users.CreateAsync(user, "Correct-Horse-482!Battery");
                Assert.True(create.Succeeded, string.Join("; ", create.Errors.Select(x => x.Description)));
                var link = await users.AddLoginAsync(user, new UserLoginInfo(EntraAuthentication.LoginProvider,
                    EntraAuthentication.ProviderKey(TenantId, ObjectId), "Microsoft Entra"));
                Assert.True(link.Succeeded, string.Join("; ", link.Errors.Select(x => x.Description)));
            }

            return new Fixture(app, key, otherKey, user, loggerProvider);
        }

        public string CreateToken(string change = "", TimeSpan? lifetime = null)
        {
            var now = DateTime.UtcNow;
            var tenant = change == "wrong-tenant" ? OtherTenantId : TenantId;
            var issuer = change == "wrong-issuer" ? "https://issuer.example.test/" : Issuer;
            var audience = change == "wrong-audience" ? "wrong-client" : ClientId.ToString("D");
            var scope = change == "wrong-scope" ? "profile" : EntraAuthentication.AccessScope;
            var authorizedParty = change == "wrong-client" ? Guid.NewGuid().ToString("D") : EntraAuthentication.NativeOfficeClientId;
            var notBefore = change == "expired" ? now.AddMinutes(-20) : now.AddMinutes(-1);
            var expiration = change == "expired" ? now.AddMinutes(-10) : now.Add(lifetime ?? TimeSpan.FromMinutes(5));
            var key = change == "wrong-signing-key" ? _otherSigningKey : _signingKey;
            var credentials = new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256);
            var claims = new[]
            {
                new Claim("tid", tenant.ToString("D")),
                new Claim("oid", ObjectId.ToString("D")),
                new Claim("scp", scope),
                new Claim("azp", authorizedParty),
                new Claim("email", "attacker@example.test"),
                new Claim(CellBridgeActor.SubjectClaim, "local:attacker-controlled"),
                new Claim(CellBridgeActor.DisplayNameClaim, "Forged token display name"),
                new Claim(CellBridgeActor.CreateClaim, "true"),
            };
            var token = new JwtSecurityToken(issuer, audience, claims, notBefore, expiration, credentials);
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public async Task<string> CreateCookieAsync(DateTimeOffset? expiry = null)
        {
            using var scope = Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<CellBridgeUser>>();
            var user = await users.FindByIdAsync(User.Id);
            Assert.NotNull(user);
            var principal = await scope.ServiceProvider.GetRequiredService<SignInManager<CellBridgeUser>>()
                .CreateUserPrincipalAsync(user);
            var now = DateTimeOffset.UtcNow;
            var properties = new AuthenticationProperties
            {
                IssuedUtc = now,
                ExpiresUtc = expiry ?? now.AddHours(1),
                AllowRefresh = true,
                IsPersistent = true,
            };
            var options = Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(IdentityConstants.ApplicationScheme);
            return options.TicketDataFormat.Protect(new AuthenticationTicket(principal, properties,
                IdentityConstants.ApplicationScheme));
        }

        public async Task<HttpResponseMessage> ExchangeCookieAsync(string? token, string? authorization = null,
            string? cookie = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, EntraAuthentication.NativeCookiePath);
            if (authorization is not null)
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            else if (token is not null)
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            if (cookie is not null)
                request.Headers.TryAddWithoutValidation("Cookie", EntraAuthentication.SessionCookieName + "=" + cookie);
            return await Client.SendAsync(request);
        }

        public async Task<HttpResponseMessage> SendOfficeRequestAsync(HttpMethod method, string path,
            string? cookieValue = null, string? authorization = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (authorization is not null)
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            if (cookieValue is not null)
                request.Headers.TryAddWithoutValidation("Cookie", EntraAuthentication.SessionCookieName + "=" + cookieValue);
            return await Client.SendAsync(request);
        }

        public async Task RemoveLoginAsync()
        {
            using var scope = Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<CellBridgeUser>>();
            var freshUser = await users.FindByIdAsync(User.Id);
            Assert.NotNull(freshUser);
            var result = await users.RemoveLoginAsync(freshUser, EntraAuthentication.LoginProvider,
                EntraAuthentication.ProviderKey(TenantId, ObjectId));
            Assert.True(result.Succeeded);
        }

        public async Task UpdateUserAsync()
        {
            using var scope = Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<CellBridgeUser>>();
            var freshUser = await users.FindByIdAsync(User.Id);
            Assert.NotNull(freshUser);
            freshUser.Enabled = User.Enabled;
            freshUser.TwoFactorEnabled = User.TwoFactorEnabled;
            freshUser.LockoutEnabled = User.LockoutEnabled;
            freshUser.LockoutEnd = User.LockoutEnd;
            var result = await users.UpdateAsync(freshUser);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(x => x.Description)));
        }

        public async Task<string?> InvokeBrowserCallbackAsync(OpenIdConnectOptions options)
        {
            var context = new DefaultHttpContext { RequestServices = Services };
            var scheme = new AuthenticationScheme(EntraAuthentication.OidcScheme,
                EntraAuthentication.OidcScheme, typeof(OpenIdConnectHandler));
            var callback = new Microsoft.AspNetCore.Authentication.OpenIdConnect.TokenValidatedContext(context, scheme, options,
                new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tid", TenantId.ToString("D")),
                    new Claim("oid", ObjectId.ToString("D")),
                    new Claim("name", "Untrusted Entra name"),
                    new Claim("email", "untrusted@example.test"),
                }, "Entra")), new AuthenticationProperties());
            await options.Events.OnTokenValidated(callback);
            return callback.Principal?.FindFirstValue(CellBridgeActor.SubjectClaim);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            _signingKey.Dispose();
            _otherSigningKey.Dispose();
            _loggerProvider.Dispose();
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];
        public IReadOnlyList<string> Messages
        {
            get { lock (_messages) return _messages.ToArray(); }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);
        public void Dispose() { }

        private sealed class CapturingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                lock (messages) messages.Add(formatter(state, exception));
            }
        }
    }
}
