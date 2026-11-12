using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CellBridge.AspNetCore;
using CellBridge.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CellBridge.FssHttp.Tests;

/// <summary>Real authentication middleware with mocked Azure discovery and token endpoints, without live Office.</summary>
public sealed class EntraAudienceFlowTests
{
    private const string Origin = "https://office.example.test";
    private const string IssuerTemplate = "https://login.microsoftonline.com/{tenantid}/v2.0";
    private static readonly Guid HomeTenant = Guid.Parse("6a51fd7e-2f82-4591-94b6-5dd6a388232f");
    private static readonly Guid OtherTenant = Guid.Parse("b94b5fd0-a56e-436b-94c7-c76a273eae40");
    private static readonly Guid PersonalTenant = Guid.Parse("9188040d-6c67-4c5b-b112-36a304b66dad");
    private static readonly Guid ClientId = Guid.Parse("283a622d-b301-45bf-8b9b-a3e5cce7e393");
    private static readonly Guid ObjectId = Guid.Parse("9d9bc3dd-708a-4265-a458-95e17ea9ec80");

    [Fact]
    public async Task NativeValidationFollowsAllFourAudiencePoliciesWithoutRebuildingTheHost()
    {
        await using var fixture = await Fixture.StartAsync();
        foreach (var (audience, tenant, accepted) in new[]
        {
            (CellBridgeEntraAccountAudience.SingleTenant, HomeTenant, true),
            (CellBridgeEntraAccountAudience.SingleTenant, OtherTenant, false),
            (CellBridgeEntraAccountAudience.Organizations, OtherTenant, true),
            (CellBridgeEntraAccountAudience.Organizations, PersonalTenant, false),
            (CellBridgeEntraAccountAudience.OrganizationsAndPersonal, OtherTenant, true),
            (CellBridgeEntraAccountAudience.OrganizationsAndPersonal, PersonalTenant, true),
            (CellBridgeEntraAccountAudience.Personal, PersonalTenant, true),
            (CellBridgeEntraAccountAudience.Personal, HomeTenant, false),
            (CellBridgeEntraAccountAudience.SingleTenant, HomeTenant, true),
        })
        {
            fixture.Policy.Audience = audience;
            using var exchange = await fixture.ExchangeAsync(fixture.Token(tenant));
            Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, exchange.StatusCode);
            Assert.Equal(accepted, HasSessionCookie(exchange));
        }
    }

    [Fact]
    public async Task BrowserCodeFlowUsesTheSelectedAuthorityForAuthorizationAndRedemptionAcrossPolicies()
    {
        await using var fixture = await Fixture.StartAsync();
        foreach (var (audience, tenant, authority) in new[]
        {
            (CellBridgeEntraAccountAudience.SingleTenant, HomeTenant, HomeTenant.ToString("D")),
            (CellBridgeEntraAccountAudience.Organizations, OtherTenant, "organizations"),
            (CellBridgeEntraAccountAudience.OrganizationsAndPersonal, PersonalTenant, "common"),
            (CellBridgeEntraAccountAudience.Personal, PersonalTenant, "consumers"),
        })
        {
            fixture.Policy.Audience = audience;
            var attempt = await fixture.BeginBrowserAsync(tenant);
            Assert.Equal($"https://login.microsoftonline.com/{authority}/oauth2/v2.0/authorize", attempt.AuthorizationEndpoint);
            Assert.Equal("code", attempt.Query["response_type"].ToString());
            Assert.Equal("S256", attempt.Query["code_challenge_method"].ToString());
            Assert.False(string.IsNullOrEmpty(attempt.Query["state"]));
            Assert.False(string.IsNullOrEmpty(attempt.Query["nonce"]));
            using var callback = await fixture.CompleteBrowserAsync(attempt);
            Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
            Assert.Equal("/complete", callback.Headers.Location!.OriginalString);
            Assert.True(HasSessionCookie(callback));
            Assert.Equal($"https://login.microsoftonline.com/{authority}/oauth2/v2.0/token", fixture.Backchannel.LastTokenEndpoint);
            Assert.Equal(attempt.Query["code_challenge"].ToString(), WebEncoders.Base64UrlEncode(
                SHA256.HashData(Encoding.ASCII.GetBytes(fixture.Backchannel.LastCodeVerifier!))));
        }
    }

    [Fact]
    public async Task PolicyNarrowingDuringBrowserSignInRejectsTheNowDisallowedAccount()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Policy.Audience = CellBridgeEntraAccountAudience.OrganizationsAndPersonal;
        var attempt = await fixture.BeginBrowserAsync(PersonalTenant);
        fixture.Policy.Audience = CellBridgeEntraAccountAudience.SingleTenant;
        using var response = await fixture.CompleteBrowserAsync(attempt);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(HasSessionCookie(response));
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("correlation")]
    [InlineData("state")]
    public async Task BrowserProtocolChecksStillRejectTamperedTransactions(string problem)
    {
        await using var fixture = await Fixture.StartAsync();
        var attempt = await fixture.BeginBrowserAsync(HomeTenant, problem == "nonce" ? "wrong-nonce" : "");
        if (problem == "correlation") fixture.Cookies.RemoveWhere(name => name.Contains("Correlation", StringComparison.Ordinal));
        using var response = await fixture.CompleteBrowserAsync(attempt, problem == "state" ? "invalid-state" : null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(HasSessionCookie(response));
    }

    [Fact]
    public async Task DiscoveryOutageBlocksNewChallengesAndInFlightCallbacksThenRecovers()
    {
        await using var fixture = await Fixture.StartAsync();
        var attempt = await fixture.BeginBrowserAsync(HomeTenant);
        fixture.Policy.Unavailable = true;
        using var failedChallenge = await fixture.Client.GetAsync("/login");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedChallenge.StatusCode);
        Assert.Null(failedChallenge.Headers.Location);
        using var failedCallback = await fixture.CompleteBrowserAsync(attempt);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedCallback.StatusCode);
        Assert.False(HasSessionCookie(failedCallback));
        Assert.Equal(0, fixture.Backchannel.TokenRequests);
        using var nativeRequest = new HttpRequestMessage(HttpMethod.Post, EntraAuthentication.NativeCookiePath);
        nativeRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer");
        using var nativeDiscovery = await fixture.Client.SendAsync(nativeRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, nativeDiscovery.StatusCode);
        Assert.Empty(nativeDiscovery.Headers.WwwAuthenticate);
        fixture.Policy.Unavailable = false;
        var recovered = await fixture.BeginBrowserAsync(HomeTenant);
        using var callback = await fixture.CompleteBrowserAsync(recovered);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.True(HasSessionCookie(callback));
    }

    [Theory]
    [InlineData("wrong-audience")]
    [InlineData("expired")]
    [InlineData("issuer-tenant")]
    [InlineData("duplicate-tid")]
    [InlineData("duplicate-oid")]
    [InlineData("duplicate-ver")]
    [InlineData("missing-ver")]
    [InlineData("wrong-ver")]
    [InlineData("wrong-scope")]
    [InlineData("wrong-client")]
    public async Task BroadAudienceStillRejectsInvalidNativeTokensBeforeMapping(string problem)
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Policy.Audience = CellBridgeEntraAccountAudience.OrganizationsAndPersonal;
        using var response = await fixture.ExchangeAsync(fixture.Token(OtherTenant, problem));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(HasSessionCookie(response));
        Assert.Equal(0, fixture.Mapping.Calls);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task SigningKeysRestrictedToOneIssuerCannotValidateAnotherTenant(
        bool personalKey, bool personalToken, bool accepted)
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Policy.Audience = CellBridgeEntraAccountAudience.OrganizationsAndPersonal;
        using var response = await fixture.ExchangeAsync(fixture.Token(personalToken ? PersonalTenant : HomeTenant,
            keyId: personalKey ? "personal-key" : "home-key"));
        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(accepted, HasSessionCookie(response));
    }

    private static bool HasSessionCookie(HttpResponseMessage response) => response.Headers.TryGetValues("Set-Cookie", out var values) &&
        values.Any(value => value.StartsWith(EntraAuthentication.SessionCookieName + "=", StringComparison.Ordinal));

    private sealed class MutablePolicy : ICellBridgeEntraAudienceProvider
    {
        public CellBridgeEntraAccountAudience Audience { get; set; } = CellBridgeEntraAccountAudience.SingleTenant;
        public bool Unavailable { get; set; }
        public ValueTask<CellBridgeEntraAudienceSnapshot> GetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Unavailable
                ? ValueTask.FromException<CellBridgeEntraAudienceSnapshot>(new EntraAudienceUnavailableException("test-unavailable"))
                : ValueTask.FromResult(new CellBridgeEntraAudienceSnapshot(Audience, HomeTenant, DateTimeOffset.UtcNow));
        }
    }

    private sealed class MappingState { public int Calls { get; set; } }

    private sealed class HostMapper(MappingState state) : ICellBridgeExternalIdentityMapper
    {
        public ValueTask<ClaimsPrincipal?> MapAsync(ValidatedExternalIdentity identity, CancellationToken cancellationToken)
        {
            state.Calls++;
            return ValueTask.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(CellBridgeActor.SubjectClaim, $"account:{identity.TenantId:D}:{identity.ObjectId:D}"),
                new Claim(ClaimTypes.Name, "Linked account"),
            }, "HostAccounts")));
        }
    }

    private sealed record BrowserAttempt(string Code, Guid Tenant, string Problem,
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> Query, string AuthorizationEndpoint);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly RSA _key;
        public MutablePolicy Policy { get; }
        public MappingState Mapping { get; }
        public CookieHandler Cookies { get; }
        public HttpClient Client { get; }
        public TokenBackchannel Backchannel { get; }

        private Fixture(WebApplication app, RSA key, MutablePolicy policy, MappingState mapping, TokenBackchannel backchannel)
        {
            _app = app;
            _key = key;
            Policy = policy;
            Mapping = mapping;
            Backchannel = backchannel;
            Cookies = new CookieHandler(app.GetTestServer().CreateHandler());
            Client = new HttpClient(Cookies) { BaseAddress = new Uri(Origin) };
        }

        public static async Task<Fixture> StartAsync()
        {
            var key = RSA.Create(2048);
            var metadata = new OpenIdConnectConfiguration
            {
                Issuer = IssuerTemplate,
                AuthorizationEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
                TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token",
                JsonWebKeySet = new JsonWebKeySet(),
            };
            foreach (var (id, issuer) in new[]
            {
                ("shared-key", IssuerTemplate),
                ("home-key", Issuer(HomeTenant)),
                ("personal-key", Issuer(PersonalTenant)),
            })
            {
                var publicKey = key.ExportParameters(false);
                var jwk = new JsonWebKey
                {
                    Kty = "RSA",
                    Kid = id,
                    N = WebEncoders.Base64UrlEncode(publicKey.Modulus!),
                    E = WebEncoders.Base64UrlEncode(publicKey.Exponent!),
                };
                jwk.AdditionalData["issuer"] = issuer;
                metadata.SigningKeys.Add(jwk);
                metadata.JsonWebKeySet.Keys.Add(jwk);
            }
            var policy = new MutablePolicy();
            var mapping = new MappingState();
            var backchannel = new TokenBackchannel();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Entra:Enabled"] = "true",
                ["Authentication:Entra:TenantId"] = HomeTenant.ToString("D"),
                ["Authentication:Entra:ClientId"] = ClientId.ToString("D"),
                ["Authentication:Entra:ClientSecret"] = "test-only-secret",
                ["Authentication:Entra:DiscoverAccountTypes"] = "true",
            }).Build();
            builder.Services.AddSingleton<ICellBridgeEntraAudienceProvider>(policy);
            builder.Services.AddSingleton(mapping);
            builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
                .AddCookie(IdentityConstants.ApplicationScheme, options =>
                {
                    options.Cookie.Name = EntraAuthentication.SessionCookieName;
                    options.Cookie.Path = "/";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                });
            builder.Services.AddCellBridge(TestStorage.Memory(), requireDurability: false,
                configure: options => { options.PublicOrigin = Origin; options.EnableOfficeRealTime = false; },
                configureHosting: hosting => hosting.Authentication(auth =>
                {
                    auth.UseMapper<HostMapper>(IdentityConstants.ApplicationScheme);
                    auth.UseEntra(options =>
                    {
                        options.TenantId = HomeTenant.ToString("D");
                        options.ClientId = ClientId.ToString("D");
                        options.DiscoverAccountTypes = true;
                        options.ClientSecret = "test-only-secret";
                    });
                }));
            BrowserEntraAuthentication.AddBrowserEntraAuthentication(builder.Services, configuration);
            builder.Services.PostConfigure<JwtBearerOptions>(EntraAuthentication.BearerScheme, options =>
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata));
            builder.Services.PostConfigure<OpenIdConnectOptions>(BrowserEntraAuthentication.OidcScheme, options =>
            {
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
                options.Backchannel = new HttpClient(backchannel);
            });
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapCellBridge();
            app.MapGet("/login", () => Results.Challenge(new AuthenticationProperties { RedirectUri = "/complete" },
                new[] { BrowserEntraAuthentication.OidcScheme }));
            app.MapGet("/complete", () => Results.Ok());
            await app.StartAsync();
            var result = new Fixture(app, key, policy, mapping, backchannel);
            backchannel.IssueToken = attempt => result.Token(attempt.Tenant, attempt.Problem,
                nonce: attempt.Problem == "wrong-nonce" ? "another-nonce" : attempt.Query["nonce"].ToString());
            return result;
        }

        public string Token(Guid tenant, string problem = "", string keyId = "shared-key", string? nonce = null)
        {
            var now = DateTimeOffset.UtcNow;
            var claims = new List<Claim>
            {
                new("tid", tenant.ToString("D")),
                new("oid", ObjectId.ToString("D")),
                new("sub", "subject-for-" + tenant.ToString("D")),
                new("iat", now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
                new("scp", problem == "wrong-scope" ? "profile" : EntraAuthentication.AccessScope),
                new("azp", problem == "wrong-client" ? Guid.NewGuid().ToString("D")
                    : nonce is not null ? ClientId.ToString("D") : EntraAuthentication.NativeOfficeClientId),
            };
            if (problem != "missing-ver") claims.Add(new("ver", problem == "wrong-ver" ? "1.0" : "2.0"));
            if (problem.StartsWith("duplicate-", StringComparison.Ordinal))
            {
                var type = problem["duplicate-".Length..];
                claims.Add(new(type, claims.First(claim => claim.Type == type).Value));
            }
            if (nonce is not null) claims.Add(new("nonce", nonce));
            var token = new JwtSecurityToken(problem == "issuer-tenant" ? Issuer(HomeTenant) : Issuer(tenant),
                problem == "wrong-audience" ? "another-app" : ClientId.ToString("D"), claims,
                now.AddMinutes(-20).UtcDateTime,
                problem == "expired" ? now.AddMinutes(-10).UtcDateTime : now.AddMinutes(5).UtcDateTime,
                new SigningCredentials(new RsaSecurityKey(_key) { KeyId = keyId }, SecurityAlgorithms.RsaSha256));
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public async Task<HttpResponseMessage> ExchangeAsync(string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, EntraAuthentication.NativeCookiePath);
            request.Headers.Authorization = new("Bearer", token);
            return await Client.SendAsync(request);
        }

        public async Task<BrowserAttempt> BeginBrowserAsync(Guid tenant, string problem = "")
        {
            using var response = await Client.GetAsync("/login");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var location = Assert.IsType<Uri>(response.Headers.Location);
            var query = QueryHelpers.ParseQuery(location.Query);
            var code = Guid.NewGuid().ToString("N");
            var attempt = new BrowserAttempt(code, tenant, problem, query, location.GetLeftPart(UriPartial.Path));
            Backchannel.Attempts.Add(code, attempt);
            return attempt;
        }

        public async Task<HttpResponseMessage> CompleteBrowserAsync(BrowserAttempt attempt, string? state = null) =>
            await Client.GetAsync(QueryHelpers.AddQueryString("/signin-oidc", new Dictionary<string, string?>
            {
                ["code"] = attempt.Code,
                ["state"] = state ?? attempt.Query["state"].ToString(),
            }));

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            _key.Dispose();
        }
    }

    private static string Issuer(Guid tenant) => $"https://login.microsoftonline.com/{tenant:D}/v2.0";

    private sealed class TokenBackchannel : HttpMessageHandler
    {
        public Dictionary<string, BrowserAttempt> Attempts { get; } = new();
        public Func<BrowserAttempt, string> IssueToken { get; set; } = null!;
        public string? LastTokenEndpoint { get; private set; }
        public string? LastCodeVerifier { get; private set; }
        public int TokenRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("login.microsoftonline.com", request.RequestUri!.Host);
            Assert.EndsWith("/oauth2/v2.0/token", request.RequestUri.AbsolutePath);
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("authorization_code", form["grant_type"].ToString());
            Assert.Equal(ClientId.ToString("D"), form["client_id"].ToString());
            Assert.Equal(Origin + "/signin-oidc", form["redirect_uri"].ToString());
            LastCodeVerifier = form["code_verifier"].ToString();
            Assert.False(string.IsNullOrEmpty(LastCodeVerifier));
            LastTokenEndpoint = request.RequestUri.AbsoluteUri;
            TokenRequests++;
            var attempt = Attempts[form["code"].ToString()];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    id_token = IssueToken(attempt),
                    access_token = "opaque-test-access-token",
                    token_type = "Bearer",
                    expires_in = 300,
                }),
            };
        }
    }

    private sealed class CookieHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);
        public void RemoveWhere(Func<string, bool> predicate)
        {
            foreach (var key in _cookies.Keys.Where(predicate).ToArray()) _cookies.Remove(key);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_cookies.Count > 0) request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", _cookies.Select(pair => pair.Key + "=" + pair.Value)));
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
            {
                foreach (var value in values)
                {
                    var parts = value.Split(';', 2)[0].Split('=', 2);
                    if (parts[1].Length == 0) _cookies.Remove(parts[0]);
                    else _cookies[parts[0]] = parts[1];
                }
            }
            return response;
        }
    }
}
