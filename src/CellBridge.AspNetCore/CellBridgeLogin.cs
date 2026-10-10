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
    string ReturnUrl, bool SignInFailed, string FormAction, string ReturnUrlParameter)
{
    public bool IsOffice { get; init; }
    public bool RequiresTwoFactor { get; init; }
    public bool UseRecoveryCode { get; init; }
    public string? ProtectedState { get; init; }
    public string? RestartAction { get; init; }
}

/// <summary>Settings for the packaged login page and Office sign-in flow.</summary>
public class CellBridgeLoginOptions
{
    public string ApplicationName { get; set; } = "CellBridge";
    /// <summary>Optional replacement HTML. Keep the supplied form action, return field and antiforgery token.</summary>
    public Func<CellBridgeLoginPageContext, string>? RenderPage { get; set; }
    /// <summary>Optional browser HTML, including the two-factor stage when Identity is used.</summary>
    public Func<CellBridgeLoginPageContext, string>? RenderBrowserPage { get; set; }
    /// <summary>Optional Office HTML, including the two-factor stage when Identity is used.</summary>
    public Func<CellBridgeLoginPageContext, string>? RenderOfficePage { get; set; }
    public PathString OfficeLoginPath { get; set; } = new("/_cellbridge/auth/login");
    /// <summary>Optional host request gate, applied to both login methods before any credential or token processing.</summary>
    public Func<HttpContext, bool>? IsRequestAllowed { get; set; }
    /// <summary>Browser destination when no safe return URL is supplied. Relative to the app's PathBase.</summary>
    public PathString DefaultReturnPath { get; set; } = new("/");
    public PathString CompletionPath { get; set; } = new("/_cellbridge/auth/complete");
    public string? PublicOrigin { get; set; }
    /// <summary>Retain cookies issued by a verified Office login after the client closes.
    /// Defaults to true. Set false for session-only Office sign-in.
    /// The host cookie's ticket lifetime and validation rules still apply.</summary>
    public bool PersistOfficeSession { get; set; } = true;
}

/// <summary>Registers a standard login page with optional custom HTML.</summary>
public static class CellBridgeLogin
{
    /// <summary>Creates a dedicated cookie scheme and enables the packaged login pages.</summary>
    /// <remarks>Requires HTTPS. Does not select or replace the host's default authentication scheme.
    /// Mixed-scheme hosts must configure their defaults or name schemes in authorization policies.
    /// Use AddCellBridgeLogin for an existing cookie, or AddCellBridgeIdentityLogin for Identity.</remarks>
    public static IServiceCollection AddCellBridgeCookieLogin<TAuthenticator>(this IServiceCollection services,
        string cookieScheme = "CellBridge", Action<CookieAuthenticationOptions>? configureCookie = null,
        Action<CellBridgeLoginOptions>? configure = null) where TAuthenticator : class, ICellBridgeLoginAuthenticator
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookieScheme);
        // Validate the login registration before adding a new authentication scheme.
        services.AddCellBridgeLogin<TAuthenticator>(cookieScheme, configure);
        services.AddAuthentication().AddCookie(cookieScheme, cookie =>
        {
            cookie.Cookie.Name = ".CellBridge." + Uri.EscapeDataString(cookieScheme);
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Lax;
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
            cookie.SlidingExpiration = false;
            cookie.LoginPath = "/auth/login";
            cookie.ReturnUrlParameter = "returnUrl";
            cookie.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
            configureCookie?.Invoke(cookie);
        });
        return services;
    }

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
        return Register(services, cookieScheme, options, identity: false);
    }

    internal static IServiceCollection Register(IServiceCollection services, string cookieScheme,
        CellBridgeLoginOptions options, bool identity)
    {
        if (services.Any(x => x.ServiceType == typeof(LoginRegistration)))
            throw new InvalidOperationException("Configure one packaged CellBridge login per host.");
        if (string.IsNullOrWhiteSpace(options.ApplicationName))
            throw new ArgumentException("ApplicationName must not be blank.", nameof(options));
        CellBridgeOfficeFormsAuthentication.ValidatePath(options.DefaultReturnPath, nameof(options.DefaultReturnPath));
        CellBridgeOfficeFormsAuthentication.ValidatePath(options.OfficeLoginPath, nameof(options.OfficeLoginPath));
        services.AddCellBridgeOfficeFormsAuthentication(cookieScheme, office =>
        { office.CompletionPath = options.CompletionPath; office.PublicOrigin = options.PublicOrigin; });
        services.AddSingleton(new LoginRegistration(options, identity));
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
        IResult Page(HttpContext context, IAntiforgery antiforgery, bool isOffice)
        {
            context.Response.Headers.CacheControl = "no-store";
            if (registration.Options.IsRequestAllowed?.Invoke(context) == false) return Results.NotFound();
            var returnUrl = ReturnUrl(context, registration, office, cookie, isOffice,
                context.Request.Query[cookie.ReturnUrlParameter]);
            var tokens = antiforgery.GetAndStoreTokens(context);
            var protectedState = CellBridgePrimaryLoginState.Create(context, registration, office, returnUrl, isOffice, tokens.RequestToken!);
            var page = new CellBridgeLoginPageContext(registration.Options.ApplicationName,
                tokens, protectedState.ReturnUrl, context.Request.Query.ContainsKey("failed"),
                (context.Request.PathBase + cookie.LoginPath).ToUriComponent(), cookie.ReturnUrlParameter)
                { IsOffice = isOffice, ProtectedState = protectedState.Token };
            return Render(registration, page);
        }
        app.MapGet(cookie.LoginPath.Value!, (HttpContext context, IAntiforgery antiforgery) => Page(context, antiforgery, false)).AllowAnonymous();
        app.MapGet(registration.Options.OfficeLoginPath.Value!, (HttpContext context, IAntiforgery antiforgery) => Page(context, antiforgery, true)).AllowAnonymous();
        app.MapPost(cookie.LoginPath.Value!, async (HttpContext context, IAntiforgery antiforgery,
            IServiceProvider services) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (registration.Options.IsRequestAllowed?.Invoke(context) == false) return Results.NotFound();
            var form = await ReadForm(context, antiforgery);
            if (form is null) return Results.BadRequest();
            var username = form["login"].ToString();
            var password = form["password"].ToString();
            var isOffice = form[PresentationField] == "office" ||
                form[cookie.ReturnUrlParameter] == (context.Request.PathBase + office.CompletionPath).ToUriComponent();
            var returnUrl = ReturnUrl(context, registration, office, cookie, isOffice, form[cookie.ReturnUrlParameter]);
            var persistSession = false;
            if (!string.IsNullOrEmpty(form[StateField]))
            {
                var state = CellBridgePrimaryLoginState.Read(context, registration, office, form[StateField].ToString(),
                    form[services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.FormFieldName].ToString());
                if (state is null) return Results.BadRequest();
                isOffice = state.IsOffice; returnUrl = state.ReturnUrl;
                persistSession = isOffice && registration.Options.PersistOfficeSession;
            }
            else if (registration.Options.RenderPage is null ||
                (isOffice ? registration.Options.RenderOfficePage : registration.Options.RenderBrowserPage) is not null)
                return Results.BadRequest();
            if (registration.Identity)
                return await services.GetRequiredService<ICellBridgeIdentityLoginHandler>()
                    .PasswordAsync(context, username, password, returnUrl, isOffice, persistSession);
            var authenticator = services.GetRequiredService<ICellBridgeLoginAuthenticator>();
            var principal = username.Length is > 0 and <= 256 && password.Length is > 0 and <= 1024
                ? await authenticator.AuthenticateAsync(context, username, password, context.RequestAborted) : null;
            if (principal is null || CellBridgeActor.FromPrincipal(principal) is null)
                return Failed(context, registration, cookie, isOffice, returnUrl);
            await context.SignInAsync(office.CookieScheme, principal, new AuthenticationProperties { IsPersistent = persistSession });
            return Results.LocalRedirect(returnUrl);
        }).AllowAnonymous();
        if (registration.Identity) app.ServiceProvider.GetRequiredService<IdentityLoginRegistration>().MapEndpoints(app);
    }

    internal const string PresentationField = "_cellbridgePresentation";
    internal const string StateField = "_cellbridgeState";
    internal const string CodeField = "code";
    internal const string MethodField = "method";
    internal const string CancelField = "cancel";

    internal static IResult Render(LoginRegistration registration, CellBridgeLoginPageContext page)
    {
        var renderer = page.IsOffice ? registration.Options.RenderOfficePage : registration.Options.RenderBrowserPage;
        renderer ??= page.RequiresTwoFactor ? CellBridgeLoginPage.Render : registration.Options.RenderPage ?? CellBridgeLoginPage.Render;
        return Results.Content(renderer(page), "text/html; charset=utf-8");
    }

    internal static IResult Failed(HttpContext context, LoginRegistration registration,
        CookieAuthenticationOptions cookie, bool isOffice, string returnUrl, bool failed = true) =>
        Results.LocalRedirect(QueryHelpers.AddQueryString(
            (context.Request.PathBase + (isOffice ? registration.Options.OfficeLoginPath : cookie.LoginPath)).ToUriComponent(),
            new Dictionary<string, string?> { ["failed"] = failed ? "1" : null, [cookie.ReturnUrlParameter] = returnUrl }));

    internal static string ReturnUrl(HttpContext context, LoginRegistration registration,
        CellBridgeOfficeFormsAuthentication.OfficeFormsRegistration office, CookieAuthenticationOptions cookie,
        bool isOffice, string? supplied)
    {
        if (isOffice) return (context.Request.PathBase + office.CompletionPath).ToUriComponent();
        var fallback = (context.Request.PathBase + registration.Options.DefaultReturnPath).ToUriComponent();
        var value = LocalReturnUrl(supplied, fallback);
        var mount = context.Request.PathBase.ToUriComponent();
        if (mount.Length == 0) return value;
        if (!Uri.TryCreate("https://cellbridge.invalid" + value, UriKind.Absolute, out var target)) return fallback;
        var path = target.AbsolutePath;
        return path == mount || path.StartsWith(mount + "/", StringComparison.Ordinal) ? value : fallback;
    }

    internal static async Task<IFormCollection?> ReadForm(HttpContext context, IAntiforgery antiforgery)
    {
        if (!context.Request.HasFormContentType ||
            context.Request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) is not true ||
            context.Request.ContentLength > 16384) return null;
        try
        {
            var form = await context.Request.ReadFormAsync(new FormOptions
                { ValueCountLimit = 16, KeyLengthLimit = 256, ValueLengthLimit = 4096 }, context.RequestAborted);
            await antiforgery.ValidateRequestAsync(context);
            return form;
        }
        catch (AntiforgeryValidationException) { return null; }
        catch (InvalidDataException) { return null; }
    }

    internal static string LocalReturnUrl(string? value, string fallback) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 2048 && value.StartsWith('/') && !value.StartsWith("//") && !value.Contains('\\') &&
        !value.Any(char.IsControl) ? value : fallback;

    internal sealed record LoginRegistration(CellBridgeLoginOptions Options, bool Identity);

    private sealed class AuthenticatorValidator(IServiceScopeFactory scopes,
        IOptionsMonitor<CookieAuthenticationOptions> cookies, IOptions<AntiforgeryOptions> antiforgery,
        CellBridgeOfficeFormsAuthentication.OfficeFormsRegistration office) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var returnField = cookies.Get(office.CookieScheme).ReturnUrlParameter;
            var fields = new[] { "login", "password", "failed", PresentationField, StateField, CodeField, MethodField, CancelField };
            var reserved = fields.Append(antiforgery.Value.FormFieldName);
            if (reserved.Contains(returnField, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("The login return URL parameter must not collide with credential, failure or antiforgery fields.");
            if (fields.Contains(antiforgery.Value.FormFieldName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("The login antiforgery field must not collide with credential or failure fields.");
            using var scope = scopes.CreateScope();
            var registration = scope.ServiceProvider.GetRequiredService<LoginRegistration>();
            var paths = new[] { cookies.Get(office.CookieScheme).LoginPath, registration.Options.OfficeLoginPath, office.CompletionPath };
            if (paths.Select(p => p.Value!.TrimEnd('/')).Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
                throw new InvalidOperationException("Browser login, Office login and completion paths must differ.");
            if (registration.Identity)
            {
                await scope.ServiceProvider.GetRequiredService<ICellBridgeIdentityLoginHandler>().ValidateAsync(cancellationToken);
                return;
            }
            _ = scope.ServiceProvider.GetService<ICellBridgeLoginAuthenticator>()
                ?? throw new InvalidOperationException("Register an ICellBridgeLoginAuthenticator for the built-in login page.");
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
