using CellBridge.AspNetCore;
using CellBridge.Mcp.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace CellBridge.WebDemo;

public static class WebDemoMcp
{
    private const string McpPath = "/mcp";
    private const string BearerScheme = "CellBridge.WebDemo.Mcp.Entra";
    private const string ScopePolicy = "CellBridge.WebDemo.Mcp.Scope";
    private static readonly object VerifiedScopeKey = new();

    public static string? AddWebDemoMcp(this IServiceCollection services, IConfiguration configuration)
    {
        var entra = configuration.GetSection("Authentication:Entra");
        if (!bool.TryParse(entra["Enabled"], out var entraEnabled) || !entraEnabled)
            return "MCP requires Authentication:Entra:Enabled to be true.";
        if (!Guid.TryParse(entra["TenantId"], out var tenantId) || tenantId == Guid.Empty)
            return "Authentication:Entra:TenantId must be a nonempty tenant GUID.";

        var mcp = configuration.GetSection("Mcp");
        var clientIdOverride = mcp["ClientId"];
        if (!Guid.TryParse(clientIdOverride ?? entra["ClientId"], out var clientId) || clientId == Guid.Empty)
            return clientIdOverride is null
                ? "Authentication:Entra:ClientId must be a nonempty application GUID."
                : "Mcp:ClientId must be a nonempty application GUID.";

        var tenantAuthority = $"https://login.microsoftonline.com/{tenantId:D}";
        var authorizationServer = mcp["AuthorizationServer"] ?? tenantAuthority + "/oauth21/2026-08";
        if (!Uri.TryCreate(authorizationServer, UriKind.Absolute, out var issuer) ||
            issuer.Scheme != Uri.UriSchemeHttps || issuer.Host.Length == 0 || issuer.UserInfo.Length != 0 ||
            issuer.Query.Length != 0 || issuer.Fragment.Length != 0)
            return "Mcp:AuthorizationServer must be an HTTPS issuer URI without credentials, a query or a fragment.";
        var requiredScope = mcp["RequiredScope"] ?? "mcp.access";
        if (string.IsNullOrEmpty(requiredScope) || requiredScope.Any(c => !IsScopeCharacter(c) || c is ':' or '/'))
            return "Mcp:RequiredScope must be a short delegated scope name.";
        var scopeSection = mcp.GetSection("ScopesSupported");
        // The Office forms registration supplies the origin used by protected-resource discovery.
        var publicOrigin = configuration["Authentication:PublicOrigin"] ?? configuration["Protocol:PublicOrigin"];
        var scopeResource = Uri.TryCreate(publicOrigin, UriKind.Absolute, out var origin)
            ? origin.GetLeftPart(UriPartial.Authority) + McpPath
            : $"api://{clientId:D}";
        var scopesSupported = scopeSection.Exists()
            ? scopeSection.Get<string[]>()
            : [$"{scopeResource}/{requiredScope}"];
        if (scopesSupported is not { Length: > 0 } || scopesSupported.Any(scope =>
            !Uri.TryCreate(scope, UriKind.Absolute, out var uri) || scope.Any(c => !IsScopeCharacter(c)) ||
            uri.Host.Length == 0 || uri.AbsolutePath == "/" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0))
            return "Mcp:ScopesSupported must contain fully qualified OAuth scopes.";

        var authority = tenantAuthority + "/v2.0";
        var audience = clientId.ToString("D");
        services.AddAuthentication().AddJwtBearer(BearerScheme, options =>
        {
            options.Authority = authority;
            options.Audience = audience;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidAudience = audience,
                ValidateAudience = true,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
            };
            CellBridgeEntraTokenValidation.Configure(options.TokenValidationParameters, authority);
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context => MapAsync(context, tenantId, requiredScope),
            };
        });
        services.AddAuthorization(options => options.AddPolicy(ScopePolicy, policy =>
            policy.AddAuthenticationSchemes(BearerScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.Resource is HttpContext http &&
                    http.Items.TryGetValue(VerifiedScopeKey, out var hasScope) && hasScope is true)));
        services.AddCellBridgeMcp(options =>
        {
            options.BearerScheme = BearerScheme;
            options.AuthorizationPolicy = ScopePolicy;
            options.AuthorizationServer = issuer;
            options.ScopesSupported = scopesSupported;
            options.MapRootProtectedResourceMetadata = true;
        });
        return null;
    }

    public static void MapWebDemoMcp(this WebApplication app, string? configurationError)
    {
        if (configurationError is null)
        {
            app.MapCellBridgeMcp(McpPath);
            return;
        }

        app.Map(McpPath, () => Results.Problem(
            title: "MCP is not configured.",
            detail: configurationError,
            statusCode: StatusCodes.Status503ServiceUnavailable)).AllowAnonymous();
    }

    private static bool IsScopeCharacter(char c) => c is '!' or (>= '#' and <= '[') or (>= ']' and <= '~');

    private static async Task MapAsync(TokenValidatedContext context, Guid homeTenant, string requiredScope)
    {
        var external = context.Principal;
        if (!CellBridgeEntraTokenValidation.TryIdentity(external, out var tenantId, out var objectId) || tenantId != homeTenant)
        {
            context.Fail("Sign-in failed.");
            return;
        }

        var scopes = external!.Claims.Where(claim => claim.Type == "scp").ToArray();
        var hasScope = scopes.Length == 1 && scopes[0].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(requiredScope, StringComparer.Ordinal);
        var mapper = context.HttpContext.RequestServices.GetRequiredService<ICellBridgeExternalIdentityMapper>();
        var principal = await mapper.MapAsync(new ValidatedExternalIdentity(EntraAuthentication.LoginProvider,
            EntraAuthentication.ProviderKey(tenantId, objectId), tenantId, objectId), context.HttpContext.RequestAborted);
        if (principal is null || CellBridgeActor.FromPrincipal(principal) is null)
        {
            context.Fail("Sign-in failed.");
            return;
        }

        // The mapped principal supplies the local actor. Only the validated token supplies the delegated scope.
        context.HttpContext.Items[VerifiedScopeKey] = hasScope;
        context.Principal = principal;
        context.Properties.ExpiresUtc = new DateTimeOffset(context.SecurityToken.ValidTo.ToUniversalTime());
    }
}
