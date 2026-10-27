using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class ConditionalApiConflictTests
{
    [Theory]
    [InlineData(HostLockOperation.Acquire, false)]
    [InlineData(HostLockOperation.Acquire, true)]
    [InlineData(HostLockOperation.Refresh, false)]
    [InlineData(HostLockOperation.Refresh, true)]
    [InlineData(HostLockOperation.Replace, false)]
    [InlineData(HostLockOperation.Replace, true)]
    [InlineData(HostLockOperation.Release, false)]
    [InlineData(HostLockOperation.Release, true)]
    public async Task HostLockMutationRejectsKnownConflictsAndPropagatesUnavailability(HostLockOperation operation, bool unavailable)
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/host-conflict.docx", MinimalDocx.Create(), TestActor.Value))!;
        if (operation != HostLockOperation.Acquire)
            Assert.True((await new SharedDocumentLocks(service).ApplyAsync(state.ResourceId,
                HostLockOperation.Acquire, "lease", TestActor.Value)).Success);
        var before = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        var fault = new FaultOnPublication(provider.State, Failure(unavailable));
        var locks = new SharedDocumentLocks(new(new(fault, provider.Content)));

        await AssertOutcomeAsync(() => locks.ApplyAsync(state.ResourceId, operation, "lease", TestActor.Value,
            replacementToken: operation == HostLockOperation.Replace ? "replacement" : null).AsTask(),
            new HostLockResult(false, null, null), fault, before);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PublicationBindingAndRetryRejectKnownConflictsAndPropagateUnavailability(bool retry, bool unavailable)
    {
        var provider = Memory();
        var state = (await new CellBridgeDocumentService(provider).CreateAsync(
            "/publication-conflict.docx", MinimalDocx.Create(), TestActor.Value))!;
        var bindingId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var destination = new UnusedDestination();
        if (retry)
        {
            Assert.True(await new ExternalRevisionPublisher(provider, destination).BindAsync(state.ResourceId,
                state.LifecycleGeneration, state.StateVersion, bindingId, "destination", "revision-1"));
            await provider.State.TransitionAsync(state.ResourceId, (current, _) =>
            {
                var next = ExternalPublication.Append(current,
                    current with { ContentVersion = current.ContentVersion + 1 }, operationId, provider.Limits);
                return new StateTransition<bool>(next with
                {
                    Publication = next.Publication! with { BlockedReason = "DestinationConflict" },
                }, true);
            });
        }
        var before = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        var fault = new FaultOnPublication(provider.State, Failure(unavailable));
        var publisher = new ExternalRevisionPublisher(new(fault, provider.Content), destination);

        await AssertOutcomeAsync(() => retry
            ? publisher.RetryAsync(state.ResourceId, bindingId, operationId).AsTask()
            : publisher.BindAsync(state.ResourceId, before.LifecycleGeneration, before.StateVersion,
                bindingId, "destination", "revision-1").AsTask(), false, fault, before);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AuthorizationBindingAndUpdateRejectKnownConflictsAndPropagateUnavailability(bool externalBinding, bool unavailable)
    {
        var provider = Memory();
        var policy = new RevisionPolicy();
        var service = externalBinding ? new CellBridgeDocumentService(provider)
            : new CellBridgeDocumentService(provider, authorizationPolicy: policy);
        var state = (await service.CreateAsync("/authorization-conflict.docx", MinimalDocx.Create(), TestActor.Value))!;
        var before = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        var fault = new FaultOnPublication(provider.State, Failure(unavailable));
        var failingProvider = new StorageProvider(fault, provider.Content);
        var failingService = externalBinding
            ? new CellBridgeDocumentService(failingProvider, documentAuthorizationProvider: new ExternalAuthority())
            : new CellBridgeDocumentService(failingProvider, authorizationPolicy: policy);

        await AssertOutcomeAsync(() => externalBinding
            ? failingService.BindExternalAuthorizationAsync(state.ResourceId, before.StateVersion,
                before.Security.AuthorizationPolicy, new("tenant", "workspace")).AsTask()
            : failingService.UpdateAuthorizationAsync(state.ResourceId, before.Security.AuthorizationPolicy,
                policy.Binding(2)).AsTask(), false, fault, before);
    }

    private static StorageProvider Memory() => new(new InMemoryStateStore(), new InMemoryContentStore());

    private static Exception Failure(bool unavailable) => unavailable
        ? new StorageUnavailableException("Injected unknown commit outcome.")
        : new StorageConflictException("Injected known rollback.");

    private static async Task AssertOutcomeAsync<T>(Func<Task<T>> operation, T conflictResult,
        FaultOnPublication fault, DocumentState before)
    {
        if (fault.Failure is StorageUnavailableException)
            Assert.Same(fault.Failure, await Assert.ThrowsAsync<StorageUnavailableException>(() => operation()));
        else
            Assert.Equal(conflictResult, await operation());
        Assert.Equal(1, fault.PreparedWrites);
        Assert.Equal(before, await fault.FindByResourceIdAsync(before.ResourceId));
    }

    private sealed class FaultOnPublication(IDocumentStateStore inner, Exception failure) : IDocumentStateStore
    {
        public Exception Failure => failure;
        public int PreparedWrites { get; private set; }
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.FindByResourceIdAsync(id, cancellationToken);
        public ValueTask<DocumentState?> FindByPathKeyAsync(string key, CancellationToken cancellationToken = default) =>
            inner.FindByPathKeyAsync(key, cancellationToken);
        public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken cancellationToken = default) =>
            inner.ListAsync(offset, limit, cancellationToken);
        public ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken cancellationToken = default) =>
            inner.TryCreateAsync(state, cancellationToken);
        public ValueTask CheckHealthAsync(CancellationToken cancellationToken = default) => inner.CheckHealthAsync(cancellationToken);
        public ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition,
            CancellationToken cancellationToken = default) => inner.TransitionAsync(id, (current, now) =>
            {
                var change = transition(current, now);
                if (change.Next is null) return change;
                PreparedWrites++;
                throw failure;
            }, cancellationToken);
    }

    private sealed class UnusedDestination : IExternalRevisionDestination
    {
        public ValueTask<ExternalDeliveryResult> CompareExchangeAsync(ExternalDeliveryRequest request, Stream verifiedContent,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Binding and retry must not deliver content.");
    }

    private sealed class ExternalAuthority : IDocumentAuthorizationProvider
    {
        public string PolicyDomain => "tests:conditional-external";
        public ValueTask<DocumentAuthorizationDecision> AuthorizeAsync(DocumentAuthorizationRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new DocumentAuthorizationDecision(true));
    }

    private sealed class RevisionPolicy : ICellBridgeAuthorizationPolicy
    {
        public string PolicyDomain => "tests:conditional-revisions";
        public DocumentAuthorizationBinding Binding(long revision) => new(PolicyDomain, 1, revision);
        public DocumentAuthorizationBinding BindNewDocument(Guid resourceId) => Binding(1);
        public ICellBridgeAuthorizationSnapshot? Resolve(DocumentState state) =>
            state.Security.AuthorizationPolicy is { ContractVersion: 1, Revision: 1 or 2 } binding && binding.PolicyDomain == PolicyDomain
                ? new Snapshot(state.ResourceId, binding) : null;
    }

    private sealed record Snapshot(Guid ResourceId, DocumentAuthorizationBinding Binding) : ICellBridgeAuthorizationSnapshot
    {
        public DocumentAccess Evaluate(string subject) => subject == TestActor.Value.Identity.Subject
            ? DocumentAccess.Read | DocumentAccess.Write : DocumentAccess.None;
    }
}
