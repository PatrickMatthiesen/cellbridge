using System.Security.Claims;
using CellBridge.AspNetCore;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CellBridge.Authentication;

public static class AuthenticationServices
{
    public const string CookieName = "CellBridge.Authentication";
    public const string AntiforgeryCookie = "CellBridge.Antiforgery";
    public const string AntiforgeryHeader = "X-CellBridge-CSRF";

    public static IServiceCollection AddCellBridgeAuthentication(this IServiceCollection services,
        IConfiguration configuration, bool renewCookies = true, bool serveOffice = true)
    {
        var connectionString = configuration.GetConnectionString("cellbridge")
            ?? throw new InvalidOperationException("Authentication requires an initialized PostgreSQL identity database.");
        var publicOrigin = configuration["Authentication:PublicOrigin"]
            ?? throw new InvalidOperationException("Set Authentication:PublicOrigin to the HTTPS origin reachable by Office.");
        if (!Uri.TryCreate(publicOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
            throw new InvalidOperationException("Authentication:PublicOrigin must be an HTTPS origin without path or credentials.");
        services.AddSingleton(new AuthenticationOrigin(origin.GetLeftPart(UriPartial.Authority)));
        services.AddDbContextFactory<AuthenticationDatabase>(o => o.UseNpgsql(connectionString));
        services.AddIdentityCore<CellBridgeUser>(o =>
        {
            o.Password.RequiredLength = 12;
            o.Lockout.MaxFailedAccessAttempts = 5;
            o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            o.SignIn.RequireConfirmedAccount = true;
        }).AddEntityFrameworkStores<AuthenticationDatabase>().AddSignInManager()
            .AddClaimsPrincipalFactory<CellBridgeClaimsFactory>();
        services.AddScoped<IUserConfirmation<CellBridgeUser>, EnabledUserConfirmation>();
        services.AddAuthentication(o =>
        {
            o.DefaultScheme = IdentityConstants.ApplicationScheme;
            o.DefaultSignInScheme = IdentityConstants.ApplicationScheme;
        }).AddIdentityCookies();
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, o =>
        {
            o.Cookie.Name = CookieName;
            o.Cookie.Path = "/";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = renewCookies;
            o.Events.OnRedirectToLogin = context =>
            {
                var request = context.Request;
                if (request.Path.StartsWithSegments("/api")) context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                else if (!serveOffice || request.Path.StartsWithSegments("/library") || request.PathBase.StartsWithSegments("/library"))
                    context.Response.Redirect(origin.GetLeftPart(UriPartial.Authority) + "/auth/login?returnUrl=" +
                        Uri.EscapeDataString(request.PathBase + request.Path + request.QueryString));
                else OfficeChallenge(context.HttpContext, origin.GetLeftPart(UriPartial.Authority));
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToAccessDenied = context =>
            { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            if (!renewCookies)
            {
                var validate = o.Events.OnValidatePrincipal;
                o.Events.OnValidatePrincipal = async context =>
                {
                    await validate(context);
                    // The public web host renews; internal catalog replies never
                    // silently renew a cookie that the browser does not receive.
                    context.ShouldRenew = false;
                };
            }
        });
        services.AddAuthorization();
        services.AddAntiforgery(o =>
        {
            o.Cookie.Name = AntiforgeryCookie;
            o.Cookie.Path = "/";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.HeaderName = AntiforgeryHeader;
        });
        services.AddDataProtection().SetApplicationName("CellBridge.Authentication.v1");
        services.AddSingleton<DatabaseKeyRepository>();
        services.AddOptions<KeyManagementOptions>().Configure<DatabaseKeyRepository>((o, repository) => o.XmlRepository = repository);
        return services;
    }

    public static void OfficeChallenge(HttpContext context, string origin)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-FORMS_BASED_AUTH_REQUIRED"] = origin + "/auth/login?returnUrl=%2Fauth%2Fcomplete";
        context.Response.Headers["X-FORMS_BASED_AUTH_RETURN_URL"] = origin + "/auth/complete";
        context.Response.Headers["X-FORMS_BASED_AUTH_DIALOG_SIZE"] = "800x600";
    }
}

public sealed record AuthenticationOrigin(string Value);

internal sealed class EnabledUserConfirmation : IUserConfirmation<CellBridgeUser>
{
    public Task<bool> IsConfirmedAsync(UserManager<CellBridgeUser> manager, CellBridgeUser user) => Task.FromResult(user.Enabled);
}

internal sealed class CellBridgeClaimsFactory(UserManager<CellBridgeUser> users, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<CellBridgeUser>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(CellBridgeUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new(CellBridgeActor.SubjectClaim, user.Subject));
        identity.AddClaim(new(CellBridgeActor.DisplayNameClaim, user.DisplayName));
        if (user.CanCreate) identity.AddClaim(new(CellBridgeActor.CreateClaim, "true"));
        return identity;
    }
}
