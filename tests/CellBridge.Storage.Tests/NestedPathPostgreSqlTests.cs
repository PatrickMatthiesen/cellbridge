using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class NestedPathPostgreSqlTests
{
    private static readonly XNamespace Protocol = "http://schemas.microsoft.com/sharepoint/soap/";
    private static readonly Guid DocumentLibraryId = Guid.Parse("c4aa42f0-0c5c-4b8d-a3cf-733ff51ed962");

    [PostgreSqlFact]
    public async Task SavedDocumentMovesToNestedUrlAcrossProviderRestart()
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!;
        var policy = new MoveEndpointPolicy();
        var root = "/shared/" + Guid.NewGuid().ToString("N");
        var oldPath = root + "/report.docx";
        var newPath = root + "/team space/東京/deep/report # %.docx";
        var duplicatePath = root + "/other/team space/東京/report # %.docx";
        var oldUrl = EscapePath(oldPath);
        var newUrl = EscapePath(newPath);
        var duplicateUrl = EscapePath(duplicatePath);
        Guid id, duplicateId;
        byte[] savedBytes;

        await using (var source = NpgsqlDataSource.Create(connection))
        {
            var provider = Provider(source);
            var service = new CellBridgeDocumentService(provider, authorizationPolicy: policy);
            var initial = (await service.CreateAsync(oldPath, MinimalDocx.Create("first"), TestActor.Value))!;
            var saved = await RepositoryTestPublications.SaveAsync(service, initial, MinimalDocx.Create("second"));
            var revisions = (await provider.Documents.ReadRevisionsAsync(initial.ResourceId, 0, 256))!.Revisions;
            savedBytes = await StorageTestDocuments.PackageAsync(provider, saved.Package);
            var moved = await service.MoveDocumentAsync(initial.ResourceId, newPath, TestActor.Value);
            id = moved.ResourceId;
            Assert.Equal(newPath, moved.Path);
            Assert.Equal(saved.Package, moved.Package);
            Assert.Equal(JsonSerializer.Serialize(revisions), JsonSerializer.Serialize((await provider.Documents.ReadRevisionsAsync(initial.ResourceId, 0, 256))!.Revisions));
            var duplicate = (await service.CreateAsync(EscapePath(duplicatePath), MinimalDocx.Create("other"), TestActor.Value))!;
            duplicateId = duplicate.ResourceId;
            policy.Install(duplicateId, 2, false);
            Assert.True(await service.UpdateAuthorizationAsync(duplicateId, policy.Binding(1), policy.Binding(2)));
        }

        await using var reopenedSource = NpgsqlDataSource.Create(connection);
        var reopenedProvider = Provider(reopenedSource);
        Assert.Null(await reopenedProvider.Documents.FindAsync(new DocumentIdentity.Path(oldPath.ToUpperInvariant())));
        Assert.Equal(id, (await reopenedProvider.Documents.FindAsync(new DocumentIdentity.Path(newPath.ToUpperInvariant())))!.ResourceId);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(policy);
        builder.Services.AddCellBridge<MoveEndpointPolicy>(reopenedProvider,
            configure: options => options.DocumentLibraryId = DocumentLibraryId);
        TestActor.Register(builder.Services);
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); app.MapCellBridge();
        await app.StartAsync();
        using var client = TestActor.CreateClient(app.GetTestServer(), "writer");

        using var currentGet = await client.GetAsync(newUrl);
        Assert.Equal(HttpStatusCode.OK, currentGet.StatusCode);
        Assert.Equal(savedBytes, await currentGet.Content.ReadAsByteArrayAsync());
        using var currentHead = await client.SendAsync(new(HttpMethod.Head, newUrl));
        Assert.Equal(HttpStatusCode.OK, currentHead.StatusCode);
        Assert.Equal(savedBytes.Length, currentHead.Content.Headers.ContentLength);
        Assert.Empty(await currentHead.Content.ReadAsByteArrayAsync());
        using var oldGet = await client.GetAsync(oldUrl);
        using var oldHead = await client.SendAsync(new(HttpMethod.Head, oldUrl));
        Assert.Equal(HttpStatusCode.NotFound, oldGet.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, oldHead.StatusCode);
        using var isolatedGet = await client.GetAsync(duplicateUrl);
        Assert.Equal(HttpStatusCode.NotFound, isolatedGet.StatusCode);

        using var stale = await Soap(client, oldUrl + "/_vti_bin/cellstorage.svc", oldUrl,
            "GetDocMetaInfo", true, id.ToString("D"));
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        var staleXml = XDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal("Success", SubError(staleXml));
        Assert.Equal("http://localhost" + newUrl, (string?)staleXml.Descendants(Protocol + "Response").Single().Attribute("Url"));
        Assert.Equal(id.ToString("N"), (string?)staleXml.Descendants(Protocol + "Response").Single().Attribute("ResourceID"));

        using var oldPathOnly = await Soap(client, oldUrl + "/_vti_bin/cellstorage.svc", oldUrl, "GetDocMetaInfo");
        Assert.Equal("FileNotExistsOrCannotBeCreated", FileError(XDocument.Parse(await oldPathOnly.Content.ReadAsStringAsync())));
        using var badId = await Soap(client, newUrl + "/_vti_bin/cellstorage.svc", newUrl,
            "GetDocMetaInfo", true, Guid.NewGuid().ToString("D"));
        Assert.Equal("FileNotExistsOrCannotBeCreated", FileError(XDocument.Parse(await badId.Content.ReadAsStringAsync())));
        using var isolatedSoap = await Soap(client, duplicateUrl + "/_vti_bin/cellstorage.svc", duplicateUrl,
            "GetDocMetaInfo", true, duplicateId.ToString("D"));
        Assert.Equal("FileUnauthorizedAccess", FileError(XDocument.Parse(await isolatedSoap.Content.ReadAsStringAsync())));

        using var versions = await Soap(client, oldUrl + "/_vti_bin/cellstorage.svc", oldUrl,
            "GetVersions", true, id.ToString("D"));
        var versionsXml = XDocument.Parse(await versions.Content.ReadAsStringAsync());
        Assert.Equal("Success", SubError(versionsXml));
        var historyUrl = (string?)versionsXml.Descendants().First(x => x.Name.LocalName == "result").Attribute("url");
        Assert.NotNull(historyUrl);
        using var history = await client.GetAsync(historyUrl);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);

        policy.Install(id, 2, false);
        Assert.True(await app.Services.GetRequiredService<CellBridgeDocumentService>()
            .UpdateAuthorizationAsync(id, policy.Binding(1), policy.Binding(2)));
        using var revokedCurrent = await client.GetAsync(newUrl);
        using var revokedHistory = await client.GetAsync(historyUrl);
        Assert.Equal(HttpStatusCode.NotFound, revokedCurrent.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, revokedHistory.StatusCode);
        using var revokedSoap = await Soap(client, oldUrl + "/_vti_bin/cellstorage.svc", oldUrl,
            "GetDocMetaInfo", true, id.ToString("D"));
        Assert.Equal("FileUnauthorizedAccess", FileError(XDocument.Parse(await revokedSoap.Content.ReadAsStringAsync())));
    }

    private static StorageProvider Provider(NpgsqlDataSource source) =>
        new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));

    private static string EscapePath(string decodedPath) =>
        string.Join('/', decodedPath.Split('/').Select(Uri.EscapeDataString));

    private static string? FileError(XDocument xml) =>
        (string?)xml.Descendants(Protocol + "Response").Single().Attribute("ErrorCode");

    private static string? SubError(XDocument xml) =>
        (string?)xml.Descendants(Protocol + "SubResponse").Single().Attribute("ErrorCode");

    private static Task<HttpResponseMessage> Soap(HttpClient client, string target, string documentUrl,
        string operation, bool useResourceId = false, string? resourceId = null)
    {
        XNamespace envelope = "http://schemas.xmlsoap.org/soap/envelope/";
        var file = new XElement(Protocol + "Request", new XAttribute("Url", documentUrl), new XAttribute("RequestToken", 1),
            useResourceId ? new XAttribute("UseResourceID", "true") : null,
            resourceId is null ? null : new XAttribute("ResourceID", resourceId),
            new XElement(Protocol + "SubRequest", new XAttribute("Type", operation), new XAttribute("SubRequestToken", 1)));
        var xml = new XElement(envelope + "Envelope", new XElement(envelope + "Body",
            new XElement(Protocol + "RequestVersion", new XAttribute("Version", 2), new XAttribute("MinorVersion", 2)),
            new XElement(Protocol + "RequestCollection", new XAttribute("CorrelationId", Guid.NewGuid()), file)));
        return client.SendAsync(new HttpRequestMessage(HttpMethod.Post, target)
            { Content = new StringContent(xml.ToString(), Encoding.UTF8, "text/xml") });
    }

    public sealed class MoveEndpointPolicy : ICellBridgeAuthorizationPolicy
    {
        private readonly ConcurrentDictionary<(Guid ResourceId, long Revision), bool> _allowed = new();
        public string PolicyDomain => "move:endpoint";
        public DocumentAuthorizationBinding Binding(long revision) => new(PolicyDomain, 1, revision);
        public DocumentAuthorizationBinding BindNewDocument(Guid resourceId)
        {
            _allowed.TryAdd((resourceId, 1), true);
            return Binding(1);
        }
        public void Install(Guid resourceId, long revision, bool allowed)
        {
            if (!_allowed.TryAdd((resourceId, revision), allowed))
                throw new InvalidOperationException("A policy revision cannot change after installation.");
        }
        public ICellBridgeAuthorizationSnapshot? Resolve(DocumentAuthorizationView state) =>
            state.Security.AuthorizationPolicy is { } binding && binding == Binding(binding.Revision) &&
            _allowed.TryGetValue((state.ResourceId, binding.Revision), out var allowed)
                ? new Snapshot(state.ResourceId, binding, allowed) : null;
        private sealed record Snapshot(Guid ResourceId, DocumentAuthorizationBinding Binding, bool Allowed) : ICellBridgeAuthorizationSnapshot
        {
            public DocumentAccess Evaluate(string subject) =>
                Allowed && subject == "tests:writer" ? DocumentAccess.Write : DocumentAccess.None;
        }
    }
}
