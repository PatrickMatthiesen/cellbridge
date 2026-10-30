using System.Net;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Tests;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class OuterDocumentOperationTests
{
    private static readonly Guid DocumentLibraryId = Guid.Parse("c4aa42f0-0c5c-4b8d-a3cf-733ff51ed962");
    [Fact]
    public async Task CoalescedFileSavePublishesTheCanonicalGraphWithoutASecondPackageCopy()
    {
        var content = new RecordingWrites(new InMemoryContentStore());
        var provider = StorageTestDocuments.Memory(content); var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/coalesced.docx", MinimalDocx.Create("original"), TestActor.Value))!;
        var bytes = MinimalDocx.Create("coalesced");
        var request = await StorageTestDocuments.SaveRequestAsync(service, initial, bytes);
        content.Writes.Clear();
        var accepted = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string> { ["Coalesce"] = "true" }, TestActor.Value);
        Assert.False(Assert.Single(accepted.Response.SubResponses).Status);
        Assert.Equal(bytes, await StorageTestDocuments.PackageAsync(provider, accepted.State.Package));
        Assert.Equal(initial.Package.Root.Cell, accepted.State.Package.Root.Cell);
        Assert.Equal(initial.ContentVersion + 1, accepted.State.ContentVersion);
        Assert.Equal(2, accepted.State.Counters.Revisions);
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        Assert.DoesNotContain(content.Writes, handle => handle.Length == bytes.Length && handle.Sha256 == digest);
        NormalizedDocumentRecovery.Validate((await provider.Documents.ExportDocumentAsync(initial.ResourceId))!, provider.Limits);
    }

    [Fact]
    public async Task MetadataCancellationAfterCommitRetainsItsSuccessfulPutAndReceiptWithoutFileSaveEvidence()
    {
        var provider = StorageTestDocuments.Memory(); var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/cancel-metadata.docx", MinimalDocx.Create(), TestActor.Value))!;
        var fixture = new GraphFixture("durable metadata"u8.ToArray());
        var request = new FsshttpbCellRequest { DataElementPackage = new(), SubRequests =
            { new(RequestTypes.PutChanges) { RequestId = 1, Data = new PutChangesSubRequestData { StorageIndex = fixture.Index } } } };
        request.DataElementPackage.DataElements.AddRange(fixture.Elements);
        using var cancellation = new CancellationTokenSource();
        var interrupted = new CellBridgeDocumentService(new(new CancelMetadataAfterCommit(provider.Documents, cancellation), provider.Content));
        var result = await interrupted.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.Metadata, request,
            new Dictionary<string, string>(), TestActor.Value, cancellation.Token);
        Assert.True(result.IsCancelled); Assert.True(result.Response.Status);
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        Assert.Empty(result.AcceptedSaves);
        Assert.Equal(1, result.State.Counters.SaveReceipts);
        Assert.Equal(initial.ContentVersion, result.State.ContentVersion);
        Assert.Equal(2, result.State.Counters.Revisions);
        var replay = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.Metadata, request,
            new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(replay.Response.SubResponses).Status);
        Assert.Empty(replay.AcceptedSaves);
        Assert.Equal(result.State, replay.State);
    }

    private sealed class CancelMetadataAfterCommit(IDocumentRepository inner, CancellationTokenSource cancellation) : RepositoryTestProxy(inner)
    {
        public override async ValueTask<PublicationResult> PublishAsync(PreparedDocumentPublication publication, CancellationToken ct = default)
        {
            var result = await Inner.PublishAsync(publication, ct);
            Assert.Equal(PublicationStatus.Accepted, result.Status);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }
    }

    private sealed class RecordingWrites(IContentStore inner) : IContentStore
    {
        public List<ContentHandle> Writes { get; } = [];
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken ct = default)
        {
            var handle = await inner.WriteAsync(source, ct); Writes.Add(handle); return handle;
        }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken ct = default) => inner.OpenReadAsync(handle, ct);
    }

    [Theory]
    [InlineData("/shared/original.docx", "/shared/renamed #%.docx", "/shared/renamed%20%23%25.docx")]
    [InlineData("/shared/team%20space/%E6%9D%B1%E4%BA%AC/original.docx",
        "/shared/team space/東京/renamed #%.docx",
        "/shared/team%20space/%E6%9D%B1%E4%BA%AC/renamed%20%23%25.docx")]
    public async Task SoapHistoryUrlsAreDownloadableAfterRenameAndRevocationDeniesHistory(
        string originalUrl, string expectedRenamedPath, string renamedUrl)
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var originalBytes = MinimalDocx.Create("first");
        var state = (await service.CreateAsync(originalUrl, originalBytes, TestActor.Value))!;
        state = await RepositoryTestPublications.SaveAsync(service, state, MinimalDocx.Create("second"));
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddCellBridge(provider, requireDurability: false,
            configure: options => options.DocumentLibraryId = DocumentLibraryId); TestActor.Register(builder.Services);
        await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapCellBridge(); await app.StartAsync();
        using var client = TestActor.CreateClient(app.GetTestServer(), "writer");
        var processor = new CellBridgeRequestProcessor(service, options: new() { DocumentLibraryId = DocumentLibraryId });
        var history = await Call(processor, state.ResourceId, SubRequestType.GetVersions);
        var results = XElement.Parse(history.SubResponseXml!).Descendants("result").ToArray();
        Assert.Equal(2, results.Length);
        Assert.Equal("1", (string?)XElement.Parse(history.SubResponseXml!).Descendants("versioning").Single().Attribute("enabled"));
        Assert.Equal(DocumentLibraryId.ToString("B"), (string?)XElement.Parse(history.SubResponseXml!).Descendants("list").Single().Attribute("id"));
        Assert.Equal("https://host.test/_cellbridge/history/settings",
            (string?)XElement.Parse(history.SubResponseXml!).Descendants("settings").Single().Attribute("url"));
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
        var changed = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.Equal(expectedRenamedPath, changed.Path);
        Assert.Null(await provider.Documents.FindAsync(new DocumentIdentity.Path(state.PathKey)));
        using var oldDownload = await client.GetAsync(originalUrl);
        Assert.Equal(HttpStatusCode.NotFound, oldDownload.StatusCode);
        using var renamedDownload = await client.GetAsync(renamedUrl);
        Assert.Equal(HttpStatusCode.OK, renamedDownload.StatusCode);
        var oldUrl = new Uri((string)results[1].Attribute("url")!);
        Assert.Equal(originalBytes, await client.GetByteArrayAsync(oldUrl.PathAndQuery));
        var wrongGeneration = oldUrl.PathAndQuery.Replace("/1/1", "/2/1", StringComparison.Ordinal);
        using var wrong = await client.GetAsync(wrongGeneration);
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        await TestStorage.SetPermissionsAsync(provider, state.ResourceId, state.Security with { Owner = "revoked" });
        using var revoked = await client.GetAsync(oldUrl.PathAndQuery);
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
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
        Assert.Equal(state.Package.Length.ToString(), (string?)Assert.Single(XElement.Parse(got.SubResponseDataXml!).Elements()).Attribute("value"));
        Assert.Equal("VersionNotFound", (await Call(processor, state.ResourceId, SubRequestType.Versioning,
            ("VersioningRequestType", "RestoreVersion"), ("Version", "999.0"))).ErrorCode);
        Assert.Equal("InvalidArgument", (await Call(processor, state.ResourceId, SubRequestType.Versioning,
            ("VersioningRequestType", "RestoreVersion"), ("Version", "@1.0"))).ErrorCode);
        Assert.Equal("VersionNotFound", (await Call(processor, state.ResourceId, SubRequestType.Versioning,
            ("VersioningRequestType", "RestoreVersion"), ("Version", "1.1"))).ErrorCode);
        Assert.Equal("Success", (await Call(processor, state.ResourceId, SubRequestType.Versioning,
            ("VersioningRequestType", "RestoreVersion"), ("Version", "1.0"))).ErrorCode);
        Assert.Equal(2, (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!.Counters.Revisions);
    }

    [Fact]
    public async Task DuplicatePropertyIdsIsInvalidArgumentWithoutMutation()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/duplicate-properties.docx", MinimalDocx.Create(), TestActor.Value))!;
        var file = File(state.ResourceId, SubRequestType.Properties, ("Properties", "PropertyGet"));
        file.SubRequests[0].SubRequestDataXml = "<SubRequestData><PropertyIds/><PropertyIds/></SubRequestData>";

        var result = await new CellBridgeRequestProcessor(service).ExecuteAsync(new() { Requests = { file } },
            "https://host.test", TestActor.Value);

        Assert.Equal("InvalidArgument", Assert.Single(Assert.Single(result.Response.Responses).SubResponses).ErrorCode);
        MicrosoftResponseSchema.Validate(result.Response.ToSoapEnvelope());
        Assert.Empty(result.AcceptedSaves);
        Assert.Equal(state, await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
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
        var collision = await Assert.ThrowsAsync<DocumentOperationLockException>(() => service.RenameDocumentAsync(first.ResourceId, "second.docx", TestActor.Value).AsTask());
        Assert.Equal("FileAlreadyExists", collision.ErrorCode);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RenameDocumentAsync(first.ResourceId, "../other.docx", TestActor.Value).AsTask());
        var published = await service.RenameDocumentAsync(first.ResourceId, "FIRST.docx", TestActor.Value);
        var renamed = await provider.Documents.FindAsync(new DocumentIdentity.Path(first.PathKey));
        Assert.Equal(renamed!.StateVersion, published.StateVersion);
        var changedPath = await service.RenameDocumentAsync(first.ResourceId, "third.docx", TestActor.Value);
        Assert.Equal((await provider.Documents.FindAsync(new DocumentIdentity.Resource(first.ResourceId)))!.Counters.RetiredPaths, changedPath.Counters.RetiredPaths);
        Assert.Contains(first.PathKey, (await provider.Documents.ExportDocumentAsync(first.ResourceId))!.PathClaims);
        Assert.Equal(first.ResourceId, renamed!.ResourceId);
        Assert.Equal(folder + "FIRST.docx", renamed.Path);
        await service.CreateAsync(folder + "my%20file%20%C3%A6%25.docx", MinimalDocx.Create(), TestActor.Value);
        var escapedCollision = await Assert.ThrowsAsync<DocumentOperationLockException>(() => service.RenameDocumentAsync(first.ResourceId, "my file æ%.docx", TestActor.Value).AsTask());
        Assert.Equal("FileAlreadyExists", escapedCollision.ErrorCode);
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
    private static StorageProvider Memory() => StorageTestDocuments.Memory();

    [Fact]
    public async Task RestoreGeneratedMetadataClearsAnAlreadySynchronizedOpaqueGraph()
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var bytes = MinimalDocx.Create("retained file");
        var initial = (await service.CreateAsync("/restore-generated-metadata.docx", bytes, TestActor.Value))!;
        var fixture = new GraphFixture("opaque metadata"u8.ToArray());
        var upload = new FsshttpbCellRequest { DataElementPackage = new(), SubRequests =
            { new(RequestTypes.PutChanges) { RequestId = 1, Data = new PutChangesSubRequestData { StorageIndex = fixture.Index } } } };
        upload.DataElementPackage.DataElements.AddRange(fixture.Elements);
        var uploaded = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.Metadata, upload,
            new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(uploaded.Response.SubResponses).Status);
        Assert.Empty(uploaded.AcceptedSaves);
        Assert.Equal(1, uploaded.State.Counters.SaveReceipts);
        var before = (await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(1) { IncludeCatalog = true, IncludeBindings = true }))!;
        var query = new FsshttpbCellRequest { SubRequests = { new(RequestTypes.QueryChanges) { RequestId = 2,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } } } };
        var opened = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.Metadata, query,
            new Dictionary<string, string>(), TestActor.Value);
        var firstResponse = Assert.IsType<QueryChangesSubResponseData>(Assert.Single(opened.Response.SubResponses).Data);
        var warm = GenericPartitionGraphSnapshot.Create(opened.Response.DataElementPackage!.DataElements, firstResponse.StorageIndexExtendedGuid);
        Assert.NotEmpty(warm.Cells);
        var oldIndex = warm.Elements.Single(element => element.DataElementExtendedGuid.Equals(warm.StorageIndex));
        var knowledge = ClientKnowledge.Deserialize(new BinaryReaderEx(BinaryKnowledgeBuilder.FromElements(warm.Elements,
            warm.Cells.First().ShortId, before.Partition.Knowledge, mappingSerials: warm.MappingSerials.Values.SelectMany(value => value))));
        var restored = await service.RestoreRevisionAsync(initial.ResourceId, 1, uploaded.State.LatestRevisionNumber, "restore-inline", TestActor.Value);
        Assert.False(restored.IsReplay);
        Assert.Equal(3UL, restored.Revision.RevisionNumber);
        Assert.Equal(bytes, await StorageTestDocuments.PackageAsync(provider, restored.Revision.Package));
        var cleared = (await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(1) { IncludeCatalog = true, IncludeBindings = true }))!;
        Assert.Empty(cleared.Elements);
        Assert.Empty(cleared.Bindings);
        Assert.Equal(0, cleared.Partition.ElementCount);
        Assert.Equal(0, cleared.Partition.ElementBytes);
        Assert.True(cleared.Partition.Knowledge > before.Partition.Knowledge);
        ((QueryChangesSubRequestData)query.SubRequests[0].Data!).Knowledge = knowledge;
        var changes = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.Metadata, query,
            new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(changes.Response.SubResponses).Status);
        var nextIndex = Assert.Single(changes.Response.DataElementPackage!.DataElements,
            element => element.DataElementType == DataElementType.StorageIndexDataElementData);
        var updates = StorageIndexPatch.Read(nextIndex);
        Assert.All(StorageIndexPatch.Read(oldIndex), previous =>
            Assert.True(Assert.Single(updates, value => value.Type == previous.Type && Equals(value.Cell, previous.Cell) &&
                Equals(value.Revision, previous.Revision)).Target.IsNull));
        var applied = new DataElement(nextIndex.DataElementType, nextIndex.DataElementExtendedGuid, nextIndex.SerialNumber)
            { Data = StorageIndexPatch.Merge(StorageIndexPatch.Read(oldIndex), updates) };
        var merged = GenericPartitionGraphSnapshot.Create(warm.Elements.Concat(changes.Response.DataElementPackage.DataElements
            .Where(element => !element.DataElementExtendedGuid.Equals(nextIndex.DataElementExtendedGuid))).Append(applied), applied.DataElementExtendedGuid);
        Assert.Empty(merged.Cells);
        Assert.Empty(merged.Roots);
        var snapshot = (await provider.Documents.ExportDocumentAsync(initial.ResourceId))!;
        NormalizedDocumentRecovery.Validate(snapshot, provider.Limits);
        Assert.Equal(3, snapshot.Current.Head.Counters.Revisions);
        Assert.Equal(1, snapshot.Current.Head.Counters.RestoreReceipts);
    }
}
