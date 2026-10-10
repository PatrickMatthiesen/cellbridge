using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CellBridge.AspNetCore;

/// <summary>Uses the host's Identity accounts and application cookie. Does not register a user database.</summary>
public sealed class CellBridgeIdentityLoginOptions : CellBridgeLoginOptions
{
    /// <summary>Stable namespace for generated subjects. Keep this unchanged after documents are created.</summary>
    public string IdentityAuthority { get; set; } = "application";
    public PathString TwoFactorPath { get; set; } = new("/_cellbridge/auth/two-factor");
    public TimeSpan PendingLifetime { get; set; } = TimeSpan.FromMinutes(5);
}

public static class CellBridgeIdentityLogin
{
    /// <summary>Supplies browser and Office pages for existing ASP.NET Core Identity accounts,
    /// including authenticator codes and recovery codes. Host account policies and cookie validation remain active.</summary>
    public static IServiceCollection AddCellBridgeIdentityLogin<TUser>(this IServiceCollection services,
        Action<CellBridgeIdentityLoginOptions>? configure = null) where TUser : class
    {
        var options = new CellBridgeIdentityLoginOptions();
        configure?.Invoke(options);
        if (string.IsNullOrWhiteSpace(options.IdentityAuthority) || options.IdentityAuthority.Length > 256)
            throw new ArgumentException("IdentityAuthority must be a stable, non-empty namespace of at most 256 characters.");
        if (options.PendingLifetime <= TimeSpan.Zero || options.PendingLifetime > TimeSpan.FromMinutes(15))
            throw new ArgumentException("PendingLifetime must be positive and at most fifteen minutes.");
        CellBridgeOfficeFormsAuthentication.ValidatePath(options.TwoFactorPath, nameof(options.TwoFactorPath));
        CellBridgeLogin.Register(services, IdentityConstants.ApplicationScheme, options, identity: true);
        services.AddSingleton(new IdentityLoginRegistration(options, app => IdentityLoginHandler<TUser>.Map(app, options)));
        services.AddScoped<ICellBridgeIdentityLoginHandler, IdentityLoginHandler<TUser>>();
        services.PostConfigure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, cookie =>
        {
            if (cookie.EventsType is not null)
                throw new InvalidOperationException("The Identity two-factor cookie must use Events, not EventsType.");
            cookie.Events = new PendingCookieEvents(cookie.Events);
        });
        return services;
    }

    internal const string AttemptItem = "CellBridge.IdentityLogin.Attempt";
    internal const string AttemptClaim = "cellbridge:login-attempt";
    internal static readonly object PersistentRecoverySignIn = new();

    private sealed class PendingCookieEvents(CookieAuthenticationEvents original) : CookieAuthenticationEvents
    {
        public override async Task SigningIn(CookieSigningInContext context)
        {
            await original.SigningIn(context);
            if (context.HttpContext.Items[AttemptItem] is string attempt && context.Principal?.Identity is ClaimsIdentity identity)
                identity.AddClaim(new(AttemptClaim, attempt));
        }
        public override Task ValidatePrincipal(CookieValidatePrincipalContext context) => original.ValidatePrincipal(context);
        public override Task CheckSlidingExpiration(CookieSlidingExpirationContext context) => original.CheckSlidingExpiration(context);
        public override Task SignedIn(CookieSignedInContext context) => original.SignedIn(context);
        public override Task SigningOut(CookieSigningOutContext context) => original.SigningOut(context);
        public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context) => original.RedirectToLogin(context);
        public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context) => original.RedirectToAccessDenied(context);
        public override Task RedirectToLogout(RedirectContext<CookieAuthenticationOptions> context) => original.RedirectToLogout(context);
        public override Task RedirectToReturnUrl(RedirectContext<CookieAuthenticationOptions> context) => original.RedirectToReturnUrl(context);
    }
}

internal sealed record IdentityLoginRegistration(CellBridgeIdentityLoginOptions Options, Action<IEndpointRouteBuilder> MapEndpoints)
{
    public bool MapPrincipal(HttpContext context, ClaimsPrincipal? principal)
    {
        if (principal is null) return false;
        var identities = principal.Identities.Where(i => i.IsAuthenticated).ToArray();
        if (identities.Length != 1) return false;
        var identity = identities[0];
        var claimType = context.RequestServices.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity.UserIdClaimType;
        var ids = identity.FindAll(claimType).ToArray();
        if (ids.Length > 1) return false;
        var subjects = identity.FindAll(CellBridgeActor.SubjectClaim).ToArray();
        if (subjects.Length > 0) return subjects.Length == 1 && !string.IsNullOrWhiteSpace(subjects[0].Value);
        if (ids.Length != 1 || string.IsNullOrWhiteSpace(ids[0].Value)) return false;
        var authority = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(Options.IdentityAuthority));
        var id = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(ids[0].Value));
        identity.AddClaim(new(CellBridgeActor.SubjectClaim, $"identity:{authority}:{id}"));
        if (!identity.HasClaim(c => c.Type == CellBridgeActor.DisplayNameClaim))
            identity.AddClaim(new(CellBridgeActor.DisplayNameClaim, identity.Name ?? ids[0].Value));
        return true;
    }
}

internal interface ICellBridgeIdentityLoginHandler
{
    Task ValidateAsync(CancellationToken cancellationToken);
    Task<IResult> PasswordAsync(HttpContext context, string username, string password, string returnUrl, bool isOffice, bool persistSession);
}

internal sealed class IdentityLoginHandler<TUser>(SignInManager<TUser> signIn, UserManager<TUser> users,
    IdentityLoginRegistration registration, CellBridgeLogin.LoginRegistration login,
    IOptionsMonitor<CookieAuthenticationOptions> cookies, IDataProtectionProvider protection,
    IAuthenticationSchemeProvider schemes, IServiceProvider services) : ICellBridgeIdentityLoginHandler where TUser : class
{
    private const string FlowCookie = "CellBridge.IdentityLogin.Pending";
    private readonly IDataProtector _protector = protection.CreateProtector("CellBridge.IdentityLogin", "v2", IdentityConstants.ApplicationScheme,
        registration.Options.IdentityAuthority, registration.Options.TwoFactorPath.Value!, registration.Options.OfficeLoginPath.Value!,
        registration.Options.CompletionPath.Value!);
    private TimeProvider Clock => services.GetService<TimeProvider>() ?? TimeProvider.System;
    private CellBridgeIdentityLoginOptions Options => registration.Options;
    private CookieAuthenticationOptions Cookie => cookies.Get(IdentityConstants.ApplicationScheme);

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        if (signIn.AuthenticationScheme != IdentityConstants.ApplicationScheme)
            throw new InvalidOperationException("CellBridge Identity login requires SignInManager.AuthenticationScheme to be Identity.Application.");
        var paths = new[] { Cookie.LoginPath, Options.OfficeLoginPath, Options.CompletionPath, Options.TwoFactorPath };
        if (paths.Select(p => p.Value!.TrimEnd('/')).Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new InvalidOperationException("Browser login, Office login, completion and two-factor paths must differ.");
        foreach (var scheme in new[] { IdentityConstants.ApplicationScheme, IdentityConstants.TwoFactorUserIdScheme })
        {
            var configured = await schemes.GetSchemeAsync(scheme);
            if (configured is null || !typeof(CookieAuthenticationHandler).IsAssignableFrom(configured.HandlerType))
                throw new InvalidOperationException($"CellBridge Identity login requires the Identity cookie scheme {scheme}.");
        }
        _ = cookies.Get(IdentityConstants.TwoFactorUserIdScheme);
    }

    public async Task<IResult> PasswordAsync(HttpContext context, string username, string password, string returnUrl, bool isOffice, bool persistSession)
    {
        await Clear(context);
        var attempt = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        context.Items[CellBridgeIdentityLogin.AttemptItem] = attempt;
        if (username.Length is not (> 0 and <= 256) || password.Length is not (> 0 and <= 1024))
            return CellBridgeLogin.Failed(context, login, Cookie, isOffice, returnUrl);
        var result = await signIn.PasswordSignInAsync(username, password, isPersistent: persistSession, lockoutOnFailure: true);
        if (result.Succeeded) return Results.LocalRedirect(returnUrl);
        if (!result.RequiresTwoFactor) return CellBridgeLogin.Failed(context, login, Cookie, isOffice, returnUrl);
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
        {
            await Clear(context);
            return CellBridgeLogin.Failed(context, login, Cookie, isOffice, returnUrl);
        }
        var providers = await users.GetValidTwoFactorProvidersAsync(user);
        if (!providers.Contains(signIn.Options.Tokens.AuthenticatorTokenProvider) && !users.SupportsUserTwoFactorRecoveryCodes)
        {
            await Clear(context);
            return CellBridgeLogin.Failed(context, login, Cookie, isOffice, returnUrl);
        }
        var stamp = users.SupportsUserSecurityStamp ? await users.GetSecurityStampAsync(user) : "";
        var state = new Pending(await users.GetUserIdAsync(user), stamp, attempt, context.Request.PathBase.ToUriComponent(),
            returnUrl, isOffice, persistSession, Clock.GetUtcNow().Add(Options.PendingLifetime).UtcTicks);
        var token = Protect(state);
        if (token.Length > CellBridgePrimaryLoginState.MaxTokenLength && !isOffice)
        {
            state = state with { ReturnUrl = (context.Request.PathBase + Options.DefaultReturnPath).ToUriComponent() };
            token = Protect(state);
        }
        if (token.Length > CellBridgePrimaryLoginState.MaxTokenLength)
        {
            await Clear(context);
            return CellBridgeLogin.Failed(context, login, Cookie, isOffice, returnUrl);
        }
        context.Response.Cookies.Append(FlowCookie, token, CookieOptions(context));
        return Results.LocalRedirect((context.Request.PathBase + Options.TwoFactorPath).ToUriComponent());
    }

    public static void Map(IEndpointRouteBuilder app, CellBridgeIdentityLoginOptions options)
    {
        app.MapGet(options.TwoFactorPath.Value!, async (HttpContext context, IAntiforgery csrf, ICellBridgeIdentityLoginHandler backend) =>
            await ((IdentityLoginHandler<TUser>)backend).TwoFactorPage(context, csrf)).AllowAnonymous();
        app.MapPost(options.TwoFactorPath.Value!, async (HttpContext context, IAntiforgery csrf, ICellBridgeIdentityLoginHandler backend) =>
            await ((IdentityLoginHandler<TUser>)backend).TwoFactorPost(context, csrf)).AllowAnonymous();
    }

    private async Task<IResult> TwoFactorPage(HttpContext context, IAntiforgery csrf)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (Options.IsRequestAllowed?.Invoke(context) == false) return Results.NotFound();
        var pending = await ReadPending(context);
        if (pending is null) return Restart(context);
        var (state, user, token) = pending.Value;
        var providers = await users.GetValidTwoFactorProvidersAsync(user);
        var page = new CellBridgeLoginPageContext(Options.ApplicationName, csrf.GetAndStoreTokens(context), state.ReturnUrl,
            context.Request.Query.ContainsKey("failed"), (context.Request.PathBase + Options.TwoFactorPath).ToUriComponent(), Cookie.ReturnUrlParameter)
        {
            IsOffice = state.IsOffice, RequiresTwoFactor = true, ProtectedState = token,
            UseRecoveryCode = context.Request.Query["method"] == "recovery" || !providers.Contains(signIn.Options.Tokens.AuthenticatorTokenProvider),
            RestartAction = (context.Request.PathBase + Options.TwoFactorPath).ToUriComponent()
        };
        return CellBridgeLogin.Render(login, page);
    }

    private async Task<IResult> TwoFactorPost(HttpContext context, IAntiforgery csrf)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (Options.IsRequestAllowed?.Invoke(context) == false) return Results.NotFound();
        var form = await CellBridgeLogin.ReadForm(context, csrf);
        if (form is null) return Results.BadRequest();
        var pending = await ReadPending(context);
        if (pending is null || form[CellBridgeLogin.StateField] != pending.Value.Token)
        {
            await Clear(context);
            return Restart(context);
        }
        var (state, user, _) = pending.Value;
        if (form[CellBridgeLogin.CancelField] == "1")
        {
            await Clear(context);
            return CellBridgeLogin.Failed(context, login, Cookie, state.IsOffice, state.ReturnUrl, failed: false);
        }
        var method = form[CellBridgeLogin.MethodField].ToString();
        var code = form[CellBridgeLogin.CodeField].ToString();
        if (method is not ("authenticator" or "recovery") || code.Length > 256) return Results.BadRequest();
        if (code.Length == 0) return Retry(context, method);
        // Recovery-code sign-in does not perform Identity's usual account/lockout precheck.
        if (!await signIn.CanSignInAsync(user) || users.SupportsUserLockout && await users.IsLockedOutAsync(user))
        {
            await Clear(context);
            return CellBridgeLogin.Failed(context, login, Cookie, state.IsOffice, state.ReturnUrl);
        }
        SignInResult result;
        if (method == "recovery")
        {
            // Identity's recovery-code API always signs in with IsPersistent=false.
            // Apply our protected flow's choice to that one application-cookie issuance,
            // before the host's SigningIn event gets its final say.
            context.Items[CellBridgeIdentityLogin.PersistentRecoverySignIn] = state.PersistSession;
            try
            {
                result = users.SupportsUserTwoFactorRecoveryCodes
                    ? await signIn.TwoFactorRecoveryCodeSignInAsync(code.Replace(" ", "")) : SignInResult.Failed;
            }
            finally { context.Items.Remove(CellBridgeIdentityLogin.PersistentRecoverySignIn); }
            if (!result.Succeeded && users.SupportsUserLockout) await users.AccessFailedAsync(user);
        }
        else result = await signIn.TwoFactorAuthenticatorSignInAsync(code.Replace(" ", "").Replace("-", ""),
            isPersistent: state.PersistSession, rememberClient: false);
        if (result.Succeeded)
        {
            context.Response.Cookies.Delete(FlowCookie, CookieOptions(context));
            return Results.LocalRedirect(state.ReturnUrl);
        }
        if (result.IsLockedOut || result.IsNotAllowed || users.SupportsUserLockout && await users.IsLockedOutAsync(user))
        {
            await Clear(context);
            return CellBridgeLogin.Failed(context, login, Cookie, state.IsOffice, state.ReturnUrl);
        }
        return Retry(context, method);
    }

    private async Task<(Pending State, TUser User, string Token)?> ReadPending(HttpContext context)
    {
        var token = context.Request.Cookies[FlowCookie];
        if (string.IsNullOrEmpty(token) || token.Length > CellBridgePrimaryLoginState.MaxTokenLength) return null;
        Pending state;
        try { state = Unprotect(token); }
        catch (Exception error) when (error is CryptographicException or FormatException or InvalidDataException or EndOfStreamException) { return null; }
        if (state.PathBase != context.Request.PathBase.ToUriComponent() ||
            CellBridgeLogin.LocalReturnUrl(state.ReturnUrl, "") != state.ReturnUrl ||
            state.IsOffice && state.ReturnUrl != (context.Request.PathBase + Options.CompletionPath).ToUriComponent()) return null;
        context.Items[typeof(Pending)] = state;
        if (state.Expires <= Clock.GetUtcNow().UtcTicks) return null;
        var auth = await context.AuthenticateAsync(IdentityConstants.TwoFactorUserIdScheme);
        if (!auth.Succeeded || auth.Principal?.FindFirst(CellBridgeIdentityLogin.AttemptClaim)?.Value != state.Attempt) return null;
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null || await users.GetUserIdAsync(user) != state.UserId ||
            users.SupportsUserSecurityStamp && await users.GetSecurityStampAsync(user) != state.SecurityStamp ||
            !users.SupportsUserTwoFactor || !await users.GetTwoFactorEnabledAsync(user)) return null;
        return (state, user, token);
    }

    private IResult Restart(HttpContext context) => context.Items[typeof(Pending)] is Pending state
        ? CellBridgeLogin.Failed(context, login, Cookie, state.IsOffice, state.ReturnUrl)
        : Results.LocalRedirect((context.Request.PathBase + Cookie.LoginPath).ToUriComponent());
    private IResult Retry(HttpContext context, string method) => Results.LocalRedirect(QueryHelpers.AddQueryString(
        (context.Request.PathBase + Options.TwoFactorPath).ToUriComponent(),
        new Dictionary<string, string?> { ["failed"] = "1", ["method"] = method }));
    private async Task Clear(HttpContext context)
    {
        context.Response.Cookies.Delete(FlowCookie, CookieOptions(context));
        await context.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
    }
    private CookieOptions CookieOptions(HttpContext context) => new()
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax,
        Path = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "/",
        IsEssential = true, MaxAge = Options.PendingLifetime
    };
    // Fixed binary fields avoid adding reflection-based JSON serialization to this flow.
    private string Protect(Pending state)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(state.UserId); writer.Write(state.SecurityStamp); writer.Write(state.Attempt);
            writer.Write(state.PathBase); writer.Write(state.ReturnUrl); writer.Write(state.IsOffice); writer.Write(state.PersistSession); writer.Write(state.Expires);
        }
        return WebEncoders.Base64UrlEncode(_protector.Protect(stream.ToArray()));
    }
    private Pending Unprotect(string token)
    {
        using var stream = new MemoryStream(_protector.Unprotect(WebEncoders.Base64UrlDecode(token)));
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        var state = new Pending(reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadInt64());
        if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected pending login fields.");
        return state;
    }
    private sealed record Pending(string UserId, string SecurityStamp, string Attempt, string PathBase, string ReturnUrl, bool IsOffice, bool PersistSession, long Expires);
}
