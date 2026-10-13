using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace CellBridge.AspNetCore;

internal static class CellBridgePublicOrigin
{
    public static string? Resolve(HttpContext context)
    {
        var fixedOrigin = context.RequestServices.GetService<CellBridgeOfficeFormsAuthentication.OfficeFormsRegistration>()?.PublicOrigin
            ?? context.RequestServices.GetRequiredService<CellBridgeOptions>().PublicOrigin;
        if (fixedOrigin is not null) return fixedOrigin.TrimEnd('/');
        var host = context.Request.Host.Host;
        var configured = context.RequestServices.GetService<IConfiguration>()?["AllowedHosts"]?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(h => h != "*").ToArray() ?? [];
        var allowed = configured.Length > 0
            ? HostString.MatchesAny(context.Request.Host.Value, configured.Select(h => new StringSegment(h)).ToArray())
            : host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
              IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
        return allowed && context.Request.Scheme is "http" or "https"
            ? $"{context.Request.Scheme}://{context.Request.Host}" : null;
    }

    public static void Validate(string? value)
    {
        if (value is null) return;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("PublicOrigin must be an HTTPS origin without a path or credentials.");
    }
}
