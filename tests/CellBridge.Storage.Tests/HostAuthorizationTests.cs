using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Tests;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed class HostAuthorizationTests
{
    private static readonly CellBridgeActor Writer = new(new("external:writer", "writer", "Writer"));
    private static readonly CellBridgeActor Reader = new(new("external:reader", "reader", "Reader"));
    private static readonly Dictionary<string, string> Attributes = [];

    [Theory]
    [InlineData("missing")]
    [InlineData("resolver")]
    [InlineData("subject")]
    public async Task FailedPermissionUpdatesLeaveBindingGenerationSessionsAndLeasesUnchanged(string failure)
    {
        var (provider, policy, service, state) = await Create();
        var join = HostIntegrationTests.File(state.ResourceId, SubRequestType.Coauth);
        join.SubRequests[0].SubRequestDataAttributes["CoauthRequestType"] = "JoinCoauthoring";
        join.SubRequests[0].SubRequestDataAttributes["ClientID"] = Guid.NewGuid().ToString();
        join.SubRequests[0].SubRequestDataAttributes["SchemaLockID"] = Guid.NewGuid().ToString();
        Assert.Equal("Success", (await new CellBridgeRequestProcessor(service).ExecuteAsync(new() { Requests = { join } }, "https://host.test", Writer)).Response.Responses[0].SubResponses[0].ErrorCode);
        var before = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Single(before.Editors); Assert.Single(before.Coordination.SchemaOwners);
        if (failure != "missing") policy.Install(state.ResourceId, 2, DocumentAccess.None);
        policy.Throw = failure == "resolver"; policy.ThrowSubject = failure == "subject";
        if (failure == "subject")
            await Assert.ThrowsAsync<IOException>(() => service.UpdateAuthorizationAsync(state.ResourceId, policy.Binding(1), policy.Binding(2)).AsTask());
        else
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateAuthorizationAsync(state.ResourceId, policy.Binding(1), policy.Binding(2)).AsTask());
        Assert.Equal(before, await provider.State.FindByResourceIdAsync(state.ResourceId));
    }

    [Fact]
    public async Task ExternalCatalogOffsetsCountOnlyAllowedRowsAcrossPhysicalPages()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var policy = new Policy(); var service = new CellBridgeDocumentService(provider, authorizationPolicy: policy);
        for (int i = 0; i < 270; i++)
        {
            var id = Guid.NewGuid(); policy.Install(id, 1, DocumentAccess.None);
            await service.CreateAsync(id, $"/shared/{i:D3}.bin", [1], TestActor.Value);
        }
        var allowed = new List<Guid>();
        for (int i = 0; i < 2; i++)
        {
            var id = Guid.NewGuid(); allowed.Add(id); policy.Install(id, 1, DocumentAccess.Read);
            await service.CreateAsync(id, $"/shared/z-{i}.bin", [1], TestActor.Value);
        }
        var first = await AuthorizedDocumentCatalog.ReadAsync(service, Writer, 0, 1);
        Assert.Equal(allowed[0], Assert.Single(first.Documents).ResourceId); Assert.Equal(1, first.NextOffset);
        var second = await AuthorizedDocumentCatalog.ReadAsync(service, Writer, first.NextOffset!.Value, 1);
        Assert.Equal(allowed[1], Assert.Single(second.Documents).ResourceId); Assert.Null(second.NextOffset);
        Assert.Empty((await AuthorizedDocumentCatalog.ReadAsync(service, Writer, 2, 1)).Documents);
    }

    [Fact]
    public async Task RevisionAwareCeilingReconcilesSessionsAndLeasesAndLegacyActorsRemainIntact()
    {
        var (provider, policy, _, state) = await Create();
        var service = new CellBridgeDocumentService(provider, new RevisionCeiling(), policy);
        var join = HostIntegrationTests.File(state.ResourceId, SubRequestType.Coauth);
        join.SubRequests[0].SubRequestDataAttributes["CoauthRequestType"] = "JoinCoauthoring";
        join.SubRequests[0].SubRequestDataAttributes["ClientID"] = Guid.NewGuid().ToString();
        join.SubRequests[0].SubRequestDataAttributes["SchemaLockID"] = Guid.NewGuid().ToString();
        Assert.Equal("Success", (await new CellBridgeRequestProcessor(service).ExecuteAsync(new() { Requests = { join } }, "https://host.test", Writer)).Response.Responses[0].SubResponses[0].ErrorCode);
        policy.Install(state.ResourceId, 2, DocumentAccess.Write);
        Assert.True(await service.UpdateAuthorizationAsync(state.ResourceId, policy.Binding(1), policy.Binding(2)));
        var downgraded = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.False(Assert.Single(downgraded.Editors).AsEditor);
        Assert.Empty(downgraded.Coordination.SchemaOwners);
        Assert.Equal(DocumentAccess.Read, service.Access(Writer, downgraded));
        policy.Install(state.ResourceId, 3, DocumentAccess.Write);
        Assert.True(await service.UpdateAuthorizationAsync(state.ResourceId, policy.Binding(2), policy.Binding(3)));
        Assert.Empty((await provider.State.FindByResourceIdAsync(state.ResourceId))!.Editors);
        var legacyActor = TestActor.Value with { AccessLimit = new(state.ResourceId, DocumentAccess.Read) };
        var legacyService = new CellBridgeDocumentService(provider, new OriginalActorEvaluator(legacyActor));
        Assert.Equal(DocumentAccess.Read, legacyService.Access(legacyActor, state with { Security = state.Security with { AuthorizationPolicy = null } }));
    }

    private sealed class RevisionCeiling : ICellBridgeAccessEvaluator
    {
        public DocumentAccess Evaluate(CellBridgeActor actor, DocumentState state)
        {
            Assert.Equal(actor.Identity.Subject, actor.Identity.Login);
            Assert.Equal(actor.Identity.Subject, actor.Identity.DisplayName);
            return state.Security.AuthorizationPolicy!.Revision switch { 1 => DocumentAccess.Write, 2 => DocumentAccess.Read, _ => DocumentAccess.None };
        }
    }
    private sealed class OriginalActorEvaluator(CellBridgeActor expected) : ICellBridgeAccessEvaluator
    { public DocumentAccess Evaluate(CellBridgeActor actor, DocumentState state) { Assert.Same(expected, actor); return DocumentAccess.Write; } }

    [PostgreSqlFact]
    public async Task DurableBindingRevocationAndReceiptsSurviveSeparateProviderInstances()
    {
        await using var data = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        var provider = new StorageProvider(new PostgreSqlStateStore(data), new PostgreSqlContentStore(data));
        var policy = new Policy(); var id = Guid.NewGuid(); policy.Install(id, 1, DocumentAccess.Write);
        var service = new CellBridgeDocumentService(provider, authorizationPolicy: policy);
        var state = (await service.CreateAsync(id, "/shared/" + id + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = Save(state);
        var saved = await service.ExecuteAsync(id, DocumentPartitionKind.FileContents, request, Attributes, Writer);
        Assert.False(saved.Response.SubResponses[0].Status);
        await using var otherData = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        var otherProvider = new StorageProvider(new PostgreSqlStateStore(otherData), new PostgreSqlContentStore(otherData));
        var otherPolicy = new Policy(); otherPolicy.Install(id, 1, DocumentAccess.Write);
        var otherService = new CellBridgeDocumentService(otherProvider, authorizationPolicy: otherPolicy);
        Assert.True(Assert.Single((await otherService.ExecuteAsync(id, DocumentPartitionKind.FileContents, request, Attributes, Writer)).AcceptedSaves).IsReplay);
        policy.Install(id, 2, DocumentAccess.Read);
        Assert.True(await service.UpdateAuthorizationAsync(id, policy.Binding(1), policy.Binding(2)));
        var committed = (await otherProvider.State.FindByResourceIdAsync(id))!;
        Assert.Equal(policy.Binding(2), committed.Security.AuthorizationPolicy);
        Assert.Equal(DocumentAccess.None, otherService.Access(Writer, committed));
        otherPolicy.Install(id, 2, DocumentAccess.Read);
        var denied = await otherService.ExecuteAsync(id, DocumentPartitionKind.FileContents, request, Attributes, Writer);
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, denied.Response.SubResponses[0].Error!.ErrorCode);
        Assert.Equal(saved.State.Content, denied.State.Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataPreparationAndHistoryRestoreAreFenced(bool restore)
    {
        var hook = new HookContent(new InMemoryContentStore());
        var (provider, policy, service, state) = await Create(hook);
        hook.OnWrite = async () =>
        {
            policy.Install(state.ResourceId, 2, DocumentAccess.Read);
            Assert.True(await service.UpdateAuthorizationAsync(state.ResourceId, policy.Binding(1), policy.Binding(2)));
            policy.Install(state.ResourceId, 3, DocumentAccess.Write);
            Assert.True(await service.UpdateAuthorizationAsync(state.ResourceId, policy.Binding(2), policy.Binding(3)));
        };
        if (restore)
            await Assert.ThrowsAsync<DocumentOperationLockException>(() => service.RestoreRevisionAsync(state.ResourceId, 1, 1, "staged-restore", Writer).AsTask());
        else
        {
            var graph = new GraphFixture([1, 2, 3], blob: true);
            var request = new FsshttpbCellRequest { DataElementPackage = new() { DataElements = graph.Elements.ToList() },
                SubRequests = { new(RequestTypes.PutChanges) { RequestId = 1, Data = new PutChangesSubRequestData { StorageIndex = graph.Index } } } };
            var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata, request, Attributes, Writer);
            Assert.True(result.Response.SubResponses[0].Status);
        }
        var current = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal(state.Content, current.Content); Assert.Equal(state.ContentVersion, current.ContentVersion);
        Assert.Equal(state.Partitions.Single(p => p.Kind == 1), current.Partitions.Single(p => p.Kind == 1));
        Assert.Empty(current.Receipts); Assert.Empty(current.RestoreReceipts);
    }

    [Fact]
    public async Task HostPolicyReplacesOwnershipAndGrantsAcrossCatalogProtocolAndHistory()
    {
        var (provider, policy, service, state) = await Create();
        Assert.Empty(state.Security.Grants);
        Assert.Equal(TestActor.Value.Identity.Subject, state.Security.Owner);
        Assert.Equal(DocumentAccess.None, service.Access(TestActor.Value, state));
        Assert.Equal(DocumentAccess.Read | DocumentAccess.Write, service.Access(Writer, state));
        Assert.Equal(DocumentAccess.Read, service.Access(Reader, state));
        await provider.State.TransitionAsync(state.ResourceId, (current, _) => new StateTransition<bool>(current with
        { Security = current.Security with { Grants = current.Security.Grants.Add(TestActor.Value.Identity.Subject, DocumentAccess.Write) } }, true));
        Assert.Empty((await AuthorizedDocumentCatalog.ReadAsync(service, TestActor.Value, 0, 10)).Documents);
        Assert.Equal(state.ResourceId, Assert.Single((await AuthorizedDocumentCatalog.ReadAsync(service, Writer, 0, 1)).Documents).ResourceId);
        Assert.Empty((await AuthorizedDocumentCatalog.ReadAsync(service, Writer, 1, 1)).Documents);
        var current = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        var denied = await new CellBridgeRequestProcessor(service).ExecuteAsync(new() { Requests =
            { HostIntegrationTests.File(state.ResourceId, SubRequestType.GetDocMetaInfo) } }, "https://host.test", TestActor.Value);
        Assert.Null(denied.Response.Responses[0].ResourceId);
        Assert.Equal("FileUnauthorizedAccess", denied.Response.Responses[0].SubResponses[0].ErrorCode);
        var metadata = await new CellBridgeRequestProcessor(service).ExecuteAsync(new() { Requests =
            { HostIntegrationTests.File(state.ResourceId, SubRequestType.GetDocMetaInfo) } }, "https://host.test", Reader);
        Assert.Equal("Success", metadata.Response.Responses[0].SubResponses[0].ErrorCode);
        Assert.Single(await service.ListRevisionsAsync(state.ResourceId, Reader));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListRevisionsAsync(state.ResourceId, TestActor.Value).AsTask());
        var access = new FsshttpbCellRequest { SubRequests = { new(RequestTypes.QueryAccess) { RequestId = 7, Data = new QueryAccessSubRequestData() } } };
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, access, Attributes, Reader);
        var permissions = Assert.IsType<QueryAccessSubResponseData>(result.Response.SubResponses[0].Data);
        Assert.Null(permissions.ReadAccessError); Assert.NotNull(permissions.WriteAccessError);
        var limited = Writer with { AccessLimit = new(state.ResourceId, DocumentAccess.Read) };
        Assert.Equal(DocumentAccess.Read, service.Access(limited, current));
        var deniedSave = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, Save(current), Attributes, Reader);
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, deniedSave.Response.SubResponses[0].Error!.ErrorCode);
        Assert.Equal(current.Content, deniedSave.State.Content);
        var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, Save(current), Attributes, Writer);
        Assert.False(saved.Response.SubResponses[0].Status);
        Assert.Equal(Writer.Identity, saved.State.Security.ModifiedBy);
        Assert.Equal(state.Security.AuthorizationPolicy, saved.State.Security.AuthorizationPolicy);
    }

    [Fact]
    public async Task CreationAndImportUseFinalIdentityAndNeverPublishAnUnboundDocument()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var policy = new Policy(); var service = new CellBridgeDocumentService(provider, authorizationPolicy: policy);
        for (int i = 0; i < 4; i++)
        {
            var id = Guid.NewGuid();
            policy.Install(id, 1, DocumentAccess.Write);
            // Generated identities are installed by the trusted creation binding selector.
            policy.OnBind = resource => policy.Install(resource, 1, DocumentAccess.Write);
            var state = i switch
            {
                0 => await service.CreateAsync("/create.bin", [1], TestActor.Value),
                1 => await service.CreateAsync(id, "/id-create.bin", [1], TestActor.Value),
                2 => await service.ImportAsync("/import.bin", [1], TestActor.Value.Identity, TestActor.Value),
                _ => await service.ImportAsync(id, "/id-import.bin", [1], TestActor.Value.Identity, TestActor.Value),
            };
            Assert.Equal(policy.Binding(1), state!.Security.AuthorizationPolicy);
            Assert.Equal(DocumentAccess.Read | DocumentAccess.Write, service.Access(Writer, state));
            if (i % 2 == 1) Assert.Equal(id, state.ResourceId);
        }
        policy.OnBind = null; policy.Unavailable = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync("/unavailable.bin", [1], TestActor.Value).AsTask());
        Assert.Equal(4, (await provider.State.ListAsync(0, 100)).Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("throw")]
    [InlineData("undefined")]
    [InlineData("foreign")]
    [InlineData("version")]
    [InlineData("revision")]
    [InlineData("unbound")]
    [InlineData("resource")]
    public async Task InvalidOrUnavailablePoliciesFailClosedEvenWithAnElevatingLegacyEvaluator(string failure)
    {
        var (provider, policy, _, state) = await Create();
        var service = new CellBridgeDocumentService(provider, new ElevatingEvaluator(), policy);
        policy.Unavailable = failure == "missing";
        policy.Throw = failure == "throw";
        if (failure == "undefined") policy.Install(state.ResourceId, 2, (DocumentAccess)4);
        var binding = state.Security.AuthorizationPolicy!;
        binding = failure switch { "foreign" => binding with { PolicyDomain = "foreign" }, "version" => binding with { ContractVersion = 2 },
            "revision" => binding with { Revision = 0 }, "undefined" => binding with { Revision = 2 }, _ => binding };
        state = state with { Security = state.Security with { AuthorizationPolicy = failure == "unbound" ? null : binding } };
        if (failure == "resource") policy.WrongResource = true;
        Assert.Equal(DocumentAccess.None, service.Access(Writer, state));
        Assert.Equal(DocumentAccess.None, new CellBridgeDocumentService(provider, new ElevatingEvaluator()).Access(Writer,
            state with { Security = state.Security with { AuthorizationPolicy = policy.Binding(1) } }));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RevocationDuringSaveFencesBothRevokedAndRegrantedWriters(bool regrant, bool negotiatedSoap)
    {
        var hook = new HookContent(new InMemoryContentStore());
        var (provider, policy, service, state) = await Create(hook);
        var peer = new CellBridgeDocumentService(provider, authorizationPolicy: policy);
        hook.OnWrite = async () =>
        {
            policy.Install(state.ResourceId, 2, DocumentAccess.Read);
            Assert.True(await peer.UpdateAuthorizationAsync(state.ResourceId, policy.Binding(1), policy.Binding(2)));
            if (regrant)
            {
                policy.Install(state.ResourceId, 3, DocumentAccess.Write);
                Assert.True(await peer.UpdateAuthorizationAsync(state.ResourceId, policy.Binding(2), policy.Binding(3)));
            }
        };
        if (negotiatedSoap)
        {
            var hashingService = new CellBridgeDocumentService(provider, authorizationPolicy: policy,
                hashing: new ProtocolHashingOptions(new byte[32]));
            var binary = Save(state);
            binary.HashOptions = new(IncludeHashes: true);
            binary.SubRequests.Insert(0, new(RequestTypes.QueryChanges) { RequestId = 99,
                Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } });
            var soap = HostIntegrationTests.File(state.ResourceId, SubRequestType.Cell);
            HostIntegrationTests.SetBinary(soap.SubRequests[0], binary.ToByteArray());
            var result = await new CellBridgeRequestProcessor(hashingService).ExecuteAsync(
                new() { Requests = { soap } }, "https://host.test", Writer);
            var response = result.Response.Responses[0].SubResponses[0];
            if (response.SubResponseDataBase64 is { Length: > 0 } payload)
            {
                var decoded = FsshttpbResponse.Deserialize(new BinaryReaderEx(payload));
                Assert.False(decoded.SubResponses[0].Status);
                Assert.True(decoded.SubResponses[1].Status);
            }
            else Assert.Equal("InvalidCoauthSession", response.ErrorCode);
            Assert.Empty(result.AcceptedSaves);
        }
        else
        {
            var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, Save(state), Attributes, Writer);
            Assert.True(saved.Response.SubResponses.Count == 0 || saved.Response.SubResponses[0].Status);
            Assert.Empty(saved.AcceptedSaves);
        }
        var current = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal(state.Content, current.Content); Assert.Equal(state.ContentVersion, current.ContentVersion);
        Assert.Empty(current.Receipts);
        Assert.Equal(policy.Binding(regrant ? 3 : 2), current.Security.AuthorizationPolicy);
    }

    [Fact]
    public async Task ReceiptReplayRechecksWriterIncludingRevocationDuringLoading()
    {
        var hook = new HookContent(new InMemoryContentStore());
        var (provider, policy, service, state) = await Create(hook);
        var request = Save(state);
        var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, Attributes, Writer);
        Assert.False(saved.Response.SubResponses[0].Status);
        var replay = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, Attributes, Writer);
        Assert.True(Assert.Single(replay.AcceptedSaves).IsReplay);
        policy.Install(state.ResourceId, 2, DocumentAccess.Write, readerAccess: DocumentAccess.Write);
        Assert.True(await service.UpdateAuthorizationAsync(state.ResourceId, policy.Binding(1), policy.Binding(2)));
        var other = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, Attributes, Reader);
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, other.Response.SubResponses[0].Error!.ErrorCode);
        hook.OnRead = async () =>
        {
            policy.Install(state.ResourceId, 3, DocumentAccess.Read);
            Assert.True(await service.UpdateAuthorizationAsync(state.ResourceId, policy.Binding(2), policy.Binding(3)));
        };
        var denied = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, Attributes, Writer);
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, denied.Response.SubResponses[0].Error!.ErrorCode);
        Assert.Empty(denied.AcceptedSaves);
        Assert.Equal(saved.State.Content, denied.State.Content);
    }

    [Fact]
    public async Task UpdatesReconcileRecordedSubjectsAcrossHostsAndRejectStaleCas()
    {
        var (provider, policy, service, state) = await Create();
        var peerPolicy = new Policy(); peerPolicy.Install(state.ResourceId, 1, DocumentAccess.Write);
        var peer = new CellBridgeDocumentService(provider, authorizationPolicy: peerPolicy);
        var client = Guid.NewGuid(); var schema = Guid.NewGuid().ToString();
        var join = HostIntegrationTests.File(state.ResourceId, SubRequestType.Coauth);
        var attributes = join.SubRequests[0].SubRequestDataAttributes;
        attributes["CoauthRequestType"] = "JoinCoauthoring"; attributes["ClientID"] = client.ToString(); attributes["SchemaLockID"] = schema;
        var processor = new CellBridgeRequestProcessor(service);
        Assert.Equal("Success", (await processor.ExecuteAsync(new() { Requests = { join } }, "https://host.test", Writer)).Response.Responses[0].SubResponses[0].ErrorCode);
        // Another authorized writer cannot use the recorded owner's client ID.
        policy.Install(state.ResourceId, 2, DocumentAccess.Write, DocumentAccess.Write);
        Assert.True(await service.UpdateAuthorizationAsync(state.ResourceId, policy.Binding(1), policy.Binding(2)));
        Assert.Equal("FileUnauthorizedAccess", (await processor.ExecuteAsync(new() { Requests = { join } }, "https://host.test", Reader)).Response.Responses[0].SubResponses[0].ErrorCode);
        var before = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal(Writer.Identity.Subject, Assert.Single(before.Editors).Owner!.Subject);
        policy.Install(state.ResourceId, 3, DocumentAccess.Read);
        policy.Install(state.ResourceId, 4, DocumentAccess.None);
        var winners = await Task.WhenAll(service.UpdateAuthorizationAsync(state.ResourceId, policy.Binding(2), policy.Binding(3)).AsTask(),
            service.UpdateAuthorizationAsync(state.ResourceId, policy.Binding(2), policy.Binding(4)).AsTask());
        Assert.Single(winners, x => x);
        var after = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal(before.Coordination.Generation + 1, after.Coordination.Generation);
        Assert.Empty(after.Coordination.SchemaOwners); Assert.Empty(after.Coordination.CoauthorClients);
        Assert.False(after.Coordination.CoauthorTransitionPending);
        Assert.All(after.Editors, editor => Assert.False(editor.AsEditor));
        Assert.Equal(DocumentAccess.None, peer.Access(Writer, after));
        var currentRevision = after.Security.AuthorizationPolicy!.Revision;
        peerPolicy.Install(state.ResourceId, currentRevision, currentRevision == 3 ? DocumentAccess.Read : DocumentAccess.None);
        Assert.Equal(service.Access(Writer, after), peer.Access(Writer, after));
        Assert.False(await service.UpdateAuthorizationAsync(state.ResourceId, policy.Binding(2), policy.Binding(4)));
        Assert.Throws<ArgumentException>(() => policy.Install(state.ResourceId, 1, DocumentAccess.None));
        var restored = JsonSerializer.Deserialize<DocumentState>(JsonSerializer.Serialize(after))!;
        Assert.Equal(after.Security.AuthorizationPolicy, restored.Security.AuthorizationPolicy);
        Assert.Equal(DocumentAccess.None, new CellBridgeDocumentService(provider).Access(Writer, restored));
        Assert.Throws<InvalidOperationException>(() => DocumentPermissionUpdates.Apply(after, DateTime.UtcNow, after.Security));
        // Remove Read for any remaining recorded reader without relying on its client identity.
        policy.Install(state.ResourceId, 5, DocumentAccess.None);
        Assert.True(await service.UpdateAuthorizationAsync(state.ResourceId, after.Security.AuthorizationPolicy, policy.Binding(5)));
        Assert.Empty((await provider.State.FindByResourceIdAsync(state.ResourceId))!.Editors);
    }

    [Fact]
    public async Task HostLocksAndExclusiveLeasesAreRemovedAndPreparedHostWriteIsFenced()
    {
        var (provider, policy, service, state) = await Create();
        var locks = new SharedDocumentLocks(service);
        var token = (await locks.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "host-token", Writer)).WriteToken!;
        var before = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => locks.ApplyAsync(state.ResourceId, HostLockOperation.Refresh, "host-token", Reader).AsTask());
        policy.Install(state.ResourceId, 2, DocumentAccess.Read);
        Assert.True(await service.UpdateAuthorizationAsync(state.ResourceId, policy.Binding(1), policy.Binding(2)));
        Assert.Null((await provider.State.FindByResourceIdAsync(state.ResourceId))!.Coordination.HostLock);
        policy.Install(state.ResourceId, 3, DocumentAccess.Write);
        Assert.True(await service.UpdateAuthorizationAsync(state.ResourceId, policy.Binding(2), policy.Binding(3)));
        Assert.False(await locks.TryCommitAsync(token, before.StateVersion, Writer, (_, _) => throw new Exception("Must not call commit"), Guid.NewGuid()));
        var request = HostIntegrationTests.File(state.ResourceId, SubRequestType.ExclusiveLock);
        request.SubRequests[0].SubRequestDataAttributes["ExclusiveLockRequestType"] = "GetLock";
        request.SubRequests[0].SubRequestDataAttributes["ExclusiveLockID"] = Guid.NewGuid().ToString();
        Assert.Equal("Success", (await new CellBridgeRequestProcessor(service).ExecuteAsync(new() { Requests = { request } }, "https://host.test", Writer)).Response.Responses[0].SubResponses[0].ErrorCode);
        Assert.NotNull((await provider.State.FindByResourceIdAsync(state.ResourceId))!.Coordination.Exclusive);
        policy.Install(state.ResourceId, 4, DocumentAccess.None);
        Assert.True(await service.UpdateAuthorizationAsync(state.ResourceId, policy.Binding(3), policy.Binding(4)));
        Assert.Null((await provider.State.FindByResourceIdAsync(state.ResourceId))!.Coordination.Exclusive);
    }

    private static async Task<(StorageProvider, Policy, CellBridgeDocumentService, DocumentState)> Create(IContentStore? content = null)
    {
        var provider = new StorageProvider(new InMemoryStateStore(), content ?? new InMemoryContentStore());
        var policy = new Policy(); var id = Guid.NewGuid(); policy.Install(id, 1, DocumentAccess.Write);
        var service = new CellBridgeDocumentService(provider, authorizationPolicy: policy);
        var state = (await service.CreateAsync(id, "/shared/" + id + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        return (provider, policy, service, state);
    }

    private static FsshttpbCellRequest Save(DocumentState state)
    {
        var request = StorageTests.Fixture("save-first");
        ((PutChangesSubRequestData)request.SubRequests[0].Data!).ExpectedStorageIndex = StorageIds.Restore(state.Partitions.Single(p => p.Kind == 0).StorageIndex!);
        return request;
    }

    private sealed class ElevatingEvaluator : ICellBridgeAccessEvaluator
    { public DocumentAccess Evaluate(CellBridgeActor actor, DocumentState state) => DocumentAccess.Write; }

    private sealed class Policy : ICellBridgeAuthorizationPolicy
    {
        private readonly ConcurrentDictionary<(Guid, DocumentAuthorizationBinding), Snapshot> _snapshots = new();
        public string PolicyDomain => "test:external-policy";
        public bool Unavailable, Throw, WrongResource, ThrowSubject;
        public Action<Guid>? OnBind;
        public DocumentAuthorizationBinding Binding(long revision) => new(PolicyDomain, 1, revision);
        public DocumentAuthorizationBinding BindNewDocument(Guid resourceId) { OnBind?.Invoke(resourceId); return Binding(1); }
        public void Install(Guid id, long revision, DocumentAccess writerAccess, DocumentAccess readerAccess = DocumentAccess.Read)
        {
            var snapshot = new Snapshot(id, Binding(revision), ImmutableDictionary<string, DocumentAccess>.Empty
                .Add(Writer.Identity.Subject, writerAccess).Add(Reader.Identity.Subject, readerAccess));
            if (!_snapshots.TryAdd((id, snapshot.Binding), snapshot) &&
                !_snapshots[(id, snapshot.Binding)].Decisions.OrderBy(x => x.Key).SequenceEqual(snapshot.Decisions.OrderBy(x => x.Key)))
                throw new ArgumentException("An installed revision is immutable.");
        }
        public ICellBridgeAuthorizationSnapshot? Resolve(DocumentState state)
        {
            if (Throw) throw new IOException("Unavailable permission service.");
            if (Unavailable || state.Security.AuthorizationPolicy is not { } binding) return null;
            var snapshot = _snapshots.GetValueOrDefault((state.ResourceId, binding));
            if (ThrowSubject && snapshot is not null) return new ThrowingSnapshot(snapshot);
            return WrongResource && snapshot is not null ? snapshot with { ResourceId = Guid.NewGuid() } : snapshot;
        }
    }

    private sealed record Snapshot(Guid ResourceId, DocumentAuthorizationBinding Binding,
        ImmutableDictionary<string, DocumentAccess> Decisions) : ICellBridgeAuthorizationSnapshot
    { public DocumentAccess Evaluate(string subject) => Decisions.GetValueOrDefault(subject); }

    private sealed class ThrowingSnapshot(ICellBridgeAuthorizationSnapshot source) : ICellBridgeAuthorizationSnapshot
    {
        public Guid ResourceId => source.ResourceId;
        public DocumentAuthorizationBinding? Binding => source.Binding;
        public DocumentAccess Evaluate(string subject) => throw new IOException("Recorded-subject permission unavailable.");
    }

    private sealed class HookContent(IContentStore inner) : IContentStore
    {
        public bool Durable => inner.Durable; public bool Shared => inner.Shared;
        public Func<Task>? OnWrite, OnRead;
        public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken ct = default)
        {
            if (OnWrite is { } hook) { OnWrite = null; await hook(); }
            return await inner.WriteAsync(source, ct);
        }
        public async ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken ct = default)
        {
            if (OnRead is { } hook) { OnRead = null; await hook(); }
            return await inner.OpenReadAsync(handle, ct);
        }
    }
}
