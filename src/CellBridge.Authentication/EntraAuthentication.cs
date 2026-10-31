using System.Security.Claims;
using CellBridge.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using JwtTokenValidatedContext = Microsoft.AspNetCore.Authentication.JwtBearer.TokenValidatedContext;

namespace CellBridge.Authentication;

/// <summary>Optional Entra authentication for native Office bearer tokens and the WebDemo browser.</summary>
public static class EntraAuthentication
{
    public const string OfficeScheme = "CellBridge.Office";
    public const string BearerScheme = "CellBridge.Entra.Bearer";
    public const string OidcScheme = "CellBridge.Entra.Oidc";
    public const string LoginProvider = "CellBridge.Entra";
    public const string AccessScope = "access_as_user";
    public const string NativeOfficeClientId = "d3590ed6-52b3-4102-aeff-aad2292ab01c";
    public const string NativeCookiePath = "/_api/SP.OAuth.NativeClient/Authenticate";
    public const string SessionCookieName = "SPOIDCRL";
    private const string NativeCookieMarker = "CellBridge.Entra.NativeCookie";

    /// <summary>Returns the stable Identity login key for an Entra tenant and object ID.</summary>
    public static string ProviderKey(Guid tenantId, Guid objectId) =>
        $"{tenantId:D}:{objectId:D}";

    internal static void AddCellBridgeEntraAuthentication(IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection("Authentication:Entra").Get<EntraAuthenticationOptions>() ?? new();
        if (!settings.Enabled) return;
        if (!Guid.TryParse(settings.TenantId, out var tenantId) || tenantId == Guid.Empty)
            throw new InvalidOperationException("Authentication:Entra:TenantId must be a tenant GUID when Entra authentication is enabled.");
        if (!Guid.TryParse(settings.ClientId, out var clientId) || clientId == Guid.Empty)
            throw new InvalidOperationException("Authentication:Entra:ClientId must be an application GUID when Entra authentication is enabled.");
        if (string.IsNullOrWhiteSpace(settings.ClientSecret))
            throw new InvalidOperationException("Authentication:Entra:ClientSecret must be configured for browser sign-in.");

        var tenant = tenantId.ToString("D");
        var client = clientId.ToString("D");
        var authority = $"https://login.microsoftonline.com/{tenant}/v2.0";
        services.AddSingleton(settings);
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, OfficeAuthenticationHandler>(OfficeScheme, _ => { })
            .AddJwtBearer(BearerScheme, options =>
            {
                options.Authority = authority;
                options.Audience = client;
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = authority,
                    ValidateAudience = true,
                    ValidAudience = client,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        OfficeAuthenticationDiagnostics.ObserveResponse(context.HttpContext);
                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = context =>
                    {
                        DiagnosticLogger(context.HttpContext).LogDebug("Office bearer rejected: {FailureType}",
                            context.Exception.GetType().Name);
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = MapAsync,
                    OnChallenge = context =>
                    {
                        DiagnosticLogger(context.HttpContext).LogDebug(
                            "Office challenge {Method} {Path}; authorization {HeaderState}; validation {FailureType}",
                            context.Request.Method, context.Request.Path.Value,
                            OfficeAuthenticationHandler.CredentialCategory(context.Request),
                            context.AuthenticateFailure?.GetType().Name ?? "none");
                        context.HandleResponse();
                        WriteChallenge(context.HttpContext, settings, StatusCodes.Status401Unauthorized);
                        return Task.CompletedTask;
                    },
                };
            })
            .AddOpenIdConnect(OidcScheme, options =>
            {
                options.Authority = authority;
                options.ClientId = client;
                options.ClientSecret = settings.ClientSecret;
                options.SignInScheme = IdentityConstants.ApplicationScheme;
                options.CallbackPath = "/signin-oidc";
                options.ResponseType = "code";
                options.UsePkce = true;
                options.MapInboundClaims = false;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = authority,
                    ValidateAudience = true,
                    ValidAudience = client,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
                options.Events = new OpenIdConnectEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var principal = await ResolveLocalPrincipalAsync(context.HttpContext.RequestServices,
                            context.Principal, tenantId, requireOfficeScope: false, requireOfficeClient: false);
                        if (principal is null)
                        {
                            context.Fail("Sign-in failed.");
                            return;
                        }
                        context.Principal = principal;
                    },
                    OnRemoteFailure = context =>
                    {
                        context.HandleResponse();
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.Headers.CacheControl = "no-store";
                        context.Response.ContentType = "text/plain; charset=utf-8";
                        return context.Response.WriteAsync("Microsoft sign-in failed. Return to the CellBridge login page and try again.");
                    },
                };
            });

        // Identity's security-stamp validator can request renewal independently of
        // sliding expiration and AllowRefresh. Keep its validation, but preserve
        // the absolute lifetime of a token-derived session. An incoming browser
        // cookie must also not renew on a failed native exchange.
        services.Configure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, cookie =>
        {
            // Match the cookie returned by the native-client exchange in the
            // OneDrive reference implementation. The ticket remains protected
            // by this host's ordinary Identity cookie scheme and key ring.
            cookie.Cookie.Name = SessionCookieName;
            var validate = cookie.Events.OnValidatePrincipal;
            var sliding = cookie.Events.OnCheckSlidingExpiration;
            cookie.Events.OnValidatePrincipal = async context =>
            {
                await validate(context);
                if (SuppressCookieRenewal(context.HttpContext, context.Properties)) context.ShouldRenew = false;
            };
            cookie.Events.OnCheckSlidingExpiration = async context =>
            {
                await sliding(context);
                if (SuppressCookieRenewal(context.HttpContext, context.Properties)) context.ShouldRenew = false;
            };
        });
    }

    private static bool SuppressCookieRenewal(HttpContext context, AuthenticationProperties properties) =>
        properties.Items.ContainsKey(NativeCookieMarker) ||
        context.GetEndpoint()?.Metadata.GetMetadata<NativeCookieExchangeMetadata>() is not null;

    private sealed class NativeCookieExchangeMetadata;

    internal static void MapNativeCookieExchange(IEndpointRouteBuilder app)
    {
        if (app.ServiceProvider.GetService<EntraAuthenticationOptions>() is null) return;
        app.MapPost(NativeCookiePath, async (HttpContext context, EntraAuthenticationOptions settings,
            IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> cookies) =>
        {
            OfficeAuthenticationDiagnostics.ObserveResponse(context);
            context.Response.Headers.CacheControl = "no-store";
            var origin = context.RequestServices.GetRequiredService<AuthenticationOrigin>().Value;
            if (context.Request.Headers.TryGetValue("Origin", out var requestOrigin) &&
                (requestOrigin.Count != 1 || !string.Equals(requestOrigin[0], origin, StringComparison.OrdinalIgnoreCase)))
                return Results.BadRequest();

            // MS-OAUTH2EX 4.3/4.4 describes an empty-Bearer discovery POST returning
            // a challenge on 200. This response never establishes a session.
            if (OfficeAuthenticationHandler.CredentialCategory(context.Request) == "empty-bearer")
            {
                WriteChallenge(context, settings, StatusCodes.Status200OK);
                OfficeAuthenticationDiagnostics.Authentication(context, BearerScheme, AuthenticateResult.NoResult());
                return Results.Empty;
            }

            // Authenticate this credential explicitly. A browser cookie cannot authorize
            // a new native session or rescue an invalid nonempty bearer token.
            var result = await context.AuthenticateAsync(BearerScheme);
            OfficeAuthenticationDiagnostics.Authentication(context, BearerScheme, result);
            var now = DateTimeOffset.UtcNow;
            if (!result.Succeeded || result.Principal is null || result.Properties?.ExpiresUtc is not { } tokenExpiry || tokenExpiry <= now)
            {
                await context.ChallengeAsync(BearerScheme);
                return Results.Empty;
            }

            var cookieLifetime = cookies.Get(IdentityConstants.ApplicationScheme).ExpireTimeSpan;
            var expires = tokenExpiry < now.Add(cookieLifetime) ? tokenExpiry : now.Add(cookieLifetime);
            var properties = new AuthenticationProperties
            {
                IssuedUtc = now,
                ExpiresUtc = expires,
                AllowRefresh = false,
                IsPersistent = true,
            };
            properties.Items[NativeCookieMarker] = "true";
            await context.SignInAsync(IdentityConstants.ApplicationScheme, result.Principal, properties);
            return Results.Ok();
        }).AllowAnonymous().WithMetadata(new NativeCookieExchangeMetadata()).WithRequestTimeout(TimeSpan.FromSeconds(30));
    }

    private static void WriteChallenge(HttpContext context, EntraAuthenticationOptions settings, int status)
    {
        var tenant = Guid.Parse(settings.TenantId!).ToString("D");
        var client = Guid.Parse(settings.ClientId!).ToString("D");
        var origin = context.RequestServices.GetRequiredService<AuthenticationOrigin>().Value;
        var cookieUri = origin + context.Request.PathBase.ToUriComponent() + NativeCookiePath;
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.WWWAuthenticate =
            $"Bearer realm=\"{tenant}\",authorization_uri=\"https://login.microsoftonline.com/{tenant}/oauth2/authorize\",client_id=\"{client}\",cookie_uri=\"{cookieUri}\"";
    }

    internal static async Task<ClaimsPrincipal?> ResolveLocalPrincipalAsync(IServiceProvider services,
        ClaimsPrincipal? entraPrincipal, Guid expectedTenant, bool requireOfficeScope, bool requireOfficeClient)
    {
        if (entraPrincipal?.Identity?.IsAuthenticated != true) return null;
        if (!Guid.TryParse(entraPrincipal.FindFirstValue("tid"), out var tenantId) || tenantId != expectedTenant ||
            !Guid.TryParse(entraPrincipal.FindFirstValue("oid"), out var objectId)) return null;
        if (requireOfficeScope && !entraPrincipal.FindAll("scp").SelectMany(x => x.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Contains(AccessScope, StringComparer.Ordinal)) return null;
        if (requireOfficeClient && !string.Equals(entraPrincipal.FindFirstValue("azp"), NativeOfficeClientId, StringComparison.OrdinalIgnoreCase))
            return null;

        var users = services.GetRequiredService<UserManager<CellBridgeUser>>();
        var user = await users.FindByLoginAsync(LoginProvider, ProviderKey(tenantId, objectId));
        if (user is null || !user.Enabled || user.TwoFactorEnabled || await users.IsLockedOutAsync(user)) return null;
        return await services.GetRequiredService<SignInManager<CellBridgeUser>>().CreateUserPrincipalAsync(user);
    }

    private static ILogger DiagnosticLogger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CellBridge.Authentication.Entra");

    private static async Task MapAsync(JwtTokenValidatedContext context)
    {
        var settings = context.HttpContext.RequestServices.GetRequiredService<EntraAuthenticationOptions>();
        if (!Guid.TryParse(settings.TenantId, out var tenantId))
        {
            context.Fail("Sign-in failed.");
            return;
        }
        var principal = await ResolveLocalPrincipalAsync(context.HttpContext.RequestServices, context.Principal,
            tenantId, requireOfficeScope: true, requireOfficeClient: true);
        if (principal is null)
        {
            var tokenClient = Guid.TryParse(context.Principal?.FindFirstValue("azp"), out var parsedClient)
                ? parsedClient.ToString("D") : "missing";
            DiagnosticLogger(context.HttpContext).LogDebug(
                "Validated Office token could not map to a permitted local account; client {Client}; delegated scope {HasScope}",
                tokenClient, context.Principal?.FindAll("scp").Any(c => c.Value.Split(' ').Contains(AccessScope)) == true);
            context.Fail("Sign-in failed.");
            return;
        }
        context.Principal = principal;
        context.Properties.ExpiresUtc = new DateTimeOffset(context.SecurityToken.ValidTo.ToUniversalTime());
    }
}

internal sealed class EntraAuthenticationOptions
{
    public bool Enabled { get; set; }
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}
