using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace CellBridge.AspNetCore;

internal static class HttpDocumentPath
{
    public static bool TryRequestPath(HttpContext context, out string path)
    {
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(raw)) raw = (context.Request.PathBase + context.Request.Path).ToUriComponent();
        if (!DocumentUrlPaths.TryDecode(raw, out path, allowTrailingSlash: true)) return false;
        return DocumentUrlPaths.StripMount(ref path, context.Request.PathBase.Value ?? "");
    }
}
