using System.Net;
using System.Text;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.FssHttp.Tests;

public sealed class DocumentDeletionEndpointTests
{
    [Fact]
    public async Task DeleteRequiresAntiforgeryAndThenTombstonesTheAuthorizedDocument()
    {
        await using var fixture = await Fixture.Start();
        using var client = TestActor.CreateClient(fixture.Server, "writer");
        var missingToken = await client.SendAsync(DeleteRequest(fixture.State, includeToken: false));
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);

        var response = await fixture.DeleteAsync(client, fixture.State, "writer");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var tombstone = await fixture.Provider.Documents.FindAsync(new DocumentIdentity.Resource(
            fixture.State.ResourceId, IncludeDeleted: true));
        Assert.NotNull(tombstone);
        Assert.True(tombstone.IsDeleted);
    }

    [Fact]
    public async Task DeleteHidesDocumentsFromNonOwnerEvenWhenTheyCanEdit()
    {
        await using var fixture = await Fixture.Start();
        await TestStorage.SetPermissionsAsync(fixture.Provider, fixture.State.ResourceId,
            fixture.State.Security with
            {
                Grants = fixture.State.Security.Grants.Add("tests:editor", DocumentAccess.Read | DocumentAccess.Write),
            });
        var current = await fixture.Provider.Documents.ReadControlAsync(fixture.State.ResourceId);
        Assert.NotNull(current);
        using var client = TestActor.CreateClient(fixture.Server, "editor");

        var response = await fixture.DeleteAsync(client,
            fixture.State with
            {
                LifecycleGeneration = current.Head.LifecycleGeneration + 1,
                StateVersion = current.Head.StateVersion + 1,
            }, "editor");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var stillThere = await fixture.Provider.Documents.FindAsync(new DocumentIdentity.Resource(fixture.State.ResourceId));
        Assert.NotNull(stillThere);
        Assert.False(stillThere.IsDeleted);
    }

    private static HttpRequestMessage DeleteRequest(DocumentHead state, bool includeToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{state.ResourceId:D}")
        {
            Content = new StringContent($"{{\"expectedLifecycleGeneration\":{state.LifecycleGeneration},\"expectedStateVersion\":{state.StateVersion}}}",
                Encoding.UTF8, "application/json"),
        };
        if (includeToken) request.Headers.Add("X-CSRF-TOKEN", "token-present");
        return request;
    }

    private sealed class Fixture(WebApplication app, TestServer server, StorageProvider provider, DocumentHead state)
        : IAsyncDisposable
    {
        public TestServer Server => server;
        public StorageProvider Provider => provider;
        public DocumentHead State => state;

        public async Task<HttpResponseMessage> DeleteAsync(HttpClient client, DocumentHead document, string subject)
        {
            using var csrfClient = TestActor.CreateClient(server, subject);
            using var csrfResponse = await csrfClient.GetAsync("/test-antiforgery");
            Assert.Equal(HttpStatusCode.OK, csrfResponse.StatusCode);
            var token = await csrfResponse.Content.ReadAsStringAsync();
            var setCookie = Assert.Single(csrfResponse.Headers.GetValues("Set-Cookie"));
            using var request = DeleteRequest(document, includeToken: false);
            request.Headers.Add("X-CSRF-TOKEN", token);
            request.Headers.Add("Cookie", setCookie.Split(';', 2)[0]);
            return await client.SendAsync(request);
        }

        public static async Task<Fixture> Start()
        {
            var provider = TestStorage.Memory();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            TestActor.Register(builder.Services);
            builder.Services.AddAuthorization();
            builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
            builder.Services.AddCellBridge(provider, requireDurability: false);
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGet("/test-antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
                Results.Text(antiforgery.GetAndStoreTokens(context).RequestToken!)).RequireAuthorization();
            app.MapCellBridgeDocumentDeletion();
            var service = app.Services.GetRequiredService<CellBridgeDocumentService>();
            var state = (await service.CreateAsync("/shared/delete-endpoint.docx", [1, 2, 3], TestActor.Value))!;
            var server = app.GetTestServer();
            await app.StartAsync();
            return new(app, server, provider, state);
        }

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }
}
