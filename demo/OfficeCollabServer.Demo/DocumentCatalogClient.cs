using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OfficeCollabServer.Demo;

public sealed class DocumentCatalogClient(
    HttpClient httpClient,
    IOptions<CollabServerOptions> options,
    ILogger<DocumentCatalogClient> logger)
{
    public async Task<string?> CreateDocumentAsync(string name, string type, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = new Uri(options.Value.BaseUrl.TrimEnd('/') + "/api/documents");
            using var response = await httpClient.PostAsJsonAsync(endpoint, new { name, type }, cancellationToken);
            if (response.IsSuccessStatusCode) return null;
            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.Conflict => "That filename already exists. Choose another name.",
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

            using var response = await httpClient.GetAsync(endpoint, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Collaboration server returned HTTP {StatusCode} for the document catalog.",
                    (int)response.StatusCode);
                return DocumentCatalogResult.Unavailable;
            }

            var documents = await response.Content.ReadFromJsonAsync<List<CollabDocument>>(JsonOptions, cancellationToken)
                ?? [];
            return DocumentCatalogResult.Success(documents);
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
