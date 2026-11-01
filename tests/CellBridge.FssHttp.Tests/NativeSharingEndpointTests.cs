using System.Net;
using System.Text;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.FssHttp.Tests;

public sealed class NativeSharingEndpointTests
{
    private const string PublicOrigin = "https://office.example";
    private const string FilePath = "/shared/native share.docx";
    private const string Route = "/_api/web/GetFileByUrl(@v)/ListItemAllFields/GetSharingInformation";

    [Fact]
    public async Task GetSharingInformationReturnsVerboseOwnerAndStoredGrantStateWithoutLinkClaims()
    {
        await using var fixture = await Fixture.Start(registerResolver: true);
        await TestStorage.SetPermissionsAsync(fixture.Provider, fixture.State.ResourceId,
            fixture.State.Security with
            {
                Grants = fixture.State.Security.Grants.Add("tests:reader", DocumentAccess.Read)
                    .Add("tests:editor", DocumentAccess.Read | DocumentAccess.Write),
            });

        using var request = SharingReadRequest(FilePath);
        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType!.ToString(), StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("d");
        Assert.Equal("SP.Sharing.SharingInformation", data.GetProperty("__metadata").GetProperty("type").GetString());
        Assert.False(data.GetProperty("canAddExternalPrincipal").GetBoolean());
        Assert.False(data.GetProperty("canAddInternalPrincipal").GetBoolean());
        Assert.False(data.GetProperty("canSendEmail").GetBoolean());
        Assert.Equal(1, data.GetProperty("sharedObjectType").GetInt32());
        Assert.Equal(PublicOrigin + "/shared/native%20share.docx", data.GetProperty("directUrl").GetString());

        var principals = data.GetProperty("permissionsInformation").GetProperty("principals").GetProperty("results");
        Assert.Equal(3, principals.GetArrayLength());
        Assert.Equal(3, principals[0].GetProperty("role").GetInt32());
        Assert.Contains(principals.EnumerateArray(), principal =>
            principal.GetProperty("principal").GetProperty("loginName").GetString() == "reader" &&
            principal.GetProperty("role").GetInt32() == 1);
        Assert.Contains(principals.EnumerateArray(), principal =>
            principal.GetProperty("principal").GetProperty("loginName").GetString() == "editor" &&
            principal.GetProperty("role").GetInt32() == 2);
        Assert.Empty(data.GetProperty("permissionsInformation").GetProperty("links").GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task GetSharingInformationRequiresExactSameOriginResourceAndOwner()
    {
        await using var fixture = await Fixture.Start();

        using var crossOrigin = SharingReadRequest(FilePath, "https://other.example/shared/native%20share.docx");
        using var crossOriginResponse = await fixture.Client.SendAsync(crossOrigin);
        Assert.Equal(HttpStatusCode.BadRequest, crossOriginResponse.StatusCode);

        using var unknown = SharingReadRequest("/shared/missing.docx");
        using var unknownResponse = await fixture.Client.SendAsync(unknown);
        Assert.Equal(HttpStatusCode.NotFound, unknownResponse.StatusCode);

        using var reader = TestActor.CreateClient(fixture.Server, "reader");
        using var denied = await reader.SendAsync(SharingReadRequest(FilePath));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [Fact]
    public async Task GetSharingInformationRejectsBodiesAndUnknownStoredPrincipals()
    {
        await using var fixture = await Fixture.Start();
        using (var withBody = SharingReadRequest(FilePath, body: "{}"))
        using (var response = await fixture.Client.SendAsync(withBody))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await TestStorage.SetPermissionsAsync(fixture.Provider, fixture.State.ResourceId,
            fixture.State.Security with { Grants = fixture.State.Security.Grants.Add("tests:reader", DocumentAccess.Read) });
        using var request = SharingReadRequest(FilePath);
        using var unsupported = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotImplemented, unsupported.StatusCode);
    }

    private static HttpRequestMessage SharingReadRequest(string path, string? absoluteUrl = null, string? body = null)
    {
        var encodedPath = "/" + string.Join('/', path.Split('/').Skip(1).Select(Uri.EscapeDataString));
        var documentUrl = absoluteUrl ?? PublicOrigin + encodedPath;
        var query = "?$expand=PermissionsInformation%2CPickerSettings&@v=" +
                    Uri.EscapeDataString("'" + documentUrl + "'");
        var request = new HttpRequestMessage(HttpMethod.Post, Route + query);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    private sealed class Fixture(WebApplication app, TestServer server, StorageProvider provider,
        DocumentHead state, HttpClient client) : IAsyncDisposable
    {
        public TestServer Server => server;
        public StorageProvider Provider => provider;
        public DocumentHead State => state;
        public HttpClient Client => client;

        public static async Task<Fixture> Start(bool registerResolver = false)
        {
            var provider = TestStorage.Memory();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            TestActor.Register(builder.Services);
            if (registerResolver)
                builder.Services.AddSingleton<ICellBridgeSharingRecipientResolver, TestRecipientResolver>();
            builder.Services.AddCellBridge(provider, requireDurability: false,
                configure: options => options.PublicOrigin = PublicOrigin);
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGroup("").RequireAuthorization().MapNativeSharing();
            var service = app.Services.GetRequiredService<CellBridgeDocumentService>();
            var state = (await service.CreateAsync(FilePath, [1, 2, 3], TestActor.Value))!;
            var server = app.GetTestServer();
            await app.StartAsync();
            return new(app, server, provider, state, TestActor.CreateClient(server, "writer"));
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class TestRecipientResolver : ICellBridgeSharingRecipientResolver
    {
        public ValueTask<CellBridgeSharingRecipient?> ResolveAsync(string subject,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<CellBridgeSharingRecipient?>(subject switch
        {
            "tests:reader" => new(subject, "reader", "Test Reader", "reader@example.test"),
            "tests:editor" => new(subject, "editor", "Test Editor", "editor@example.test"),
            _ => null,
        });
    }
}
