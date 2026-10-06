using System.Net;
using System.Text.RegularExpressions;
using CellBridge.DocumentLibrary;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace CellBridge.DocumentLibrary.Tests;

public sealed class OfficeAuthenticationTests
{
    private static readonly Uri Origin = new("https://dev.tail5a9a42.ts.net:8446");

    [Theory]
    [InlineData("OPTIONS", "/shared/")]
    [InlineData("HEAD", "/shared/welcome.docx")]
    [InlineData("GET", "/shared/welcome.docx")]
    [InlineData("POST", "/_vti_bin/cellstorage.svc")]
    [InlineData("POST", "/shared/welcome.docx/_vti_bin/cellstorage.svc")]
    [InlineData("GET", "/_cellbridge/history/00000000-0000-0000-0000-000000000001/1/1")]
    public async Task AnonymousOfficeRequestsReceiveFormsChallengeInsteadOfLoginHtml(string method, string path)
    {
        using var files = new TemporaryDirectory();
        await using var factory = new LibraryFactory(files.Path);
        using var client = Client(factory);
        using var response = await client.SendAsync(new(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(Origin + "local-login?ReturnUrl=%2F_cellbridge%2Fauth%2Fcomplete",
            response.Headers.GetValues("X-FORMS_BASED_AUTH_REQUIRED").Single());
        Assert.Equal(Origin + "_cellbridge/auth/complete",
            response.Headers.GetValues("X-FORMS_BASED_AUTH_RETURN_URL").Single());
        Assert.Equal("800x600", response.Headers.GetValues("X-FORMS_BASED_AUTH_DIALOG_SIZE").Single());
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task OfficeLoginCompletesAndItsCookieOpensTheDocumentWithoutRelaxingPermissions()
    {
        using var files = new TemporaryDirectory();
        await using var factory = new LibraryFactory(files.Path);
        using var owner = Client(factory);
        using var anonymousComplete = await owner.GetAsync("/_cellbridge/auth/complete");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousComplete.StatusCode);
        using var anonymousHead = await owner.SendAsync(new(HttpMethod.Head, "/_cellbridge/auth/complete"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousHead.StatusCode);
        using var browserChallenge = await owner.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, browserChallenge.StatusCode);
        Assert.Contains("/local-login", browserChallenge.Headers.Location!.ToString());

        using var login = await SignIn(owner, "/auth/login", "owner", "/_cellbridge/auth/complete");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/_cellbridge/auth/complete", login.Headers.Location!.ToString());
        var cookie = login.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("CellBridge.DocumentLibrary.Local="));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        using var complete = await owner.GetAsync("/_cellbridge/auth/complete");
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Empty(await complete.Content.ReadAsStringAsync());
        Assert.True(complete.Headers.CacheControl!.NoStore);
        using var completeHead = await owner.SendAsync(new(HttpMethod.Head, "/_cellbridge/auth/complete"));
        Assert.Equal(HttpStatusCode.OK, completeHead.StatusCode);
        Assert.Empty(await completeHead.Content.ReadAsByteArrayAsync());

        var library = factory.Services.GetRequiredService<DocumentLibraryService>();
        var bytes = TestOfficeFile.Docx("Authenticated Office document");
        var state = await library.UploadAsync("welcome.docx", new MemoryStream(bytes), DocumentLibraryHarness.Owner);
        using var head = await owner.SendAsync(new(HttpMethod.Head, "/shared/welcome.docx"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(state.Etag, head.Headers.ETag!.ToString());
        Assert.Equal(bytes.Length, head.Content.Headers.ContentLength);
        Assert.Equal(bytes, await owner.GetByteArrayAsync("/shared/welcome.docx"));
        using var discovery = await owner.SendAsync(new(HttpMethod.Options, "/shared/"));
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        Assert.True(discovery.Headers.Contains("X-MSFSSHTTP"));

        using var reader = Client(factory);
        using var readerLogin = await SignIn(reader, "/auth/login", "reader", "/_cellbridge/auth/complete");
        Assert.Equal(bytes, await reader.GetByteArrayAsync("/shared/welcome.docx"));
        using var adminDenied = await reader.PostAsync($"/library/files/{state.ResourceId:D}/permissions",
            new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.Forbidden, adminDenied.StatusCode);
        Assert.False(adminDenied.Headers.Contains("X-FORMS_BASED_AUTH_REQUIRED"));
        await library.SetPermissionAsync(state.ResourceId, DocumentLibraryService.ReaderSubject, DocumentAccess.None);
        using var revoked = await reader.GetAsync("/shared/welcome.docx");
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.Null(revoked.Headers.Location);
        Assert.False(revoked.Headers.Contains("X-FORMS_BASED_AUTH_REQUIRED"));
    }

    [Theory]
    [InlineData("/local-login")]
    [InlineData("/auth/login")]
    public async Task BothLoginAliasesRequireCsrfAndUseSafeReturnUrls(string path)
    {
        using var files = new TemporaryDirectory();
        await using var factory = new LibraryFactory(files.Path);
        using var client = Client(factory);
        using var missingCsrf = await client.PostAsync(path,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["user"] = "owner" }));
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        using var signedIn = await SignIn(client, path, "owner", "//evil.example/", tamper: true);
        Assert.Equal("/_cellbridge/auth/complete", signedIn.Headers.Location!.ToString());

        var remoteGet = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
            context.Request.Method = "GET";
            context.Request.Path = path;
        });
        Assert.Equal(404, remoteGet.Response.StatusCode);
        var remotePost = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
            context.Request.Method = "POST";
            context.Request.Path = path;
        });
        Assert.Equal(404, remotePost.Response.StatusCode);
    }

    private static HttpClient Client(LibraryFactory factory) => factory.CreateClient(new()
    {
        BaseAddress = Origin,
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    private static async Task<HttpResponseMessage> SignIn(HttpClient client, string path, string user, string returnUrl,
        bool tamper = false)
    {
        using var page = await client.GetAsync(path + "?returnUrl=" + Uri.EscapeDataString(returnUrl));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.True(page.Headers.CacheControl!.NoStore);
        var html = await page.Content.ReadAsStringAsync();
        var token = WebUtility.HtmlDecode(Regex.Match(html,
            "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(token);
        var action = WebUtility.HtmlDecode(Regex.Match(html,
            "<form method=\"post\" action=\"([^\"]+)\"").Groups[1].Value);
        var hiddenReturn = WebUtility.HtmlDecode(Regex.Match(html,
            "name=\"returnUrl\" value=\"([^\"]+)\"").Groups[1].Value);
        Assert.Equal("/local-login", action);
        Assert.Equal(tamper ? "/_cellbridge/auth/complete" : returnUrl, hiddenReturn);
        return await client.PostAsync(tamper ? path : action, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["user"] = user,
            ["returnUrl"] = tamper ? returnUrl : hiddenReturn,
            ["__RequestVerificationToken"] = token,
        }));
    }

    private sealed class LibraryFactory(string destination) : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DocumentLibrary:StorageProvider"] = "InMemory",
                ["DocumentLibrary:DestinationRoot"] = destination,
            }));
        }
    }
}
