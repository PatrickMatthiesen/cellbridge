using System.Text.Json;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;
using CellBridge.Tests;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class CancellationExecutionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PreCancelledRequestThrowsWithoutAcceptingOrMutating(bool postgreSql, bool soap)
    {
        await using var test = ProtocolExecutionTestProvider.Create(postgreSql);
        var before = await Create(test.Provider);
        var state = new HookState(test.Provider.State);
        var content = new HookContent(test.Provider.Content);
        var service = Service(test.Provider, state, content);
        var request = Proposal(before, MinimalDocx.Create("never accepted"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        if (soap)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CellBridgeRequestProcessor(service)
                .ExecuteAsync(Soap((before, request, false)), "https://host.test", TestActor.Value, cancellation.Token));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Execute(service, before, request, cancellation.Token));

        Assert.Equal(0, state.Mutations);
        Assert.Equal(0, content.Writes);
        await Unchanged(test.Provider, before);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationDuringPreparationOrBeforeCommitDoesNotAcceptSave(bool postgreSql, bool beforeCommit)
    {
        await using var test = ProtocolExecutionTestProvider.Create(postgreSql);
        var before = await Create(test.Provider);
        using var cancellation = new CancellationTokenSource();
        var state = new HookState(test.Provider.State);
        var content = new HookContent(test.Provider.Content);
        var triggered = false;
        void Cancel(CancellationToken token)
        {
            triggered = true;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        }
        if (beforeCommit)
            state.BeforeCommit = (_, token) => Cancel(token);
        else
            content.BeforeRead = (_, token) => { Cancel(token); return ValueTask.CompletedTask; };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Execute(Service(test.Provider, state, content),
            before, Proposal(before, MinimalDocx.Create("not committed")), cancellation.Token));

        Assert.True(triggered);
        Assert.Equal(0, state.Mutations);
        if (beforeCommit) Assert.True(content.Writes > 0);
        await Unchanged(test.Provider, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBetweenBinaryOperationsKeepsAcceptedSaveAndStopsRemainingOperations(bool postgreSql)
    {
        await using var test = ProtocolExecutionTestProvider.Create(postgreSql);
        var before = await Create(test.Provider);
        using var cancellation = new CancellationTokenSource();
        var state = new HookState(test.Provider.State);
        var content = new HookContent(test.Provider.Content);
        var triggered = false;
        state.AfterRead = (current, token) =>
        {
            if (current is not { Receipts.IsEmpty: false }) return;
            state.AfterRead = null;
            triggered = true;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        };
        var service = Service(test.Provider, state, content);
        var save = Proposal(before, MinimalDocx.Create("accepted before next query"));
        var request = Clone(save);
        request.SubRequests.Add(new(RequestTypes.QueryChanges)
            { RequestId = 2, Data = new QueryChangesSubRequestData() });
        request.SubRequests.Add(new(RequestTypes.AllocateExtendedGuidRange)
            { RequestId = 3, Data = new AllocateExtendedGuidRangeSubRequestData { RequestIdCount = 10 } });

        var result = await Execute(service, before, request, cancellation.Token);

        Assert.True(triggered);
        Cancelled(result);
        Assert.Equal(1UL, Assert.Single(result.Response.SubResponses).RequestId);
        var accepted = Assert.Single(result.AcceptedSaves);
        Assert.False(accepted.IsReplay);
        Assert.Equal(before.ContentVersion + 1, accepted.ContentVersion);
        await VerifyCommittedAndReplay(test.Provider, service, state, content, before, save, result);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LastFileSaveReturnsCancellationWhenCommitReturnsOrThrowsAfterPublishing(bool postgreSql, bool throwAfterCommit)
    {
        await using var test = ProtocolExecutionTestProvider.Create(postgreSql);
        var before = await Create(test.Provider);
        using var cancellation = new CancellationTokenSource();
        var state = new HookState(test.Provider.State);
        var content = new HookContent(test.Provider.Content);
        state.AfterCommit = (_, token) =>
        {
            state.AfterCommit = null;
            cancellation.Cancel();
            if (throwAfterCommit) token.ThrowIfCancellationRequested();
        };
        var service = Service(test.Provider, state, content);
        var request = Proposal(before, MinimalDocx.Create("last save committed"));

        var result = await Execute(service, before, request, cancellation.Token);

        Cancelled(result);
        Assert.Equal(before.ResourceId, Assert.Single(result.AcceptedSaves).ResourceId);
        Assert.Equal(1, state.Mutations);
        if (throwAfterCommit)
        {
            Assert.True(state.UncancelledReadsAfterCancellation > 0);
            Assert.True(content.UncancelledReadsAfterCancellation > 0);
        }
        await VerifyCommittedAndReplay(test.Provider, service, state, content, before, request, result);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MetadataCommitCancellationPreservesCompletedMetadataWithoutFileDelivery(bool postgreSql, bool throwAfterCommit)
    {
        await using var test = ProtocolExecutionTestProvider.Create(postgreSql);
        var before = await Create(test.Provider);
        using var cancellation = new CancellationTokenSource();
        var state = new HookState(test.Provider.State);
        var content = new HookContent(test.Provider.Content);
        state.AfterCommit = (_, token) =>
        {
            state.AfterCommit = null;
            cancellation.Cancel();
            if (throwAfterCommit) token.ThrowIfCancellationRequested();
        };
        var service = Service(test.Provider, state, content);
        var request = Metadata();

        var result = await Execute(service, before, request, cancellation.Token, DocumentPartitionKind.Metadata);

        Cancelled(result);
        Assert.Empty(result.AcceptedSaves);
        Assert.Equal(before.Content, result.State.Content);
        Assert.Equal(before.ContentVersion, result.State.ContentVersion);
        Assert.NotNull(result.State.Partitions.Single(p => p.Kind == 1).StorageIndex);
        Assert.Equal(1, Assert.Single(result.State.Receipts).PartitionKind);
        Assert.Equal(1, state.Mutations);
        await VerifyCommittedAndReplay(test.Provider, service, state, content, before, request, result,
            DocumentPartitionKind.Metadata);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancelledReceiptReadRevalidatesWithUncancelledTokenWithoutPublishingAgain(bool postgreSql, bool metadata)
    {
        await using var test = ProtocolExecutionTestProvider.Create(postgreSql);
        var before = await Create(test.Provider);
        var kind = metadata ? DocumentPartitionKind.Metadata : DocumentPartitionKind.FileContents;
        var request = metadata ? Metadata() : Proposal(before, MinimalDocx.Create("replayed save"));
        var saved = await Execute(new(test.Provider), before, request, default, kind);
        Assert.False(saved.IsCancelled);
        var receipt = Assert.Single(saved.State.Receipts);
        using var cancellation = new CancellationTokenSource();
        var state = new HookState(test.Provider.State);
        var content = new HookContent(test.Provider.Content);
        var triggered = false;
        content.BeforeRead = (handle, token) =>
        {
            Assert.Equal(receipt.Response, handle);
            content.BeforeRead = null;
            triggered = true;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        };
        var service = Service(test.Provider, state, content);

        var result = await Execute(service, before, request, cancellation.Token, kind);

        Assert.True(triggered);
        Cancelled(result);
        if (metadata) Assert.Empty(result.AcceptedSaves);
        else Assert.True(Assert.Single(result.AcceptedSaves).IsReplay);
        Assert.True(state.UncancelledReadsAfterCancellation > 0);
        Assert.True(content.UncancelledReadsAfterCancellation > 0);
        Assert.Equal(0, state.Mutations);
        Assert.Equal(0, content.Writes);
        await Unchanged(test.Provider, saved.State);
        var replay = await Execute(service, before, request, default, kind);
        Assert.False(replay.IsCancelled);
        Assert.Equal(saved.Response.ToByteArray(), replay.Response.ToByteArray());
        Assert.Equal(0, state.Mutations);
        Assert.Equal(0, content.Writes);
        await Unchanged(test.Provider, saved.State);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ParsedSoapPropagatesInnerCancellationAndKeepsAllEarlierSaves(bool postgreSql, bool metadata)
    {
        await using var test = ProtocolExecutionTestProvider.Create(postgreSql);
        var earlier = await Create(test.Provider);
        var current = await Create(test.Provider);
        var skipped = await Create(test.Provider);
        using var cancellation = new CancellationTokenSource();
        var state = new HookState(test.Provider.State);
        var content = new HookContent(test.Provider.Content);
        var earlierSave = Proposal(earlier, MinimalDocx.Create("earlier file"));
        var currentSave = metadata ? Metadata() : Proposal(current, MinimalDocx.Create("current file"));
        var triggered = false;
        state.AfterCommit = (committed, token) =>
        {
            if (committed.ResourceId != current.ResourceId) return;
            state.AfterCommit = null;
            triggered = true;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        };
        var service = Service(test.Provider, state, content);
        var request = Soap((earlier, earlierSave, false), (current, currentSave, metadata),
            (skipped, Proposal(skipped, MinimalDocx.Create("must not run")), false));
        state.UnreadableId = skipped.ResourceId;

        var result = await new CellBridgeRequestProcessor(service)
            .ExecuteAsync(request, "https://host.test", TestActor.Value, cancellation.Token);

        Assert.True(triggered);
        Assert.True(result.IsCancelled);
        Assert.Equal(2, result.Response.Responses.Count);
        Assert.Null(result.Response.Responses[0].ErrorCode);
        Assert.Equal("Success", Assert.Single(result.Response.Responses[0].SubResponses).ErrorCode);
        Assert.Equal("SubRequestFail", result.Response.Responses[1].ErrorCode);
        var cancelledCell = Assert.Single(result.Response.Responses[1].SubResponses);
        Assert.Equal(SubRequestType.Cell, cancelledCell.Type);
        Assert.Equal("CellRequestFail", cancelledCell.ErrorCode);
        Cancelled(FsshttpbResponse.Deserialize(new(cancelledCell.SubResponseDataBase64!)));
        Assert.Equal(metadata ? new[] { earlier.ResourceId } : new[] { earlier.ResourceId, current.ResourceId },
            result.AcceptedSaves.Select(s => s.ResourceId));
        MicrosoftResponseSchema.Validate(result.Response.ToSoapEnvelope());
        Assert.All(result.Response.Responses, file => Assert.Single(file.SubResponses));
        Assert.Equal(2, state.Mutations);
        await Unchanged(test.Provider, skipped);
        var currentState = (await test.Provider.State.FindByResourceIdAsync(current.ResourceId))!;
        Assert.Single(currentState.Receipts);
        if (metadata)
        {
            Assert.Equal(current.Content, currentState.Content);
            Assert.Equal(current.ContentVersion, currentState.ContentVersion);
        }
        var writes = content.Writes;
        var mutations = state.Mutations;
        foreach (var (document, save, kind) in new[]
        {
            (earlier, earlierSave, DocumentPartitionKind.FileContents),
            (current, currentSave, metadata ? DocumentPartitionKind.Metadata : DocumentPartitionKind.FileContents),
        })
        {
            var persisted = (await test.Provider.State.FindByResourceIdAsync(document.ResourceId))!;
            var expected = await test.Provider.Content.ReadVerifiedAsync(Assert.Single(persisted.Receipts).Response!);
            var retry = await Execute(service, document, save, default, kind);
            Assert.False(retry.IsCancelled);
            Assert.Equal(expected, retry.Response.ToByteArray());
            await Unchanged(test.Provider, persisted);
        }
        Assert.Equal(writes, content.Writes);
        Assert.Equal(mutations, state.Mutations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationDuringFinalSoapIdentityLookupRetainsAcceptedSave(bool postgreSql, bool metadata)
    {
        await using var test = ProtocolExecutionTestProvider.Create(postgreSql);
        var before = await Create(test.Provider);
        var skipped = await Create(test.Provider);
        using var cancellation = new CancellationTokenSource();
        var state = new HookState(test.Provider.State) { UnreadableId = skipped.ResourceId };
        var content = new HookContent(test.Provider.Content);
        var triggered = false;
        state.AfterRead = (current, token) =>
        {
            if (current is not { Receipts.IsEmpty: false }) return;
            state.AfterRead = null;
            triggered = true;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        };
        var save = metadata ? Metadata() : Proposal(before, MinimalDocx.Create("saved before final lookup"));
        var first = HostIntegrationTests.File(before.ResourceId, SubRequestType.Cell);
        HostIntegrationTests.SetBinary(first.SubRequests[0], save.ToByteArray());
        if (metadata) first.SubRequests[0].SubRequestDataAttributes["PartitionID"] = StoredDocument.MetadataPartitionId.ToString();
        var second = HostIntegrationTests.File(skipped.ResourceId, SubRequestType.GetDocMetaInfo);
        second.RequestToken = 2;
        var service = Service(test.Provider, state, content);

        var result = await new CellBridgeRequestProcessor(service).ExecuteAsync(
            new() { Requests = { first, second } }, "https://host.test", TestActor.Value, cancellation.Token);

        Assert.True(triggered);
        Assert.True(result.IsCancelled);
        var file = Assert.Single(result.Response.Responses);
        Assert.Equal("SubRequestFail", file.ErrorCode);
        Assert.Equal("Success", Assert.Single(file.SubResponses).ErrorCode);
        Assert.False(FsshttpbResponse.Deserialize(new(file.SubResponses[0].SubResponseDataBase64!)).Status);
        if (metadata) Assert.Empty(result.AcceptedSaves);
        else Assert.False(Assert.Single(result.AcceptedSaves).IsReplay);
        MicrosoftResponseSchema.Validate(result.Response.ToSoapEnvelope());
        Assert.Equal(1, state.Mutations);
        await Unchanged(test.Provider, skipped);
        var current = (await test.Provider.State.FindByResourceIdAsync(before.ResourceId))!;
        if (metadata)
        {
            Assert.Equal(before.Content, current.Content);
            Assert.Equal(before.ContentVersion, current.ContentVersion);
            Assert.Equal(1, Assert.Single(current.Receipts).PartitionKind);
        }
        var writes = content.Writes;
        var replay = await Execute(service, before, save, default,
            metadata ? DocumentPartitionKind.Metadata : DocumentPartitionKind.FileContents);
        Assert.Equal(await test.Provider.Content.ReadVerifiedAsync(Assert.Single(current.Receipts).Response!),
            replay.Response.ToByteArray());
        Assert.Equal(writes, content.Writes);
        Assert.Equal(1, state.Mutations);
        await Unchanged(test.Provider, current);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancelledReplayDoesNotBypassCurrentAuthorityOrReceiptDigest(bool postgreSql, bool changeDigest)
    {
        await using var test = ProtocolExecutionTestProvider.Create(postgreSql);
        var before = await Create(test.Provider);
        var request = Proposal(before, MinimalDocx.Create("accepted original"));
        var saved = await Execute(new(test.Provider), before, request);
        using var cancellation = new CancellationTokenSource();
        var state = new HookState(test.Provider.State);
        var content = new HookContent(test.Provider.Content);
        DocumentState? changed = null;
        content.BeforeRead = async (_, token) =>
        {
            content.BeforeRead = null;
            changed = await test.Provider.State.TransitionAsync(before.ResourceId, (current, _) =>
            {
                var next = changeDigest
                    ? current with { Receipts = [Assert.Single(current.Receipts) with { Digest = "changed digest" }] }
                    : current with { Security = current.Security with { Owner = "revoked-owner" } };
                next = next with { StateVersion = current.StateVersion + 1 };
                return new StateTransition<DocumentState>(next, next);
            });
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Execute(Service(test.Provider, state, content),
            before, request, cancellation.Token));

        Assert.NotNull(changed);
        Assert.True(state.UncancelledReadsAfterCancellation > 0);
        Assert.Equal(0, state.Mutations);
        Assert.Equal(0, content.Writes);
        Assert.Equal(saved.State.Content, changed.Content);
        await Unchanged(test.Provider, changed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReceiptReadDoesNotSwallowUnrelatedCancellationOrStorageFailure(bool postgreSql, bool storageFailure)
    {
        await using var test = ProtocolExecutionTestProvider.Create(postgreSql);
        var before = await Create(test.Provider);
        var request = Proposal(before, MinimalDocx.Create("accepted before unrelated failure"));
        var saved = await Execute(new(test.Provider), before, request);
        using var callerCancellation = new CancellationTokenSource();
        using var otherCancellation = new CancellationTokenSource();
        otherCancellation.Cancel();
        var state = new HookState(test.Provider.State);
        var content = new HookContent(test.Provider.Content);
        Exception failure = storageFailure ? new IOException("Injected receipt read failure.")
            : new OperationCanceledException("Injected unrelated cancellation.", otherCancellation.Token);
        content.BeforeRead = (_, _) =>
        {
            content.BeforeRead = null;
            if (storageFailure) callerCancellation.Cancel();
            throw failure;
        };

        var thrown = await Record.ExceptionAsync(() => Execute(Service(test.Provider, state, content),
            before, request, callerCancellation.Token));

        Assert.Same(failure, thrown);
        Assert.Equal(0, state.Mutations);
        Assert.Equal(0, content.Writes);
        await Unchanged(test.Provider, saved.State);
    }

    private static CellBridgeDocumentService Service(StorageProvider provider, HookState state, HookContent content) =>
        new(new(state, content, provider.Limits));

    private static Task<CellExecution> Execute(CellBridgeDocumentService service, DocumentState document,
        FsshttpbCellRequest request, CancellationToken token = default,
        DocumentPartitionKind kind = DocumentPartitionKind.FileContents) =>
        service.ExecuteAsync(document.ResourceId, kind, request, new Dictionary<string, string>(), TestActor.Value, token).AsTask();

    private static async Task<DocumentState> Create(StorageProvider provider) =>
        (await new CellBridgeDocumentService(provider).CreateAsync("/cancel-" + Guid.NewGuid().ToString("N") + ".docx",
            MinimalDocx.Create(), TestActor.Value))!;

    private static async Task Unchanged(StorageProvider provider, DocumentState expected) =>
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(
            await provider.State.FindByResourceIdAsync(expected.ResourceId)));

    private static void Cancelled(CellExecution result)
    {
        Assert.True(result.IsCancelled);
        Cancelled(result.Response);
        Cancelled(FsshttpbResponse.Deserialize(new(result.Response.ToByteArray())));
    }

    private static void Cancelled(FsshttpbResponse response)
    {
        Assert.True(response.Status);
        Assert.Equal(ErrorType.Win32, response.Error!.Type);
        Assert.Equal(995UL, response.Error.ErrorCode);
    }

    private static async Task VerifyCommittedAndReplay(StorageProvider provider, CellBridgeDocumentService service,
        HookState state, HookContent content, DocumentState before, FsshttpbCellRequest request, CellExecution result,
        DocumentPartitionKind kind = DocumentPartitionKind.FileContents)
    {
        var current = (await provider.State.FindByResourceIdAsync(before.ResourceId))!;
        Assert.Equal(JsonSerializer.Serialize(current), JsonSerializer.Serialize(result.State));
        Assert.Equal(before.StateVersion + 1, current.StateVersion);
        Assert.Single(current.Receipts);
        var receiptBytes = await provider.Content.ReadVerifiedAsync(current.Receipts[0].Response!);
        var completed = new FsshttpbResponse
            { SubResponses = result.Response.SubResponses, DataElementPackage = result.Response.DataElementPackage };
        Assert.Equal(receiptBytes, completed.ToByteArray());
        var writes = content.Writes;
        var mutations = state.Mutations;
        var replay = await Execute(service, before, request, default, kind);
        Assert.False(replay.IsCancelled);
        Assert.Equal(receiptBytes, replay.Response.ToByteArray());
        if (kind == DocumentPartitionKind.FileContents)
        {
            var accepted = Assert.Single(result.AcceptedSaves);
            Assert.Equal(current.Content, accepted.Content);
            Assert.Equal(accepted with { IsReplay = true }, Assert.Single(replay.AcceptedSaves));
            Assert.Equal(PartitionGraphSnapshot.Create(request.DataElementPackage!.DataElements,
                Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data).StorageIndex).Materialize(),
                await provider.Content.ReadVerifiedAsync(current.Content));
            var graph = await DocumentPartition.RestoreFileGraphAsync(current, provider.Content, provider.Limits);
            Assert.Equal(await provider.Content.ReadVerifiedAsync(current.Content), graph.Materialize());
        }
        else Assert.Empty(replay.AcceptedSaves);
        Assert.Equal(writes, content.Writes);
        Assert.Equal(mutations, state.Mutations);
        await Unchanged(provider, current);
    }

    private static FsshttpbCellRequest Clone(FsshttpbCellRequest request) =>
        FsshttpbCellRequest.Deserialize(new(request.ToByteArray()));

    private static FsshttpbCellRequest Proposal(DocumentState state, byte[] bytes)
    {
        var id = DocumentStorageIdentity.Create();
        var identity = new StorageManifestBuilder.StableIdentity(id.StorageManifestGuid, id.CellManifestGuid,
            id.RevisionManifestGuid, id.ObjectGroupGuid, id.ObjectDataBlobGuid, id.ObjectGuid,
            id.RevisionId, id.CellId, id.SerialGuid);
        var generated = FileContentPartitionBuilder.BuildQueryChangesResponse(1, bytes, identity, 1);
        var index = Assert.IsType<QueryChangesSubResponseData>(generated.SubResponses[0].Data).StorageIndexExtendedGuid;
        return new() { DataElementPackage = generated.DataElementPackage, SubRequests =
        { new(RequestTypes.PutChanges) { RequestId = 1, Data = new PutChangesSubRequestData
            { StorageIndex = index, ExpectedStorageIndex = StorageIds.Restore(state.Partitions[0].StorageIndex!), Flags = 1 } } } };
    }

    private static FsshttpbCellRequest Metadata() => MetadataPublicationTests.Initial(new GraphFixture([1, 2, 3], blob: true));

    private static CellStorageRequest Soap(params (DocumentState Document, FsshttpbCellRequest Save, bool Metadata)[] files)
    {
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace ns = "http://schemas.microsoft.com/sharepoint/soap/";
        var requests = files.Select((file, i) =>
        {
            var bytes = file.Save.ToByteArray();
            var data = new XElement(ns + "SubRequestData", new XAttribute("BinaryDataSize", bytes.Length),
                Convert.ToBase64String(bytes));
            if (file.Metadata) data.Add(new XAttribute("PartitionID", StoredDocument.MetadataPartitionId));
            var request = new XElement(ns + "Request", new XAttribute("Url", "https://host.test" + file.Document.Path),
                new XAttribute("RequestToken", i + 1), new XAttribute("UseResourceID", true),
                new XAttribute("ResourceID", file.Document.ResourceId),
                new XElement(ns + "SubRequest", new XAttribute("Type", "Cell"), new XAttribute("SubRequestToken", 1), data));
            if (i == 1) request.Add(new XElement(ns + "SubRequest", new XAttribute("Type", "SchemaLock"), new XAttribute("SubRequestToken", 2),
                    new XElement(ns + "SubRequestData", new XAttribute("SchemaLockRequestType", "GetLock"),
                        new XAttribute("SchemaLockID", Guid.NewGuid()), new XAttribute("ClientID", Guid.NewGuid()),
                        new XAttribute("Timeout", 3600))));
            return request;
        });
        return CellStorageRequestParser.Parse(new XElement(soap + "Envelope", new XElement(soap + "Body",
            new XElement(ns + "ExecuteCellStorageRequest", new XElement(ns + "RequestVersion",
                new XAttribute("Version", 2), new XAttribute("MinorVersion", 0)),
                new XElement(ns + "RequestCollection", new XAttribute("CorrelationId", Guid.NewGuid()), requests)))).ToString());
    }

    private sealed class HookContent(IContentStore inner) : IContentStore
    {
        public Func<ContentHandle, CancellationToken, ValueTask>? BeforeRead { get; set; }
        public int Writes { get; private set; }
        public int UncancelledReadsAfterCancellation { get; private set; }
        private CancellationToken requestToken;
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;

        public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken token = default)
        {
            if (token.CanBeCanceled) requestToken = token;
            Writes++;
            return await inner.WriteAsync(source, token);
        }

        public async ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken token = default)
        {
            if (token.CanBeCanceled) requestToken = token;
            if (requestToken.IsCancellationRequested && !token.CanBeCanceled) UncancelledReadsAfterCancellation++;
            if (BeforeRead is { } hook) await hook(handle, token);
            return await inner.OpenReadAsync(handle, token);
        }
    }

    private sealed class HookState(IDocumentStateStore inner) : IDocumentStateStore
    {
        public Action<DocumentState?, CancellationToken>? AfterRead { get; set; }
        public Action<DocumentState, CancellationToken>? BeforeCommit { get; set; }
        public Action<DocumentState, CancellationToken>? AfterCommit { get; set; }
        public Guid? UnreadableId { get; set; }
        public int Mutations { get; private set; }
        public int UncancelledReadsAfterCancellation { get; private set; }
        private CancellationToken requestToken;
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;

        public async ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken token = default)
        {
            Assert.NotEqual(UnreadableId, id);
            ObserveRead(token);
            var current = await inner.FindByResourceIdAsync(id, token);
            AfterRead?.Invoke(current, token);
            return current;
        }

        public async ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition,
            CancellationToken token = default)
        {
            ObserveRead(token);
            DocumentState? next = null;
            bool publishedReceipt = false;
            var result = await inner.TransitionAsync(id, (current, now) =>
            {
                var change = transition(current, now);
                next = change.Next;
                publishedReceipt = next is not null && next.Receipts.Length > current.Receipts.Length;
                if (publishedReceipt) BeforeCommit?.Invoke(next!, token);
                return change;
            }, token);
            if (next is not null) Mutations++;
            if (publishedReceipt) AfterCommit?.Invoke(next!, token);
            return result;
        }

        private void ObserveRead(CancellationToken token)
        {
            if (token.CanBeCanceled) requestToken = token;
            if (requestToken.IsCancellationRequested && !token.CanBeCanceled) UncancelledReadsAfterCancellation++;
        }

        public ValueTask<DocumentState?> FindByPathKeyAsync(string path, CancellationToken token = default) => inner.FindByPathKeyAsync(path, token);
        public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken token = default) => inner.ListAsync(offset, limit, token);
        public ValueTask<bool> TryCreateAsync(DocumentState document, CancellationToken token = default) => inner.TryCreateAsync(document, token);
        public ValueTask CheckHealthAsync(CancellationToken token = default) => inner.CheckHealthAsync(token);
    }
}
