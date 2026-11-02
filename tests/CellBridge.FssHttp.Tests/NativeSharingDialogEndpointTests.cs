using System.Net;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.FssHttp.Tests;

public sealed class NativeSharingDialogEndpointTests
{
    private const string Route = "/_layouts/15/sharedialog.aspx";
    private const string Origin = "https://office.example";

    [Fact]
    public async Task InitialNativePostShowsOnlyDocumentsTheCallerCanReadAndDoesNotEchoOfficeToken()
    {
        await using var fixture = await Fixture.Start();
        await fixture.CreateDocumentAsync("/shared/reader file.docx", "tests:reader");
        await fixture.CreateDocumentAsync("/shared/hidden file.docx", "tests:owner");

        using var client = TestActor.CreateClient(fixture.Server, "reader");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            Route + "?share_mode=2&scenarioId=CopyLink_Ribbon_Win32&crossdomain=true&clientId=word&clickTime=1")
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("access_token", "opaque-office-token")]),
        };
        using var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Choose a document", html);
        Assert.Contains("reader file.docx", html);
        Assert.DoesNotContain("hidden file.docx", html);
        Assert.DoesNotContain("opaque-office-token", html);
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.True(response.Headers.CacheControl.NoStore);
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'none'", policy);
        Assert.DoesNotContain("unsafe-inline", policy);
        Assert.False(response.Headers.Contains("X-Frame-Options"));
    }

    [Fact]
    public async Task SelectedDocumentShowsCanonicalLinkAndOwnerGrantListWithoutChangingAccess()
    {
        await using var fixture = await Fixture.Start(registerResolver: true);
        var document = await fixture.CreateDocumentAsync("/shared/copy & review.docx", "tests:writer");
        await TestStorage.SetPermissionsAsync(fixture.Provider, document.ResourceId,
            document.Security with { Grants = document.Security.Grants.Add("tests:reader", DocumentAccess.Read) });

        using var response = await fixture.Client.GetAsync(Route + "?resourceId=" + document.ResourceId.ToString("D"));
        var html = await response.Content.ReadAsStringAsync();
        var current = await fixture.Provider.Documents.FindAsync(new DocumentIdentity.Resource(document.ResourceId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("https://office.example/shared/copy%20%26%20review.docx", html);
        Assert.Contains("Link copied.", html);
        Assert.Contains("Test writer", html);
        Assert.Contains("Test reader", html);
        Assert.Contains("Can view", html);
        Assert.Contains("does not grant access", html);
        Assert.Contains("tests:reader", current!.Security.Grants.Keys);
    }

    [Fact]
    public async Task ReaderCanCopyCanonicalUrlButDoesNotSeeOwnerAccessList()
    {
        await using var fixture = await Fixture.Start(registerResolver: true);
        var document = await fixture.CreateDocumentAsync("/shared/shared with reader.docx", "tests:writer");
        await TestStorage.SetPermissionsAsync(fixture.Provider, document.ResourceId,
            document.Security with { Grants = document.Security.Grants.Add("tests:reader", DocumentAccess.Read) });
        using var reader = TestActor.CreateClient(fixture.Server, "reader");

        using var response = await reader.GetAsync(Route + "?resourceId=" + document.ResourceId.ToString("D"));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("https://office.example/shared/shared%20with%20reader.docx", html);
        Assert.Contains("The file owner can view the full access list", html);
        Assert.DoesNotContain("People with access</h2><ul>", html);
    }

    [Fact]
    public async Task UnknownOrUnauthorizedResourceGetsAUsefulPageWithoutLeakingItsUrl()
    {
        await using var fixture = await Fixture.Start();
        var document = await fixture.CreateDocumentAsync("/shared/private document.docx", "tests:writer");
        using var reader = TestActor.CreateClient(fixture.Server, "reader");

        using var response = await reader.GetAsync(Route + "?resourceId=" + document.ResourceId.ToString("D"));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("unavailable or you no longer have access", html);
        Assert.DoesNotContain("private%20document.docx", html);
        Assert.DoesNotContain("access_token", html);
    }

    [Fact]
    public async Task UnsafeConsumerUrlIsRejectedFromDialogOutput()
    {
        await using var fixture = await Fixture.Start(linkProvider: new FixedLinkProvider(new Uri("https://user:pass@evil.example/file")));
        var document = await fixture.CreateDocumentAsync("/shared/safe.docx", "tests:writer");

        using var response = await fixture.Client.GetAsync(Route + "?resourceId=" + document.ResourceId.ToString("D"));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("A link for this document is not configured", html);
        Assert.DoesNotContain("user:pass@evil.example", html);
    }

    [Fact]
    public async Task CanonicalLinkEscapesNestedPathSpacesAndFragmentCharacters()
    {
        await using var fixture = await Fixture.Start();
        var document = await fixture.CreateDocumentAsync("/shared/review #1/sub dir/file #5.docx", "tests:writer");

        using var response = await fixture.Client.GetAsync(Route + "?resourceId=" + document.ResourceId.ToString("D"));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("https://office.example/shared/review%20%231/sub%20dir/file%20%235.docx", html);
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        var nonceSource = policy.Split(';').Single(part => part.TrimStart().StartsWith("script-src ", StringComparison.Ordinal)).Trim();
        var nonce = nonceSource["script-src 'nonce-".Length..^1];
        Assert.Contains("<script nonce=\"" + nonce + "\">", WebUtility.HtmlDecode(html));
        Assert.Contains("<style nonce=\"" + nonce + "\">", WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("unsafe-inline", policy);
        Assert.Contains("width:100%", html);
        Assert.Contains("overflow-wrap:anywhere", html);
    }

    [Theory]
    [InlineData("/shared/Budget%2520plan.docx", "/shared/Budget%20plan.docx")]
    [InlineData("/shared/nested%252Ffolder/Budget%252Fplan.docx", "/shared/nested%2Ffolder/Budget%2Fplan.docx")]
    public async Task CanonicalLinkPreservesLiteralEscapeText(string escapedPath, string storedPath)
    {
        await using var fixture = await Fixture.Start();
        var document = await fixture.CreateDocumentAsync(escapedPath, "tests:writer");
        Assert.Equal(storedPath, document.Path);

        using var response = await fixture.Client.GetAsync(Route + "?resourceId=" + document.ResourceId.ToString("D"));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Origin + escapedPath, html);
    }

    [Fact]
    public async Task NativePostBodyIsDiscardedWithinConfiguredRequestLimit()
    {
        await using var fixture = await Fixture.Start(maxRequestBytes: 16);
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new ByteArrayContent(new byte[17]),
        };

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.DoesNotContain("access_token", await response.Content.ReadAsStringAsync());
    }

    private sealed class FixedLinkProvider(Uri link) : ICellBridgeNativeDocumentLinkProvider
    {
        public Uri? GetDocumentUrl(HttpContext context, DocumentHead document) => link;
    }

    private sealed class Fixture(WebApplication app, TestServer server, StorageProvider provider, HttpClient client) : IAsyncDisposable
    {
        public TestServer Server => server;
        public StorageProvider Provider => provider;
        public HttpClient Client => client;

        public static async Task<Fixture> Start(bool registerResolver = false, ICellBridgeNativeDocumentLinkProvider? linkProvider = null,
            long maxRequestBytes = 128L * 1024 * 1024)
        {
            var provider = TestStorage.Memory();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            TestActor.Register(builder.Services);
            if (registerResolver)
                builder.Services.AddSingleton<ICellBridgeSharingRecipientResolver, TestRecipientResolver>();
            if (linkProvider is not null)
                builder.Services.AddSingleton(linkProvider);
            builder.Services.AddCellBridge(provider, requireDurability: false, configure: options =>
            {
                options.PublicOrigin = Origin;
                options.MaxRequestBytes = maxRequestBytes;
            });
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGroup("").RequireAuthorization().MapNativeSharingDialog();
            var server = app.GetTestServer();
            await app.StartAsync();
            return new(app, server, provider, TestActor.CreateClient(server, "writer"));
        }

        public async Task<DocumentHead> CreateDocumentAsync(string path, string owner)
        {
            var actor = new CellBridgeActor(new(owner, owner, owner), CanCreate: true);
            return (await app.Services.GetRequiredService<CellBridgeDocumentService>()
                .CreateAsync(path, [1, 2, 3], actor))!;
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class TestRecipientResolver : ICellBridgeSharingRecipientResolver
    {
        public ValueTask<CellBridgeSharingRecipient?> ResolveAsync(string subject, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CellBridgeSharingRecipient?>(subject switch
            {
                "tests:reader" => new(subject, "reader", "Test reader", "reader@example.test"),
                _ => null,
            });
    }
}
