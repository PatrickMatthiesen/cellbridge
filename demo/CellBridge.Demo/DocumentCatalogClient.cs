using CellBridge.Authentication;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CellBridge.Demo;

public sealed class DocumentCatalogClient(
    HttpClient httpClient,
    IOptions<CollabServerOptions> options,
    ILogger<DocumentCatalogClient> logger,
    IHttpContextAccessor? caller = null)
{
    public async Task<string?> CreateDocumentAsync(string name, string type, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = new Uri(options.Value.BaseUrl.TrimEnd('/') + "/api/documents");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(new { name, type }) };
            ForwardCaller(request, mutation: true);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return null;
            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.Conflict => "That filename already exists. Choose another name.",
                System.Net.HttpStatusCode.Forbidden => "You do not have permission to create documents.",
                System.Net.HttpStatusCode.Unauthorized => "Your sign-in expired. Sign in again.",
                System.Net.HttpStatusCode.BadRequest => "Use a valid filename and choose Word, Excel or PowerPoint. Avoid slashes and special filename characters.",
                _ => "The server could not create the file. Please try again.",
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "The request timed out. Refresh the library before retrying to check whether the file was created.";
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Unable to create a document on the collaboration server.");
            return "The server could not be reached. Refresh the library before retrying to check whether the file was created.";
        }
    }

    private void ForwardCaller(HttpRequestMessage request, bool mutation)
    {
        var context = caller?.HttpContext;
        if (context is null) return;
        var cookies = context.Request.Cookies.Where(p => p.Key == AuthenticationServices.CookieName ||
            IsCookieChunk(p.Key) || mutation && p.Key == AuthenticationServices.AntiforgeryCookie)
            .Select(p => p.Key + "=" + p.Value);
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", context.Request.Scheme);
        if (mutation)
        {
            var token = context.Request.Headers[AuthenticationServices.AntiforgeryHeader].ToString();
            if (token.Length == 0 && context.Request.HasFormContentType)
                token = context.Request.Form["__RequestVerificationToken"].ToString();
            request.Headers.TryAddWithoutValidation(AuthenticationServices.AntiforgeryHeader, token);
        }
    }

    private static bool IsCookieChunk(string name) => name.StartsWith(AuthenticationServices.CookieName + "C", StringComparison.Ordinal) &&
        int.TryParse(name.AsSpan(AuthenticationServices.CookieName.Length + 1), out var chunk) && chunk > 0;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<DocumentCatalogResult> GetDocumentsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = new Uri(options.Value.BaseUrl, UriKind.Absolute);
            var endpoint = new Uri(baseUrl.AbsoluteUri.EndsWith('/')
                ? baseUrl.AbsoluteUri + "api/documents"
                : baseUrl.AbsoluteUri + "/api/documents");

            var documents = new List<CollabDocument>();
            int offset = 0;
            while (true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    offset == 0 ? endpoint : new Uri(endpoint + "?offset=" + offset));
                ForwardCaller(request, mutation: false);
                using var response = await httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Collaboration server returned HTTP {StatusCode} for the document catalog.", (int)response.StatusCode);
                    return DocumentCatalogResult.Unavailable;
                }
                documents.AddRange(await response.Content.ReadFromJsonAsync<List<CollabDocument>>(JsonOptions, cancellationToken) ?? []);
                if (!response.Headers.TryGetValues("X-CellBridge-Next-Offset", out var values))
                    return DocumentCatalogResult.Success(documents);
                if (!int.TryParse(values.SingleOrDefault(), out var next) || next <= offset)
                    return DocumentCatalogResult.Unavailable;
                offset = next;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(exception, "The collaboration server document catalog request timed out.");
            return DocumentCatalogResult.Unavailable;
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Unable to reach the collaboration server document catalog.");
            return DocumentCatalogResult.Unavailable;
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "The collaboration server returned an invalid document catalog.");
            return DocumentCatalogResult.Unavailable;
        }
        catch (UriFormatException exception)
        {
            logger.LogWarning(exception, "The collaboration server URL is invalid.");
            return DocumentCatalogResult.Unavailable;
        }
    }
}

public sealed record CollabDocument(
    string Path,
    string Name,
    string ContentType,
    long Size,
    uint Version,
    DateTime LastModifiedUtc,
    int ActiveEditors);

public sealed record DocumentCatalogResult(IReadOnlyList<CollabDocument> Documents, bool IsAvailable)
{
    public static DocumentCatalogResult Unavailable { get; } = new([], false);

    public static DocumentCatalogResult Success(IReadOnlyList<CollabDocument> documents) => new(documents, true);
}
