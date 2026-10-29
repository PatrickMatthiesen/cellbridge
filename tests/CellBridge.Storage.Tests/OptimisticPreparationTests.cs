using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;
using CellBridge.Tests;

namespace CellBridge.Storage.Tests;

public sealed class OptimisticPreparationTests
{
    private const string LockId = "11111111-1111-1111-1111-111111111111";

    public static IEnumerable<object[]> AuthorityChanges()
    {
        foreach (bool capability in new[] { true })
        foreach (var kind in new[] { DocumentPartitionKind.FileContents, DocumentPartitionKind.Metadata })
        foreach (var change in new[] { "expire", "refresh", "release", "reacquire", "other-owner", "revoke", "delete" })
            yield return [capability, kind, change, false];
        yield return [true, DocumentPartitionKind.FileContents, "refresh", true];
        yield return [true, DocumentPartitionKind.FileContents, "reacquire", true];
    }

    [Theory]
    [MemberData(nameof(AuthorityChanges))]
    public async Task PublicationRechecksChangesAfterReadOnlyPreparation(bool capability, DocumentPartitionKind kind,
        string change, bool afterEntryRead)
    {
        var fixture = await SetupAsync(capability);
        await fixture.LeaseAsync(fixture.Store.UtcNow.AddMinutes(1));
        var before = await fixture.CurrentAsync();
        var request = await fixture.RequestAsync(before, kind, "stale");
        DocumentHead? expected = null;
        async Task ChangeAsync()
        {
            if (change == "expire") fixture.Store.UtcNow = fixture.Store.UtcNow.AddHours(2);
            else if (change == "delete")
                Assert.True((await fixture.Inner.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(before.ResourceId,
                    before.LifecycleGeneration, before.StateVersion))).Applied);
            else await fixture.ChangeAsync(change);
            expected = await fixture.Inner.FindAsync(new DocumentIdentity.Resource(before.ResourceId, true));
        }
        if (afterEntryRead) fixture.Store.AfterNextPreparationRead = ChangeAsync;
        else fixture.Content.BeforeNextWrite = ChangeAsync;

        AssertRejected(await fixture.ExecuteAsync(request, kind));

        Assert.NotNull(expected);
        Assert.Equal(JsonSerializer.Serialize(expected),
            JsonSerializer.Serialize(await fixture.Inner.FindAsync(new DocumentIdentity.Resource(before.ResourceId, true))));
        Assert.Equal(0, expected.Counters.SaveReceipts);
    }

    [Theory]
    [InlineData(true, DocumentPartitionKind.FileContents, 3)]
    [InlineData(true, DocumentPartitionKind.Metadata, 3)]
    public async Task FileAndMetadataSavesUseOneCoherentPreparationAndOnePublication(
        bool capability, DocumentPartitionKind kind, int expectedFinds)
    {
        var fixture = await SetupAsync(capability);
        await fixture.LeaseAsync(fixture.Store.UtcNow.AddMinutes(1));
        var request = await fixture.RequestAsync(await fixture.CurrentAsync(), kind, "read count");
        fixture.Store.Finds = 0;
        fixture.Store.Transitions = 0;

        var result = await fixture.ExecuteAsync(request, kind);

        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        Assert.Equal(expectedFinds, fixture.Store.Finds);
        Assert.Equal(1, fixture.Store.PreparationReads);
        Assert.Equal(1, fixture.Store.Transitions);
    }

    public static IEnumerable<object[]> ClockCases()
    {
        foreach (bool capability in new[] { true })
        foreach (var kind in new[] { DocumentPartitionKind.FileContents, DocumentPartitionKind.Metadata })
        {
            yield return [capability, kind, false, true];
            yield return [capability, kind, true, false];
            yield return [capability, kind, true, true];
        }
    }

    [Theory]
    [MemberData(nameof(ClockCases))]
    public async Task PreparationAndPublicationUseProviderTimeIncludingAlreadyExpiredLeases(
        bool capability, DocumentPartitionKind kind, bool expired, bool explicitToken)
    {
        var fixture = await SetupAsync(capability);
        // The active lease is in the application's past. The expired lease is in its future.
        fixture.Store.UtcNow = new DateTime(expired ? 2040 : 2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await fixture.LeaseAsync(fixture.Store.UtcNow.AddMinutes(expired ? -1 : 1));
        var before = await fixture.CurrentAsync();
        fixture.Store.Transitions = 0;
        var result = await fixture.ExecuteAsync(await fixture.RequestAsync(before, kind, "clock"), kind, explicitToken);

        if (expired && explicitToken)
        {
            Assert.Equal("InvalidCoauthSession", result.LockError);
            Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await fixture.CurrentAsync()));
        }
        else
        {
            Assert.False(Assert.Single(result.Response.SubResponses).Status);
            Assert.Equal(1, result.State.Counters.SaveReceipts);
            Assert.Equal(fixture.Store.UtcNow, (await fixture.Inner.ReadRevisionAsync(fixture.Id, result.State.LatestRevisionNumber))!.Revision!.CreatedUtc);
            if (expired) Assert.Null((await fixture.Inner.ReadControlAsync(fixture.Id))!.Coordination.Exclusive);
            else Assert.NotNull((await fixture.Inner.ReadControlAsync(fixture.Id))!.Coordination.Exclusive);
        }
        Assert.Equal(1, fixture.Store.PreparationReads);
        Assert.Equal(expired && explicitToken ? 0 : 1, fixture.Store.Transitions);
    }

    [Theory]
    [InlineData(true, DocumentPartitionKind.FileContents)]
    [InlineData(true, DocumentPartitionKind.Metadata)]
    public async Task SaveTakesFreshInitialAuthorityAfterTheOuterExecutionRead(bool capability, DocumentPartitionKind kind)
    {
        var fixture = await SetupAsync(capability);
        await fixture.LeaseAsync(fixture.Store.UtcNow.AddMinutes(1));
        var request = await fixture.RequestAsync(await fixture.CurrentAsync(), kind, "fresh authority");
        fixture.Store.AfterNextFind = () => fixture.ChangeAsync("reacquire");

        var result = await fixture.ExecuteAsync(request, kind);

        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        Assert.Equal(1, result.State.Counters.SaveReceipts);
        Assert.Equal(2, result.State.Counters.Revisions);
        Assert.Equal(3L, (await fixture.Inner.ReadControlAsync(fixture.Id))!.Coordination.Generation);
    }

    [Theory]
    [InlineData(true, DocumentPartitionKind.FileContents, false)]
    [InlineData(true, DocumentPartitionKind.Metadata, false)]
    [InlineData(true, DocumentPartitionKind.FileContents, true)]
    public async Task ExpiryOfAnotherSchemaOwnerCannotChangeDetachedAuthorityDuringPreparation(
        bool capability, DocumentPartitionKind kind, bool afterEntryRead)
    {
        var fixture = await SetupAsync(capability);
        const string schema = "22222222-2222-2222-2222-222222222222";
        await fixture.SchemaAsync(schema, "writer", 120000);
        await fixture.SchemaAsync(schema, "expiring", 3600);
        var before = await fixture.CurrentAsync();
        var leases = (await fixture.Inner.ReadControlAsync(fixture.Id))!.Coordination.SchemaOwners;
        Assert.True(leases.Single(lease => lease.Client == "writer").ExpiresUtc > fixture.Store.UtcNow.AddHours(2));
        Assert.True(leases.Single(lease => lease.Client == "expiring").ExpiresUtc < fixture.Store.UtcNow.AddHours(2));
        Task ExpirePeerAsync()
        {
            fixture.Store.UtcNow = fixture.Store.UtcNow.AddHours(2);
            return Task.CompletedTask;
        }
        if (afterEntryRead) fixture.Store.AfterNextPreparationRead = ExpirePeerAsync;
        else fixture.Content.BeforeNextWrite = ExpirePeerAsync;

        var result = await new CellBridgeDocumentService(new(fixture.Store, fixture.Content)).ExecuteAsync(fixture.Id,
            kind, await fixture.RequestAsync(before, kind, "expired peer"),
            new Dictionary<string, string> { ["SchemaLockID"] = schema, ["ClientID"] = "writer" }, TestActor.Value);

        // The writer's lease remains valid. Only the detached authority comparison rejects this save.
        AssertRejected(result);
        var after = (await fixture.Inner.ExportDocumentAsync(fixture.Id))!;
        var charge = after.ContentReferences.Where(reference => reference.IsPending)
            .Sum(reference => reference.Handle.Length + reference.AdmissionBytes);
        Assert.True(charge > 0);
        Assert.Equal(JsonSerializer.Serialize(before with { Counters = before.Counters with
            { StoredBytes = checked(before.Counters.StoredBytes + charge) } }), JsonSerializer.Serialize(after.Current.Head));
        Assert.Equal(after.Current.Head.Counters.StoredBytes, NormalizedDocumentRecovery.StoredBytes(after));
    }

    [Theory]
    [InlineData(true, DocumentPartitionKind.FileContents, false)]
    [InlineData(true, DocumentPartitionKind.Metadata, false)]
    [InlineData(true, DocumentPartitionKind.FileContents, true)]
    public async Task ReceiptPublishedDuringPreparationReplaysOnceEvenAfterLeaseExpiry(bool capability,
        DocumentPartitionKind kind, bool afterEntryRead)
    {
        var fixture = await SetupAsync(capability);
        await fixture.LeaseAsync(fixture.Store.UtcNow.AddMinutes(1));
        var request = await fixture.RequestAsync(await fixture.CurrentAsync(), kind, "same operation");
        CellExecution? winner = null;
        async Task PublishAsync()
        {
            winner = await fixture.ExecuteWithoutWriteHookAsync(request, kind);
            Assert.False(Assert.Single(winner.Response.SubResponses).Status);
            fixture.Store.UtcNow = fixture.Store.UtcNow.AddHours(2);
        }
        if (afterEntryRead) fixture.Store.AfterNextPreparationRead = PublishAsync;
        else fixture.Content.BeforeNextWrite = PublishAsync;

        var raced = await fixture.ExecuteAsync(request, kind);
        var replayed = await fixture.ExecuteAsync(request, kind);

        Assert.NotNull(winner);
        Assert.False(Assert.Single(raced.Response.SubResponses).Status);
        Assert.False(Assert.Single(replayed.Response.SubResponses).Status);
        Assert.Equal(winner.Response.ToByteArray(), raced.Response.ToByteArray());
        Assert.Equal(winner.Response.ToByteArray(), replayed.Response.ToByteArray());
        var current = await fixture.CurrentAsync();
        Assert.Equal(1, current.Counters.SaveReceipts);
        Assert.Equal(2, current.Counters.Revisions);
        Assert.Equal(winner.State.StateVersion, current.StateVersion);

        // A receipt still verifies the request digest when it bypasses lease validation.
        request.DataElementPackage!.DataElements.First(e => e.Data is { Length: > 0 }).Data![0] ^= 1;
        AssertRejected(await fixture.ExecuteAsync(request, kind));
        Assert.Equal(current.StateVersion, (await fixture.CurrentAsync()).StateVersion);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("supersede")]
    public async Task ReceiptPresentAtEntryStillRequiresAuthoritativeReplayValidation(string change)
    {
        var fixture = await SetupAsync(true);
        await fixture.LeaseAsync(fixture.Store.UtcNow.AddMinutes(1));
        var request = await fixture.RequestAsync(await fixture.CurrentAsync(), DocumentPartitionKind.FileContents, "accepted");
        var accepted = await fixture.ExecuteAsync(request, DocumentPartitionKind.FileContents);
        Assert.False(Assert.Single(accepted.Response.SubResponses).Status);
        DocumentHead? expected = null;
        fixture.Store.AfterNextPreparationRead = async () =>
        {
            if (change == "revoke")
                await fixture.ChangeAsync("revoke");
            else
            {
                var competing = await fixture.RequestAsync(accepted.State, DocumentPartitionKind.FileContents, "newer accepted revision");
                var winner = await fixture.ExecuteWithoutWriteHookAsync(competing, DocumentPartitionKind.FileContents);
                Assert.False(Assert.Single(winner.Response.SubResponses).Status);
            }
            expected = await fixture.CurrentAsync();
        };

        var replay = await fixture.ExecuteAsync(request, DocumentPartitionKind.FileContents);

        AssertRejected(replay);
        Assert.NotNull(expected);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(await fixture.CurrentAsync()));
        Assert.Equal(change == "revoke" ? accepted.State.ContentVersion : accepted.State.ContentVersion + 1,
            expected.ContentVersion);
    }

    [Theory]
    [InlineData(true, DocumentPartitionKind.FileContents)]
    [InlineData(true, DocumentPartitionKind.Metadata)]
    public async Task CompetingContentPublishedAfterPreparationCannotBeOverwritten(bool capability, DocumentPartitionKind kind)
    {
        var fixture = await SetupAsync(capability);
        await fixture.LeaseAsync(fixture.Store.UtcNow.AddMinutes(1));
        var before = await fixture.CurrentAsync();
        var stale = await fixture.RequestAsync(before, kind, "stale content");
        var competing = await fixture.RequestAsync(before, kind, "winning content");
        DocumentHead? winner = null;
        fixture.Content.BeforeNextWrite = async () =>
        {
            var result = await fixture.ExecuteWithoutWriteHookAsync(competing, kind);
            Assert.False(Assert.Single(result.Response.SubResponses).Status);
            winner = result.State;
        };

        AssertRejected(await fixture.ExecuteAsync(stale, kind));

        Assert.NotNull(winner);
        Assert.Equal(JsonSerializer.Serialize(winner), JsonSerializer.Serialize(await fixture.CurrentAsync()));
        Assert.Equal(1, winner.Counters.SaveReceipts);
        Assert.Equal(2, winner.Counters.Revisions);
    }

    private static void AssertRejected(CellExecution result)
    {
        Assert.Empty(result.AcceptedSaves);
        Assert.True(result.LockError is not null || result.Response.Status ||
            result.Response.SubResponses.Count != 0 && result.Response.SubResponses.All(r => r.Status));
    }

    private static async Task<Fixture> SetupAsync(bool capability)
    {
        Assert.True(capability); // Indexed preparation is now mandatory for every provider.
        var rawContent = new InMemoryContentStore(); var clock = new ManualClock();
        var inner = new InMemoryDocumentRepository(rawContent, clock: clock);
        var store = new ControlledState(inner, clock);
        var content = new WriteHook(rawContent);
        var service = new CellBridgeDocumentService(new(store, content));
        var state = (await service.CreateAsync("/optimistic.docx", MinimalDocx.Create(), TestActor.Value))!;
        return new(inner, store, rawContent, content, state.ResourceId);
    }

    private sealed record Fixture(InMemoryDocumentRepository Inner, ControlledState Store, InMemoryContentStore RawContent,
        WriteHook Content, Guid Id)
    {
        public async Task<DocumentHead> CurrentAsync() => (await Inner.FindAsync(new DocumentIdentity.Resource(Id)))!;
        public async Task LeaseAsync(DateTime expires)
        {
            var targetClock = Store.UtcNow;
            if (expires <= targetClock) Store.UtcNow = expires.AddMinutes(-1);
            await OfficeAsync(SubRequestType.ExclusiveLock, "ExclusiveLockRequestType", "GetLock", "client", LockId,
                checked((int)(expires - Store.UtcNow).TotalSeconds));
            Store.UtcNow = targetClock;
        }
        public async Task ChangeAsync(string change)
        {
            var current = await CurrentAsync();
            if (change == "revoke")
            {
                Assert.True((await Inner.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(Id,
                    StorageTestDocuments.Admission(current), current.Security with { Owner = "revoked" }, current.AuthorityGeneration))).Changed);
                return;
            }
            if (change == "refresh")
            {
                await OfficeAsync(SubRequestType.ExclusiveLock, "ExclusiveLockRequestType", "RefreshLock", "client", LockId, 300);
                return;
            }
            await OfficeAsync(SubRequestType.ExclusiveLock, "ExclusiveLockRequestType", "ReleaseLock", "client", LockId);
            if (change == "release") return;
            if (change == "other-owner")
            {
                current = await CurrentAsync();
                var other = new CellBridgeActor(new("other", "other", "Other"));
                Assert.True((await Inner.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(Id,
                    StorageTestDocuments.Admission(current), current.Security with { Grants = current.Security.Grants.SetItem("other", DocumentAccess.Write) }, current.AuthorityGeneration))).Changed);
                await OfficeAsync(SubRequestType.ExclusiveLock, "ExclusiveLockRequestType", "GetLock", "other", LockId, actor: other);
                return;
            }
            await OfficeAsync(SubRequestType.ExclusiveLock, "ExclusiveLockRequestType", "GetLock", "client", LockId);
        }
        public Task SchemaAsync(string schema, string client, int timeout) =>
            OfficeAsync(SubRequestType.SchemaLock, "SchemaLockRequestType", "GetLock", client, schema, timeout);
        private async Task OfficeAsync(SubRequestType type, string operationKey, string operation, string client,
            string token, int timeout = 3600, CellBridgeActor? actor = null)
        {
            var current = await CurrentAsync(); actor ??= TestActor.Value;
            var request = new FssHttpSubRequest { Type = type, SubRequestDataAttributes =
            { [operationKey] = operation, ["ClientID"] = client, ["Timeout"] = timeout.ToString(),
                [type == SubRequestType.SchemaLock ? "SchemaLockID" : "ExclusiveLockID"] = token } };
            var result = await Inner.ExecuteControlAsync(new DocumentControlCommand.Office(Id, StorageTestDocuments.Admission(current, actor), request));
            Assert.Null(result.ErrorCode); Assert.True(result.Changed);
        }
        public async Task<FsshttpbCellRequest> RequestAsync(DocumentHead state, DocumentPartitionKind kind, string text)
        {
            if (kind == DocumentPartitionKind.FileContents)
                return await StorageTestDocuments.SaveRequestAsync(new(new(Inner, RawContent)), state, MinimalDocx.Create(text));
            var indexNamespace = Guid.NewGuid();
            var graph = new GraphFixture(System.Text.Encoding.UTF8.GetBytes(text),
                identity: value => value == 1 ? new(value, indexNamespace) : GraphFixture.Id(value));
            return StorageTestDocuments.MetadataRequest(graph);
        }
        public Task<CellExecution> ExecuteAsync(FsshttpbCellRequest request, DocumentPartitionKind kind, bool explicitToken = true) =>
            new CellBridgeDocumentService(new(Store, Content)).ExecuteAsync(Id, kind, request,
                explicitToken ? new Dictionary<string, string> { ["ExclusiveLockID"] = LockId, ["Timeout"] = "3600" } : [], TestActor.Value).AsTask();
        public Task<CellExecution> ExecuteWithoutWriteHookAsync(FsshttpbCellRequest request, DocumentPartitionKind kind) =>
            new CellBridgeDocumentService(new(Store, RawContent)).ExecuteAsync(Id, kind, request,
                new Dictionary<string, string> { ["ExclusiveLockID"] = LockId, ["Timeout"] = "3600" }, TestActor.Value).AsTask();
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTime UtcNow = new(2040, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public override DateTimeOffset GetUtcNow() => new(UtcNow);
    }
    private sealed class ControlledState(IDocumentRepository inner, ManualClock clock) : RepositoryTestProxy(inner)
    {
        public DateTime UtcNow { get => clock.UtcNow; set => clock.UtcNow = value; }
        public int Transitions;
        public int Finds;
        public int PreparationReads;
        public Func<Task>? AfterNextFind;
        public Func<Task>? AfterNextPreparationRead;
        public override async ValueTask<DocumentHead?> FindAsync(DocumentIdentity identity, CancellationToken ct = default)
        {
            Finds++;
            var state = await Inner.FindAsync(identity, ct);
            if (AfterNextFind is { } hook) { AfterNextFind = null; await hook(); }
            return state;
        }
        public override ValueTask<PublicationResult> PublishAsync(PreparedDocumentPublication publication, CancellationToken ct = default)
        { Transitions++; return Inner.PublishAsync(publication, ct); }
        public override async ValueTask<SavePreparationRead?> PrepareSaveAsync(Guid id, SavePreparationRequest request, CancellationToken ct = default)
        {
            PreparationReads++;
            var view = await Inner.PrepareSaveAsync(id, request, ct);
            if (AfterNextPreparationRead is { } hook) { AfterNextPreparationRead = null; await hook(); }
            return view;
        }
        public override ValueTask<NormalizedDocumentSnapshot?> ExportDocumentAsync(Guid id, CancellationToken ct = default) =>
            throw new InvalidOperationException("A normal save must not export aggregate history.");
    }

    private sealed class WriteHook(IContentStore inner) : IContentStore
    {
        public Func<Task>? BeforeNextWrite;
        public bool Durable => false;
        public bool Shared => false;
        public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
        {
            if (BeforeNextWrite is { } hook) { BeforeNextWrite = null; await hook(); }
            return await inner.WriteAsync(source, cancellationToken);
        }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) => inner.OpenReadAsync(handle, cancellationToken);
    }
}
