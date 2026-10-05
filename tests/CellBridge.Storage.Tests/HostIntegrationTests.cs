using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed class HostIntegrationTests
{
    [Fact]
    public Task HostIdentitySurvivesCreationImportAndRestoration() => CheckIdentity(Memory());

    [PostgreSqlFact]
    public async Task PostgreSqlHostIdentitySurvivesProviderRecreation()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await CheckIdentity(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)),
            () => new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)));
    }

    private static async Task CheckIdentity(StorageProvider provider, Func<StorageProvider>? recreate = null)
    {
        var service = new CellBridgeDocumentService(provider);
        var id = Guid.NewGuid();
        var path = "/shared/" + id.ToString("N") + "%20%23.docx";
        var state = (await service.CreateAsync(id, path, MinimalDocx.Create(), TestActor.Value))!;
        Assert.Equal(id, state.ResourceId);
        Assert.Contains(id.ToString("D").ToUpperInvariant(), state.Etag);
        Assert.Null(await service.CreateAsync(id, "/other.docx", MinimalDocx.Create("different"), TestActor.Value));
        Assert.Null(await service.CreateAsync(Guid.NewGuid(), path, MinimalDocx.Create("different"), TestActor.Value));
        var importId = Guid.NewGuid();
        var imported = await service.ImportAsync(importId, "/" + importId.ToString("N") + ".docx", MinimalDocx.Create(),
            TestActor.Value.Identity, TestActor.Value);
        Assert.Equal(importId, imported!.ResourceId);
        var restarted = new CellBridgeDocumentService(recreate?.Invoke() ?? provider);
        var restored = await StoredDocument.RestoreAsync((await restarted.Provider.State.FindByResourceIdAsync(id))!, restarted.Provider.Content);
        Assert.Equal(id, restored.TransitionId);
        Assert.Equal(state.Etag, restored.Etag);
        var request = File(id, SubRequestType.GetDocMetaInfo, SubRequestType.GetVersions);
        request.Url = "https://unrelated.invalid/absent.docx";
        var result = await new CellBridgeRequestProcessor(restarted).ExecuteAsync(new() { Requests = { request } }, "https://host.test", TestActor.Value);
        var response = Assert.Single(result.Response.Responses);
        Assert.Equal(id, response.ResourceId);
        Assert.Equal("https://host.test" + path, response.Url);
        Assert.All(response.SubResponses, sub => Assert.Equal("Success", sub.ErrorCode));
        Assert.Equal("https://unrelated.invalid/absent.docx", request.Url);
        request.ResourceId = Guid.NewGuid().ToString();
        request.Url = "https://host.test" + state.Path;
        var unknown = await new CellBridgeRequestProcessor(restarted).ExecuteAsync(new() { Requests = { request } }, "https://host.test", TestActor.Value);
        Assert.Equal("FileNotExistsOrCannotBeCreated", Assert.Single(unknown.Response.Responses).ErrorCode);
    }

    [Fact]
    public async Task HostIdentityHasAtomicUniquenessAcrossPaths()
    {
        var provider = Memory();
        var id = Guid.NewGuid();
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => new CellBridgeDocumentService(provider)
            .CreateAsync(id, $"/concurrent-{i}.docx", MinimalDocx.Create(), TestActor.Value).AsTask()));
        Assert.Single(attempts, s => s is not null);
        Assert.Single(await provider.State.ListAsync(0, 100));
    }

    [Fact]
    public void InvalidCapabilitiesCannotBeConstructed()
    {
        Assert.Throws<ArgumentException>(() => new DocumentAccessLimit(Guid.Empty, DocumentAccess.Read));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentAccessLimit(Guid.NewGuid(), (DocumentAccess)4));
        Assert.Equal(DocumentAccess.Read | DocumentAccess.Write, new DocumentAccessLimit(Guid.NewGuid(), DocumentAccess.Write).Access);
    }

    [Fact]
    public async Task CapabilityOnlyRestrictsItsBoundResourceAndDoesNotReplaceStoredRevocation()
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var first = (await service.CreateAsync("/first.docx", MinimalDocx.Create(), TestActor.Value))!;
        var second = (await service.CreateAsync("/second.docx", MinimalDocx.Create(), TestActor.Value))!;
        var actor = TestActor.Value with { AccessLimit = new(first.ResourceId, DocumentAccess.Read) };
        var request = new CellStorageRequest { Requests =
        {
            File(first.ResourceId, SubRequestType.GetDocMetaInfo, SubRequestType.SchemaLock),
            File(second.ResourceId, SubRequestType.GetDocMetaInfo),
        } };
        request.Requests[0].SubRequests[1].SubRequestDataAttributes["SchemaLockRequestType"] = "GetLock";
        var response = (await new CellBridgeRequestProcessor(service).ExecuteAsync(request, "https://host.test", actor)).Response;
        Assert.Equal("Success", response.Responses[0].SubResponses[0].ErrorCode);
        Assert.Equal("FileUnauthorizedAccess", response.Responses[0].SubResponses[1].ErrorCode);
        Assert.Equal("FileUnauthorizedAccess", response.Responses[1].SubResponses[0].ErrorCode);
        Assert.Null(response.Responses[1].ResourceId);
        Assert.Equal(first.StateVersion, (await provider.State.FindByResourceIdAsync(first.ResourceId))!.StateVersion);
        Assert.Equal(second.StateVersion, (await provider.State.FindByResourceIdAsync(second.ResourceId))!.StateVersion);
        var limitedWrite = TestActor.Value with { AccessLimit = new(first.ResourceId, DocumentAccess.Write) };
        await provider.State.TransitionAsync(first.ResourceId, (current, now) => new StateTransition<bool>(current with
        { Security = current.Security with { Owner = "another-owner" } }, true));
        Assert.Equal(DocumentAccess.None, service.Access(limitedWrite, (await provider.State.FindByResourceIdAsync(first.ResourceId))!));
    }

    [Fact]
    public async Task HostEvaluatorAndReaderLimitsDoNotChangeSharedEditorGraph()
    {
        var provider = Memory();
        var owner = new CellBridgeActor(new("host:owner", "owner", "Owner"), true);
        var service = new CellBridgeDocumentService(provider, new HostAccess());
        var state = (await service.ImportAsync(Guid.NewGuid(), "/editors.docx", MinimalDocx.Create(), owner.Identity, TestActor.Value))!;
        var writer = TestActor.Value with { AccessLimit = new(state.ResourceId, DocumentAccess.Write) };
        var join = File(state.ResourceId, SubRequestType.EditorsTable);
        var attrs = join.SubRequests[0].SubRequestDataAttributes;
        attrs["EditorsTableRequestType"] = "JoinEditingSession";
        attrs["ClientID"] = Guid.NewGuid().ToString(); attrs["Timeout"] = "3600"; attrs["AsEditor"] = "true";
        var processor = new CellBridgeRequestProcessor(service);
        var joined = await processor.ExecuteAsync(new() { Requests = { join } }, "https://host.test", writer);
        Assert.Equal("Success", joined.Response.Responses[0].SubResponses[0].ErrorCode);
        var query = new FsshttpbCellRequest { SubRequests = { new(RequestTypes.QueryChanges) { RequestId = 1,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } } } };
        var first = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.EditorsTable, query, new Dictionary<string, string>(), writer);
        var second = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.EditorsTable, query, new Dictionary<string, string>(),
            writer with { AccessLimit = new(state.ResourceId, DocumentAccess.Read) });
        Assert.Equal(first.Response.ToByteArray(), second.Response.ToByteArray());
        Assert.False(first.State.Security.AccessFor(writer.Identity.Subject).HasFlag(DocumentAccess.Write));
        // Metadata editor presence is authorized by the host evaluator, not by a copied ACL.
        Assert.Single(first.State.Editors, e => e.AsEditor && e.Owner?.Subject == writer.Identity.Subject);
        var denied = await processor.ExecuteAsync(new() { Requests = { join } }, "https://host.test",
            writer with { AccessLimit = new(state.ResourceId, DocumentAccess.Read) });
        Assert.Equal("FileUnauthorizedAccess", denied.Response.Responses[0].SubResponses[0].ErrorCode);
    }

    [Fact]
    public async Task AcceptedSaveDescriptorsPreserveExactRevisionAndReceiptReplay()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/saved.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = StorageTests.Fixture("save-first");
        var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data);
        put.ExpectedStorageIndex = StorageIds.Restore(state.Partitions[0].StorageIndex!);
        var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        var accepted = Assert.Single(saved.AcceptedSaves);
        Assert.False(accepted.IsReplay); Assert.Equal(saved.State.Content, accepted.Content);
        Assert.Equal(saved.State.ContentVersion, accepted.ContentVersion); Assert.Equal(state.ResourceId, accepted.ResourceId);
        var retry = await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal(accepted with { IsReplay = true }, Assert.Single(retry.AcceptedSaves));
        var next = StorageTests.Fixture("save-second");
        var batch = new CellStorageRequest { Requests = { File(state.ResourceId, SubRequestType.Cell, SubRequestType.Properties) } };
        batch.Requests[0].SubRequests[0].SubRequestDataBinary = next.ToByteArray();
        var result = await new CellBridgeRequestProcessor(service).ExecuteAsync(batch, "https://host.test", TestActor.Value);
        Assert.False(Assert.Single(result.AcceptedSaves).IsReplay);
        Assert.Equal("NotSupported", result.Response.Responses[0].SubResponses[1].ErrorCode);
        Assert.NotEqual(accepted.ContentVersion, result.AcceptedSaves[0].ContentVersion);
        Assert.NotEqual(accepted.Content, result.AcceptedSaves[0].Content);
        Assert.Equal(saved.State.Content, accepted.Content);
    }

    [Fact]
    public async Task ReadOnlyRequestReportsWriteDenialAndCannotPublishASave()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/read-only.docx", MinimalDocx.Create(), TestActor.Value))!;
        var actor = TestActor.Value with { AccessLimit = new(state.ResourceId, DocumentAccess.Read) };
        var binary = StorageTests.Fixture("save-first");
        binary.SubRequests.Insert(0, new(RequestTypes.QueryAccess)
            { RequestId = 99, Data = new QueryAccessSubRequestData() });
        var request = File(state.ResourceId, SubRequestType.Cell);
        request.SubRequests[0].SubRequestDataBinary = binary.ToByteArray();
        var result = await new CellBridgeRequestProcessor(service).ExecuteAsync(new() { Requests = { request } }, "https://host.test", actor);
        var response = FsshttpbResponse.Deserialize(new BinaryReaderEx(result.Response.Responses[0].SubResponses[0].SubResponseDataBase64!));
        var access = Assert.IsType<QueryAccessSubResponseData>(response.SubResponses[0].Data);
        Assert.Null(access.ReadAccessError);
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, access.WriteAccessError!.ErrorCode);
        Assert.True(response.SubResponses[1].Status);
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, response.SubResponses[1].Error!.ErrorCode);
        Assert.Empty(result.AcceptedSaves);
        Assert.Equal(state.Content, (await provider.State.FindByResourceIdAsync(state.ResourceId))!.Content);
        Assert.Equal(state.StateVersion, (await provider.State.FindByResourceIdAsync(state.ResourceId))!.StateVersion);
    }

    [Fact]
    public async Task WriteOnlyEvaluatorStillAllowsAReadCeiling()
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider, new HostAccess(DocumentAccess.Write));
        var state = (await service.CreateAsync("/normalize.docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.Equal(DocumentAccess.Read, service.Access(TestActor.Value with
            { AccessLimit = new(state.ResourceId, DocumentAccess.Read) }, state));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedSaveRemainsVisibleWhenALaterBinaryQueryFails(bool useSoap)
    {
        var memory = Memory(); var content = new FailingReadsAfterCommit(memory.Content);
        var stateStore = new ObserveCommits(memory.State, () => content.FailReads = true);
        var service = new CellBridgeDocumentService(new(stateStore, content));
        var state = (await service.CreateAsync("/partial.docx", MinimalDocx.Create(), TestActor.Value))!;
        var binary = StorageTests.Fixture("save-first");
        Assert.IsType<PutChangesSubRequestData>(binary.SubRequests[0].Data).ExpectedStorageIndex = StorageIds.Restore(state.Partitions[0].StorageIndex!);
        binary.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 99,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } });
        AcceptedSave receipt;
        if (useSoap)
        {
            var request = File(state.ResourceId, SubRequestType.Cell);
            request.SubRequests[0].SubRequestDataBinary = binary.ToByteArray();
            var result = await new CellBridgeRequestProcessor(service).ExecuteAsync(new() { Requests = { request } }, "https://host.test", TestActor.Value);
            Assert.Equal("CellRequestFail", result.Response.Responses[0].SubResponses[0].ErrorCode);
            receipt = Assert.Single(result.AcceptedSaves);
        }
        else
        {
            var error = await Assert.ThrowsAsync<AcceptedSaveException>(() => service.ExecuteAsync(state.ResourceId,
                DocumentPartitionKind.FileContents, binary, new Dictionary<string, string>(), TestActor.Value).AsTask());
            Assert.IsType<IOException>(error.InnerException);
            receipt = Assert.Single(error.AcceptedSaves);
        }
        var current = (await memory.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal(current.ContentVersion, receipt.ContentVersion);
        Assert.Equal(current.Content, receipt.Content);
        Assert.False(receipt.IsReplay);
    }

    [Fact]
    public async Task AcceptedSaveRemainsVisibleWhenALaterFileLookupFails()
    {
        var memory = Memory(); var later = Guid.NewGuid();
        var store = new ObserveCommits(memory.State, () => { }) { UnavailableId = later };
        var service = new CellBridgeDocumentService(new(store, memory.Content));
        var state = (await service.CreateAsync("/lookup.docx", MinimalDocx.Create(), TestActor.Value))!;
        var first = File(state.ResourceId, SubRequestType.Cell);
        var binary = StorageTests.Fixture("save-first");
        Assert.IsType<PutChangesSubRequestData>(binary.SubRequests[0].Data).ExpectedStorageIndex = StorageIds.Restore(state.Partitions[0].StorageIndex!);
        first.SubRequests[0].SubRequestDataBinary = binary.ToByteArray();
        var error = await Assert.ThrowsAsync<AcceptedSaveException>(() => new CellBridgeRequestProcessor(service)
            .ExecuteAsync(new() { Requests = { first, File(later, SubRequestType.GetDocMetaInfo) } }, "https://host.test", TestActor.Value));
        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal((await memory.State.FindByResourceIdAsync(state.ResourceId))!.Content, Assert.Single(error.AcceptedSaves).Content);
    }

    [Theory]
    [InlineData("file:///tmp/example")]
    [InlineData("https://user:password@host.test")]
    [InlineData("https://host.test/path")]
    [InlineData("https://host.test/path/..")]
    [InlineData("https://host.test///")]
    [InlineData("https://host.test?query=1")]
    [InlineData("https://host.test#fragment")]
    public async Task ProcessorRejectsNonOrigins(string origin)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new CellBridgeRequestProcessor(new(Memory()))
            .ExecuteAsync(new(), origin, TestActor.Value));
    }

    internal static FssHttpRequest File(Guid id, params SubRequestType[] types) => new()
    {
        UseResourceId = true, ResourceId = id.ToString("D"), Url = "https://host.test/unused.docx", RequestToken = 1,
        SubRequests = types.Select((type, i) => new FssHttpSubRequest { Type = type, SubRequestToken = (ulong)i + 1 }).ToList(),
    };
    private static StorageProvider Memory() => new(new InMemoryStateStore(), new InMemoryContentStore());
    private sealed class HostAccess(DocumentAccess access = DocumentAccess.Read | DocumentAccess.Write) : ICellBridgeAccessEvaluator
    { public DocumentAccess Evaluate(CellBridgeActor actor, DocumentState state) => access; }

    private sealed class FailingReadsAfterCommit(IContentStore inner) : IContentStore
    {
        public bool FailReads { get; set; }
        public bool Durable => false; public bool Shared => false;
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken ct = default) => inner.WriteAsync(source, ct);
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken ct = default)
            => FailReads ? throw new IOException("Injected query read failure after a save.") : inner.OpenReadAsync(handle, ct);
    }

    private sealed class ObserveCommits(IDocumentStateStore inner, Action committed) : IDocumentStateStore
    {
        public Guid? UnavailableId { get; init; }
        public bool Durable => false; public bool Shared => false;
        public ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken ct = default)
            => id == UnavailableId ? throw new IOException("Injected later file lookup failure.") : inner.FindByResourceIdAsync(id, ct);
        public ValueTask<DocumentState?> FindByPathKeyAsync(string key, CancellationToken ct = default) => inner.FindByPathKeyAsync(key, ct);
        public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken ct = default) => inner.ListAsync(offset, limit, ct);
        public ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken ct = default) => inner.TryCreateAsync(state, ct);
        public ValueTask CheckHealthAsync(CancellationToken ct = default) => inner.CheckHealthAsync(ct);
        public async ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> change, CancellationToken ct = default)
        {
            var published = false;
            var result = await inner.TransitionAsync(id, (current, now) =>
            {
                var next = change(current, now);
                published = next.Next is not null && next.Next.Receipts.Length > current.Receipts.Length;
                return next;
            }, ct);
            if (published) committed();
            return result;
        }
    }
}
