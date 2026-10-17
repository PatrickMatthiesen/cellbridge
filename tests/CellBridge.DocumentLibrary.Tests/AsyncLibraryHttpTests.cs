using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.DocumentLibrary;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CellBridge.DocumentLibrary.Tests;

public sealed class AsyncLibraryHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InteractiveRoutesUseDurableAuthorityAndInvalidateAllowsAndDenies(bool cached)
    {
        using var files = new TemporaryDirectory();
        await using var factory = Factory(files.Path, cached);
        using var owner = OfficeAuthenticationTests.Client(factory);
        using var editor = OfficeAuthenticationTests.Client(factory);
        using var reader = OfficeAuthenticationTests.Client(factory);
        using var ownerLogin = await OfficeAuthenticationTests.SignIn(owner, "/auth/login", "owner", "/");
        using var editorLogin = await OfficeAuthenticationTests.SignIn(editor, "/auth/login", "editor", "/");
        using var readerLogin = await OfficeAuthenticationTests.SignIn(reader, "/auth/login", "reader", "/");
        var csrf = Token(await owner.GetStringAsync("/"));
        using var create = await owner.PostAsync("/library/create", Form(csrf,
            ("name", "created"), ("type", "docx")));
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        var baseline = TestOfficeFile.Docx("uploaded baseline");
        using var upload = new MultipartFormDataContent();
        upload.Add(new StringContent(csrf), "__RequestVerificationToken");
        upload.Add(new ByteArrayContent(baseline), "file", "uploaded.docx");
        using var uploaded = await owner.PostAsync("/library/upload", upload);
        Assert.Equal(HttpStatusCode.Redirect, uploaded.StatusCode);
        var service = factory.Services.GetRequiredService<CellBridgeDocumentService>();
        var authority = factory.Services.GetRequiredService<RecordingAuthority>();
        var state = (await service.Provider.State.FindByPathKeyAsync(StorageIds.PathKey("/shared/uploaded.docx")))!;
        Assert.NotNull(state);
        Assert.All(authority.Requests.Where(r => r.Action == DocumentAction.Create), r =>
        {
            Assert.Equal(DocumentLibraryService.OwnerSubject, r.Subject);
            Assert.Equal(DocumentLibraryPermissionProvider.Scope, r.Scope);
        });
        Assert.Equal(2, authority.Requests.Count(r => r.Action == DocumentAction.Create));
        Assert.Equal(2, state.Security.AuthorizationPolicy!.ContractVersion);
        Assert.Equal(cached, factory.Services.GetService<CachedDocumentAuthorizationProvider>() is not null);

        var history = $"/library/files/{state.ResourceId:D}/history";
        var beforeReads = authority.Count(DocumentLibraryService.ReaderSubject, DocumentAction.Read);
        using var firstHistory = await reader.GetAsync(history);
        using var secondHistory = await reader.GetAsync(history);
        Assert.Equal(HttpStatusCode.OK, firstHistory.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondHistory.StatusCode);
        Assert.Equal(cached ? 1 : 2,
            authority.Count(DocumentLibraryService.ReaderSubject, DocumentAction.Read) - beforeReads);

        var delivered = $"/library/files/{state.ResourceId:D}/download";
        var revision = $"/_cellbridge/history/{state.ResourceId:D}/{state.LifecycleGeneration}/1";
        Assert.Equal(baseline, await reader.GetByteArrayAsync(delivered));
        Assert.Equal(baseline, await reader.GetByteArrayAsync("/shared/uploaded.docx"));
        Assert.Equal(baseline, await reader.GetByteArrayAsync(revision));
        Assert.Contains("uploaded.docx", await reader.GetStringAsync("/"));

        // Delivered download checked both capabilities and warmed a cached Write deny.
        var deniedWrite = await Save(reader, state, "reader cannot save");
        Assert.True(Assert.Single(deniedWrite.SubResponses).Status);
        await ChangePermission(owner, csrf, state.ResourceId, DocumentLibraryService.ReaderSubject, "write");
        var grantedWrite = await Save(reader, state, "reader granted write");
        Assert.False(Assert.Single(grantedWrite.SubResponses).Status);
        state = (await service.Provider.State.FindByResourceIdAsync(state.ResourceId))!;

        var allowedWrite = await Save(editor, state, "editor accepted write");
        Assert.False(Assert.Single(allowedWrite.SubResponses).Status);
        state = (await service.Provider.State.FindByResourceIdAsync(state.ResourceId))!;
        var beforeRevoke = state;
        await ChangePermission(owner, csrf, state.ResourceId, DocumentLibraryService.EditorSubject, "read");
        var revokedWrite = await Save(editor, state, "editor write revoked");
        Assert.True(Assert.Single(revokedWrite.SubResponses).Status);
        var afterDeniedSave = (await service.Provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal(beforeRevoke.Content, afterDeniedSave.Content);
        Assert.Equal(beforeRevoke.ContentVersion, afterDeniedSave.ContentVersion);
        Assert.Equal(beforeRevoke.Security.AuthorizationPolicy, afterDeniedSave.Security.AuthorizationPolicy);

        await ChangePermission(owner, csrf, state.ResourceId, DocumentLibraryService.ReaderSubject, "none");
        foreach (var path in new[] { delivered, "/shared/uploaded.docx", history, revision })
        {
            using var denied = await reader.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            Assert.Null(denied.Headers.ETag);
            Assert.Empty(await denied.Content.ReadAsByteArrayAsync());
        }
        Assert.DoesNotContain("uploaded.docx", await reader.GetStringAsync("/"));
        var manifest = await factory.Services.GetRequiredService<DocumentLibraryDestination>().GetAsync(state.ResourceId);
        Assert.Single(manifest.PermissionSnapshots);
        Assert.Equal(DocumentAccess.None, manifest.Permissions[DocumentLibraryService.ReaderSubject]);
        Assert.Equal(DocumentAccess.Read, manifest.Permissions[DocumentLibraryService.EditorSubject]);

        // The provider reads durable host state without a snapshot-install notification.
        var raw = factory.Services.GetRequiredService<DocumentLibraryPermissionProvider>();
        var request = authority.Requests.First(r => r.ResourceId == state.ResourceId &&
            r.Subject == DocumentLibraryService.ReaderSubject && r.Action == DocumentAction.Read);
        Assert.False((await raw.AuthorizeAsync(request)).Allowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderCreateDenialAndUnavailableAuthorityFailClosed(bool cached)
    {
        using var files = new TemporaryDirectory();
        await using var factory = Factory(files.Path, cached);
        using var owner = OfficeAuthenticationTests.Client(factory);
        using var login = await OfficeAuthenticationTests.SignIn(owner, "/auth/login", "owner", "/");
        var token = Token(await owner.GetStringAsync("/"));
        var authority = factory.Services.GetRequiredService<RecordingAuthority>();
        authority.DenyCreate = true;
        using var denied = await owner.PostAsync("/library/create", Form(token, ("name", "denied"), ("type", "docx")));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var upload = new MultipartFormDataContent();
        upload.Add(new StringContent(token), "__RequestVerificationToken");
        upload.Add(new ByteArrayContent(TestOfficeFile.Docx("denied upload")), "file", "denied-upload.docx");
        using var deniedUpload = await owner.PostAsync("/library/upload", upload);
        Assert.Equal(HttpStatusCode.Forbidden, deniedUpload.StatusCode);
        Assert.All(authority.Requests, r => Assert.Equal(DocumentLibraryService.OwnerSubject, r.Subject));
        var destination = factory.Services.GetRequiredService<DocumentLibraryDestination>();
        Assert.Empty(await destination.ListAsync());
        var service = factory.Services.GetRequiredService<CellBridgeDocumentService>();
        Assert.Empty(await service.Provider.State.ListAsync(0, 100));

        authority.DenyCreate = false;
        using var accepted = await owner.PostAsync("/library/create", Form(token, ("name", "accepted"), ("type", "docx")));
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        var manifest = Assert.Single(await destination.ListAsync());
        authority.Unavailable = true;
        // Remove any already admitted allows before simulating a failed authority call.
        factory.Services.GetService<CachedDocumentAuthorizationProvider>()?.InvalidateResource(manifest.ResourceId);
        using var unavailable = await owner.GetAsync("/shared/accepted.docx");
        Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
        Assert.Empty(await unavailable.Content.ReadAsByteArrayAsync());
        Assert.Null(unavailable.Headers.ETag);
    }

    private static OfficeAuthenticationTests.LibraryFactory Factory(string path, bool cached) => new(path, services =>
    {
        services.AddSingleton<RecordingAuthority>();
        if (cached)
            services.Replace(ServiceDescriptor.Singleton(sp => new CachedDocumentAuthorizationProvider(
                sp.GetRequiredService<RecordingAuthority>(), new() { Capacity = 64, DecisionTtl = TimeSpan.FromMinutes(1) })));
        else
            services.Replace(ServiceDescriptor.Singleton<IDocumentAuthorizationProvider>(sp => sp.GetRequiredService<RecordingAuthority>()));
    }, cached);

    private sealed class RecordingAuthority(DocumentLibraryPermissionProvider inner) : IDocumentAuthorizationProvider
    {
        public string PolicyDomain => inner.PolicyDomain;
        public ConcurrentQueue<DocumentAuthorizationRequest> Requests { get; } = new();
        public bool DenyCreate { get; set; }
        public bool Unavailable { get; set; }
        public int Count(string subject, DocumentAction action) => Requests.Count(r => r.Subject == subject && r.Action == action);
        public async ValueTask<DocumentAuthorizationDecision> AuthorizeAsync(DocumentAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            if (Unavailable) throw new IOException("Authority unavailable.");
            if (DenyCreate && request.Action == DocumentAction.Create) return new(false);
            return await inner.AuthorizeAsync(request, cancellationToken);
        }
    }

    private static string Token(string html) => WebUtility.HtmlDecode(Regex.Match(html,
        "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);

    private static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] values) =>
        new(values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value))
            .Append(new("__RequestVerificationToken", token)));

    private static async Task ChangePermission(HttpClient owner, string token, Guid resourceId, string subject, string access)
    {
        using var response = await owner.PostAsync($"/library/files/{resourceId:D}/permissions",
            Form(token, ("subject", subject), ("access", access)));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<FsshttpbResponse> Save(HttpClient client, DocumentState state, string text)
    {
        var binary = DocumentLibraryHarness.SaveRequest(state, TestOfficeFile.Docx(text)).ToByteArray();
        XNamespace protocol = CellStorageRequest.Namespace;
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        var envelope = new XElement(soap + "Envelope", new XElement(soap + "Body",
            new XElement(protocol + "RequestVersion", new XAttribute("Version", 2), new XAttribute("MinorVersion", 2)),
            new XElement(protocol + "RequestCollection", new XAttribute("CorrelationId", Guid.NewGuid()),
                new XElement(protocol + "Request", new XAttribute("Url", "https://dev.tail5a9a42.ts.net:8446" + state.Path),
                    new XAttribute("RequestToken", 1), new XAttribute("UseResourceID", "true"),
                    new XAttribute("ResourceID", state.ResourceId.ToString("N")),
                    new XElement(protocol + "SubRequest", new XAttribute("Type", "Cell"), new XAttribute("SubRequestToken", 1),
                        new XElement(protocol + "SubRequestData", new XAttribute("BinaryDataSize", binary.Length),
                            Convert.ToBase64String(binary)))))));
        using var response = await client.PostAsync("/_vti_bin/cellstorage.svc",
            new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var payload = xml.Descendants(protocol + "SubResponseData").Single().Value;
        return FsshttpbResponse.Deserialize(new BinaryReaderEx(Convert.FromBase64String(payload)));
    }
}
