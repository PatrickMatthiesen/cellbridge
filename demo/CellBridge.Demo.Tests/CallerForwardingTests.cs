using System.Net;
using System.Net.Http.Json;
using CellBridge.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CellBridge.Demo.Tests;

public sealed class CallerForwardingTests
{
    [Fact]
    public async Task SharedHttpClientForwardsOnlyEachCallersCredentialsAndPairedCsrf()
    {
        using var http = new HttpClient(new Stub(request =>
        {
            var cookie = request.Headers.GetValues("Cookie").Single();
            Assert.DoesNotContain("unrelated", cookie);
            var name = (awaitName(request));
            Assert.Equal($"CellBridge.Authentication=chunks-2; CellBridge.AuthenticationC1={name}-1; CellBridge.AuthenticationC2={name}-2; CellBridge.Antiforgery={name}-csrf", cookie);
            Assert.Equal(name + "-token", request.Headers.GetValues(AuthenticationServices.AntiforgeryHeader).Single());
            Assert.Equal("https", request.Headers.GetValues("X-Forwarded-Proto").Single());
            return new(HttpStatusCode.Created);
        }));
        static string awaitName(HttpRequestMessage request) => request.Content!.ReadFromJsonAsync<Create>().GetAwaiter().GetResult()!.Name;
        DocumentCatalogClient Caller(string name)
        {
            var context = new DefaultHttpContext();
            context.Request.Scheme = "https";
            context.Request.Headers.Cookie = $"CellBridge.Authentication=chunks-2; CellBridge.AuthenticationC1={name}-1; CellBridge.AuthenticationC2={name}-2; CellBridge.Antiforgery={name}-csrf; unrelated=secret";
            context.Request.Headers[AuthenticationServices.AntiforgeryHeader] = name + "-token";
            return new(http, Options.Create(new CollabServerOptions()), NullLogger<DocumentCatalogClient>.Instance,
                new HttpContextAccessor { HttpContext = context });
        }
        // Build/call independently so HttpContextAccessor's AsyncLocal context
        // corresponds to each request while the underlying client is pooled.
        await Task.WhenAll(Task.Run(() => Caller("writer").CreateDocumentAsync("writer", "docx", default)),
            Task.Run(() => Caller("reader").CreateDocumentAsync("reader", "docx", default)));
        Assert.False(http.DefaultRequestHeaders.Contains("Cookie"));
        Assert.False(http.DefaultRequestHeaders.Contains(AuthenticationServices.AntiforgeryHeader));
    }

    [Fact]
    public async Task CatalogFollowsVisibleOffsetsUsingCallerOnEveryPage()
    {
        int calls = 0;
        using var http = new HttpClient(new Stub(request =>
        {
            Assert.Equal("CellBridge.Authentication=reader", request.Headers.GetValues("Cookie").Single());
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[]
            { new CollabDocument("/shared/" + calls, "file", "application/octet-stream", 1, 1, DateTime.UtcNow, 0) }) };
            if (calls++ == 0) response.Headers.Add("X-CellBridge-Next-Offset", "1");
            else Assert.Equal("?offset=1", request.RequestUri!.Query);
            return response;
        }));
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "CellBridge.Authentication=reader";
        var client = new DocumentCatalogClient(http, Options.Create(new CollabServerOptions()), NullLogger<DocumentCatalogClient>.Instance,
            new HttpContextAccessor { HttpContext = context });
        var result = await client.GetDocumentsAsync(default);
        Assert.True(result.IsAvailable);
        Assert.Equal(2, result.Documents.Count);
    }

    private sealed record Create(string Name, string Type);
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request));
    }
}
