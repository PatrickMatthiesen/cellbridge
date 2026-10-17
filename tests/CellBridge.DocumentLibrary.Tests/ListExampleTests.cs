using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using CellBridge.DocumentLibrary;
using CellBridge.Lists;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CellBridge.DocumentLibrary.Tests;

public sealed class ListExampleTests
{
    [Fact]
    public async Task ListsRemainOptInForExistingDocumentHosts()
    {
        using var files = new TemporaryDirectory();
        var engine = new PageEngine();
        await using var factory = new ListFactory(files.Path, engine, listsEnabled: false);
        using var client = Client(factory);
        using var login = await OfficeAuthenticationTests.SignIn(client, "/auth/login", "owner", "/lists");
        using var response = await client.GetAsync("/lists");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(engine.Discoveries);
    }

    [Fact]
    public async Task ListPageRequiresAuthenticationBeforeWorkspaceDiscovery()
    {
        using var files = new TemporaryDirectory();
        var engine = new PageEngine();
        await using var factory = Factory(files.Path, engine);
        using var client = Client(factory);
        using var response = await client.GetAsync("/lists");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/auth/login", response.Headers.Location!.ToString());
        Assert.Empty(engine.Discoveries);
    }

    [Fact]
    public async Task ReaderPageOnlyShowsAuthorizedCustomerAndReadControls()
    {
        using var files = new TemporaryDirectory();
        var engine = new PageEngine();
        await using var factory = Factory(files.Path, engine);
        using var client = Client(factory);
        using var login = await OfficeAuthenticationTests.SignIn(client, "/auth/login", "reader", "/lists");
        using var response = await client.GetAsync("/lists");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(DocumentLibraryLists.WorkspaceId.ToString("D"), html);
        Assert.DoesNotContain(DocumentLibraryLists.OtherWorkspaceId.ToString("D"), html);
        Assert.DoesNotContain("Contoso", html);
        Assert.DoesNotContain("id=\"new-record\"", html);
        Assert.DoesNotContain("id=\"add-field\"", html);
        Assert.DoesNotContain("id=\"create-list\"", html);
        Assert.Contains("data-can-edit=\"false\"", html);
        Assert.Contains("src=\"/lists.js\" defer", html);
        Assert.Contains("href=\"/lists.css\"", html);
        Assert.DoesNotContain("<script>", html);
        Assert.All(engine.Discoveries, d => Assert.Equal(DocumentLibraryService.ReaderSubject, d.Actor.Subject));
        Assert.Equal(2, engine.Discoveries.Count);
    }

    [Fact]
    public async Task IssuedPageTokenProtectsTheNativeCookieQueryEndpoint()
    {
        using var files = new TemporaryDirectory();
        var engine = new PageEngine();
        await using var factory = Factory(files.Path, engine);
        using var client = Client(factory);
        using var login = await OfficeAuthenticationTests.SignIn(client, "/auth/login", "owner", "/lists");
        var html = await client.GetStringAsync("/lists");
        Assert.Contains("id=\"new-record\"", html);
        Assert.Contains("id=\"add-field\"", html);
        Assert.Contains("id=\"create-list\"", html);
        var token = Meta(html, "list-csrf-token");
        var header = Meta(html, "list-csrf-header");
        Assert.NotEmpty(token);
        var path = $"/api/workspaces/{DocumentLibraryLists.WorkspaceId:D}/lists/{DocumentLibraryLists.TasksListId:D}/records/query";
        using var denied = await client.PostAsJsonAsync(path, new { query = new { limit = 100 }, cursor = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Equal(0, engine.Queries);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { query = new { limit = 100 }, cursor = (string?)null })
        };
        request.Headers.Add(header, token);
        using var allowed = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(1, engine.Queries);
        Assert.Equal(DocumentLibraryService.OwnerSubject, engine.LastQueryActor!.Subject);
        Assert.Contains("\"records\":[]", await allowed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnavailableWorkspaceDoesNotExposeStoreErrorDetails()
    {
        using var files = new TemporaryDirectory();
        var engine = new PageEngine { Unavailable = true };
        await using var factory = Factory(files.Path, engine);
        using var client = Client(factory);
        using var login = await OfficeAuthenticationTests.SignIn(client, "/auth/login", "reader", "/lists");
        var html = await client.GetStringAsync("/lists");
        Assert.Contains("Some customer lists are unavailable", html);
        Assert.DoesNotContain("secret_database_host", html);
        Assert.DoesNotContain(DocumentLibraryLists.OtherWorkspaceId.ToString("D"), html);
    }

    private static string Meta(string html, string name) => WebUtility.HtmlDecode(
        Regex.Match(html, $"name=\"{name}\" content=\"([^\"]+)\"").Groups[1].Value);

    private static ListFactory Factory(string path, PageEngine engine) => new(path, engine);

    private static HttpClient Client(ListFactory factory) => factory.CreateClient(new()
    {
        BaseAddress = new Uri("https://dev.tail5a9a42.ts.net:8446"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    private sealed class ListFactory(string destination, PageEngine engine, bool listsEnabled = true) : WebApplicationFactory<global::Program>
    {
        private DocumentLibraryDestination? ownedDestination;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Protocol:PublicOrigin", "https://dev.tail5a9a42.ts.net:8446");
            builder.UseSetting("DocumentLibrary:StorageProvider", "InMemory");
            builder.UseSetting("DocumentLibrary:DestinationRoot", destination);
            builder.UseSetting("Lists:Enabled", listsEnabled ? "true" : "false");
            builder.UseSetting("ConnectionStrings:lists", "Host=localhost;Database=unused;Username=unused");
            builder.ConfigureTestServices(services =>
            {
                ownedDestination = new DocumentLibraryDestination(destination);
                services.Replace(ServiceDescriptor.Singleton(ownedDestination));
                services.Replace(ServiceDescriptor.Singleton<IExternalRevisionDestination>(ownedDestination));
                services.Replace(ServiceDescriptor.Singleton<IListEngine>(engine));
            });
        }
        public override async ValueTask DisposeAsync()
        {
            try { await base.DisposeAsync(); }
            finally { ownedDestination?.Dispose(); }
        }
    }

    private sealed class PageEngine : IListEngine
    {
        private static readonly ListDefinition Definition = new(DocumentLibraryLists.TasksListId,
            DocumentLibraryLists.WorkspaceId, "Tasks", 1,
            [new(DocumentLibraryLists.TaskTitleFieldId, "Title", ListFieldType.Text, Required: true)]);
        public List<(ListActor Actor, Guid Workspace)> Discoveries { get; } = [];
        public bool Unavailable { get; init; }
        public int Queries { get; private set; }
        public ListActor? LastQueryActor { get; private set; }
        public Task<IReadOnlyList<ListDefinition>> GetListsAsync(ListActor actor, Guid workspaceId, CancellationToken ct = default)
        {
            Discoveries.Add((actor, workspaceId));
            if (Unavailable) throw new ListException(ListFailure.Unavailable, "secret_database_host");
            if (workspaceId != DocumentLibraryLists.WorkspaceId) throw new ListException(ListFailure.Forbidden, "Hidden customer");
            return Task.FromResult<IReadOnlyList<ListDefinition>>([Definition]);
        }
        public Task<ListDefinition> GetListAsync(ListActor actor, Guid workspaceId, Guid listId, CancellationToken ct = default) => Task.FromResult(Definition);
        public Task<RecordPage> QueryAsync(ListActor actor, Guid workspaceId, Guid listId, ListQuery query, CancellationToken ct = default)
        {
            Queries++; LastQueryActor = actor;
            return Task.FromResult(new RecordPage([], null, Definition.SchemaVersion));
        }
        public Task<ListDefinition> CreateListAsync(ListActor actor, Guid workspaceId, CreateListRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ListDefinition> PublishSchemaAsync(ListActor actor, Guid workspaceId, Guid listId, PublishSchemaRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CreateRecordResult> CreateRecordAsync(ListActor actor, Guid workspaceId, Guid listId, CreateRecordRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ListRecord> GetRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ListRecord> UpdateRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId, UpdateRecordRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId, long expectedRevision, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MergePreview> PreviewMergeAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId, MergeRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SavedListView>> GetViewsAsync(ListActor actor, Guid workspaceId, Guid listId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SavedListView> SaveViewAsync(ListActor actor, Guid workspaceId, Guid listId, SaveViewRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ReferenceLabel> ResolveReferenceAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId, Guid fieldId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetMembershipAsync(ListActor actor, Guid workspaceId, string subject, bool member, bool canCreateLists, bool canManageGrants, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetListGrantAsync(ListActor actor, Guid workspaceId, Guid listId, string subject, ListAccess access, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AuditEvent>> GetAuditAsync(ListActor actor, Guid workspaceId, int limit = 100, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
