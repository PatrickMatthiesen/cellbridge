using System.Security.Claims;
using CellBridge.AspNetCore;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace CellBridge.Authentication;

/// <summary>Browser OIDC configuration owned by the development host.</summary>
internal static class BrowserEntraAuthentication
{
    internal const string OidcScheme = "CellBridge.Entra.Oidc";
    internal const string AuthorityProperty = "CellBridge.Entra.AuthorityTenant";

    internal static void AddBrowserEntraAuthentication(IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection("Authentication:Entra").Get<BrowserEntraOptions>() ?? new();
        if (!settings.Enabled) return;
        if (!Guid.TryParse(settings.TenantId, out var tenantId) || tenantId == Guid.Empty ||
            !Guid.TryParse(settings.ClientId, out var clientId) || clientId == Guid.Empty)
            throw new InvalidOperationException("Configure Entra home tenant and application GUIDs for browser sign-in.");
        if (string.IsNullOrWhiteSpace(settings.ClientSecret))
            throw new InvalidOperationException("Authentication:Entra:ClientSecret must be configured for browser sign-in.");
        var client = clientId.ToString("D");
        // The metadata and validation options are immutable. Each browser transaction selects
        // its authorization and token endpoints using a protected authority identifier.
        var metadataTenant = settings.DiscoverAccountTypes ? "common" : tenantId.ToString("D");
        var authority = $"https://login.microsoftonline.com/{metadataTenant}/v2.0";
        services.AddAuthentication().AddOpenIdConnect(OidcScheme, options =>
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
            // PAR would require selecting and validating a third endpoint for each transaction.
            // Use the ordinary authorization-code flow with nonce, correlation and PKCE.
            options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidAudience = client,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromMinutes(1),
            };
            CellBridgeEntraTokenValidation.Configure(options.TokenValidationParameters, authority);
            options.Events = new OpenIdConnectEvents
            {
                OnRedirectToIdentityProvider = async context =>
                {
                    try
                    {
                        var snapshot = await Audience(context.HttpContext);
                        context.Properties.Items[AuthorityProperty] = snapshot.AuthorityTenant;
                        context.ProtocolMessage.IssuerAddress = Endpoint(snapshot.AuthorityTenant, "authorize");
                    }
                    catch (EntraAudienceUnavailableException)
                    {
                        context.HandleResponse();
                        await Unavailable(context.HttpContext);
                    }
                },
                OnAuthorizationCodeReceived = async context =>
                {
                    try
                    {
                        // Check availability before exchanging a code. Use the original authority
                        // for redemption even if Azure's policy changed while the browser was away.
                        _ = await Audience(context.HttpContext);
                        if (context.Properties is null || !context.Properties.Items.TryGetValue(AuthorityProperty, out var selected) ||
                            !ValidAuthority(selected, tenantId) || context.TokenEndpointRequest is null)
                        {
                            context.Fail("Invalid Microsoft sign-in state.");
                            return;
                        }
                        context.TokenEndpointRequest.TokenEndpoint = Endpoint(selected!, "token");
                    }
                    catch (EntraAudienceUnavailableException)
                    {
                        context.HandleResponse();
                        await Unavailable(context.HttpContext);
                    }
                },
                OnTokenValidated = async context =>
                {
                    try
                    {
                        var snapshot = await Audience(context.HttpContext);
                        if (!CellBridgeEntraTokenValidation.TryIdentity(context.Principal, out var tokenTenant, out var objectId) ||
                            !snapshot.Allows(tokenTenant))
                        {
                            context.Fail("Microsoft account is not permitted by the application registration.");
                            return;
                        }
                        var mapper = context.HttpContext.RequestServices.GetRequiredService<ICellBridgeExternalIdentityMapper>();
                        var principal = await mapper.MapAsync(new ValidatedExternalIdentity(
                            EntraAuthentication.LoginProvider, EntraAuthentication.ProviderKey(tokenTenant, objectId),
                            tokenTenant, objectId), context.HttpContext.RequestAborted);
                        if (principal is null || CellBridgeActor.FromPrincipal(principal) is null)
                        {
                            context.Fail("Sign-in failed.");
                            return;
                        }
                        context.Principal = principal;
                    }
                    catch (EntraAudienceUnavailableException)
                    {
                        context.HandleResponse();
                        await Unavailable(context.HttpContext);
                    }
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
    }

    private static ValueTask<CellBridgeEntraAudienceSnapshot> Audience(HttpContext context) =>
        context.RequestServices.GetRequiredService<ICellBridgeEntraAudienceProvider>().GetAsync(context.RequestAborted);

    private static bool ValidAuthority(string? value, Guid homeTenant) => value is "common" or "organizations" or "consumers" ||
        string.Equals(value, homeTenant.ToString("D"), StringComparison.Ordinal);

    private static string Endpoint(string tenant, string action) =>
        $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/{action}";

    private static Task Unavailable(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync("Microsoft sign-in is temporarily unavailable. Please try again shortly.");
    }
}

internal sealed class BrowserEntraOptions
{
    public bool Enabled { get; set; }
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public bool DiscoverAccountTypes { get; set; } = true;
}
