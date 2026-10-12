using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace CellBridge.AspNetCore;

// URL inputs are escaped. Storage paths and MoveDocumentAsync inputs are decoded.
internal static class DocumentUrlPaths
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static bool TryDecode(string url, out string path, bool allowTrailingSlash = false)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(url)) return false;
        var escaped = url;
        if (url.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) return false;
            // Inspect the original path before Uri removes dot segments.
            var start = url.IndexOfAny(['/', '?', '#'], url.IndexOf("://", StringComparison.Ordinal) + 3);
            if (start < 0 || url[start] != '/') return false;
            escaped = url[start..];
        }
        if (escaped.Contains('#')) return false;
        escaped = escaped.Split('?', 2)[0];
        if (!escaped.StartsWith('/')) escaped = "/" + escaped;
        if (allowTrailingSlash && escaped.EndsWith('/')) escaped = escaped[..^1];
        var segments = escaped.Split('/');
        if (segments.Length < 2) return false;
        var decoded = new List<string>();
        foreach (var segment in segments.Skip(1))
        {
            if (segment.Length == 0) return false;
            for (var i = 0; i < segment.Length; i++)
            {
                if (segment[i] != '%') continue;
                if (i + 2 >= segment.Length || !Uri.IsHexDigit(segment[i + 1]) || !Uri.IsHexDigit(segment[i + 2])) return false;
                var escape = segment.Substring(i, 3);
                if (escape.Equals("%2f", StringComparison.OrdinalIgnoreCase) ||
                    escape.Equals("%5c", StringComparison.OrdinalIgnoreCase)) return false;
                i += 2;
            }
            // Uri.UnescapeDataString leaves invalid UTF-8 escapes unchanged. Reject
            // them instead of treating an invalid encoding as a literal percent name.
            var valueBuilder = new StringBuilder();
            for (var i = 0; i < segment.Length;)
            {
                if (segment[i] != '%') { valueBuilder.Append(segment[i++]); continue; }
                var bytes = new List<byte>();
                while (i < segment.Length && segment[i] == '%')
                {
                    bytes.Add(Convert.ToByte(segment.Substring(i + 1, 2), 16));
                    i += 3;
                }
                try { valueBuilder.Append(StrictUtf8.GetString(bytes.ToArray())); }
                catch (DecoderFallbackException) { return false; }
            }
            var value = valueBuilder.ToString();
            if (value is "." or ".." || value.Contains('\\') || value.Any(char.IsControl)) return false;
            decoded.Add(value);
        }
        path = "/" + string.Join('/', decoded);
        return true;
    }

    public static bool TryRequestPath(HttpContext context, out string path)
    {
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(raw)) raw = (context.Request.PathBase + context.Request.Path).ToUriComponent();
        if (!TryDecode(raw, out path, allowTrailingSlash: true)) return false;
        return StripMount(ref path, context.Request.PathBase.Value ?? "");
    }

    public static bool StripMount(ref string path, string pathBase)
    {
        if (pathBase.Length == 0) return true;
        if (!path.StartsWith(pathBase + "/", StringComparison.OrdinalIgnoreCase)) return false;
        path = path[pathBase.Length..];
        return true;
    }

    public static string ValidateMount(string pathBase)
    {
        if (pathBase.Length == 0) return "";
        if (!pathBase.StartsWith('/') || pathBase.EndsWith('/') || pathBase.Contains('?') || pathBase.Contains('#') ||
            pathBase.Split('/').Skip(1).Any(s => s.Length == 0 || s is "." or ".." || s.Contains('\\') || s.Any(char.IsControl)))
            throw new ArgumentException("An application-rooted decoded mount path without a trailing slash is required.", nameof(pathBase));
        return string.Join('/', pathBase.Split('/').Select(Uri.EscapeDataString));
    }
}
