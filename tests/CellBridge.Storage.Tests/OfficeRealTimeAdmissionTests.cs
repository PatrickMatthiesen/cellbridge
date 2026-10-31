using System.Collections.Concurrent;
using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;
using CellBridge.Tests;

namespace CellBridge.Storage.Tests;

public sealed class OfficeRealTimeAdmissionTests
{
    private static readonly CellBridgeActor Other = new(new("rtc:other", "other", "Other"));
    private static readonly DocumentAuthorizationScope Scope = new("rtc-tenant", "rtc-workspace");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedDocumentActivityReportsBusyAndCanRetryWithoutPermissionChanges(bool together)
    {
        var provider = StorageTestDocuments.Memory();
        var state = (await new CellBridgeDocumentService(provider).CreateAsync(
            "/rtc-activity.docx", MinimalDocx.Create(), TestActor.Value))!;
        var repository = new ChurningControlReadRepository(provider.Documents);
        var service = new CellBridgeDocumentService(new StorageProvider(repository, provider.Content));

        async Task Authorize()
        {
            if (together)
            {
                var allowed = await service.AuthorizeOfficeRealTimeTogetherAsync(state.ResourceId,
                    state.LifecycleGeneration, [new(TestActor.Value, null, DocumentAccess.Read, false)],
                    CancellationToken.None);
                Assert.Equal([true], allowed);
            }
            else
                Assert.NotNull(await service.AuthorizeOfficeRealTimeAsync(state.ResourceId,
                    state.LifecycleGeneration, TestActor.Value, null, DocumentAccess.Read, false,
                    CancellationToken.None));
        }

        await Assert.ThrowsAsync<OfficeRealTimeAdmissionBusyException>(Authorize);
        Assert.Equal(8, repository.ReadCount);
        var current = (await provider.Documents.ReadControlAsync(state.ResourceId))!;
        Assert.Equal(state.AuthorityGeneration, current.Head.AuthorityGeneration);
        repository.Churn = false;
        await Authorize();
    }

    [Fact]
    public async Task ReadAdmissionUsesCurrentGrantAndHonorsAccessLimit()
    {
        var service = new CellBridgeDocumentService(StorageTestDocuments.Memory());
        var state = (await service.CreateAsync("/rtc-read.docx", MinimalDocx.Create(), TestActor.Value))!;

        var admitted = await service.AuthorizeOfficeRealTimeAsync(state.ResourceId, state.LifecycleGeneration,
            TestActor.Value, null, DocumentAccess.Read, requireOwnedClient: false, CancellationToken.None);
        Assert.NotNull(admitted);

        var limited = TestActor.Value with { AccessLimit = new(state.ResourceId, DocumentAccess.None) };
        Assert.Null(await service.AuthorizeOfficeRealTimeAsync(state.ResourceId, state.LifecycleGeneration,
            limited, null, DocumentAccess.Read, requireOwnedClient: false, CancellationToken.None));
        Assert.Null(await service.AuthorizeOfficeRealTimeAsync(state.ResourceId, state.LifecycleGeneration + 1,
            TestActor.Value, null, DocumentAccess.Read, requireOwnedClient: false, CancellationToken.None));
        Assert.Null(await service.AuthorizeOfficeRealTimeAsync(state.ResourceId, state.LifecycleGeneration,
            Other, null, DocumentAccess.Read, requireOwnedClient: false, CancellationToken.None));
    }

    [Fact]
    public async Task WriteAdmissionRequiresOwnedClientLeaseAndCurrentWriteGrant()
    {
        var clock = new ManualClock();
        var content = new InMemoryContentStore();
        var provider = new StorageProvider(new InMemoryDocumentRepository(content, clock: clock), content);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/rtc-write.docx", MinimalDocx.Create(), TestActor.Value))!;
        var client = Guid.NewGuid();

        Assert.Equal("Success", await ApplyExclusiveLock(provider, state.ResourceId, client));
        Assert.NotNull(await service.AuthorizeOfficeRealTimeAsync(state.ResourceId, state.LifecycleGeneration,
            TestActor.Value, client, DocumentAccess.Write, requireOwnedClient: true, CancellationToken.None));
        Assert.Null(await service.AuthorizeOfficeRealTimeAsync(state.ResourceId, state.LifecycleGeneration,
            Other, client, DocumentAccess.Write, requireOwnedClient: true, CancellationToken.None));
        Assert.Null(await service.AuthorizeOfficeRealTimeAsync(state.ResourceId, state.LifecycleGeneration,
            TestActor.Value, Guid.NewGuid(), DocumentAccess.Write, requireOwnedClient: true, CancellationToken.None));

        clock.Advance(TimeSpan.FromHours(2));
        Assert.Null(await service.AuthorizeOfficeRealTimeAsync(state.ResourceId, state.LifecycleGeneration,
            TestActor.Value, client, DocumentAccess.Write, requireOwnedClient: true, CancellationToken.None));
        Assert.Null(await service.AuthorizeOfficeRealTimeAsync(state.ResourceId, state.LifecycleGeneration,
            TestActor.Value, Guid.Empty, DocumentAccess.Read, requireOwnedClient: true, CancellationToken.None));
    }

    [Fact]
    public async Task ReadOnlyOwnedEditorAllowsPresenceReadButNeverWrite()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/rtc-editor.docx", MinimalDocx.Create(), TestActor.Value))!;
        var client = Guid.NewGuid();

        Assert.Equal("Success", await ApplyReadOnlyEditorJoin(provider, state.ResourceId, client));
        Assert.NotNull(await service.AuthorizeOfficeRealTimeAsync(state.ResourceId, state.LifecycleGeneration,
            TestActor.Value, client, DocumentAccess.Read, requireOwnedClient: true, CancellationToken.None));
        Assert.Null(await service.AuthorizeOfficeRealTimeAsync(state.ResourceId, state.LifecycleGeneration,
            TestActor.Value, client, DocumentAccess.Write, requireOwnedClient: false, CancellationToken.None));
    }

    [Fact]
    public async Task ExternalBindingChangeDuringAuthorityCallRetriesAgainstNewBinding()
    {
        var policy = new GatedPolicy();
        var provider = StorageTestDocuments.Memory();
        var state = (await new CellBridgeDocumentService(provider).CreateAsync(
            "/rtc-binding-race.docx", MinimalDocx.Create(), TestActor.Value))!;
        var service = new CellBridgeDocumentService(provider, documentAuthorizationProvider: policy);
        Assert.True(await service.BindExternalAuthorizationAsync(state.ResourceId, state.StateVersion, null, Scope));

        var admissionTask = service.AuthorizeOfficeRealTimeAsync(state.ResourceId, state.LifecycleGeneration,
            TestActor.Value, null, DocumentAccess.Read, requireOwnedClient: false, CancellationToken.None).AsTask();
        await policy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var current = (await service.Provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.True(await service.BindExternalAuthorizationAsync(current.ResourceId, current.StateVersion,
            current.Security.AuthorizationPolicy, Scope with { WorkspaceId = "rtc-workspace-rebound" }));
        policy.Release.TrySetResult();

        var admission = await admissionTask;
        Assert.NotNull(admission);
        Assert.Equal("rtc-workspace-rebound", admission.Actor.Authorization!.Binding!.WorkspaceId);
        var requests = policy.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal("rtc-workspace", requests[0].Binding.WorkspaceId);
        Assert.Equal("rtc-workspace-rebound", requests[1].Binding.WorkspaceId);
    }

    [Fact]
    public async Task ExternalGrantRevokedWhileAuthorityCallWaitsDeniesAdmission()
    {
        var policy = new GatedPolicy();
        var provider = StorageTestDocuments.Memory();
        var state = (await new CellBridgeDocumentService(provider).CreateAsync(
            "/rtc-revocation-race.docx", MinimalDocx.Create(), TestActor.Value))!;
        var service = new CellBridgeDocumentService(provider, documentAuthorizationProvider: policy);
        Assert.True(await service.BindExternalAuthorizationAsync(state.ResourceId, state.StateVersion, null, Scope));

        var admissionTask = service.AuthorizeOfficeRealTimeAsync(state.ResourceId, state.LifecycleGeneration,
            TestActor.Value, null, DocumentAccess.Read, requireOwnedClient: false, CancellationToken.None).AsTask();
        await policy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        policy.Allowed = false;
        policy.Release.TrySetResult();

        Assert.Null(await admissionTask);
        Assert.Single(policy.Requests);
    }

    [Fact]
    public async Task TogetherAdmissionRetriesWhenStoredGrantChangesBeforeFinalRead()
    {
        var content = new InMemoryContentStore();
        var repository = new InMemoryDocumentRepository(content);
        var rawProvider = new StorageProvider(repository, content);
        var state = (await new CellBridgeDocumentService(rawProvider).CreateAsync(
            "/rtc-together-race.docx", MinimalDocx.Create(), TestActor.Value))!;
        await TestStorage.SetPermissionsAsync(rawProvider, state.ResourceId, state.Security with
        {
            Grants = state.Security.Grants.Add(Other.Identity.Subject, DocumentAccess.Read)
        });

        var gate = new FinalReadGateRepository(repository);
        var service = new CellBridgeDocumentService(new StorageProvider(gate, content));
        var checks = new OfficeRealTimeAccessCheck[]
        {
            new(TestActor.Value, null, DocumentAccess.Read, RequireOwnedClient: false),
            new(Other, null, DocumentAccess.Read, RequireOwnedClient: false)
        };

        var admissionTask = service.AuthorizeOfficeRealTimeTogetherAsync(state.ResourceId,
            state.LifecycleGeneration, checks, CancellationToken.None).AsTask();
        await gate.FinalReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var current = (await repository.ReadControlAsync(state.ResourceId))!;
        await TestStorage.SetPermissionsAsync(rawProvider, state.ResourceId, current.Head.Security with
        {
            Grants = current.Head.Security.Grants.Remove(Other.Identity.Subject)
        });
        gate.ReleaseFinalRead.TrySetResult();

        var allowed = await admissionTask;
        Assert.Equal([true, false], allowed);
        Assert.Equal(4, gate.ReadCount);
    }

    [Fact]
    public async Task TogetherAdmissionAppliesAccessLimitLifecycleAndOwnedClientFences()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/rtc-together-fences.docx", MinimalDocx.Create(), TestActor.Value))!;
        var client = Guid.NewGuid();
        Assert.Equal("Success", await ApplyExclusiveLock(provider, state.ResourceId, client));

        var limitedActor = TestActor.Value with
        {
            AccessLimit = new(state.ResourceId, DocumentAccess.None)
        };
        var checks = new OfficeRealTimeAccessCheck[]
        {
            new(TestActor.Value, client, DocumentAccess.Write, RequireOwnedClient: true),
            new(TestActor.Value, Guid.NewGuid(), DocumentAccess.Write, RequireOwnedClient: true),
            new(limitedActor, client, DocumentAccess.Write, RequireOwnedClient: true)
        };

        var validGeneration = await service.AuthorizeOfficeRealTimeTogetherAsync(
            state.ResourceId, state.LifecycleGeneration, checks, CancellationToken.None);
        var staleGeneration = await service.AuthorizeOfficeRealTimeTogetherAsync(
            state.ResourceId, state.LifecycleGeneration + 1, checks, CancellationToken.None);
        var missingResource = await service.AuthorizeOfficeRealTimeTogetherAsync(
            Guid.NewGuid(), state.LifecycleGeneration, checks, CancellationToken.None);
        Assert.Equal(new[] { true, false, false }, validGeneration);
        Assert.Equal(new[] { false, false, false }, staleGeneration);
        Assert.Equal(new[] { false, false, false }, missingResource);
    }

    private static async Task<string?> ApplyExclusiveLock(StorageProvider provider, Guid resourceId, Guid clientId)
    {
        var response = await new CellBridgeRequestProcessor(new(provider)).ExecuteAsync(new() { Requests =
        {
            new FssHttpRequest { UseResourceId = true, ResourceId = resourceId.ToString(), Url = "https://host.test/rtc.docx",
                RequestToken = 1, SubRequests = { new FssHttpSubRequest { Type = SubRequestType.ExclusiveLock,
                    SubRequestToken = 1, SubRequestDataXml = "<SubRequestData />",
                    SubRequestDataAttributes = { ["ExclusiveLockRequestType"] = "GetLock",
                        ["ExclusiveLockID"] = "11111111-1111-1111-1111-111111111111",
                        ["SchemaLockID"] = "22222222-2222-2222-2222-222222222222",
                        ["ClientID"] = clientId.ToString("D"), ["Timeout"] = "3600" } } } }
        } }, "https://host.test", TestActor.Value);
        return response.Response.Responses[0].SubResponses[0].ErrorCode ?? "Success";
    }

    private static async Task<string?> ApplyReadOnlyEditorJoin(StorageProvider provider, Guid resourceId, Guid clientId)
    {
        var response = await new CellBridgeRequestProcessor(new(provider)).ExecuteAsync(new() { Requests =
        {
            new FssHttpRequest { UseResourceId = true, ResourceId = resourceId.ToString(), Url = "https://host.test/rtc.docx",
                RequestToken = 1, SubRequests = { new FssHttpSubRequest { Type = SubRequestType.EditorsTable,
                    SubRequestToken = 1, SubRequestDataXml = "<SubRequestData />",
                    SubRequestDataAttributes = { ["EditorsTableRequestType"] = "JoinEditingSession",
                        ["ClientID"] = clientId.ToString("D"), ["Timeout"] = "3600", ["AsEditor"] = "false" } } } }
        } }, "https://host.test", TestActor.Value);
        return response.Response.Responses[0].SubResponses[0].ErrorCode ?? "Success";
    }

    [Fact]
    public async Task MissingDeletedAndRevokedDocumentsFailClosed()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/rtc-revoked.docx", MinimalDocx.Create(), TestActor.Value))!;

        await TestStorage.SetPermissionsAsync(provider, state.ResourceId, state.Security with { Owner = "rtc:other" });
        Assert.Null(await service.AuthorizeOfficeRealTimeAsync(state.ResourceId, state.LifecycleGeneration,
            TestActor.Value, null, DocumentAccess.Read, requireOwnedClient: false, CancellationToken.None));
        Assert.Null(await service.AuthorizeOfficeRealTimeAsync(Guid.NewGuid(), null, TestActor.Value,
            null, DocumentAccess.Read, requireOwnedClient: false, CancellationToken.None));

        var current = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.True((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(
            current.ResourceId, current.LifecycleGeneration, current.StateVersion))).Applied);
        Assert.Null(await service.AuthorizeOfficeRealTimeAsync(state.ResourceId, null, TestActor.Value,
            null, DocumentAccess.Read, requireOwnedClient: false, CancellationToken.None));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-30T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class GatedPolicy : IDocumentAuthorizationProvider
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<DocumentAuthorizationRequest> _requests = new();
        private int _gated;
        public string PolicyDomain => "tests:office-rtc";
        public bool Allowed { get; set; } = true;
        public TaskCompletionSource Entered => _entered;
        public TaskCompletionSource Release => _release;
        public IReadOnlyCollection<DocumentAuthorizationRequest> Requests => _requests.ToArray();

        public async ValueTask<DocumentAuthorizationDecision> AuthorizeAsync(DocumentAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            _requests.Enqueue(request);
            if (Interlocked.Exchange(ref _gated, 1) == 0)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            var now = DateTimeOffset.UtcNow;
            return new(Allowed, now, now.AddMinutes(1));
        }
    }

    private sealed class ChurningControlReadRepository(IDocumentRepository inner) : RepositoryTestProxy(inner)
    {
        public bool Churn { get; set; } = true;
        public int ReadCount { get; private set; }

        public override async ValueTask<DocumentControlRead?> ReadControlAsync(Guid id,
            CancellationToken ct = default)
        {
            ReadCount++;
            if (Churn && ReadCount % 2 == 0)
            {
                var current = (await Inner.ReadControlAsync(id, ct))!;
                var path = "/rtc-activity-" + ReadCount + ".docx";
                Assert.True((await Inner.ChangePathAsync(new(id, TestStorage.Admit(current.Head),
                    path, path.ToUpperInvariant(), ImmutableDictionary<string, string>.Empty), ct)).Changed);
            }
            return await base.ReadControlAsync(id, ct);
        }
    }

    private sealed class FinalReadGateRepository(IDocumentRepository inner) : RepositoryTestProxy(inner)
    {
        private int _readCount;
        public TaskCompletionSource FinalReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFinalRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadCount => Volatile.Read(ref _readCount);

        public override async ValueTask<DocumentControlRead?> ReadControlAsync(Guid id,
            CancellationToken ct = default)
        {
            var count = Interlocked.Increment(ref _readCount);
            if (count == 2)
            {
                FinalReadEntered.TrySetResult();
                await ReleaseFinalRead.Task.WaitAsync(ct);
            }
            return await base.ReadControlAsync(id, ct);
        }
    }
}
