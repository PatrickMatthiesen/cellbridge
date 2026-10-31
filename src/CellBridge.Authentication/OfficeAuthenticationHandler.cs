using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CellBridge.Authentication;

/// <summary>Routes Office credentials without treating an empty Bearer probe as a token.</summary>
internal sealed class OfficeAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var category = CredentialCategory(Request);
        var scheme = category is "absent" or "empty-bearer"
            ? IdentityConstants.ApplicationScheme : EntraAuthentication.BearerScheme;
        OfficeAuthenticationDiagnostics.ObserveResponse(Context);
        var result = await Context.AuthenticateAsync(scheme);
        OfficeAuthenticationDiagnostics.Authentication(Context, scheme, result);
        return result;
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        Context.ChallengeAsync(EntraAuthentication.BearerScheme, properties);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        Context.ForbidAsync(EntraAuthentication.BearerScheme, properties);

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

    internal static void Authentication(HttpContext context, string scheme, AuthenticateResult result)
    {
        var cookie = CookieName(context);
        var present = context.Request.Cookies.ContainsKey(cookie);
        var outcome = result.Succeeded ? "succeeded" : result.Failure is not null ? "rejected" : "no-result";
        Logger(context).LogDebug(
            "Office authentication {RequestId} {Method} {Path}; credential {Credential}; session cookie present {CookiePresent}; cookie header present {CookieHeaderPresent}; cookie name {CookieName}; scheme {Scheme}; outcome {Outcome}; failure {FailureType}",
            context.TraceIdentifier, context.Request.Method, context.Request.Path.Value,
            OfficeAuthenticationHandler.CredentialCategory(context.Request), present,
            context.Request.Headers.ContainsKey("Cookie"), cookie, scheme, outcome,
            result.Failure?.GetType().Name ?? "none");
    }

    internal static void ObserveResponse(HttpContext context)
    {
        if (!context.Items.TryAdd(ResponseObserved, true)) return;
        var cookie = CookieName(context);
        var logger = Logger(context);
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
            logger.LogDebug("Office response {RequestId} {Method} {Path}; status {Status}; session Set-Cookie issued {CookieIssued}",
                requestId, method, path, context.Response.StatusCode, issued);
            return Task.CompletedTask;
        });
    }

    private static string CookieName(HttpContext context) => context.RequestServices
        .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme).Cookie.Name!;

    private static ILogger Logger(HttpContext context) => context.RequestServices
        .GetRequiredService<ILoggerFactory>().CreateLogger("CellBridge.Authentication.Entra");
}
