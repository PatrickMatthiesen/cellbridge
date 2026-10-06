using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CellBridge.AspNetCore;

/// <summary>Checks credentials against the host's account system. Return null on failure.</summary>
/// <remarks>Return an authenticated principal with CellBridge claims only after all required checks,
/// including account status, lockout and any second factor. Never log credentials.</remarks>
public interface ICellBridgeLoginAuthenticator
{
    Task<ClaimsPrincipal?> AuthenticateAsync(HttpContext context, string username, string password,
        CancellationToken cancellationToken);
}

/// <summary>Values for the standard or host-supplied login HTML. HTML-encode values when rendering them.</summary>
public sealed record CellBridgeLoginPageContext(string ApplicationName, AntiforgeryTokenSet Antiforgery,
    string ReturnUrl, bool SignInFailed, string FormAction, string ReturnUrlParameter);

/// <summary>Settings for the packaged login page and Office sign-in flow.</summary>
public sealed class CellBridgeLoginOptions
{
    public string ApplicationName { get; set; } = "CellBridge";
    /// <summary>Optional replacement HTML. Keep the supplied form action, return field and antiforgery token.</summary>
    public Func<CellBridgeLoginPageContext, string>? RenderPage { get; set; }
    /// <summary>Optional host request gate, applied to both login methods before any credential or token processing.</summary>
    public Func<HttpContext, bool>? IsRequestAllowed { get; set; }
    public PathString CompletionPath { get; set; } = new("/_cellbridge/auth/complete");
    public string? PublicOrigin { get; set; }
}

/// <summary>Registers a standard login page with optional custom HTML.</summary>
public static class CellBridgeLogin
{
    /// <summary>Enables the standard login page and Office authentication with a scoped host credential checker.
    /// The selected cookie's LoginPath and ReturnUrlParameter determine the login route and return field.</summary>
    public static IServiceCollection AddCellBridgeLogin<TAuthenticator>(this IServiceCollection services,
        string cookieScheme = CookieAuthenticationDefaults.AuthenticationScheme,
        Action<CellBridgeLoginOptions>? configure = null) where TAuthenticator : class, ICellBridgeLoginAuthenticator
    {
        services.AddScoped<ICellBridgeLoginAuthenticator, TAuthenticator>();
        return services.AddCellBridgeLogin(cookieScheme, configure);
    }

    /// <summary>Uses an ICellBridgeLoginAuthenticator already registered by the host.</summary>
    public static IServiceCollection AddCellBridgeLogin(this IServiceCollection services,
        string cookieScheme = CookieAuthenticationDefaults.AuthenticationScheme,
        Action<CellBridgeLoginOptions>? configure = null)
    {
        var options = new CellBridgeLoginOptions();
        configure?.Invoke(options);
        if (string.IsNullOrWhiteSpace(options.ApplicationName))
            throw new ArgumentException("ApplicationName must not be blank.", nameof(configure));
        services.AddCellBridgeOfficeFormsAuthentication(cookieScheme, office =>
        { office.CompletionPath = options.CompletionPath; office.PublicOrigin = options.PublicOrigin; });
        services.AddSingleton(new LoginRegistration(options.ApplicationName, options.RenderPage,
            options.IsRequestAllowed));
        services.AddAntiforgery();
        services.AddSingleton<IHostedService, AuthenticatorValidator>();
        return services;
    }

    internal static void MapLogin(IEndpointRouteBuilder app,
        CellBridgeOfficeFormsAuthentication.OfficeFormsRegistration office)
    {
        var registration = app.ServiceProvider.GetService<LoginRegistration>();
        if (registration is null) return;
        var cookie = app.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(office.CookieScheme);
        app.MapGet(cookie.LoginPath.Value!, (HttpContext context, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (registration.IsRequestAllowed?.Invoke(context) == false) return Results.NotFound();
            var completion = (context.Request.PathBase + office.CompletionPath).ToUriComponent();
            var returnUrl = LocalReturnUrl(context.Request.Query[cookie.ReturnUrlParameter], completion);
            var page = new CellBridgeLoginPageContext(registration.ApplicationName,
                antiforgery.GetAndStoreTokens(context), returnUrl, context.Request.Query.ContainsKey("failed"),
                (context.Request.PathBase + cookie.LoginPath).ToUriComponent(), cookie.ReturnUrlParameter);
            return Results.Content((registration.RenderPage ?? CellBridgeLoginPage.Render)(page), "text/html; charset=utf-8");
        }).AllowAnonymous();
        app.MapPost(cookie.LoginPath.Value!, async (HttpContext context, IAntiforgery antiforgery,
            ICellBridgeLoginAuthenticator authenticator) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (registration.IsRequestAllowed?.Invoke(context) == false) return Results.NotFound();
            if (context.Request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) is not true)
                return Results.BadRequest();
            if (context.Request.ContentLength > 16384) return Results.BadRequest();
            IFormCollection form;
            try
            {
                form = await context.Request.ReadFormAsync(new FormOptions
                { ValueCountLimit = 16, KeyLengthLimit = 256, ValueLengthLimit = 4096 }, context.RequestAborted);
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            catch (InvalidDataException) { return Results.BadRequest(); }
            var username = form["login"].ToString();
            var password = form["password"].ToString();
            var completion = (context.Request.PathBase + office.CompletionPath).ToUriComponent();
            var returnUrl = LocalReturnUrl(form[cookie.ReturnUrlParameter], completion);
            var principal = username.Length is > 0 and <= 256 && password.Length is > 0 and <= 1024
                ? await authenticator.AuthenticateAsync(context, username, password, context.RequestAborted) : null;
            if (principal is null || CellBridgeActor.FromPrincipal(principal) is null)
                return Results.LocalRedirect(QueryHelpers.AddQueryString(
                    (context.Request.PathBase + cookie.LoginPath).ToUriComponent(),
                    new Dictionary<string, string?> { ["failed"] = "1", [cookie.ReturnUrlParameter] = returnUrl }));
            await context.SignInAsync(office.CookieScheme, principal, new AuthenticationProperties { IsPersistent = false });
            return Results.LocalRedirect(returnUrl);
        }).AllowAnonymous();
    }

    internal static string LocalReturnUrl(string? value, string fallback) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 2048 && value.StartsWith('/') && !value.StartsWith("//") && !value.Contains('\\') &&
        !value.Any(char.IsControl) ? value : fallback;

    private sealed record LoginRegistration(string ApplicationName, Func<CellBridgeLoginPageContext, string>? RenderPage,
        Func<HttpContext, bool>? IsRequestAllowed);

    private sealed class AuthenticatorValidator(IServiceScopeFactory scopes) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = scopes.CreateScope();
            _ = scope.ServiceProvider.GetService<ICellBridgeLoginAuthenticator>()
                ?? throw new InvalidOperationException("Register an ICellBridgeLoginAuthenticator for the built-in login page.");
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
