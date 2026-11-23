using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace CellBridge.AspNetCore;

internal static class CellBridgePublicOrigin
{
    public static string? Resolve(HttpContext context) =>
        Resolve(context.RequestServices, context.Request.Scheme, context.Request.Host);

    internal static string? Resolve(IServiceProvider services, string scheme, HostString requestHost)
    {
        var fixedOrigin = services.GetService<CellBridgeOfficeFormsAuthentication.OfficeFormsRegistration>()?.PublicOrigin
            ?? services.GetRequiredService<CellBridgeOptions>().PublicOrigin;
        if (fixedOrigin is not null) return fixedOrigin.TrimEnd('/');
        var host = requestHost.Host;
        var configured = services.GetService<IConfiguration>()?["AllowedHosts"]?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(h => h != "*").ToArray() ?? [];
        var allowed = configured.Length > 0
            ? HostString.MatchesAny(requestHost.Value, configured.Select(h => new StringSegment(h)).ToArray())
            : host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
              IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
        return allowed && scheme is "http" or "https" ? $"{scheme}://{requestHost}" : null;
    }

    public static void Validate(string? value)
    {
        if (value is null) return;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("PublicOrigin must be an HTTPS origin without a path or credentials.");
    }
}
