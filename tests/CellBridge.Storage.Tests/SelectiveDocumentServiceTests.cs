using System.Collections.Immutable;
using CellBridge.AspNetCore;
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
        var raw = new InMemoryStateStore();
        var content = new InMemoryContentStore();
        var seed = new CellBridgeDocumentService(new(raw, content));
        var state = (await seed.CreateAsync("/shared/selective-service.docx", MinimalDocx.Create(), TestActor.Value))!;
        await raw.TransitionAsync(state.ResourceId, (current, now) =>
        {
            var next = current with { ContentVersion = current.ContentVersion + 1, ModifiedUtc = now };
            next = RevisionHistory.Append(current, next, TestActor.Value.Identity, now);
            return new StateTransition<DocumentState>(next, next);
        });
        var selective = new SelectiveOnlyStateStore(raw);
        var service = new CellBridgeDocumentService(new(selective, content));

        var metadata = await service.GetDocumentMetadataAsync(state.ResourceId, TestActor.Value);
        var page = await service.ListRevisionSummariesAsync(state.ResourceId, 0, 1, TestActor.Value);
        var revision = await service.GetRevisionSummaryAsync(state.ResourceId, 1, TestActor.Value);

        Assert.Equal(state.ResourceId, metadata.ResourceId);
        Assert.Equal(2UL, metadata.LatestRevisionNumber);
        Assert.Equal(2UL, Assert.Single(page.Revisions).RevisionNumber);
        Assert.Equal(1, page.NextOffset);
        Assert.Equal(state.Content, revision.Content);
        Assert.Equal(0, selective.FullReadCount);
        Assert.True(selective.MetadataReadCount >= 1);
        Assert.True(selective.HistoryReadCount >= 2);
    }

    [Fact]
    public async Task ExternalAdmissionIsFencedWhenBindingChangesDuringAsyncRead()
    {
        var raw = new InMemoryStateStore();
        var content = new InMemoryContentStore();
        var seed = new CellBridgeDocumentService(new(raw, content));
        var state = (await seed.CreateAsync("/shared/selective-external.docx", MinimalDocx.Create(), TestActor.Value))!;
        var gate = new GatedAuthorizationProvider();
        var selective = new SelectiveOnlyStateStore(raw);
        var service = new CellBridgeDocumentService(new(selective, content), documentAuthorizationProvider: gate);
        Assert.True(await service.BindExternalAuthorizationAsync(state.ResourceId, state.StateVersion, null, Scope));

        var read = service.GetDocumentMetadataAsync(state.ResourceId, Reader).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var before = (await raw.FindByResourceIdAsync(state.ResourceId))!;
        Assert.True(await service.BindExternalAuthorizationAsync(state.ResourceId, before.StateVersion,
            before.Security.AuthorizationPolicy, new("tenant", "rebound-workspace")));
        gate.Release.TrySetResult();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => read);
        Assert.Equal(0, selective.FullReadCount);
    }

    [Fact]
    public async Task ExternalAdmissionIsFencedWhenDocumentIsDeletedDuringAsyncRead()
    {
        var raw = new InMemoryStateStore();
        var content = new InMemoryContentStore();
        var seed = new CellBridgeDocumentService(new(raw, content));
        var state = (await seed.CreateAsync("/shared/selective-delete.docx", MinimalDocx.Create(), TestActor.Value))!;
        var gate = new GatedAuthorizationProvider();
        var selective = new SelectiveOnlyStateStore(raw);
        var service = new CellBridgeDocumentService(new(selective, content), documentAuthorizationProvider: gate);
        Assert.True(await service.BindExternalAuthorizationAsync(state.ResourceId, state.StateVersion, null, Scope));

        var read = service.GetDocumentMetadataAsync(state.ResourceId, Reader).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var current = (await raw.FindByResourceIdAsync(state.ResourceId))!;
        Assert.True(await ((IDocumentLifecycleStore)raw).TryDeleteAsync(state.ResourceId,
            current.LifecycleGeneration, current.StateVersion));
        gate.Release.TrySetResult();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => read);
        Assert.Equal(0, selective.FullReadCount);
    }

    [Fact]
    public async Task UnknownAndDeniedExternalDocumentsFailClosed()
    {
        var raw = new InMemoryStateStore();
        var content = new InMemoryContentStore();
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
        var raw = new InMemoryStateStore();
        var content = new InMemoryContentStore();
        var seed = new CellBridgeDocumentService(new(raw, content));
        var state = (await seed.CreateAsync("/shared/selective-grants.docx", MinimalDocx.Create(), TestActor.Value))!;
        await raw.TransitionAsync(state.ResourceId, (current, _) => new StateTransition<bool>(current with
        {
            Security = current.Security with
            { Grants = current.Security.Grants.SetItem(Reader.Identity.Subject, DocumentAccess.Read) }
        }, true));
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
    public async Task CustomEvaluatorFallsBackToCompleteDocumentState()
    {
        var raw = new InMemoryStateStore();
        var content = new InMemoryContentStore();
        var seed = new CellBridgeDocumentService(new(raw, content));
        var state = (await seed.CreateAsync("/shared/selective-fallback.docx", MinimalDocx.Create(), TestActor.Value))!;
        var evaluator = new InspectingEvaluator();
        var selective = new SelectiveOnlyStateStore(raw, throwOnFullReads: false);
        var service = new CellBridgeDocumentService(new(selective, content), evaluator);

        var metadata = await service.GetDocumentMetadataAsync(state.ResourceId, TestActor.Value);

        Assert.Equal(state.ResourceId, metadata.ResourceId);
        Assert.Equal(1, evaluator.SeenRevisionCount);
        Assert.True(selective.FullReadCount > 0);
        Assert.Equal(0, selective.MetadataReadCount);
    }

    [Fact]
    public async Task CatalogCarriesPublicationStatusWithoutLoadingFullState()
    {
        var raw = new InMemoryStateStore();
        var content = new InMemoryContentStore();
        var seed = new CellBridgeDocumentService(new(raw, content));
        var state = (await seed.CreateAsync("/shared/selective-catalog.docx", MinimalDocx.Create(), TestActor.Value))!;
        var pending = new ExternalRevision(Guid.NewGuid(), state.ResourceId, state.LifecycleGeneration,
            1, state.ContentVersion, state.Content);
        await raw.TransitionAsync(state.ResourceId, (current, _) => new StateTransition<bool>(current with
        {
            Publication = new(Guid.NewGuid(), "destination", "remote-r0", 2, [pending])
            { BlockedReason = "destination conflict" }
        }, true));
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
        var raw = new InMemoryStateStore();
        var content = new InMemoryContentStore();
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
    public async Task VersionOneAuthorizationPolicyUsesFullStateFallback()
    {
        var raw = new InMemoryStateStore();
        var content = new InMemoryContentStore();
        var policy = new VersionOnePolicy();
        var seed = new CellBridgeDocumentService(new(raw, content), authorizationPolicy: policy);
        var state = (await seed.CreateAsync("/shared/selective-v1.docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.Equal(1, state.Security.AuthorizationPolicy!.ContractVersion);
        var selective = new SelectiveOnlyStateStore(raw, throwOnFullReads: false);
        var service = new CellBridgeDocumentService(new(selective, content), authorizationPolicy: policy);

        var metadata = await service.GetDocumentMetadataAsync(state.ResourceId, TestActor.Value);

        Assert.Equal(state.ResourceId, metadata.ResourceId);
        Assert.Equal(1, policy.ObservedRevisionCount);
        Assert.True(selective.FullReadCount > 0);
        Assert.Equal(0, selective.MetadataReadCount);
    }

    [Fact]
    public async Task LegacyHistoryReadReusesOneFullStateWhenExternalAdmissionDoesNotChangeIt()
    {
        var (raw, service, selective, gate, state) = await CreateExternalFullStateService("selective-legacy-unchanged");

        var listing = service.ListRevisionsAsync(state.ResourceId, Reader).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        gate.Release.TrySetResult();

        Assert.Single(await listing);
        Assert.Equal(1, selective.FullReadCount);
        Assert.Equal(1, selective.MetadataReadCount);
    }

    [Fact]
    public async Task LegacyHistoryReadReloadsFullStateWhenItChangesDuringExternalAdmission()
    {
        var (raw, service, selective, gate, state) = await CreateExternalFullStateService("selective-legacy-changed");

        var listing = service.ListRevisionsAsync(state.ResourceId, Reader).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await raw.TransitionAsync(state.ResourceId, (current, now) =>
        {
            var next = current with { ContentVersion = current.ContentVersion + 1, ModifiedUtc = now };
            next = RevisionHistory.Append(current, next, TestActor.Value.Identity, now);
            return new StateTransition<bool>(next, true);
        });
        gate.Release.TrySetResult();

        var revisions = await listing;
        Assert.Equal(2, revisions.Count);
        Assert.Equal(2UL, revisions[0].RevisionNumber);
        Assert.Equal(2, selective.FullReadCount);
        Assert.Equal(1, selective.MetadataReadCount);
    }

    [Fact]
    public async Task LegacyHistoryReadDeniesCurrentExternalAuthorizationFailure()
    {
        var raw = new InMemoryStateStore();
        var content = new InMemoryContentStore();
        var seed = new CellBridgeDocumentService(new(raw, content));
        var state = (await seed.CreateAsync("/shared/selective-legacy-denied.docx", MinimalDocx.Create(), TestActor.Value))!;
        var gate = new GatedAuthorizationProvider { Allowed = false };
        var selective = new SelectiveOnlyStateStore(raw, throwOnFullReads: false);
        var service = new CellBridgeDocumentService(new(selective, content), documentAuthorizationProvider: gate);
        Assert.True(await service.BindExternalAuthorizationAsync(state.ResourceId, state.StateVersion, null, Scope));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListRevisionsAsync(state.ResourceId, Reader).AsTask());

        Assert.Equal(1, selective.FullReadCount);
        Assert.Equal(1, selective.MetadataReadCount);
    }

    [Fact]
    public async Task ServiceCapsRevisionPagesAt256AndUsesLiveNewestFirstOffsets()
    {
        var raw = new InMemoryStateStore();
        var provider = new StorageProvider(raw, new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/selective-pages.docx", MinimalDocx.Create(), TestActor.Value))!;
        await raw.TransitionAsync(state.ResourceId, (current, _) =>
        {
            var seed = current.Revisions[0];
            var revisions = Enumerable.Range(1, 300).Select(number => seed with { RevisionNumber = (ulong)number }).ToImmutableArray();
            var next = current with { Revisions = revisions };
            return new StateTransition<bool>(next, true);
        });

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
        public int SeenRevisionCount { get; private set; }
        public DocumentAccess Evaluate(CellBridgeActor actor, DocumentState state)
        {
            SeenRevisionCount = state.Revisions.Length;
            return DocumentAccess.Read;
        }
    }

    private sealed class VersionOnePolicy : ICellBridgeAuthorizationPolicy
    {
        public string? PolicyDomain => "tests:selective-v1";
        public int ObservedRevisionCount { get; private set; }
        public DocumentAuthorizationBinding? BindNewDocument(Guid resourceId) => new(PolicyDomain!, 1, 1);
        public ICellBridgeAuthorizationSnapshot? Resolve(DocumentState state)
        {
            ObservedRevisionCount = state.Revisions.Length;
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
        public string PolicyDomain => "tests:selective-catalog";
        public int RequestCount => Volatile.Read(ref _requestCount);
        public ValueTask<DocumentAuthorizationDecision> AuthorizeAsync(DocumentAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requestCount);
            return ValueTask.FromResult(new DocumentAuthorizationDecision(true));
        }
    }

    private static async Task<(InMemoryStateStore Raw, CellBridgeDocumentService Service,
        SelectiveOnlyStateStore Selective, GatedAuthorizationProvider Gate, DocumentState State)>
        CreateExternalFullStateService(string name)
    {
        var raw = new InMemoryStateStore();
        var content = new InMemoryContentStore();
        var seed = new CellBridgeDocumentService(new(raw, content));
        var state = (await seed.CreateAsync($"/shared/{name}.docx", MinimalDocx.Create(), TestActor.Value))!;
        var gate = new GatedAuthorizationProvider();
        var selective = new SelectiveOnlyStateStore(raw, throwOnFullReads: false);
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

internal sealed class SelectiveOnlyStateStore(IDocumentStateStore inner, bool throwOnFullReads = true) : IDocumentStateStore, IDocumentReadStore
{
    private int _fullReadCount;
    private int _metadataReadCount;
    private int _historyReadCount;
    public int FullReadCount => Volatile.Read(ref _fullReadCount);
    public int MetadataReadCount => Volatile.Read(ref _metadataReadCount);
    public int HistoryReadCount => Volatile.Read(ref _historyReadCount);
    public bool Durable => inner.Durable;
    public bool Shared => inner.Shared;

    public ValueTask<DocumentState?> FindByResourceIdAsync(Guid resourceId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _fullReadCount);
        if (throwOnFullReads) throw new InvalidOperationException("Selective read path materialized full state.");
        return inner.FindByResourceIdAsync(resourceId, cancellationToken);
    }

    public ValueTask<DocumentState?> FindByPathKeyAsync(string pathKey, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _fullReadCount);
        if (throwOnFullReads) throw new InvalidOperationException("Selective read path materialized full state.");
        return inner.FindByPathKeyAsync(pathKey, cancellationToken);
    }

    public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken cancellationToken = default) =>
        inner.ListAsync(offset, limit, cancellationToken);
    public ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken cancellationToken = default) => inner.TryCreateAsync(state, cancellationToken);
    public ValueTask<T> TransitionAsync<T>(Guid resourceId, Func<DocumentState, DateTime, StateTransition<T>> transition,
        CancellationToken cancellationToken = default) => inner.TransitionAsync(resourceId, transition, cancellationToken);
    public ValueTask CheckHealthAsync(CancellationToken cancellationToken = default) => inner.CheckHealthAsync(cancellationToken);

    public ValueTask<DocumentReadMetadata?> FindReadMetadataByResourceIdAsync(Guid resourceId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _metadataReadCount);
        return ((IDocumentReadStore)inner).FindReadMetadataByResourceIdAsync(resourceId, cancellationToken);
    }

    public ValueTask<DocumentReadMetadata?> FindReadMetadataByPathKeyAsync(string pathKey, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _metadataReadCount);
        return ((IDocumentReadStore)inner).FindReadMetadataByPathKeyAsync(pathKey, cancellationToken);
    }

    public ValueTask<DocumentRevisionReadPage?> ReadRevisionSummariesAsync(Guid resourceId, int offset, int limit,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _historyReadCount);
        return ((IDocumentReadStore)inner).ReadRevisionSummariesAsync(resourceId, offset, limit, cancellationToken);
    }

    public ValueTask<DocumentRevisionRead?> ReadRevisionSummaryAsync(Guid resourceId, ulong revisionNumber,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _historyReadCount);
        return ((IDocumentReadStore)inner).ReadRevisionSummaryAsync(resourceId, revisionNumber, cancellationToken);
    }
}
