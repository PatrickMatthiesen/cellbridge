using System.Collections.Concurrent;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class AsyncDocumentAuthorizationTests
{
    private static readonly CellBridgeActor Owner = new(new("async:owner", "owner", "Owner"), CanCreate: true);
    private static readonly CellBridgeActor Reader = new(new("async:reader", "reader", "Reader"));
    private static readonly DocumentAuthorizationScope Scope = new("tenant-a", "workspace-b");

    private static StorageProvider Memory(IContentStore? content = null) =>
        new(new InMemoryStateStore(), content ?? new InMemoryContentStore());

    [Fact]
    public async Task BoundDocumentUsesFreshAsyncDecisionsAndIgnoresTheStoredOwnerGrant()
    {
        var policy = new Policy();
        var service = Service(policy);
        var state = await Import(service);

        Assert.Equal(DocumentAccess.None, service.Access(Owner, state));
        Assert.Equal(DocumentAccess.None, service.Access(Reader, state));
        Assert.Equal(DocumentAccess.Read | DocumentAccess.Write, await service.GetAccessAsync(Owner, state));
        var afterAccess = policy.Requests.ToArray();
        Assert.Equal([DocumentAction.Read, DocumentAction.Write], afterAccess.Select(r => r.Action));
        Assert.All(afterAccess, r => AssertRequest(r, state, Owner, Scope));

        await service.ListRevisionsAsync(state.ResourceId, Reader);
        await service.ListRevisionsAsync(state.ResourceId, Reader);
        Assert.Equal(2, policy.Count(DocumentAction.Read, Reader.Identity.Subject));
        Assert.Equal(0, policy.Count(DocumentAction.Write, Reader.Identity.Subject));

        var limited = Owner with { AccessLimit = new(state.ResourceId, DocumentAccess.Read) };
        Assert.Equal(DocumentAccess.Read, await service.GetAccessAsync(limited, state));
    }

    [Fact]
    public async Task ExternalCreationRequiresCreateDecisionAndStoresItsScope()
    {
        var policy = new Policy();
        var service = Service(policy);
        var id = Guid.NewGuid();
        var created = await service.CreateExternalAsync(id, "/created.docx", [], Scope, Owner);

        Assert.NotNull(created);
        var request = Assert.Single(policy.Requests);
        Assert.Equal(DocumentAction.Create, request.Action);
        Assert.Equal(Owner.Identity.Subject, request.Subject);
        Assert.Equal(Scope, request.Scope);
        Assert.Equal(id, request.ResourceId);
        Assert.Equal(1, request.LifecycleGeneration);
        Assert.Equal("tenant-a", request.Binding.TenantId);
        Assert.Equal("workspace-b", request.Binding.WorkspaceId);

        policy.Allow = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateExternalAsync(Guid.NewGuid(),
            "/denied.docx", [], Scope, Owner).AsTask());
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("expired")]
    [InlineData("future")]
    public async Task DenialAndInvalidFreshnessFailClosed(string resultKind)
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-17T12:00:00Z"));
        var policy = new Policy();
        policy.Decision = (_, _) => resultKind switch
        {
            "denied" => new(false, time.GetUtcNow(), time.GetUtcNow().AddMinutes(1)),
            "expired" => new(true, time.GetUtcNow().AddMinutes(-2), time.GetUtcNow().AddMinutes(-1)),
            _ => new(true, time.GetUtcNow().AddSeconds(1), time.GetUtcNow().AddMinutes(1)),
        };
        var service = Service(policy, time);
        var state = await Import(service);

        Assert.Equal(DocumentAccess.None, await service.GetAccessAsync(Reader, state));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListRevisionsAsync(state.ResourceId, Reader).AsTask());
    }

    [Fact]
    public async Task OrdinaryPolicyRevocationDoesNotCancelAdmittedSaveButBlocksLaterOperations()
    {
        var policy = new Policy();
        var content = new PausedContentStore();
        var service = Service(policy, content: content);
        var state = await Import(service, []);
        content.Pause = true;
        var saving = service.SaveWholeFileAsync(state.ResourceId, new MemoryStream(MinimalDocx.Create("accepted")),
            "save-1", Reader).AsTask();
        await content.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        policy.Allow = false;
        content.Continue.SetResult();
        var accepted = await saving;
        Assert.False(accepted.IsReplay);
        Assert.Equal(state.ContentVersion + 1, accepted.Revision.ContentVersion);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListRevisionsAsync(state.ResourceId, Reader).AsTask());
        Assert.Equal(1, policy.Count(DocumentAction.Write, Reader.Identity.Subject));
    }

    [Fact]
    public async Task BindingCasDuringPreparationFencesTheAdmittedSaveAndClearsCoordination()
    {
        var policy = new Policy();
        var content = new PausedContentStore();
        var service = Service(policy, content: content);
        var original = await Import(service, []);
        var locks = new SharedDocumentLocks(service);
        Assert.True((await locks.ApplyAsync(original.ResourceId, HostLockOperation.Acquire, "lease", Reader)).Success);
        original = (await service.Provider.State.FindByResourceIdAsync(original.ResourceId))!;
        content.Pause = true;
        var saving = service.SaveWholeFileAsync(original.ResourceId,
            new MemoryStream(MinimalDocx.Create("stale")), "stale", Reader, "lease").AsTask();
        await content.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(await service.BindExternalAuthorizationAsync(original.ResourceId, original.StateVersion,
            original.Security.AuthorizationPolicy, new("tenant-a", "workspace-c")));
        var rebound = (await service.Provider.State.FindByResourceIdAsync(original.ResourceId))!;
        Assert.Equal("workspace-c", rebound.Security.AuthorizationPolicy!.WorkspaceId);
        Assert.Empty(rebound.Editors);
        Assert.Null(rebound.Coordination.HostLock);
        Assert.Empty(rebound.Coordination.CoauthorClients);
        content.Continue.SetResult();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => saving);
        var unchanged = (await service.Provider.State.FindByResourceIdAsync(original.ResourceId))!;
        Assert.Equal(rebound.Content, unchanged.Content);
        Assert.Equal(rebound.ContentVersion, unchanged.ContentVersion);

        Assert.False(await service.BindExternalAuthorizationAsync(original.ResourceId, original.StateVersion,
            original.Security.AuthorizationPolicy, Scope));
    }

    [Fact]
    public async Task AsyncPolicyCallsRunOutsideDocumentStateTransitions()
    {
        var state = new InMemoryStateStore();
        var observedInsideTransition = 0;
        var measured = new MeasuringStateStore(state,
            value => Volatile.Write(ref observedInsideTransition, value ? 1 : 0));
        var provider = new Policy();
        provider.OnAuthorize = () => Assert.False(Volatile.Read(ref observedInsideTransition) != 0);
        var service = new CellBridgeDocumentService(new(measured, new InMemoryContentStore()),
            documentAuthorizationProvider: provider);
        var stateDocument = await service.ImportExternalAsync(Guid.NewGuid(), "/measured.docx", [], Scope,
            Owner.Identity, Owner);
        Assert.NotNull(stateDocument);
        await service.ListRevisionsAsync(stateDocument.ResourceId, Reader);
        Assert.Equal(1, provider.Count(DocumentAction.Read, Reader.Identity.Subject));
    }

    [Fact]
    public async Task ReadAdmissionIsFencedByBindingMigrationButSurvivesContentUpdate()
    {
        var policy = new GatedPolicy();
        var service = Service(policy);
        var state = await Import(service);
        var lockResult = await new SharedDocumentLocks(service).ApplyAsync(state.ResourceId,
            HostLockOperation.Acquire, "read-race", Owner);
        var lockToken = Assert.IsType<HostWriteToken>(lockResult.WriteToken);
        policy.ResetGate();

        var listing = service.ListRevisionsAsync(state.ResourceId, Reader).AsTask();
        await policy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var beforeSave = (await service.Provider.State.FindByResourceIdAsync(state.ResourceId))!;
        var saved = await service.SaveWholeFileAsync(state.ResourceId,
            new MemoryStream(MinimalDocx.Create("content changed")), "content-update", Owner, lockToken.LockToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(beforeSave.ContentVersion + 1, saved.Revision.ContentVersion);
        policy.Release.TrySetResult();

        var revisions = await listing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(revisions, revision => revision.ContentVersion == saved.Revision.ContentVersion);

        policy.ResetGate();
        var staleBinding = (await service.Provider.State.FindByResourceIdAsync(state.ResourceId))!;
        var afterMigration = service.ListRevisionsAsync(state.ResourceId, Reader).AsTask();
        await policy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await service.BindExternalAuthorizationAsync(state.ResourceId, staleBinding.StateVersion,
            staleBinding.Security.AuthorizationPolicy, new("tenant-a", "workspace-migrated")));
        policy.Release.TrySetResult();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => afterMigration);
    }

    [Fact]
    public async Task SoapMetadataAndRenameUseOneFreshAdmissionPerSubrequest()
    {
        var policy = new Policy();
        var service = Service(policy);
        var state = await Import(service);
        var processor = new CellBridgeRequestProcessor(service);
        var metadata = HostIntegrationTests.File(state.ResourceId, SubRequestType.GetDocMetaInfo);
        var metadataResult = await processor.ExecuteAsync(new() { Requests = { metadata } }, "https://host.test", Reader);
        Assert.Equal("Success", Assert.Single(Assert.Single(metadataResult.Response.Responses).SubResponses).ErrorCode);
        Assert.Equal(2, policy.Count(DocumentAction.Read, Reader.Identity.Subject)); // file envelope plus the subrequest

        policy.Clear();
        var rename = HostIntegrationTests.File(state.ResourceId, SubRequestType.FileOperation);
        rename.SubRequests[0].SubRequestDataAttributes["FileOperation"] = "Rename";
        rename.SubRequests[0].SubRequestDataAttributes["NewFileName"] = "renamed.docx";
        var renameResult = await processor.ExecuteAsync(new() { Requests = { rename } }, "https://host.test", Owner);
        Assert.Equal("Success", Assert.Single(Assert.Single(renameResult.Response.Responses).SubResponses).ErrorCode);
        Assert.Equal(1, policy.Count(DocumentAction.Write, Owner.Identity.Subject));
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("restore")]
    public async Task DeniedSoapWriteDoesNotRetryNestedAuthorization(string operation)
    {
        var policy = new Policy();
        var service = Service(policy);
        var state = await Import(service);
        var before = (await service.Provider.State.FindByResourceIdAsync(state.ResourceId))!;
        var path = before.Path;
        var content = before.Content;
        var version = before.ContentVersion;
        var writes = 0;
        policy.Decision = (request, _) =>
        {
            if (request.Action == DocumentAction.Write)
                return new(++writes > 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));
            return new(true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));
        };
        policy.Clear();

        var subrequest = operation == "rename"
            ? HostIntegrationTests.File(state.ResourceId, SubRequestType.FileOperation).SubRequests[0]
            : HostIntegrationTests.File(state.ResourceId, SubRequestType.Versioning).SubRequests[0];
        if (operation == "rename")
        {
            subrequest.SubRequestDataAttributes["FileOperation"] = "Rename";
            subrequest.SubRequestDataAttributes["NewFileName"] = "should-not-rename.docx";
        }
        else
        {
            subrequest.SubRequestDataAttributes["VersioningRequestType"] = "RestoreVersion";
            subrequest.SubRequestDataAttributes["Version"] = "1.0";
        }
        var file = HostIntegrationTests.File(state.ResourceId, subrequest.Type);
        file.SubRequests[0] = subrequest;
        var result = await new CellBridgeRequestProcessor(service).ExecuteAsync(new() { Requests = { file } },
            "https://host.test", Owner);

        Assert.Equal("FileUnauthorizedAccess", Assert.Single(Assert.Single(result.Response.Responses).SubResponses).ErrorCode);
        Assert.Equal(1, writes);
        var after = (await service.Provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal(path, after.Path);
        Assert.Equal(content, after.Content);
        Assert.Equal(version, after.ContentVersion);
    }

    [Fact]
    public async Task InvalidInitialBindingCannotBeUpgradedByNestedSoapWrite()
    {
        var policy = new Policy();
        var service = Service(policy);
        var state = await Import(service);
        await service.Provider.State.TransitionAsync(state.ResourceId, (current, _) => new StateTransition<bool>(current with
        {
            Security = current.Security with
            {
                AuthorizationPolicy = new("tests:retired-domain", 2, 1) { TenantId = "tenant-a", WorkspaceId = "workspace-b" },
            },
        }, true));
        var rename = HostIntegrationTests.File(state.ResourceId, SubRequestType.FileOperation);
        rename.SubRequests[0].SubRequestDataAttributes["FileOperation"] = "Rename";
        rename.SubRequests[0].SubRequestDataAttributes["NewFileName"] = "should-not-rename.docx";

        var result = await new CellBridgeRequestProcessor(service).ExecuteAsync(new() { Requests = { rename } },
            "https://host.test", Owner);

        var fileResponse = Assert.Single(result.Response.Responses);
        Assert.Equal("FileUnauthorizedAccess", fileResponse.ErrorCode);
        Assert.Equal("FileUnauthorizedAccess", Assert.Single(fileResponse.SubResponses).ErrorCode);
        Assert.Empty(policy.Requests);
        Assert.Equal(state.Path, (await service.Provider.State.FindByResourceIdAsync(state.ResourceId))!.Path);
    }

    [Fact]
    public async Task DeniedBinaryReadDoesNotExposeGetFilePropsFromAllowedSoapEnvelope()
    {
        var policy = new Policy();
        policy.Decision = (request, call) => new(request.Action == DocumentAction.Read && call == 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));
        var service = Service(policy);
        var state = await Import(service);
        policy.Clear();
        var binary = new FsshttpbCellRequest
        {
            SubRequests = { new(RequestTypes.QueryChanges) { RequestId = 1,
                Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } } },
        };
        var file = HostIntegrationTests.File(state.ResourceId, SubRequestType.Cell);
        file.SubRequests[0].SubRequestDataAttributes["GetFileProps"] = "true";
        HostIntegrationTests.SetBinary(file.SubRequests[0], binary.ToByteArray());

        var result = await new CellBridgeRequestProcessor(service).ExecuteAsync(new() { Requests = { file } },
            "https://host.test", Owner);

        var subresponse = Assert.Single(Assert.Single(result.Response.Responses).SubResponses);
        Assert.Equal("Success", subresponse.ErrorCode);
        Assert.DoesNotContain("Etag", subresponse.SubResponseDataAttributes.Keys);
        Assert.DoesNotContain("CreateTime", subresponse.SubResponseDataAttributes.Keys);
        Assert.DoesNotContain("LastModifiedTime", subresponse.SubResponseDataAttributes.Keys);
        Assert.DoesNotContain("ModifiedBy", subresponse.SubResponseDataAttributes.Keys);
        Assert.Equal(2, policy.Requests.Count); // envelope Read succeeds; binary QueryChanges Read is denied
    }

    [Fact]
    public async Task BinaryQueryAccessChecksReadAndWriteAndLaterDeniedRequestHasNoPayload()
    {
        var policy = new Policy();
        var service = Service(policy);
        var state = await Import(service);
        var query = new FsshttpbCellRequest
        {
            SubRequests = { new(RequestTypes.QueryAccess) { RequestId = 1, Data = new QueryAccessSubRequestData() } },
        };
        var first = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, query,
            new Dictionary<string, string>(), Owner);
        var access = Assert.IsType<QueryAccessSubResponseData>(Assert.Single(first.Response.SubResponses).Data);
        Assert.Null(access.ReadAccessError);
        Assert.Null(access.WriteAccessError);
        Assert.Equal(1, policy.Count(DocumentAction.Read, Owner.Identity.Subject));
        Assert.Equal(1, policy.Count(DocumentAction.Write, Owner.Identity.Subject));

        policy.Allow = false;
        var denied = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, query,
            new Dictionary<string, string>(), Owner);
        var deniedAccess = Assert.IsType<QueryAccessSubResponseData>(Assert.Single(denied.Response.SubResponses).Data);
        Assert.NotNull(deniedAccess.ReadAccessError);
        Assert.NotNull(deniedAccess.WriteAccessError);
        Assert.Null(denied.Response.DataElementPackage);
    }

    [Fact]
    public async Task RevocationFencesPreparedLockCommitButLeaseRemainsUntilBindingMigration()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new MutableTimeProvider(now);
        var policy = new SubjectPolicy(Owner.Identity.Subject, Reader.Identity.Subject);
        var service = Service(policy, time);
        var state = await Import(service);
        var locks = new SharedDocumentLocks(service);
        var acquired = await locks.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "lease", Owner);
        var token = Assert.IsType<HostWriteToken>(acquired.WriteToken);
        var version = (await service.Provider.State.FindByResourceIdAsync(state.ResourceId))!.StateVersion;

        policy.OwnerAllowed = false;
        Assert.False(await locks.TryCommitAsync(token, version, Owner, (current, _) => current with
        { ModifiedUtc = current.ModifiedUtc.AddSeconds(1) }, Guid.NewGuid()));
        var afterRevoke = (await service.Provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal("lease", afterRevoke.Coordination.HostLock!.Token);
        Assert.False((await locks.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "other", Reader)).Success);

        Assert.True(await service.BindExternalAuthorizationAsync(state.ResourceId, afterRevoke.StateVersion,
            afterRevoke.Security.AuthorizationPolicy, new("tenant-a", "workspace-c")));
        Assert.Null((await service.Provider.State.FindByResourceIdAsync(state.ResourceId))!.Coordination.HostLock);
        Assert.True((await locks.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "other", Reader)).Success);
    }

    private static CellBridgeDocumentService Service(IDocumentAuthorizationProvider policy, TimeProvider? time = null, IContentStore? content = null) =>
        new(Memory(content), documentAuthorizationProvider: policy, authorizationTimeProvider: time);

    private static async Task<DocumentState> Import(CellBridgeDocumentService service, byte[]? bytes = null)
    {
        var imported = await service.ImportExternalAsync(Guid.NewGuid(), "/external-" + Guid.NewGuid().ToString("N") + ".docx",
            bytes ?? MinimalDocx.Create("original"), Scope, Owner.Identity, Owner);
        return imported!;
    }

    private static void AssertRequest(DocumentAuthorizationRequest request, DocumentState state,
        CellBridgeActor actor, DocumentAuthorizationScope scope)
    {
        Assert.Equal(actor.Identity.Subject, request.Subject);
        Assert.Equal(scope, request.Scope);
        Assert.Equal(state.ResourceId, request.ResourceId);
        Assert.Equal(state.LifecycleGeneration, request.LifecycleGeneration);
        Assert.Equal(state.Security.AuthorizationPolicy, request.Binding);
    }

    private sealed class Policy : IDocumentAuthorizationProvider
    {
        private readonly ConcurrentQueue<DocumentAuthorizationRequest> _requests = new();
        public string PolicyDomain => "tests:ordinary-async";
        public bool Allow { get; set; } = true;
        public Func<DocumentAuthorizationRequest, int, DocumentAuthorizationDecision>? Decision { get; set; }
        public Action? OnAuthorize { get; set; }
        public IReadOnlyCollection<DocumentAuthorizationRequest> Requests => _requests.ToArray();
        public int Count(DocumentAction action, string subject) => _requests.Count(r => r.Action == action && r.Subject == subject);
        public void Clear() { while (_requests.TryDequeue(out _)) { } }

        public ValueTask<DocumentAuthorizationDecision> AuthorizeAsync(DocumentAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnAuthorize?.Invoke();
            _requests.Enqueue(request);
            var index = _requests.Count;
            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(Decision?.Invoke(request, index) ?? new(Allow, now, now.AddMinutes(1)));
        }
    }

    private sealed class SubjectPolicy(string owner, string reader) : IDocumentAuthorizationProvider
    {
        public string PolicyDomain => "tests:ordinary-async-lock";
        public bool OwnerAllowed { get; set; } = true;
        public ValueTask<DocumentAuthorizationDecision> AuthorizeAsync(DocumentAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            var allowed = request.Subject == owner ? OwnerAllowed : request.Subject == reader;
            return ValueTask.FromResult(new DocumentAuthorizationDecision(allowed));
        }
    }

    private sealed class GatedPolicy : IDocumentAuthorizationProvider
    {
        private int _gateNextReaderRead = 1;
        private TaskCompletionSource _entered = NewSource();
        private TaskCompletionSource _release = NewSource();
        public string PolicyDomain => "tests:ordinary-async";
        public TaskCompletionSource Entered => _entered;
        public TaskCompletionSource Release => _release;

        public async ValueTask<DocumentAuthorizationDecision> AuthorizeAsync(DocumentAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.Subject == Reader.Identity.Subject && request.Action == DocumentAction.Read &&
                Interlocked.Exchange(ref _gateNextReaderRead, 0) == 1)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            return new(true);
        }

        public void ResetGate()
        {
            _entered = NewSource();
            _release = NewSource();
            Interlocked.Exchange(ref _gateNextReaderRead, 1);
        }

        private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class PausedContentStore : IContentStore
    {
        private readonly InMemoryContentStore _inner = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Durable => false;
        public bool Shared => false;
        public bool Pause { get; set; }
        public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
        {
            if (Pause)
            {
                Pause = false;
                Entered.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken);
            }
            return await _inner.WriteAsync(source, cancellationToken);
        }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) =>
            _inner.OpenReadAsync(handle, cancellationToken);
    }

    private sealed class MeasuringStateStore(IDocumentStateStore inner, Action<bool> transitionFlag) : IDocumentStateStore
    {
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public ValueTask<DocumentState?> FindByResourceIdAsync(Guid resourceId, CancellationToken cancellationToken = default) =>
            inner.FindByResourceIdAsync(resourceId, cancellationToken);
        public ValueTask<DocumentState?> FindByPathKeyAsync(string pathKey, CancellationToken cancellationToken = default) =>
            inner.FindByPathKeyAsync(pathKey, cancellationToken);
        public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken cancellationToken = default) =>
            inner.ListAsync(offset, limit, cancellationToken);
        public ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken cancellationToken = default) =>
            inner.TryCreateAsync(state, cancellationToken);
        public ValueTask<T> TransitionAsync<T>(Guid resourceId, Func<DocumentState, DateTime, StateTransition<T>> transition,
            CancellationToken cancellationToken = default) => inner.TransitionAsync(resourceId, (current, now) =>
            {
                transitionFlag(true);
                try { return transition(current, now); }
                finally { transitionFlag(false); }
            }, cancellationToken);
        public ValueTask CheckHealthAsync(CancellationToken cancellationToken = default) => inner.CheckHealthAsync(cancellationToken);
    }
}
