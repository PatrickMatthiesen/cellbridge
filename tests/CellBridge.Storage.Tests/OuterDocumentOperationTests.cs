using System.Net;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed class OuterDocumentOperationTests
{
    [Fact]
    public async Task SoapHistoryUrlsAreDownloadableAfterRenameAndRevocationDeniesHistory()
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var originalBytes = MinimalDocx.Create("first");
        var state = (await service.CreateAsync("/shared/original.docx", originalBytes, TestActor.Value))!;
        state = await RevisionHistoryTests.Save(service, state, "second");
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddCellBridge(provider, requireDurability: false); TestActor.Register(builder.Services);
        await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapCellBridge(); await app.StartAsync();
        using var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("X-Test-User", "writer");
        var processor = new CellBridgeRequestProcessor(service);
        var history = await Call(processor, state.ResourceId, SubRequestType.GetVersions);
        var results = XElement.Parse(history.SubResponseXml!).Descendants("result").ToArray();
        Assert.Equal(2, results.Length);
        Assert.Equal("1", (string?)XElement.Parse(history.SubResponseXml!).Descendants("versioning").Single().Attribute("enabled"));
        foreach (var result in results)
        {
            var url = new Uri((string)result.Attribute("url")!);
            using var download = await client.GetAsync(url.PathAndQuery);
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(long.Parse((string)result.Attribute("size")!), (await download.Content.ReadAsByteArrayAsync()).LongLength);
            Assert.Equal("no-store, private", string.Join(", ", download.Headers.CacheControl!.ToString().Split(", ").Order()));
        }
        var rename = await Call(processor, state.ResourceId, SubRequestType.FileOperation,
            ("FileOperation", "Rename"), ("NewFileName", "renamed #%.docx"));
        Assert.Equal("Success", rename.ErrorCode);
        var changed = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal("/shared/renamed #%.docx", changed.Path);
        Assert.Null(await provider.State.FindByPathKeyAsync(state.PathKey));
        using var renamedDownload = await client.GetAsync("/shared/renamed%20%23%25.docx");
        Assert.Equal(HttpStatusCode.OK, renamedDownload.StatusCode);
        var oldUrl = new Uri((string)results[1].Attribute("url")!);
        Assert.Equal(originalBytes, await client.GetByteArrayAsync(oldUrl.PathAndQuery));
        await provider.State.TransitionAsync(state.ResourceId, (current, _) => new StateTransition<bool>(current with
            { Security = current.Security with { Owner = "revoked" } }, true));
        using var revoked = await client.GetAsync(oldUrl.PathAndQuery);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
    }

    [Fact]
    public async Task VersioningAndPropertiesUseNormativeWireSchema()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/properties.docx", MinimalDocx.Create(), TestActor.Value))!;
        var processor = new CellBridgeRequestProcessor(service);
        var listed = await Call(processor, state.ResourceId, SubRequestType.Versioning, ("VersioningRequestType", "GetVersionList"));
        var envelope = new CellStorageResponse { Responses = { new() { SubResponses = { listed } } } }.ToSoapEnvelope();
        var xml = XDocument.Parse(envelope);
        Assert.Single(xml.Descendants(), e => e.Name.LocalName == "SubResponseData");
        var version = xml.Descendants().Single(e => e.Name.LocalName == "Version");
        Assert.Equal("1.0", (string?)version.Attribute("Number"));
        Assert.Equal(state.ModifiedUtc.ToFileTimeUtc().ToString(), (string?)version.Attribute("LastModifiedTime"));
        var enumeration = await Call(processor, state.ResourceId, SubRequestType.Properties, ("Properties", "PropertyEnumerate"));
        Assert.Contains(XElement.Parse(enumeration.SubResponseDataXml!).Elements(), e => (string?)e.Attribute("id") == "vti_filesize");
        var file = File(state.ResourceId, SubRequestType.Properties, ("Properties", "PropertyGet"));
        file.SubRequests[0].SubRequestDataXml = "<SubRequestData><PropertyIds><PropertyId id=\"vti_filesize\"/><PropertyId id=\"absent\"/></PropertyIds></SubRequestData>";
        var got = (await processor.ExecuteAsync(new() { Requests = { file } }, "https://host.test", TestActor.Value)).Response.Responses[0].SubResponses[0];
        Assert.Equal("Success", got.ErrorCode);
        Assert.Equal(state.Content.Length.ToString(), (string?)Assert.Single(XElement.Parse(got.SubResponseDataXml!).Elements()).Attribute("value"));
        Assert.Equal("VersionNotFound", (await Call(processor, state.ResourceId, SubRequestType.Versioning,
            ("VersioningRequestType", "RestoreVersion"), ("Version", "999.0"))).ErrorCode);
        Assert.Equal("InvalidArgument", (await Call(processor, state.ResourceId, SubRequestType.Versioning,
            ("VersioningRequestType", "RestoreVersion"), ("Version", "@1.0"))).ErrorCode);
        Assert.Equal("Success", (await Call(processor, state.ResourceId, SubRequestType.Versioning,
            ("VersioningRequestType", "RestoreVersion"), ("Version", "1.0"))).ErrorCode);
        Assert.Equal(2, (await provider.State.FindByResourceIdAsync(state.ResourceId))!.Revisions.Length);
    }

    [Fact]
    public Task RenameUniquenessAndCaseOnlyNames() => CheckRename(Memory());

    [PostgreSqlFact]
    public async Task PostgreSqlRenameUniquenessAndCaseOnlyNames()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await CheckRename(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)));
    }

    private static async Task CheckRename(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider); var folder = "/" + Guid.NewGuid().ToString("N") + "/";
        var first = (await service.CreateAsync(folder + "first.docx", MinimalDocx.Create(), TestActor.Value))!;
        await service.CreateAsync(folder + "second.docx", MinimalDocx.Create(), TestActor.Value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenameDocumentAsync(first.ResourceId, "second.docx", TestActor.Value).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => service.RenameDocumentAsync(first.ResourceId, "../other.docx", TestActor.Value).AsTask());
        await service.RenameDocumentAsync(first.ResourceId, "FIRST.docx", TestActor.Value);
        var renamed = await provider.State.FindByPathKeyAsync(first.PathKey);
        Assert.Equal(first.ResourceId, renamed!.ResourceId);
        Assert.Equal(folder + "FIRST.docx", renamed.Path);
        await service.CreateAsync(folder + "my%20file%20%C3%A6%25.docx", MinimalDocx.Create(), TestActor.Value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenameDocumentAsync(first.ResourceId, "my file æ%.docx", TestActor.Value).AsTask());
    }

    private static async Task<FssHttpSubResponse> Call(CellBridgeRequestProcessor processor, Guid id, SubRequestType type,
        params (string Key, string Value)[] attributes) =>
        (await processor.ExecuteAsync(new() { Requests = { File(id, type, attributes) } }, "https://host.test", TestActor.Value))
        .Response.Responses[0].SubResponses[0];
    private static FssHttpRequest File(Guid id, SubRequestType type, params (string Key, string Value)[] attributes)
    {
        var sub = new FssHttpSubRequest { Type = type, SubRequestToken = 1 };
        foreach (var (key, value) in attributes) sub.SubRequestDataAttributes[key] = value;
        return new() { Url = "https://stale.test/absent.docx", ResourceId = id.ToString(), UseResourceId = true, RequestToken = 1, SubRequests = { sub } };
    }
    private static StorageProvider Memory() => new(new InMemoryStateStore(), new InMemoryContentStore());
}
