using System.Net;
using System.Text.RegularExpressions;
using CellBridge.AspNetCore;
using CellBridge.Lists;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CellBridge.DocumentLibrary.Tests;

public sealed class CompositionTests
{
    private static readonly Uri Origin = new("https://localhost");

    [Fact]
    public async Task CookieLoginProtectsBothEndpointFamiliesAndMapsTheCallerSubject()
    {
        var engine = new ExampleEngine();
        await using var factory = new CompositionFactory(engine);
        using var anonymous = Client(factory);

        using var challenge = await anonymous.GetAsync("/api/workspaces/" +
            ExampleListAuthorization.Workspace + "/lists");
        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);

        using var owner = Client(factory);
        using var login = await SignIn(owner, "owner", includeToken: true);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        using var documents = await owner.SendAsync(new(HttpMethod.Options, "/shared/"));
        Assert.Equal(HttpStatusCode.OK, documents.StatusCode);
        Assert.True(documents.Headers.Contains("X-MSFSSHTTP"));

        var listPath = "/api/workspaces/" + ExampleListAuthorization.Workspace + "/lists";
        using var lists = await owner.GetAsync(listPath);
        Assert.Equal(HttpStatusCode.OK, lists.StatusCode);
        Assert.Equal("example:owner", engine.LastActor!.Subject);
        Assert.Equal("example-tenant", engine.LastActor.TenantId);

        using var reader = Client(factory);
        using var readerLogin = await SignIn(reader, "reader", includeToken: true);
        Assert.Equal(HttpStatusCode.Redirect, readerLogin.StatusCode);
        using var deniedDocument = await reader.GetAsync("/shared/example.txt");
        Assert.Equal(HttpStatusCode.NotFound, deniedDocument.StatusCode);
        using var deniedLists = await reader.GetAsync(listPath);
        Assert.Equal(HttpStatusCode.NotFound, deniedLists.StatusCode);
        Assert.Equal("example:reader", engine.LastActor.Subject);
    }

    [Fact]
    public async Task PackagedLoginRejectsAFormWithoutItsAntiforgeryToken()
    {
        var engine = new ExampleEngine();
        await using var factory = new CompositionFactory(engine);
        using var client = Client(factory);
        using var page = await client.GetAsync("/auth/login");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var returnUrl = Field(html, "returnUrl");
        using var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = "owner",
            ["password"] = "example-only",
            ["returnUrl"] = returnUrl
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static HttpClient Client(CompositionFactory factory) => factory.CreateClient(new()
    {
        BaseAddress = Origin,
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static async Task<HttpResponseMessage> SignIn(HttpClient client, string username, bool includeToken)
    {
        using var page = await client.GetAsync("/auth/login");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var fields = new Dictionary<string, string>
        {
            ["login"] = username,
            ["password"] = "example-only",
            ["returnUrl"] = Field(html, "returnUrl"),
            ["_cellbridgeState"] = Field(html, "_cellbridgeState")
        };
        if (includeToken) fields["__RequestVerificationToken"] = Field(html, "__RequestVerificationToken");
        return await client.PostAsync("/auth/login", new FormUrlEncodedContent(fields));
    }

    private static string Field(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html,
        $"name=\"{Regex.Escape(name)}\" value=\"([^\"]+)\"").Groups[1].Value);

    private sealed class CompositionFactory(ExampleEngine engine) : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:lists", "Host=localhost;Database=unused;Username=unused");
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IListEngine>(engine)));
        }
    }

    private sealed class ExampleEngine : IListEngine
    {
        private static readonly ListDefinition Definition = new(Guid.NewGuid(), ExampleListAuthorization.Workspace,
            "Tasks", 1, []);

        public ListActor? LastActor { get; private set; }

        public Task<IReadOnlyList<ListDefinition>> GetListsAsync(ListActor actor, Guid workspaceId,
            CancellationToken ct = default)
        {
            LastActor = actor;
            if (actor.Subject != "example:owner")
                throw new ListException(ListFailure.Forbidden, "Denied.");
            return Task.FromResult<IReadOnlyList<ListDefinition>>([Definition]);
        }

        public Task<ListDefinition> CreateListAsync(ListActor actor, Guid workspaceId, CreateListRequest request, CancellationToken ct = default) => throw Unsupported();
        public Task<ListDefinition> GetListAsync(ListActor actor, Guid workspaceId, Guid listId, CancellationToken ct = default) => throw Unsupported();
        public Task<ListDefinition> PublishSchemaAsync(ListActor actor, Guid workspaceId, Guid listId, PublishSchemaRequest request, CancellationToken ct = default) => throw Unsupported();
        public Task<CreateRecordResult> CreateRecordAsync(ListActor actor, Guid workspaceId, Guid listId, CreateRecordRequest request, CancellationToken ct = default) => throw Unsupported();
        public Task<ListRecord> GetRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId, CancellationToken ct = default) => throw Unsupported();
        public Task<ListRecord> UpdateRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId, UpdateRecordRequest request, CancellationToken ct = default) => throw Unsupported();
        public Task DeleteRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId, long expectedRevision, CancellationToken ct = default) => throw Unsupported();
        public Task<RecordPage> QueryAsync(ListActor actor, Guid workspaceId, Guid listId, ListQuery query, CancellationToken ct = default) => throw Unsupported();
        public Task<MergePreview> PreviewMergeAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId, MergeRequest request, CancellationToken ct = default) => throw Unsupported();
        public Task<IReadOnlyList<SavedListView>> GetViewsAsync(ListActor actor, Guid workspaceId, Guid listId, CancellationToken ct = default) => throw Unsupported();
        public Task<SavedListView> SaveViewAsync(ListActor actor, Guid workspaceId, Guid listId, SaveViewRequest request, CancellationToken ct = default) => throw Unsupported();
        public Task<ReferenceLabel> ResolveReferenceAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId, Guid fieldId, CancellationToken ct = default) => throw Unsupported();
        public Task SetMembershipAsync(ListActor actor, Guid workspaceId, string subject, bool member, bool canCreateLists, bool canManageGrants, CancellationToken ct = default) => throw Unsupported();
        public Task SetListGrantAsync(ListActor actor, Guid workspaceId, Guid listId, string subject, ListAccess access, CancellationToken ct = default) => throw Unsupported();
        public Task<IReadOnlyList<AuditEvent>> GetAuditAsync(ListActor actor, Guid workspaceId, int limit = 100, CancellationToken ct = default) => throw Unsupported();

        private static NotSupportedException Unsupported() => new("The composition test only covers list discovery.");
    }
}
