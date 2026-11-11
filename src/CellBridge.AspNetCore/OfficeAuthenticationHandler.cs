using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CellBridge.AspNetCore;

/// <summary>Routes Office credentials without treating an empty Bearer probe as a token.</summary>
internal sealed class OfficeAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private string CookieScheme => Context.RequestServices.GetRequiredService<EntraRegistration>().CookieScheme;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var category = CredentialCategory(Request);
        var scheme = category is "absent" or "empty-bearer"
            ? CookieScheme : EntraAuthentication.BearerScheme;
        OfficeAuthenticationDiagnostics.ObserveResponse(Context);
        // Office can explicitly probe authentication without reusing its cookie.
        // This changes challenge negotiation only; it never grants access.
        var ignoreCookie = scheme == CookieScheme &&
            IsForcedAuthenticationProbe() && HeaderIs("IgnoreCookieAuthentication", "t");
        var result = ignoreCookie ? AuthenticateResult.NoResult() : await Context.AuthenticateAsync(scheme);
        OfficeAuthenticationDiagnostics.Authentication(Context, scheme, result);
        return result;
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (await Context.TryCellBridgeOfficeFormsFallbackAsync(properties)) return;
        var category = CredentialCategory(Request);
        if (category is "absent" or "empty-bearer")
        {
            if (IsForcedAuthenticationProbe())
            {
                await Context.ChallengeAsync(EntraAuthentication.BearerScheme, properties);
                return;
            }
            // Office also requests cookie-uri metadata on ordinary, repeated
            // capability probes. Challenging every such probe restarts native
            // authentication and document queries even after cookie acquisition.
            if (HttpMethods.IsOptions(Request.Method) && HeaderIs("X-MS-CookieUri-Requested", "t"))
            {
                Response.OnStarting(async () =>
                {
                    if (Response.StatusCode == StatusCodes.Status200OK)
                        await EntraAuthentication.WriteChallengeAsync(Context,
                            Context.RequestServices.GetRequiredService<EntraRegistration>(), StatusCodes.Status200OK);
                });
            }
            if (await Context.TryHandleCellBridgeOfficeDiscoveryAsync()) return;
        }
        await Context.ChallengeAsync(EntraAuthentication.BearerScheme, properties);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        Context.ForbidAsync(EntraAuthentication.BearerScheme, properties);

    private bool IsForcedAuthenticationProbe() => HttpMethods.IsOptions(Request.Method) &&
        HeaderIs("X-IDCRL_OPTIONS", "force-auth-challenge");

    private bool HeaderIs(string name, string value) => Request.Headers.TryGetValue(name, out var values) &&
        values.Count == 1 && string.Equals(values[0]?.Trim(), value, StringComparison.OrdinalIgnoreCase);

    internal static string CredentialCategory(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Authorization", out var headers)) return "absent";
        if (headers.Count != 1) return "malformed-or-other";
        var value = headers[0]?.Trim();
        if (string.Equals(value, "Bearer", StringComparison.OrdinalIgnoreCase)) return "empty-bearer";
        return value?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            ? "bearer" : "malformed-or-other";
    }
}

internal static class OfficeAuthenticationDiagnostics
{
    private static readonly object ResponseObserved = new();
    private static readonly object AuthenticatedSubject = new();

    internal static void Authentication(HttpContext context, string scheme, AuthenticateResult result)
    {
        var logger = Logger(context);
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        var subject = result.Succeeded && EntraAuthentication.IsMappedPrincipal(result.Principal)
            ? result.Principal!.FindFirstValue(CellBridgeActor.SubjectClaim) : null;
        context.Items[AuthenticatedSubject] = subject;
        var cookie = CookieName(context);
        var present = context.Request.Cookies.ContainsKey(cookie);
        var outcome = result.Succeeded ? "succeeded" : result.Failure is not null ? "rejected" : "no-result";
        logger.LogDebug(
            "Office authentication {RequestId} {Method} {Path}; credential {Credential}; session cookie present {CookiePresent}; scheme {Scheme}; outcome {Outcome}; subject {Subject}; failure {FailureType}",
            context.TraceIdentifier, context.Request.Method, context.Request.Path.Value,
            OfficeAuthenticationHandler.CredentialCategory(context.Request), present,
            scheme, outcome, subject, result.Failure?.GetType().Name ?? "none");
    }

    internal static void ObserveResponse(HttpContext context)
    {
        if (!context.Items.TryAdd(ResponseObserved, true)) return;
        var cookie = CookieName(context);
        var logger = Logger(context);
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        var requestId = context.TraceIdentifier;
        var method = context.Request.Method;
        var path = context.Request.Path.Value;
        // Cookie middleware can append renewal headers in its own OnStarting
        // callback. Observe the completed response so that this reports the
        // final headers rather than an earlier snapshot.
        context.Response.OnCompleted(() =>
        {
            var issued = context.Response.Headers.SetCookie.Any(value =>
                value?.StartsWith(cookie + "=", StringComparison.Ordinal) == true);
            context.Items.TryGetValue(AuthenticatedSubject, out var subject);
            logger.LogDebug("Office response {RequestId} {Method} {Path}; status {Status}; subject {Subject}; session Set-Cookie issued {CookieIssued}",
                requestId, method, path, context.Response.StatusCode, subject, issued);
            return Task.CompletedTask;
        });
    }

    private static string CookieName(HttpContext context) => context.RequestServices
        .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(context.RequestServices.GetRequiredService<EntraRegistration>().CookieScheme).Cookie.Name!;

    private static ILogger Logger(HttpContext context) => context.RequestServices
        .GetRequiredService<ILoggerFactory>().CreateLogger("CellBridge.Authentication.Entra");
}
