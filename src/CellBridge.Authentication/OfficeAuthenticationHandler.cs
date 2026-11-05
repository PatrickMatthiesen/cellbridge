using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using CellBridge.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

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
        // Office can explicitly probe authentication without reusing its cookie.
        // This changes challenge negotiation only; it never grants access.
        var ignoreCookie = scheme == IdentityConstants.ApplicationScheme &&
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
                Response.OnStarting(() =>
                {
                    if (Response.StatusCode == StatusCodes.Status200OK)
                        EntraAuthentication.WriteChallenge(Context,
                            Context.RequestServices.GetRequiredService<EntraAuthenticationOptions>(), StatusCodes.Status200OK);
                    return Task.CompletedTask;
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

    internal static void Authentication(HttpContext context, string scheme, AuthenticateResult result)
    {
        var logger = Logger(context);
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        var cookie = CookieName(context);
        var present = context.Request.Cookies.ContainsKey(cookie);
        var outcome = result.Succeeded ? "succeeded" : result.Failure is not null ? "rejected" : "no-result";
        var fingerprint = Fingerprint(ReadCookie(cookie, context.Request.Cookies.Count,
            name => context.Request.Cookies[name]));
        // Failed cookie authentication does not expose the decrypted ticket.
        // Do not decrypt it again just to fill a diagnostic field.
        var expiry = scheme == IdentityConstants.ApplicationScheme
            ? result.Ticket?.Properties.ExpiresUtc : null;
        logger.LogDebug(
            "Office authentication {RequestId} {Method} {Path}; credential {Credential}; session cookie present {CookiePresent}; cookie header present {CookieHeaderPresent}; cookie name {CookieName}; scheme {Scheme}; outcome {Outcome}; failure {FailureType}; cookie SHA256 {CookieSha256}; cookie length {CookieLength}; ticket expiry UTC {CookieTicketExpiresUtc}",
            context.TraceIdentifier, context.Request.Method, context.Request.Path.Value,
            OfficeAuthenticationHandler.CredentialCategory(context.Request), present,
            context.Request.Headers.ContainsKey("Cookie"), cookie, scheme, outcome,
            result.Failure?.GetType().Name ?? "none", fingerprint.Sha256, fingerprint.Length,
            expiry?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
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
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            DateTimeOffset? expiry = null;
            foreach (var header in context.Response.Headers.SetCookie)
            {
                if (!SetCookieHeaderValue.TryParse(header, out var parsed)) continue;
                var name = parsed.Name.ToString();
                if (name != cookie && !name.StartsWith(cookie + "C", StringComparison.Ordinal)) continue;
                values[name] = parsed.Value.ToString();
                if (name == cookie) expiry = parsed.Expires;
            }
            var fingerprint = Fingerprint(ReadCookie(cookie, values.Count,
                name => values.GetValueOrDefault(name)));
            logger.LogDebug("Office response {RequestId} {Method} {Path}; status {Status}; session Set-Cookie issued {CookieIssued}; cookie SHA256 {CookieSha256}; cookie length {CookieLength}; cookie Expires UTC {CookieExpiresUtc}",
                requestId, method, path, context.Response.StatusCode, issued,
                fingerprint.Sha256, fingerprint.Length,
                expiry?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            return Task.CompletedTask;
        });
    }

    private static (string? Sha256, int? Length) Fingerprint(string? value)
    {
        if (value is null) return (null, null);
        var bytes = Encoding.UTF8.GetBytes(value);
        return (Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);
    }

    private static string? ReadCookie(string name, int availableParts, Func<string, string?> read)
    {
        var value = read(name);
        if (value is null || !value.StartsWith("chunks-", StringComparison.Ordinal)) return value;
        // Match ChunkingCookieManager's wire representation. Incomplete chunks
        // have no fingerprint; the shared marker alone does not identify a ticket.
        if (!int.TryParse(value.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
            count < 1 || count >= availableParts) return null;
        var combined = new StringBuilder();
        for (var index = 1; index <= count; index++)
        {
            var part = read(name + "C" + index.ToString(CultureInfo.InvariantCulture));
            if (part is null) return null;
            combined.Append(part);
        }
        return combined.ToString();
    }

    private static string CookieName(HttpContext context) => context.RequestServices
        .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme).Cookie.Name!;

    private static ILogger Logger(HttpContext context) => context.RequestServices
        .GetRequiredService<ILoggerFactory>().CreateLogger("CellBridge.Authentication.Entra");
}
