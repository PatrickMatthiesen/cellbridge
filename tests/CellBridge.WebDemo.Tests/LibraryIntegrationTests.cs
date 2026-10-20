using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.RegularExpressions;
using CellBridge.AspNetCore;
using CellBridge.Authentication;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CellBridge.WebDemo.Tests;

public sealed class LibraryIntegrationTests
{
    [Fact]
    public async Task AnonymousLibraryRequiresSignInAndApiReturnsUnauthorized()
    {
        await using var fixture = await Fixture.Start();
        using var client = fixture.Client();
        using var page = await client.GetAsync("/library");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.EndsWith("/auth/login?ReturnUrl=%2Flibrary", page.Headers.Location!.AbsoluteUri);
        using var api = await client.GetAsync("/api/documents");
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
    }

    [Fact]
    public async Task RazorCreationUsesTheSignedInActorAndTheSameCatalogAsTheApi()
    {
        await using var fixture = await Fixture.Start();
        using var writer = fixture.Client();
        await fixture.SignIn(writer, "writer");
        var token = await fixture.PageToken(writer);
        using var created = await writer.PostAsync("/library?handler=Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["NewName"] = "Quarter #1",
            ["NewType"] = "xlsx",
        }));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal("/library", created.Headers.Location!.OriginalString);
        using var page = await writer.GetAsync("/library");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Quarter #1.xlsx", html);
        Assert.Contains("ms-excel:ofe|u|https://office.example/shared/Quarter%20%231.xlsx", html);
        Assert.Contains("/css/site.css", html);
        Assert.Contains("no-store", page.Headers.CacheControl!.ToString());
        var documents = await writer.GetFromJsonAsync<DocumentListing[]>("/api/documents");
        Assert.Equal("Quarter #1.xlsx", Assert.Single(documents!).Name);
        var state = Assert.Single(await fixture.Service.Provider.State.ListAsync(0, 100));
        var persisted = await fixture.Service.Provider.State.FindByResourceIdAsync(state.ResourceId);
        Assert.Equal("writer", persisted!.Security.CreatedBy!.Subject);
    }

    [Fact]
    public async Task BothMutationRoutesRequireAntiforgeryAndKeepTheStoreUnchangedWhenRejected()
    {
        await using var fixture = await Fixture.Start();
        using var client = fixture.Client();
        await fixture.SignIn(client, "writer");
        using var api = await client.PostAsJsonAsync("/api/documents", new { name = "unsafe", type = "docx" });
        Assert.Equal(HttpStatusCode.BadRequest, api.StatusCode);
        using var page = await client.PostAsync("/library?handler=Create", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["NewName"] = "unsafe", ["NewType"] = "docx" }));
        Assert.Equal(HttpStatusCode.BadRequest, page.StatusCode);
        Assert.Empty(await fixture.Service.Provider.State.ListAsync(0, 100));
    }

    [Fact]
    public async Task ApiCreationRetainsValidationConflictAndAuthorizedCatalogBehavior()
    {
        await using var fixture = await Fixture.Start();
        using var writer = fixture.Client();
        await fixture.SignIn(writer, "writer");
        var token = await fixture.PageToken(writer);
        async Task<HttpResponseMessage> Create(string name, string type)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/documents")
            { Content = JsonContent.Create(new { name, type }) };
            request.Headers.Add(AuthenticationServices.AntiforgeryHeader, token);
            return await writer.SendAsync(request);
        }
        using var invalid = await Create("../bad", "docx");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var created = await Create("Budget", "xlsx");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/shared/Budget.xlsx", created.Headers.Location!.OriginalString);
        using var duplicate = await Create("Budget", "xlsx");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var other = fixture.Client();
        await fixture.SignIn(other, "other");
        Assert.Empty((await other.GetFromJsonAsync<DocumentListing[]>("/api/documents"))!);
        var html = await other.GetStringAsync("/library");
        Assert.DoesNotContain("Budget.xlsx", html);
    }

    [Fact]
    public async Task ReaderCannotCreateThroughPageOrApi()
    {
        await using var fixture = await Fixture.Start();
        using var client = fixture.Client();
        await fixture.SignIn(client, "reader", canCreate: false);
        var token = await fixture.PageToken(client);
        var html = await client.GetStringAsync("/library");
        Assert.DoesNotContain("class=\"create-form\"", html);
        using var api = await client.PostAsJsonAsync("/api/documents", new { name = "denied", type = "docx" });
        Assert.Equal(HttpStatusCode.Forbidden, api.StatusCode);
        using var page = await client.PostAsync("/library?handler=Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["NewName"] = "denied", ["NewType"] = "docx",
        }));
        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.Empty(await fixture.Service.Provider.State.ListAsync(0, 100));
    }

    private sealed class Fixture(WebApplication app) : IAsyncDisposable
    {
        public CellBridgeDocumentService Service => app.Services.GetRequiredService<CellBridgeDocumentService>();
        public HttpClient Client()
        {
            var client = new HttpClient(new CookieHandler(app.GetTestServer().CreateHandler()));
            client.BaseAddress = new Uri("https://localhost");
            return client;
        }
        public async Task SignIn(HttpClient client, string subject, bool canCreate = true)
        {
            using var response = await client.GetAsync($"/test-sign-in/{subject}?create={canCreate.ToString().ToLowerInvariant()}");
            response.EnsureSuccessStatusCode();
        }
        public async Task<string> PageToken(HttpClient client)
        {
            var html = await client.GetStringAsync("/library");
            return WebUtility.HtmlDecode(Regex.Match(html,
                "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        }
        public static async Task<Fixture> Start()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            { ApplicationName = typeof(WebDemoEndpoints).Assembly.FullName, EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddCellBridge(new(new InMemoryStateStore(), new InMemoryContentStore()), requireDurability: false);
            builder.Services.AddSingleton(new AuthenticationOrigin("https://office.example"));
            builder.Services.AddAuthentication("test-cookie").AddCookie("test-cookie", options =>
            {
                options.LoginPath = "/auth/login";
                options.Events.OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = 401;
                    else context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                { context.Response.StatusCode = 403; return Task.CompletedTask; };
            });
            builder.Services.AddAuthorization();
            builder.Services.AddAntiforgery(options => options.HeaderName = AuthenticationServices.AntiforgeryHeader);
            builder.Services.AddWebDemo();
            var app = builder.Build();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseAntiforgery();
            app.MapWebDemo();
            app.MapGet("/test-sign-in/{subject}", async (HttpContext context, string subject, bool create) =>
            {
                var claims = new List<Claim> { new(CellBridgeActor.SubjectClaim, subject), new(ClaimTypes.Name, subject) };
                if (create) claims.Add(new(CellBridgeActor.CreateClaim, "true"));
                await context.SignInAsync("test-cookie", new ClaimsPrincipal(new ClaimsIdentity(claims, "test-cookie")));
                return Results.Ok();
            });
            await app.StartAsync();
            return new(app);
        }
        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private sealed class CookieHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly CookieContainer cookies = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var cookie = cookies.GetCookieHeader(request.RequestUri!);
            if (cookie.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookie);
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var value in values) cookies.SetCookies(request.RequestUri!, value);
            return response;
        }
    }
}
