namespace OfficeCollabServer.Demo;

public sealed class CollabServerOptions
{
    public string BaseUrl { get; set; } = "https://localhost:7292/";

    /// <summary>
    /// Browser and Office clients may use a different origin than the server-side
    /// HTTP client (for example, an Aspire public endpoint).
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    public Uri GetPublicBaseUri()
    {
        var value = string.IsNullOrWhiteSpace(PublicBaseUrl) ? BaseUrl : PublicBaseUrl;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("The collaboration server public URL is invalid.");
        }

        return new Uri(uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + '/', UriKind.Absolute);
    }
}
