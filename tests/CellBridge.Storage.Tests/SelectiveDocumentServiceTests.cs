using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Tests;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class SelectiveDocumentServiceTests
{
    private static readonly CellBridgeActor Reader = new(new("selective:reader", "reader", "Reader"));
    private static readonly DocumentAuthorizationScope Scope = new("tenant", "workspace");

    [Fact]
    public async Task BuiltInAuthorizationUsesSelectiveReadsWithoutFullStateMaterialization()
    {
        var content = new InMemoryContentStore();
        var raw = new InMemoryDocumentRepository(content);
        var seed = new CellBridgeDocumentService(new(raw, content));
        var state = (await seed.CreateAsync("/shared/selective-service.docx", MinimalDocx.Create(), TestActor.Value))!;
        await RevisionHistoryTests.Save(seed, state, "second");
        var selective = new SelectiveOnlyStateStore(raw);
        var service = new CellBridgeDocumentService(new(selective, content));

        var metadata = await service.GetDocumentMetadataAsync(state.ResourceId, TestActor.Value);
        var page = await service.ListRevisionSummariesAsync(state.ResourceId, 0, 1, TestActor.Value);
        var revision = await service.GetRevisionSummaryAsync(state.ResourceId, 1, TestActor.Value);

        Assert.Equal(state.ResourceId, metadata.ResourceId);
        Assert.Equal(2UL, metadata.LatestRevisionNumber);
        Assert.Equal(2UL, Assert.Single(page.Revisions).RevisionNumber);
        Assert.Equal(1, page.NextOffset);
        Assert.Equal(state.Package, revision.Package);
        Assert.Equal(0, selective.FullReadCount);
        Assert.True(selective.MetadataReadCount >= 1);
        Assert.True(selective.HistoryReadCount >= 2);
    }

    [Fact]
    public async Task ExternalAdmissionIsFencedWhenBindingChangesDuringAsyncRead()
    {
        var content = new InMemoryContentStore();
        var raw = new InMemoryDocumentRepository(content);
        var seed = new CellBridgeDocumentService(new(raw, content));
        var state = (await seed.CreateAsync("/shared/selective-external.docx", MinimalDocx.Create(), TestActor.Value))!;
        var gate = new GatedAuthorizationProvider();
        var selective = new SelectiveOnlyStateStore(raw);
        var service = new CellBridgeDocumentService(new(selective, content), documentAuthorizationProvider: gate);
        Assert.True(await service.BindExternalAuthorizationAsync(state.ResourceId, state.StateVersion, null, Scope));

        var read = service.GetDocumentMetadataAsync(state.ResourceId, Reader).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var before = (await raw.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.True(await service.BindExternalAuthorizationAsync(state.ResourceId, before.StateVersion,
            before.Security.AuthorizationPolicy, new("tenant", "rebound-workspace")));
        gate.Release.TrySetResult();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => read);
        Assert.Equal(0, selective.FullReadCount);
    }

    [Fact]
    public async Task ExternalAdmissionIsFencedWhenDocumentIsDeletedDuringAsyncRead()
    {
        var content = new InMemoryContentStore();
        var raw = new InMemoryDocumentRepository(content);
        var seed = new CellBridgeDocumentService(new(raw, content));
        var state = (await seed.CreateAsync("/shared/selective-delete.docx", MinimalDocx.Create(), TestActor.Value))!;
        var gate = new GatedAuthorizationProvider();
        var selective = new SelectiveOnlyStateStore(raw);
        var service = new CellBridgeDocumentService(new(selective, content), documentAuthorizationProvider: gate);
        Assert.True(await service.BindExternalAuthorizationAsync(state.ResourceId, state.StateVersion, null, Scope));

        var read = service.GetDocumentMetadataAsync(state.ResourceId, Reader).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var current = (await raw.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.True((await raw.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(state.ResourceId,
            current.LifecycleGeneration, current.StateVersion))).Applied);
        gate.Release.TrySetResult();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => read);
        Assert.Equal(0, selective.FullReadCount);
    }

    [Fact]
    public async Task UnknownAndDeniedExternalDocumentsFailClosed()
    {
        var content = new InMemoryContentStore();
        var raw = new InMemoryDocumentRepository(content);
        var seed = new CellBridgeDocumentService(new(raw, content));
        var state = (await seed.CreateAsync("/shared/selective-denial.docx", MinimalDocx.Create(), TestActor.Value))!;
        var auth = new GatedAuthorizationProvider { Allowed = false };
        var service = new CellBridgeDocumentService(new(new SelectiveOnlyStateStore(raw), content),
            documentAuthorizationProvider: auth);
        Assert.True(await service.BindExternalAuthorizationAsync(state.ResourceId, state.StateVersion, null, Scope));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetDocumentMetadataAsync(Guid.NewGuid(), Reader).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetDocumentMetadataAsync(state.ResourceId, Reader).AsTask());
        Assert.Equal(1, auth.RequestCount);
    }

    [Fact]
    public async Task NativeGrantsAndActorCeilingsApplyToSelectiveReadMethods()
    {
        var content = new InMemoryContentStore();
        var raw = new InMemoryDocumentRepository(content);
        var seed = new CellBridgeDocumentService(new(raw, content));
        var state = (await seed.CreateAsync("/shared/selective-grants.docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.True((await raw.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(state.ResourceId,
            StorageTestDocuments.Admission(state), state.Security with
            { Grants = state.Security.Grants.SetItem(Reader.Identity.Subject, DocumentAccess.Read) }, state.AuthorityGeneration))).Changed);
        var service = new CellBridgeDocumentService(new(new SelectiveOnlyStateStore(raw), content));

        var metadata = await service.GetDocumentMetadataAsync(state.ResourceId, Reader);
        var summary = await service.GetRevisionSummaryAsync(state.ResourceId, 1, Reader);
        var limited = Reader with { AccessLimit = new(state.ResourceId, DocumentAccess.None) };

        Assert.Equal(state.ResourceId, metadata.ResourceId);
        Assert.Equal(state.ResourceId, summary.ResourceId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetDocumentMetadataAsync(state.ResourceId, limited).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetRevisionSummaryAsync(state.ResourceId, 1, limited).AsTask());
    }

    [Fact]
    public async Task CustomEvaluatorReceivesBoundedCurrentAuthorizationMetadata()
    {
        var content = new InMemoryContentStore();
        var raw = new InMemoryDocumentRepository(content);
        var seed = new CellBridgeDocumentService(new(raw, content));
        var state = (await seed.CreateAsync("/shared/selective-fallback.docx", MinimalDocx.Create(), TestActor.Value))!;
        var evaluator = new InspectingEvaluator();
        var selective = new SelectiveOnlyStateStore(raw);
        var service = new CellBridgeDocumentService(new(selective, content), evaluator);

        var metadata = await service.GetDocumentMetadataAsync(state.ResourceId, TestActor.Value);

        Assert.Equal(state.ResourceId, metadata.ResourceId);
        Assert.Equal(state.ContentVersion, evaluator.SeenContentVersion);
        Assert.Equal(state.Package.Length, evaluator.SeenContentLength);
        Assert.Equal(0, selective.FullReadCount);
        Assert.True(selective.MetadataReadCount > 0);
    }

    [Fact]
    public async Task CatalogCarriesPublicationStatusWithoutLoadingFullState()
    {
        var content = new InMemoryContentStore();
        var raw = new InMemoryDocumentRepository(content);
        var seed = new CellBridgeDocumentService(new(raw, content));
        var state = (await seed.CreateAsync("/shared/selective-catalog.docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.True((await raw.UpdateDeliveryAsync(new DocumentDeliveryCommand.Bind(state.ResourceId,
            state.LifecycleGeneration, state.StateVersion, new(Guid.NewGuid(), "destination", "remote-r0", 1)))).Applied);
        await RevisionHistoryTests.Save(seed, state, "pending external content");
        var pending = (await raw.ReadNextDeliveryAsync(state.ResourceId))!;
        Assert.True((await raw.UpdateDeliveryAsync(new DocumentDeliveryCommand.Block(state.ResourceId, pending, "destination conflict"))).Applied);
        var selective = new SelectiveOnlyStateStore(raw);
        var service = new CellBridgeDocumentService(new(selective, content));

        var page = await AuthorizedDocumentCatalog.ReadAsync(service, TestActor.Value, 0, 10);

        var summary = Assert.Single(page.Documents);
        Assert.Equal(1, summary.PendingDeliveryCount);
        Assert.Equal("destination conflict", summary.DeliveryBlock);
        Assert.Equal(0, selective.FullReadCount);
    }

    [Fact]
    public async Task CatalogSkipsNonSharedCandidatesBeforeExternalAuthorization()
    {
        var content = new InMemoryContentStore();
        var raw = new InMemoryDocumentRepository(content);
        var seed = new CellBridgeDocumentService(new(raw, content));
        var hidden = (await seed.CreateAsync("/private/selective-hidden.docx", MinimalDocx.Create(), TestActor.Value))!;
        var visible = (await seed.CreateAsync("/shared/selective-visible.docx", MinimalDocx.Create(), TestActor.Value))!;
        var authorization = new CountingAuthorizationProvider();
        var selective = new SelectiveOnlyStateStore(raw);
        var service = new CellBridgeDocumentService(new(selective, content), documentAuthorizationProvider: authorization);
        Assert.True(await service.BindExternalAuthorizationAsync(hidden.ResourceId, hidden.StateVersion, null, Scope));
        Assert.True(await service.BindExternalAuthorizationAsync(visible.ResourceId, visible.StateVersion, null, Scope));

        var page = await AuthorizedDocumentCatalog.ReadAsync(service, Reader, 0, 10);

        Assert.Equal(visible.ResourceId, Assert.Single(page.Documents).ResourceId);
        Assert.Equal(1, authorization.RequestCount);
        Assert.Equal(0, selective.FullReadCount);
    }

    [Fact]
    public async Task CustomAuthorizationPolicyUsesBoundedCurrentAuthorizationMetadata()
    {
        var content = new InMemoryContentStore();
        var raw = new InMemoryDocumentRepository(content);
        var policy = new VersionOnePolicy();
        var seed = new CellBridgeDocumentService(new(raw, content), authorizationPolicy: policy);
        var state = (await seed.CreateAsync("/shared/selective-v1.docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.Equal(1, state.Security.AuthorizationPolicy!.ContractVersion);
        var selective = new SelectiveOnlyStateStore(raw);
        var service = new CellBridgeDocumentService(new(selective, content), authorizationPolicy: policy);

        var metadata = await service.GetDocumentMetadataAsync(state.ResourceId, TestActor.Value);

        Assert.Equal(state.ResourceId, metadata.ResourceId);
        Assert.Equal(state.ContentVersion, policy.ObservedContentVersion);
        Assert.Equal(0, selective.FullReadCount);
        Assert.True(selective.MetadataReadCount > 0);
    }

    [Fact]
    public async Task HistoryReadSelectsOnePageAfterUnchangedExternalAdmission()
    {
        var (raw, service, selective, gate, state) = await CreateExternalService("selective-legacy-unchanged");

        var listing = service.ListRevisionsAsync(state.ResourceId, Reader).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        gate.Release.TrySetResult();

        Assert.Single(await listing);
        Assert.Equal(0, selective.FullReadCount);
        Assert.True(selective.MetadataReadCount >= 1);
    }

    [Fact]
    public async Task HistoryReadSelectsCurrentPageWhenItChangesDuringExternalAdmission()
    {
        var (raw, service, selective, gate, state) = await CreateExternalService("selective-legacy-changed");

        var listing = service.ListRevisionsAsync(state.ResourceId, Reader).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var writer = new CellBridgeDocumentService(new(raw, service.Provider.Content), documentAuthorizationProvider:
            new CountingAuthorizationProvider { PolicyDomain = gate.PolicyDomain });
        var current = (await raw.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        await RevisionHistoryTests.Save(writer, current, "published during admission");
        gate.Release.TrySetResult();

        var revisions = await listing;
        Assert.Equal(2, revisions.Count);
        Assert.Equal(2UL, revisions[0].RevisionNumber);
        Assert.Equal(0, selective.FullReadCount);
        Assert.True(selective.MetadataReadCount >= 1);
    }

    [Fact]
    public async Task HistoryReadDeniesCurrentExternalAuthorizationFailure()
    {
        var content = new InMemoryContentStore();
        var raw = new InMemoryDocumentRepository(content);
        var seed = new CellBridgeDocumentService(new(raw, content));
        var state = (await seed.CreateAsync("/shared/selective-legacy-denied.docx", MinimalDocx.Create(), TestActor.Value))!;
        var gate = new GatedAuthorizationProvider { Allowed = false };
        var selective = new SelectiveOnlyStateStore(raw);
        var service = new CellBridgeDocumentService(new(selective, content), documentAuthorizationProvider: gate);
        Assert.True(await service.BindExternalAuthorizationAsync(state.ResourceId, state.StateVersion, null, Scope));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListRevisionsAsync(state.ResourceId, Reader).AsTask());

        Assert.Equal(0, selective.FullReadCount);
        Assert.True(selective.MetadataReadCount >= 1);
    }

    [Fact]
    public async Task ServiceCapsRevisionPagesAt256AndUsesLiveNewestFirstOffsets()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/selective-pages.docx", MinimalDocx.Create(), TestActor.Value))!;
        for (var number = 2; number <= 300; number++)
        {
            var index = new ExGuid(1, Guid.NewGuid());
            var request = new FsshttpbCellRequest { DataElementPackage = new() };
            request.DataElementPackage.DataElements.Add(new(DataElementType.StorageIndexDataElementData, index, SerialNumber.Null) { Data = [] });
            request.SubRequests.Add(new(RequestTypes.PutChanges) { RequestId = 1, Data = new PutChangesSubRequestData { StorageIndex = index } });
            var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request,
                new Dictionary<string, string>(), TestActor.Value);
            Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.ListRevisionSummariesAsync(state.ResourceId, 0, int.MaxValue, TestActor.Value).AsTask());
        var first = await service.ListRevisionSummariesAsync(state.ResourceId, 0, 256, TestActor.Value);
        var next = await service.ListRevisionSummariesAsync(state.ResourceId, first.NextOffset!.Value, 256, TestActor.Value);

        Assert.Equal(256, first.Revisions.Count);
        Assert.Equal(300UL, first.Revisions[0].RevisionNumber);
        Assert.Equal(45UL, first.Revisions[^1].RevisionNumber);
        Assert.Equal(256, first.NextOffset);
        Assert.Equal(44, next.Revisions.Count);
        Assert.Equal(44UL, next.Revisions[0].RevisionNumber);
        Assert.Null(next.NextOffset);
    }

    private sealed class InspectingEvaluator : ICellBridgeAccessEvaluator
    {
        public uint SeenContentVersion { get; private set; }
        public long SeenContentLength { get; private set; }
        public DocumentAccess Evaluate(CellBridgeActor actor, DocumentAuthorizationView state)
        {
            SeenContentVersion = state.ContentVersion;
            SeenContentLength = state.ContentLength;
            return DocumentAccess.Read;
        }
    }

    private sealed class VersionOnePolicy : ICellBridgeAuthorizationPolicy
    {
        public string? PolicyDomain => "tests:selective-v1";
        public uint ObservedContentVersion { get; private set; }
        public DocumentAuthorizationBinding? BindNewDocument(Guid resourceId) => new(PolicyDomain!, 1, 1);
        public ICellBridgeAuthorizationSnapshot? Resolve(DocumentAuthorizationView state)
        {
            ObservedContentVersion = state.ContentVersion;
            var binding = state.Security.AuthorizationPolicy;
            return binding is null ? null : new VersionOneSnapshot(state.ResourceId, binding);
        }

        private sealed record VersionOneSnapshot(Guid ResourceId, DocumentAuthorizationBinding Binding) : ICellBridgeAuthorizationSnapshot
        {
            public DocumentAccess Evaluate(string subject) => DocumentAccess.Read;
        }
    }

    private sealed class CountingAuthorizationProvider : IDocumentAuthorizationProvider
    {
        private int _requestCount;
        public string PolicyDomain { get; init; } = "tests:selective-catalog";
        public int RequestCount => Volatile.Read(ref _requestCount);
        public ValueTask<DocumentAuthorizationDecision> AuthorizeAsync(DocumentAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requestCount);
            return ValueTask.FromResult(new DocumentAuthorizationDecision(true));
        }
    }

    private static async Task<(InMemoryDocumentRepository Raw, CellBridgeDocumentService Service,
        SelectiveOnlyStateStore Selective, GatedAuthorizationProvider Gate, DocumentHead State)>
        CreateExternalService(string name)
    {
        var content = new InMemoryContentStore();
        var raw = new InMemoryDocumentRepository(content);
        var seed = new CellBridgeDocumentService(new(raw, content));
        var state = (await seed.CreateAsync($"/shared/{name}.docx", MinimalDocx.Create(), TestActor.Value))!;
        var gate = new GatedAuthorizationProvider();
        var selective = new SelectiveOnlyStateStore(raw);
        var service = new CellBridgeDocumentService(new(selective, content), documentAuthorizationProvider: gate);
        Assert.True(await service.BindExternalAuthorizationAsync(state.ResourceId, state.StateVersion, null, Scope));
        return (raw, service, selective, gate, state);
    }

    private sealed class GatedAuthorizationProvider : IDocumentAuthorizationProvider
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;
        public string PolicyDomain => "tests:selective-service";
        public TaskCompletionSource Entered => _entered;
        public TaskCompletionSource Release => _release;
        public bool Allowed { get; init; } = true;
        public int RequestCount => Volatile.Read(ref _requestCount);

        public async ValueTask<DocumentAuthorizationDecision> AuthorizeAsync(DocumentAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _requestCount);
            if (Allowed)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            return new(Allowed);
        }
    }
}

internal sealed class SelectiveOnlyStateStore(IDocumentRepository inner) : RepositoryTestProxy(inner)
{
    private int _fullReadCount;
    private int _metadataReadCount;
    private int _historyReadCount;
    public int FullReadCount => Volatile.Read(ref _fullReadCount);
    public int MetadataReadCount => Volatile.Read(ref _metadataReadCount);
    public int HistoryReadCount => Volatile.Read(ref _historyReadCount);
    public override ValueTask<DocumentHead?> FindAsync(DocumentIdentity identity, CancellationToken ct = default)
    { Interlocked.Increment(ref _metadataReadCount); return Inner.FindAsync(identity, ct); }
    public override ValueTask<DocumentRevisionPage?> ReadRevisionsAsync(Guid id, int offset, int limit, ulong? beforeRevision = null, CancellationToken ct = default)
    { Interlocked.Increment(ref _historyReadCount); return Inner.ReadRevisionsAsync(id, offset, limit, beforeRevision, ct); }
    public override ValueTask<DocumentRevisionSelection?> ReadRevisionAsync(Guid id, ulong number, CancellationToken ct = default)
    { Interlocked.Increment(ref _historyReadCount); return Inner.ReadRevisionAsync(id, number, ct); }
    public override ValueTask<NormalizedDocumentSnapshot?> ExportDocumentAsync(Guid id, CancellationToken ct = default)
    { Interlocked.Increment(ref _fullReadCount); throw new InvalidOperationException("An ordinary read exported full document history."); }
    public override ValueTask<ProviderSnapshot> CaptureRecoveryAsync(CancellationToken ct = default)
    { Interlocked.Increment(ref _fullReadCount); throw new InvalidOperationException("An ordinary read captured recovery state."); }
}
