using Microsoft.AspNetCore.Http;

namespace CellBridge.AspNetCore;

internal static class DocumentDelivery
{
    public static void Apply(HttpContext context, string path)
    {
        // Only Office formats are returned with their native media types. Arbitrary
        // imported or renamed content must not execute in the application's origin.
        var type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => null,
        };
        context.Response.ContentType = type ?? "application/octet-stream";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
        if (type is null || context.Request.Query["download"] == "true")
            context.Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
            { FileNameStar = Path.GetFileName(path) }.ToString();
    }
}
