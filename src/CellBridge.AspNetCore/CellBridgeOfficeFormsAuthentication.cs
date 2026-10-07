using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CellBridge.AspNetCore;

/// <summary>Settings for Office forms authentication using the host's existing cookie scheme.</summary>
public sealed class CellBridgeOfficeFormsAuthenticationOptions
{
    /// <summary>The library-owned sign-in completion path. The host's login page must accept a local return URL.</summary>
    public PathString CompletionPath { get; set; } = new("/_cellbridge/auth/complete");

    /// <summary>Optional external HTTPS origin. Otherwise URLs use the request's scheme and host.</summary>
    public string? PublicOrigin { get; set; }
}

/// <summary>Connects the host's cookie login to Office's MS-OFBA challenge and completion flow.</summary>
public static class CellBridgeOfficeFormsAuthentication
{
    /// <summary>
    /// Enables Office forms authentication on endpoints mapped by MapCellBridge. LoginPath and
    /// ReturnUrlParameter come from the selected cookie scheme. The host retains accounts, login,
    /// CSRF protection and claim mapping. DI-resolved CookieAuthenticationOptions.EventsType is unsupported.
    /// </summary>
    public static IServiceCollection AddCellBridgeOfficeFormsAuthentication(this IServiceCollection services,
        string cookieScheme = CookieAuthenticationDefaults.AuthenticationScheme,
        Action<CellBridgeOfficeFormsAuthenticationOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookieScheme);
        if (services.Any(x => x.ServiceType == typeof(OfficeFormsRegistration)))
            throw new InvalidOperationException("Configure one Office cookie scheme per CellBridge host.");
        var settings = new CellBridgeOfficeFormsAuthenticationOptions();
        configure?.Invoke(settings);
        ValidatePath(settings.CompletionPath, nameof(settings.CompletionPath));
        string? origin = null;
        if (settings.PublicOrigin is not null)
        {
            if (!Uri.TryCreate(settings.PublicOrigin, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
                throw new ArgumentException("PublicOrigin must be an HTTPS origin without a path or credentials.", nameof(configure));
            origin = uri.GetLeftPart(UriPartial.Authority);
        }
        var registration = new OfficeFormsRegistration(cookieScheme, settings.CompletionPath, origin);
        services.AddSingleton(registration);
        services.PostConfigure<CookieAuthenticationOptions>(cookieScheme, options =>
        {
            if (options.EventsType is not null)
                throw new InvalidOperationException("Office forms authentication requires CookieAuthenticationOptions.Events, not EventsType.");
            ValidatePath(options.LoginPath, nameof(options.LoginPath));
            if (options.LoginPath == registration.CompletionPath)
                throw new InvalidOperationException("The Office completion path must differ from the cookie login path.");
            if (string.IsNullOrWhiteSpace(options.ReturnUrlParameter) || options.ReturnUrlParameter.Any(char.IsControl))
                throw new InvalidOperationException("The cookie return URL parameter must be non-empty and contain no control characters.");
            options.Events = new OfficeCookieEvents(options.Events, registration);
        });
        services.AddSingleton<IHostedService, OfficeCookieSchemeValidator>();
        return services;
    }

    internal static OfficeFormsRegistration? MapCompletion(IEndpointRouteBuilder app)
    {
        var registration = app.ServiceProvider.GetService<OfficeFormsRegistration>();
        if (registration is null) return null;
        if (app is RouteGroupBuilder)
            throw new InvalidOperationException("MapCellBridge with Office forms authentication must be mapped at the application root. Use PathBase for a mounted application.");
        CellBridgeLogin.MapLogin(app, registration);
        app.MapMethods(registration.CompletionPath.Value!, ["GET", "HEAD"], async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var result = await context.AuthenticateAsync(registration.CookieScheme);
            return result.Succeeded && result.Principal is not null && CellBridgeActor.FromPrincipal(result.Principal) is not null
                ? Results.StatusCode(StatusCodes.Status200OK)
                : Results.Unauthorized();
        }).AllowAnonymous();
        return registration;
    }

    internal static void ValidatePath(PathString path, string name)
    {
        var value = path.Value;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith('/') || value.StartsWith("//") ||
            value.Any(x => char.IsControl(x) || x is '\\' or '?' or '#' or '{' or '}'))
            throw new ArgumentException("Use a literal local path without query, fragment, backslash, braces or control characters.", name);
    }

    internal sealed record OfficeFormsRegistration(string CookieScheme, PathString CompletionPath, string? PublicOrigin);
    internal sealed class OfficeEndpointMetadata { }

    private sealed class OfficeCookieSchemeValidator(IAuthenticationSchemeProvider schemes,
        IOptionsMonitor<CookieAuthenticationOptions> cookies, OfficeFormsRegistration registration) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var scheme = await schemes.GetSchemeAsync(registration.CookieScheme);
            if (scheme is null || !typeof(CookieAuthenticationHandler).IsAssignableFrom(scheme.HandlerType))
                throw new InvalidOperationException("Office forms authentication requires a registered cookie scheme: " + registration.CookieScheme);
            _ = cookies.Get(registration.CookieScheme);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class OfficeCookieEvents(CookieAuthenticationEvents original, OfficeFormsRegistration registration)
        : CookieAuthenticationEvents
    {
        public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
        {
            if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<OfficeEndpointMetadata>() is null)
                return original.RedirectToLogin(context);
            var request = context.Request;
            var origin = registration.PublicOrigin ?? $"{request.Scheme}://{request.Host}";
            var completion = (request.PathBase + registration.CompletionPath).ToUriComponent();
            var packaged = context.HttpContext.RequestServices.GetService<CellBridgeLogin.LoginRegistration>();
            var login = origin + request.PathBase + (packaged?.Options.OfficeLoginPath ?? context.Options.LoginPath);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-FORMS_BASED_AUTH_REQUIRED"] = QueryHelpers.AddQueryString(login,
                context.Options.ReturnUrlParameter, completion);
            context.Response.Headers["X-FORMS_BASED_AUTH_RETURN_URL"] = origin + completion;
            context.Response.Headers["X-FORMS_BASED_AUTH_DIALOG_SIZE"] = "800x600";
            return Task.CompletedTask;
        }

        public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
        {
            if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<OfficeEndpointMetadata>() is null)
                return original.RedirectToAccessDenied(context);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
        {
            await original.ValidatePrincipal(context);
            if (context.Principal is not null && context.HttpContext.RequestServices.GetService<IdentityLoginRegistration>() is { } identity &&
                !identity.MapPrincipal(context.HttpContext, context.Principal)) context.RejectPrincipal();
        }
        public override Task CheckSlidingExpiration(CookieSlidingExpirationContext context) => original.CheckSlidingExpiration(context);
        public override async Task SigningIn(CookieSigningInContext context)
        {
            await original.SigningIn(context);
            if (context.HttpContext.RequestServices.GetService<IdentityLoginRegistration>() is { } identity &&
                !identity.MapPrincipal(context.HttpContext, context.Principal))
                throw new InvalidOperationException("Identity sign-in requires one authenticated identity with a stable user ID or explicit CellBridge subject.");
        }
        public override Task SignedIn(CookieSignedInContext context) => original.SignedIn(context);
        public override Task SigningOut(CookieSigningOutContext context) => original.SigningOut(context);
        public override Task RedirectToLogout(RedirectContext<CookieAuthenticationOptions> context) => original.RedirectToLogout(context);
        public override Task RedirectToReturnUrl(RedirectContext<CookieAuthenticationOptions> context) => original.RedirectToReturnUrl(context);
    }
}
