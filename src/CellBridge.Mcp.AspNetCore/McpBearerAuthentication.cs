using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using CellBridge.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CellBridge.Mcp.AspNetCore;

internal sealed class McpBearerAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> schemes, ILoggerFactory logger, UrlEncoder encoder,
    CellBridgeMcpOptions options, TimeProvider timeProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemes, logger, encoder)
{
    public const string SchemeName = "CellBridge.Mcp.Bearer";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization;
        if (Request.Headers.ContainsKey("Cookie") || authorization.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(authorization[0], out var header) ||
            !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter))
            return AuthenticateResult.NoResult();

        var result = await Context.AuthenticateAsync(options.BearerScheme);
        if (result.Succeeded && GetExpiration(result) is { } expiresAt && expiresAt <= timeProvider.GetUtcNow())
            return AuthenticateResult.Fail("The bearer credential has expired.");
        return result;
    }

    internal static DateTimeOffset? GetExpiration(AuthenticateResult result)
    {
        var expiresAt = result.Properties?.ExpiresUtc;
        var expiration = result.Principal?.Identities.FirstOrDefault(identity => identity.IsAuthenticated &&
                identity.HasClaim(claim => claim.Type == CellBridgeActor.SubjectClaim))?.FindFirst("exp")?.Value;
        if (long.TryParse(expiration, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds) &&
            seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() &&
            seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            var claimExpiration = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (expiresAt is null || claimExpiration < expiresAt.Value) expiresAt = claimExpiration;
        }
        return expiresAt;
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var origin = McpProtectedResource.GetOrigin(Context);
        if (!McpProtectedResource.IsAllowedOrigin(Context, origin))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        WriteBearerHeader(origin);
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        var origin = McpProtectedResource.GetOrigin(Context);
        if (McpProtectedResource.IsAllowedOrigin(Context, origin))
            WriteBearerHeader(origin, "insufficient_scope");
        return Task.CompletedTask;
    }

    private void WriteBearerHeader(string? origin, string? error = null)
    {
        var parameters = new List<string>(3);
        if (error is not null)
            parameters.Add($"error=\"{error}\"");
        var metadataUrl = options.AuthorizationServer is not null && origin is not null
            && McpProtectedResource.ResolveEndpointPath(Context) is { } endpointPath
            ? origin + Request.PathBase.ToUriComponent() + McpProtectedResource.RootMetadataPath +
                endpointPath
            : null;
        if (metadataUrl is not null)
            parameters.Add($"resource_metadata=\"{metadataUrl}\"");
        if (options.ScopesSupported.Count > 0)
            parameters.Add($"scope=\"{string.Join(' ', options.ScopesSupported)}\"");
        Response.Headers.WWWAuthenticate = parameters.Count == 0
            ? "Bearer"
            : "Bearer " + string.Join(", ", parameters);
    }
}

internal sealed class McpProtectedResource(RoutePattern pattern, TemplateBinderFactory binderFactory)
{
    public const string RootMetadataPath = "/.well-known/oauth-protected-resource";
    // The request already matched the route. Bind its captured values without outbound policies.
    private readonly TemplateBinder _binder = binderFactory.Create(new RouteTemplate(pattern), new RouteValueDictionary(pattern.Defaults));

    public static string? ResolveEndpointPath(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<McpProtectedResource>()?.BindPath(context.Request.RouteValues);

    public string? BindPath(RouteValueDictionary routeValues)
    {
        var values = new RouteValueDictionary();
        foreach (var parameter in pattern.Parameters)
            if (routeValues.TryGetValue(parameter.Name, out var value))
                values.Add(parameter.Name, value);
        return _binder.GetValues(null, values) is { } bound ? _binder.BindValues(bound.AcceptedValues) : null;
    }

    public static string? GetOrigin(HttpContext context)
    {
        var value = CellBridgeInProcessHost.ResolvePublicOrigin(context);
        return value is null ? null : ParseOrigin(value)?.GetLeftPart(UriPartial.Authority);
    }

    public static bool IsAllowedOrigin(HttpContext context, string? expectedOrigin) =>
        !context.Request.Headers.TryGetValue("Origin", out var origins) ||
        origins.Count == 1 && ParseOrigin(origins.ToString()) is { } origin &&
        string.Equals(origin.GetLeftPart(UriPartial.Authority), expectedOrigin, StringComparison.OrdinalIgnoreCase);

    private static Uri? ParseOrigin(string value)
    {
        var separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0 || value.AsSpan(separator + 3).ContainsAny("/?#\\") ||
            value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0 || uri.Host.Length == 0)
            return null;
        return uri;
    }
}
