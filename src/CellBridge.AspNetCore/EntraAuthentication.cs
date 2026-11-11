using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using JwtTokenValidatedContext = Microsoft.AspNetCore.Authentication.JwtBearer.TokenValidatedContext;

namespace CellBridge.AspNetCore;

/// <summary>Native Office Entra authentication using a host-owned account mapper and cookie scheme.</summary>
public static class EntraAuthentication
{
    public const string OfficeScheme = "CellBridge.Office";
    public const string BearerScheme = "CellBridge.Entra.Bearer";
    public const string LoginProvider = "CellBridge.Entra";
    public const string AccessScope = "access_as_user";
    public const string NativeOfficeClientId = "d3590ed6-52b3-4102-aeff-aad2292ab01c";
    public const string NativeCookiePath = "/_api/SP.OAuth.NativeClient/Authenticate";
    public const string SessionCookieName = "SPOIDCRL";
    private const string NativeCookieMarker = "CellBridge.Entra.NativeCookie";
    private static readonly object AudienceUnavailableKey = new();

    /// <summary>Returns the stable Identity login key for an Entra tenant and object ID.</summary>
    public static string ProviderKey(Guid tenantId, Guid objectId) =>
        $"{tenantId:D}:{objectId:D}";

    internal static void Register(IServiceCollection services, CellBridgeEntraOptions options, CellBridgeOptions core)
    {
        if (services.Any(d => d.ServiceType == typeof(EntraRegistration)))
            throw new InvalidOperationException("Configure native Office Entra authentication once per host.");
        if (!Guid.TryParse(options.TenantId, out var tenantId) || tenantId == Guid.Empty)
            throw new ArgumentException("Entra TenantId must be a nonempty tenant GUID.");
        if (!Guid.TryParse(options.ClientId, out var clientId) || clientId == Guid.Empty)
            throw new ArgumentException("Entra ClientId must be a nonempty application GUID.");
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CookieScheme);
        if (core.PublicOrigin is null)
            throw new ArgumentException("Set CellBridge PublicOrigin to the HTTPS origin reachable by Office before enabling Entra.");
        CellBridgePublicOrigin.Validate(core.PublicOrigin);
        var settings = new EntraRegistration(tenantId, clientId, options.CookieScheme, core.PublicOrigin.TrimEnd('/'));
        var tenant = tenantId.ToString("D");
        var client = clientId.ToString("D");
        var authority = $"https://login.microsoftonline.com/{(options.DiscoverAccountTypes ? "common" : tenant)}/v2.0";
        services.AddSingleton(settings);
        CellBridgeEntraAudienceRegistration.Add(services, options, tenantId, clientId);
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
                CellBridgeEntraTokenValidation.Configure(options.TokenValidationParameters, authority);
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = async context =>
                    {
                        OfficeAuthenticationDiagnostics.ObserveResponse(context.HttpContext);
                        try
                        {
                            await context.HttpContext.RequestServices.GetRequiredService<ICellBridgeEntraAudienceProvider>()
                                .GetAsync(context.HttpContext.RequestAborted);
                        }
                        catch (EntraAudienceUnavailableException error)
                        {
                            context.HttpContext.Items[AudienceUnavailableKey] = error;
                            context.Fail("Entra account policy is temporarily unavailable.");
                        }
                    },
                    OnAuthenticationFailed = context =>
                    {
                        DiagnosticLogger(context.HttpContext).LogDebug("Office bearer rejected: {FailureType}",
                            context.Exception.GetType().Name);
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = MapAsync,
                    OnChallenge = async context =>
                    {
                        DiagnosticLogger(context.HttpContext).LogDebug(
                            "Office challenge {Method} {Path}; authorization {HeaderState}; validation {FailureType}",
                            context.Request.Method, context.Request.Path.Value,
                            OfficeAuthenticationHandler.CredentialCategory(context.Request),
                            context.AuthenticateFailure?.GetType().Name ?? "none");
                        context.HandleResponse();
                        await WriteChallengeAsync(context.HttpContext, settings, StatusCodes.Status401Unauthorized);
                    },
                };
            });

        // Identity's security-stamp validator can request renewal independently of
        // sliding expiration and AllowRefresh. Keep its validation, but preserve
        // the absolute lifetime of a token-derived session. An incoming browser
        // cookie must also not renew on a failed native exchange.
        services.PostConfigure<CookieAuthenticationOptions>(settings.CookieScheme, cookie =>
        {
            ValidateCookie(cookie);
            cookie.Events = new NativeCookieEvents(cookie.Events);
        });
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService, EntraValidator>();
    }

    private static void ValidateCookie(CookieAuthenticationOptions cookie)
    {
        if (cookie.EventsType is not null)
            throw new InvalidOperationException("Office Entra authentication requires CookieAuthenticationOptions.Events, not EventsType.");
        if (cookie.Cookie.Name != SessionCookieName || cookie.Cookie.Path != "/" ||
            cookie.Cookie.Domain is not null || !cookie.Cookie.HttpOnly || cookie.Cookie.SecurePolicy != CookieSecurePolicy.Always ||
            cookie.ExpireTimeSpan <= TimeSpan.Zero)
            throw new InvalidOperationException("Configure the selected Office cookie explicitly with Name=SPOIDCRL, Path=/, no Domain, HttpOnly=true, SecurePolicy=Always and a positive lifetime.");
    }

    private sealed class EntraValidator(IAuthenticationSchemeProvider schemes,
        IOptionsMonitor<CookieAuthenticationOptions> cookies, EntraRegistration registration,
        IServiceScopeFactory scopes) : Microsoft.Extensions.Hosting.IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var scheme = await schemes.GetSchemeAsync(registration.CookieScheme);
            if (scheme is null || !typeof(CookieAuthenticationHandler).IsAssignableFrom(scheme.HandlerType))
                throw new InvalidOperationException("Office Entra requires a registered host cookie scheme: " + registration.CookieScheme);
            _ = cookies.Get(registration.CookieScheme);
            using var scope = scopes.CreateScope();
            if (scope.ServiceProvider.GetService<ICellBridgeExternalIdentityMapper>() is null)
                throw new InvalidOperationException("Configure an external account mapper with UseIdentity or UseMapper.");
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NativeCookieEvents(CookieAuthenticationEvents original) : CookieAuthenticationEvents
    {
        public override async Task SigningIn(CookieSigningInContext context)
        {
            var native = context.Properties.Items.ContainsKey(NativeCookieMarker);
            var expiry = context.Properties.ExpiresUtc;
            var issued = context.Properties.IssuedUtc;
            await original.SigningIn(context);
            if (!native) return;
            if (expiry is null) throw new InvalidOperationException("A native Office session must have an absolute token expiry.");
            context.Properties.ExpiresUtc = context.Properties.ExpiresUtc is { } hostExpiry && hostExpiry < expiry ? hostExpiry : expiry;
            context.Properties.IssuedUtc = issued;
            context.Properties.AllowRefresh = false;
            context.Properties.Items[NativeCookieMarker] = "true";
            context.CookieOptions.SameSite = SameSiteMode.None;
            context.CookieOptions.Secure = true;
            context.CookieOptions.HttpOnly = true;
            context.CookieOptions.Path = "/";
            context.CookieOptions.Domain = null;
            context.CookieOptions.MaxAge = null;
            if (context.Properties.IsPersistent) context.CookieOptions.Expires = context.Properties.ExpiresUtc;
        }

        public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
        {
            var native = context.Properties.Items.ContainsKey(NativeCookieMarker);
            var suppress = SuppressCookieRenewal(context.HttpContext, context.Properties);
            var expiry = context.Properties.ExpiresUtc;
            var ticketPrincipal = context.Principal;
            var ticketSubject = ticketPrincipal?.FindFirstValue(CellBridgeActor.SubjectClaim);
            await original.ValidatePrincipal(context);
            if (suppress)
            {
                context.ShouldRenew = false;
                context.Properties.ExpiresUtc = context.Properties.ExpiresUtc is { } hostExpiry && hostExpiry < expiry ? hostExpiry : expiry;
                context.Properties.AllowRefresh = false;
            }
            if (!native || context.Principal is null) return;
            // Identity security-stamp validation can replace the principal with fresh host claims.
            // Restore the same account mapping on that principal without reviving a rejected ticket,
            // changing the document subject, or renewing the token-derived session.
            var principal = context.Principal;
            if (!ReferenceEquals(principal, ticketPrincipal) &&
                context.HttpContext.RequestServices.GetRequiredService<ICellBridgeExternalIdentityMapper>() is ICellBridgeExternalPrincipalRefresher refresher)
                principal = await refresher.RefreshAsync(principal, context.HttpContext.RequestAborted);
            if (!IsMappedPrincipal(principal) || !string.Equals(ticketSubject,
                principal?.FindFirstValue(CellBridgeActor.SubjectClaim), StringComparison.Ordinal))
                context.RejectPrincipal();
            else if (!ReferenceEquals(principal, context.Principal)) context.ReplacePrincipal(principal!);
        }

        public override async Task CheckSlidingExpiration(CookieSlidingExpirationContext context)
        {
            var suppress = SuppressCookieRenewal(context.HttpContext, context.Properties);
            await original.CheckSlidingExpiration(context);
            if (suppress) context.ShouldRenew = false;
        }

        public override Task SignedIn(CookieSignedInContext context) => original.SignedIn(context);
        public override Task SigningOut(CookieSigningOutContext context) => original.SigningOut(context);
        public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context) => original.RedirectToLogin(context);
        public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context) => original.RedirectToAccessDenied(context);
        public override Task RedirectToLogout(RedirectContext<CookieAuthenticationOptions> context) => original.RedirectToLogout(context);
        public override Task RedirectToReturnUrl(RedirectContext<CookieAuthenticationOptions> context) => original.RedirectToReturnUrl(context);
    }

    private static bool SuppressCookieRenewal(HttpContext context, AuthenticationProperties properties) =>
        properties.Items.ContainsKey(NativeCookieMarker) ||
        context.GetEndpoint()?.Metadata.GetMetadata<NativeCookieExchangeMetadata>() is not null ||
        context.GetEndpoint()?.Metadata.GetMetadata<CellBridgeOfficeFormsAuthentication.OfficeEndpointMetadata>() is not null &&
        OfficeAuthenticationHandler.CredentialCategory(context.Request) is not ("absent" or "empty-bearer");

    private sealed class NativeCookieExchangeMetadata;

    internal static EntraRegistration? MapNativeCookieExchange(IEndpointRouteBuilder app)
    {
        var registration = app.ServiceProvider.GetService<EntraRegistration>();
        if (registration is null) return null;
        if (app is RouteGroupBuilder)
            throw new InvalidOperationException("MapCellBridge with Entra must be mapped at the application root. Use PathBase for a mounted application.");
        var forms = app.ServiceProvider.GetService<CellBridgeOfficeFormsAuthentication.OfficeFormsRegistration>();
        if (forms is not null && (forms.CookieScheme != registration.CookieScheme ||
            forms.PublicOrigin is not null && !string.Equals(forms.PublicOrigin, registration.PublicOrigin, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Office forms and Entra must use the same cookie scheme and public origin.");
        app.MapPost(NativeCookiePath, async (HttpContext context, EntraRegistration settings,
            IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> cookies) =>
        {
            OfficeAuthenticationDiagnostics.ObserveResponse(context);
            context.Response.Headers.CacheControl = "no-store";
            var origin = settings.PublicOrigin;
            if (context.Request.Headers.TryGetValue("Origin", out var requestOrigin) &&
                (requestOrigin.Count != 1 || !string.Equals(requestOrigin[0], origin, StringComparison.OrdinalIgnoreCase)))
                return Results.BadRequest();

            // MS-OAUTH2EX 4.3/4.4 describes an empty-Bearer discovery POST returning
            // a challenge on 200. This response never establishes a session.
            if (OfficeAuthenticationHandler.CredentialCategory(context.Request) == "empty-bearer")
            {
                await WriteChallengeAsync(context, settings, StatusCodes.Status200OK);
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

            var cookieLifetime = cookies.Get(settings.CookieScheme).ExpireTimeSpan;
            var expires = tokenExpiry < now.Add(cookieLifetime) ? tokenExpiry : now.Add(cookieLifetime);
            var properties = new AuthenticationProperties
            {
                IssuedUtc = now,
                ExpiresUtc = expires,
                AllowRefresh = false,
                IsPersistent = true,
            };
            properties.Items[NativeCookieMarker] = "true";
            await context.SignInAsync(settings.CookieScheme, result.Principal, properties);
            return Results.Ok();
        }).AllowAnonymous().WithMetadata(new NativeCookieExchangeMetadata()).WithRequestTimeout(TimeSpan.FromSeconds(30));
        return registration;
    }

    internal static async Task WriteChallengeAsync(HttpContext context, EntraRegistration settings, int status)
    {
        if (context.Items.TryGetValue(AudienceUnavailableKey, out var failure) && failure is EntraAudienceUnavailableException prior)
        {
            WriteUnavailable(context, prior);
            return;
        }
        CellBridgeEntraAudienceSnapshot snapshot;
        try
        {
            snapshot = await context.RequestServices.GetRequiredService<ICellBridgeEntraAudienceProvider>()
                .GetAsync(context.RequestAborted);
        }
        catch (EntraAudienceUnavailableException error)
        {
            WriteUnavailable(context, error);
            return;
        }
        var tenant = settings.TenantId.ToString("D");
        var client = settings.ClientId.ToString("D");
        var cookieUri = settings.PublicOrigin + context.Request.PathBase.ToUriComponent() + NativeCookiePath;
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        // Office uses a resource-based authorization request. The API registration
        // selects the access-token version independently of this endpoint version.
        context.Response.Headers.WWWAuthenticate =
            $"Bearer realm=\"{tenant}\",authorization_uri=\"https://login.microsoftonline.com/{snapshot.AuthorityTenant}/oauth2/authorize\",client_id=\"{client}\",cookie_uri=\"{cookieUri}\"";
    }

    private static void WriteUnavailable(HttpContext context, EntraAudienceUnavailableException error)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Remove("WWW-Authenticate");
        if (error.RetryAfter is { } retry)
            context.Response.Headers.RetryAfter = Math.Clamp((int)Math.Ceiling(retry.TotalSeconds), 1, 300)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        DiagnosticLogger(context).LogWarning("Office Entra policy unavailable: {ErrorCode}", error.ErrorCode);
    }

    internal static bool IsMappedPrincipal(ClaimsPrincipal? principal)
    {
        if (principal is null) return false;
        var authenticated = principal.Identities.Where(i => i.IsAuthenticated).ToArray();
        if (authenticated.Length != 1) return false;
        var subjects = principal.FindAll(CellBridgeActor.SubjectClaim).ToArray();
        return subjects.Length == 1 && authenticated[0].HasClaim(c => c == subjects[0]) &&
            !string.IsNullOrWhiteSpace(subjects[0].Value) && !subjects[0].Value.Any(char.IsControl);
    }

    private static ILogger DiagnosticLogger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CellBridge.Authentication.Entra");

    private static async Task MapAsync(JwtTokenValidatedContext context)
    {
        var services = context.HttpContext.RequestServices;
        var external = context.Principal;
        // These checks follow cryptographic JWT validation and precede host account lookup.
        var clients = external?.FindAll("azp").ToArray() ?? [];
        var scopes = external?.FindAll("scp").ToArray() ?? [];
        if (!CellBridgeEntraTokenValidation.TryIdentity(external, out var tenantId, out var objectId) ||
            clients.Length != 1 || !string.Equals(clients[0].Value, NativeOfficeClientId, StringComparison.OrdinalIgnoreCase) ||
            scopes.Length != 1 || !scopes[0].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(AccessScope, StringComparer.Ordinal))
        {
            context.Fail("Sign-in failed.");
            return;
        }
        try
        {
            var snapshot = await services.GetRequiredService<ICellBridgeEntraAudienceProvider>()
                .GetAsync(context.HttpContext.RequestAborted);
            if (!snapshot.Allows(tenantId))
            {
                context.Fail("Sign-in failed.");
                return;
            }
        }
        catch (EntraAudienceUnavailableException error)
        {
            context.HttpContext.Items[AudienceUnavailableKey] = error;
            context.Fail("Entra account policy is temporarily unavailable.");
            return;
        }
        var principal = await services.GetRequiredService<ICellBridgeExternalIdentityMapper>().MapAsync(
            new ValidatedExternalIdentity(LoginProvider, ProviderKey(tenantId, objectId), tenantId, objectId),
            context.HttpContext.RequestAborted);
        if (!IsMappedPrincipal(principal))
        {
            var tokenClient = Guid.TryParse(context.Principal?.FindFirstValue("azp"), out var parsedClient)
                ? parsedClient.ToString("D") : "missing";
            DiagnosticLogger(context.HttpContext).LogDebug(
                "Validated Office token could not map to a permitted local account; request {RequestId}; tenant {TenantId}; object {ObjectId}; client {Client}; delegated scope {HasScope}",
                context.HttpContext.TraceIdentifier, tenantId, objectId, tokenClient,
                context.Principal?.FindAll("scp").Any(c => c.Value.Split(' ').Contains(AccessScope)) == true);
            context.Fail("Sign-in failed.");
            return;
        }
        DiagnosticLogger(context.HttpContext).LogDebug(
            "Office Entra identity mapped; request {RequestId}; tenant {TenantId}; object {ObjectId}; subject {Subject}",
            context.HttpContext.TraceIdentifier, tenantId, objectId, principal!.FindFirstValue(CellBridgeActor.SubjectClaim));
        context.Principal = principal;
        context.Properties.ExpiresUtc = new DateTimeOffset(context.SecurityToken.ValidTo.ToUniversalTime());
    }
}

/// <summary>Enables native Office Entra authentication. Browser OIDC and account linking remain host-owned.</summary>
public sealed class CellBridgeEntraOptions
{
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    /// <summary>Read Supported account types from the Azure registration instead of fixing access to TenantId.</summary>
    public bool DiscoverAccountTypes { get; set; }
    /// <summary>Credential used only to read the registration through Microsoft Graph. Native token validation does not use it.</summary>
    public string? ClientSecret { get; set; }
    /// <summary>Maximum time a successfully read Azure audience may be reused.</summary>
    public TimeSpan AudienceRefreshInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan DiscoveryTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Existing host cookie scheme. Configure its name as SPOIDCRL and keep its account validation enabled.</summary>
    public string CookieScheme { get; set; } = IdentityConstants.ApplicationScheme;
}

internal sealed record EntraRegistration(Guid TenantId, Guid ClientId, string CookieScheme, string PublicOrigin);
