using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace OfficeCollabServer.Demo.Tests;

public sealed class DocumentCreationClientTests
{
    [Theory]
    [InlineData(HttpStatusCode.Created, null)]
    [InlineData(HttpStatusCode.Conflict, "already exists")]
    [InlineData(HttpStatusCode.BadRequest, "valid filename")]
    [InlineData(HttpStatusCode.InternalServerError, "could not create")]
    public async Task SendsNameAndTypeAndReportsCreationResult(HttpStatusCode status, string? expectedError)
    {
        using var http = new HttpClient(new Stub(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://localhost:7292/api/documents", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("Budget", body.RootElement.GetProperty("name").GetString());
            Assert.Equal("xlsx", body.RootElement.GetProperty("type").GetString());
            return new HttpResponseMessage(status);
        }));
        var client = new DocumentCatalogClient(http, Options.Create(new CollabServerOptions()),
            NullLogger<DocumentCatalogClient>.Instance);
        var error = await client.CreateDocumentAsync("Budget", "xlsx", CancellationToken.None);
        if (expectedError is null) Assert.Null(error);
        else Assert.Contains(expectedError, error);
    }

    private sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
