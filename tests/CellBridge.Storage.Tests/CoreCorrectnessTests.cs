using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Tests;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class CoreCorrectnessTests
{
    [Fact]
    public async Task UnsupportedOuterVersionReturnsVersionErrorWithoutDispatch()
    {
        var provider = Memory();
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/version.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = new CellStorageRequest { Version = 3, MinorVersion = 0,
            Requests = { HostIntegrationTests.File(state.ResourceId, SubRequestType.GetDocMetaInfo) } };

        var result = await new CellBridgeRequestProcessor(new(provider)).ExecuteAsync(request, "https://host.test", TestActor.Value);

        Assert.Equal("IncompatibleVersion", result.Response.VersionErrorCode);
        Assert.NotNull(result.Response.VersionErrorMessage);
        Assert.Empty(result.Response.Responses);
        Assert.Equal(state, await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
    }

    [Fact]
    public async Task DirectDuplicateSubRequestTokenPreflightsBeforeEarlierSave()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/duplicate.docx", MinimalDocx.Create(), TestActor.Value))!;
        var binary = StorageTests.Fixture("save-first");
        StorageTests.Rebase(binary, await StorageTestDocuments.GraphAsync(service,state));
        var file = HostIntegrationTests.File(state.ResourceId, SubRequestType.Cell, SubRequestType.WhoAmI);
        HostIntegrationTests.SetBinary(file.SubRequests[0], binary.ToByteArray());
        file.SubRequests[1].SubRequestToken = file.SubRequests[0].SubRequestToken;

        var result = await new CellBridgeRequestProcessor(service).ExecuteAsync(
            new() { Requests = { file } }, "https://host.test", TestActor.Value);

        Assert.Equal("InvalidRequestDependencyType", Assert.Single(result.Response.Responses).ErrorCode);
        Assert.Empty(result.AcceptedSaves);
        Assert.Equal(state, await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
    }

    [Fact]
    public async Task QueryAndMetadataRejectStaleEtagWithoutMutation()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/etag.docx", MinimalDocx.Create(), TestActor.Value))!;
        var query = new FsshttpbCellRequest
        {
            SubRequests = { new(RequestTypes.QueryChanges) { RequestId = 1, Data = new QueryChangesSubRequestData() } },
        };
        var stale = new Dictionary<string, string> { ["Etag"] = "\"stale\"" };

        var read = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, query, stale, TestActor.Value);
        Assert.Equal("CellRequestFail", read.LockError);
        Assert.Equal(state, await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));

        var metadata = MetadataPublicationTests.Initial(new GraphFixture([1], blob: true));
        var write = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata, metadata, stale, TestActor.Value);
        Assert.Equal("CellRequestFail", write.LockError);
        Assert.Equal(state, await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
    }

    [Fact]
    public async Task ReceiptReplayPrecedesStaleEtagAndDoesNotReapplyTimestamp()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/replay.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = StorageTests.Fixture("save-first");
        StorageTests.Rebase(request, await StorageTestDocuments.GraphAsync(service,state));
        var supplied = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var attributes = new Dictionary<string, string>
        {
            ["Etag"] = state.Etag,
            ["LastModifiedTime"] = supplied.ToFileTimeUtc().ToString(),
        };

        var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, attributes, TestActor.Value);
        Assert.Equal(supplied, saved.State.ModifiedUtc);
        Assert.False(Assert.Single(saved.AcceptedSaves).IsReplay);

        attributes["LastModifiedTime"] = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToFileTimeUtc().ToString();
        var replay = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, attributes, TestActor.Value);
        Assert.True(Assert.Single(replay.AcceptedSaves).IsReplay);
        Assert.Equal(supplied, replay.State.ModifiedUtc);
        Assert.Equal(saved.State.StateVersion, replay.State.StateVersion);
    }

    [Fact]
    public async Task EtagIsRecheckedInsideFinalFileCommitTransition()
    {
        var hookedContent = new HookContent(new InMemoryContentStore());
        var provider = StorageTestDocuments.Memory(hookedContent);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/etag-race.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = StorageTests.Fixture("save-first");
        StorageTests.Rebase(request, await StorageTestDocuments.GraphAsync(service,state));
        var concurrentBytes = MinimalDocx.Create("concurrent");
        var concurrentRequest = await StorageTestDocuments.SaveRequestAsync(service,state,concurrentBytes);
        hookedContent.AfterNextWrite = async () =>
        {
            var concurrent = await service.ExecuteAsync(state.ResourceId,DocumentPartitionKind.FileContents,concurrentRequest,
                new Dictionary<string,string>(),TestActor.Value);
            Assert.False(Assert.Single(concurrent.Response.SubResponses).Status);
        };

        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string> { ["Etag"] = state.Etag }, TestActor.Value);

        Assert.Equal("CellRequestFail", result.LockError);
        var current = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.Equal(concurrentBytes,await StorageTestDocuments.PackageAsync(provider,current.Package));
        Assert.Equal(state.ContentVersion + 1, current.ContentVersion);
        Assert.Equal(1,current.Counters.SaveReceipts);
    }

    [Theory]
    [InlineData("ExclusiveLockID", "not-a-guid")]
    [InlineData("Timeout", "59")]
    [InlineData("LastModifiedTime", "not-a-filetime")]
    public async Task DirectServiceRejectsMalformedCellScalarsBeforeContentIo(string key, string value)
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/scalar.docx", MinimalDocx.Create(), TestActor.Value))!;
        var query = new FsshttpbCellRequest
        {
            SubRequests = { new(RequestTypes.QueryAccess) { RequestId = 0 } },
        };
        var attributes = new Dictionary<string, string> { [key] = value };
        if (key == "Timeout") attributes["ExclusiveLockID"] = Guid.NewGuid().ToString();

        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            query, attributes, TestActor.Value);

        Assert.Equal("InvalidArgument", result.LockError);
        Assert.Equal(state, await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
    }

    [Fact]
    public async Task LaterMalformedBinaryOperationRejectsWholeRequestBeforeSave()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/atomic.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = StorageTests.Fixture("save-first");
        StorageTests.Rebase(request, await StorageTestDocuments.GraphAsync(service,state));
        request.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 99 });

        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);

        Assert.True(result.Response.Status);
        Assert.Equal((ulong)CellErrorCode.RequestStreamSchemaError, result.Response.Error?.ErrorCode);
        Assert.Empty(result.AcceptedSaves);
        Assert.Equal(state, await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
    }

    private static StorageProvider Memory() => StorageTestDocuments.Memory();

    private sealed class HookContent(IContentStore inner) : IContentStore
    {
        public Func<Task>? AfterNextWrite { get; set; }
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
        {
            var handle = await inner.WriteAsync(source, cancellationToken);
            var hook = AfterNextWrite;
            AfterNextWrite = null;
            if (hook is not null) await hook();
            return handle;
        }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(handle, cancellationToken);
    }
}
