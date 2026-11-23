using CellBridge.AspNetCore;
using CellBridge.Mcp.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace CellBridge.WebDemo;

public static class WebDemoMcp
{
    private const string BearerScheme = "CellBridge.WebDemo.Mcp.Entra";
    private const string ScopePolicy = "CellBridge.WebDemo.Mcp.Scope";
    private static readonly object VerifiedScopeKey = new();

    public static bool AddWebDemoMcp(this IServiceCollection services, IConfiguration configuration)
    {
        var mcp = configuration.GetSection("Mcp");
        if (!mcp.GetValue<bool>("Enabled")) return false;

        var entra = configuration.GetSection("Authentication:Entra");
        if (!entra.GetValue<bool>("Enabled"))
            throw new InvalidOperationException("Mcp:Enabled requires Authentication:Entra:Enabled.");
        if (!Guid.TryParse(entra["TenantId"], out var tenantId) || tenantId == Guid.Empty)
            throw new InvalidOperationException("Authentication:Entra:TenantId must be a nonempty tenant GUID.");
        if (!Guid.TryParse(mcp["ClientId"], out var clientId) || clientId == Guid.Empty ||
            (Guid.TryParse(entra["ClientId"], out var officeClientId) && clientId == officeClientId))
            throw new InvalidOperationException("Mcp:ClientId must be a nonempty application GUID distinct from Authentication:Entra:ClientId.");

        if (!Uri.TryCreate(mcp["AuthorizationServer"], UriKind.Absolute, out var issuer))
            throw new InvalidOperationException("Mcp:AuthorizationServer must be an HTTPS issuer URI.");
        var requiredScope = mcp["RequiredScope"];
        if (string.IsNullOrEmpty(requiredScope) || requiredScope.Any(c =>
            c is not ('!' or (>= '#' and <= '[') or (>= ']' and <= '~')) || c is ':' or '/'))
            throw new InvalidOperationException("Mcp:RequiredScope must be a short delegated scope name.");
        var scopesSupported = mcp.GetSection("ScopesSupported").Get<string[]>();
        if (scopesSupported is not { Length: > 0 } || scopesSupported.Any(scope =>
            !Uri.TryCreate(scope, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.AbsolutePath == "/" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0))
            throw new InvalidOperationException("Mcp:ScopesSupported must contain fully qualified OAuth scopes.");

        var authority = $"https://login.microsoftonline.com/{tenantId:D}/v2.0";
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
        return true;
    }

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
