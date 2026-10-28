using System.Text.Json;
using CellBridge.AspNetCore;
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
        foreach (bool capability in new[] { false, true })
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
        var request = Request(before, kind, "stale");
        DocumentState? expected = null;
        async Task ChangeAsync()
        {
            if (change == "expire") fixture.Store.UtcNow = fixture.Store.UtcNow.AddMinutes(2);
            else if (change == "delete")
                Assert.True(await fixture.Inner.TryDeleteAsync(before.ResourceId, before.LifecycleGeneration, before.StateVersion));
            else
                await fixture.Store.TransitionAsync(before.ResourceId, (current, now) =>
                {
                    if (change == "revoke")
                        return new StateTransition<bool>(DocumentPermissionUpdates.Apply(current, now,
                            current.Security with { Owner = "revoked" }), true);
                    var coordination = current.Coordination;
                    var next = change switch
                    {
                        "refresh" => coordination with { Exclusive = coordination.Exclusive! with { ExpiresUtc = now.AddMinutes(5) } },
                        "release" => coordination with { Exclusive = null },
                        "other-owner" => coordination with { Exclusive = coordination.Exclusive! with { OwnerSubject = "other" } },
                        // Releasing and reacquiring the same token creates a new authority epoch.
                        _ => coordination with { Generation = coordination.Generation + 2 },
                    };
                    return new StateTransition<bool>(current with
                    { Coordination = change == "reacquire" ? next : CoordinationFencing.Capture(coordination, next) }, true);
                });
            expected = await fixture.Inner.FindLifecycleAsync(before.ResourceId);
        }
        if (afterEntryRead) fixture.Store.AfterNextPreparationRead = ChangeAsync;
        else fixture.Content.BeforeNextWrite = ChangeAsync;

        if (change == "delete")
            await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.ExecuteAsync(request, kind));
        else
            AssertRejected(await fixture.ExecuteAsync(request, kind));

        Assert.NotNull(expected);
        Assert.Equal(JsonSerializer.Serialize(expected),
            JsonSerializer.Serialize(await fixture.Inner.FindLifecycleAsync(before.ResourceId)));
        Assert.Empty(expected.Receipts);
    }

    [Theory]
    [InlineData(false, DocumentPartitionKind.FileContents, 2)]
    [InlineData(true, DocumentPartitionKind.FileContents, 1)]
    [InlineData(false, DocumentPartitionKind.Metadata, 3)]
    [InlineData(true, DocumentPartitionKind.Metadata, 3)]
    public async Task CapableFileSaveUsesOneCoherentEntryReadAndKeepsOtherReadPaths(
        bool capability, DocumentPartitionKind kind, int expectedFinds)
    {
        var fixture = await SetupAsync(capability);
        await fixture.LeaseAsync(fixture.Store.UtcNow.AddMinutes(1));
        var request = Request(await fixture.CurrentAsync(), kind, "read count");
        fixture.Store.Finds = 0;
        fixture.Store.Transitions = 0;

        var result = await fixture.ExecuteAsync(request, kind);

        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        Assert.Equal(expectedFinds, fixture.Store.Finds);
        Assert.Equal(capability ? 1 : 0, fixture.Store.PreparationReads);
        Assert.Equal(capability ? 1 : 2, fixture.Store.Transitions);
    }

    public static IEnumerable<object[]> ClockCases()
    {
        foreach (bool capability in new[] { false, true })
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
        var result = await fixture.ExecuteAsync(Request(before, kind, "clock"), kind, explicitToken);

        if (expired && explicitToken)
        {
            Assert.Equal("InvalidCoauthSession", result.LockError);
            Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await fixture.CurrentAsync()));
        }
        else
        {
            Assert.False(Assert.Single(result.Response.SubResponses).Status);
            Assert.Single(result.State.Receipts);
            Assert.Equal(fixture.Store.UtcNow, result.State.Revisions[^1].CreatedUtc);
            if (expired) Assert.Null(result.State.Coordination.Exclusive);
            else Assert.NotNull(result.State.Coordination.Exclusive);
        }
        Assert.Equal(capability ? 1 : 0, fixture.Store.PreparationReads);
        Assert.Equal((expired && explicitToken ? 0 : 1) + (capability ? 0 : 1), fixture.Store.Transitions);
    }

    [Theory]
    [InlineData(false, DocumentPartitionKind.FileContents)]
    [InlineData(true, DocumentPartitionKind.FileContents)]
    [InlineData(false, DocumentPartitionKind.Metadata)]
    [InlineData(true, DocumentPartitionKind.Metadata)]
    public async Task SaveTakesFreshInitialAuthorityAfterTheOuterExecutionRead(bool capability, DocumentPartitionKind kind)
    {
        var fixture = await SetupAsync(capability);
        await fixture.LeaseAsync(fixture.Store.UtcNow.AddMinutes(1));
        var request = Request(await fixture.CurrentAsync(), kind, "fresh authority");
        fixture.Store.AfterNextFind = async () =>
            await fixture.Store.TransitionAsync(fixture.Id, (current, _) => new StateTransition<bool>(current with
            { Coordination = current.Coordination with { Generation = current.Coordination.Generation + 2 } }, true));

        var result = await fixture.ExecuteAsync(request, kind);

        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        Assert.Single(result.State.Receipts);
        Assert.Equal(2, result.State.Revisions.Length);
        Assert.Equal(2L, result.State.Coordination.Generation);
    }

    [Theory]
    [InlineData(false, DocumentPartitionKind.FileContents, false)]
    [InlineData(true, DocumentPartitionKind.FileContents, false)]
    [InlineData(false, DocumentPartitionKind.Metadata, false)]
    [InlineData(true, DocumentPartitionKind.Metadata, false)]
    [InlineData(true, DocumentPartitionKind.FileContents, true)]
    public async Task ExpiryOfAnotherSchemaOwnerCannotChangeDetachedAuthorityDuringPreparation(
        bool capability, DocumentPartitionKind kind, bool afterEntryRead)
    {
        var fixture = await SetupAsync(capability);
        const string schema = "22222222-2222-2222-2222-222222222222";
        await fixture.Store.TransitionAsync(fixture.Id, (current, now) => new StateTransition<bool>(current with
        {
            Coordination = current.Coordination with
            {
                SchemaId = schema,
                SchemaOwners =
                [
                    new(schema, "writer", now.AddMinutes(10), 0, schema, TestActor.Value.Identity.Subject),
                    new(schema, "expiring", now.AddMinutes(1), 0, schema, TestActor.Value.Identity.Subject),
                ],
            },
        }, true));
        var before = await fixture.CurrentAsync();
        Task ExpirePeerAsync()
        {
            fixture.Store.UtcNow = fixture.Store.UtcNow.AddMinutes(2);
            return Task.CompletedTask;
        }
        if (afterEntryRead) fixture.Store.AfterNextPreparationRead = ExpirePeerAsync;
        else fixture.Content.BeforeNextWrite = ExpirePeerAsync;

        var result = await new CellBridgeDocumentService(new(fixture.Store, fixture.Content)).ExecuteAsync(fixture.Id,
            kind, Request(before, kind, "expired peer"),
            new Dictionary<string, string> { ["SchemaLockID"] = schema, ["ClientID"] = "writer" }, TestActor.Value);

        // The writer's lease remains valid. Only the detached authority comparison rejects this save.
        AssertRejected(result);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await fixture.CurrentAsync()));
    }

    [Theory]
    [InlineData(false, DocumentPartitionKind.FileContents, false)]
    [InlineData(true, DocumentPartitionKind.FileContents, false)]
    [InlineData(false, DocumentPartitionKind.Metadata, false)]
    [InlineData(true, DocumentPartitionKind.Metadata, false)]
    [InlineData(true, DocumentPartitionKind.FileContents, true)]
    public async Task ReceiptPublishedDuringPreparationReplaysOnceEvenAfterLeaseExpiry(bool capability,
        DocumentPartitionKind kind, bool afterEntryRead)
    {
        var fixture = await SetupAsync(capability);
        await fixture.LeaseAsync(fixture.Store.UtcNow.AddMinutes(1));
        var request = Request(await fixture.CurrentAsync(), kind, "same operation");
        CellExecution? winner = null;
        async Task PublishAsync()
        {
            winner = await fixture.ExecuteWithoutWriteHookAsync(request, kind);
            Assert.False(Assert.Single(winner.Response.SubResponses).Status);
            fixture.Store.UtcNow = fixture.Store.UtcNow.AddMinutes(2);
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
        Assert.Single(current.Receipts);
        Assert.Equal(2, current.Revisions.Length);
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
        var request = Request(await fixture.CurrentAsync(), DocumentPartitionKind.FileContents, "accepted");
        var accepted = await fixture.ExecuteAsync(request, DocumentPartitionKind.FileContents);
        Assert.False(Assert.Single(accepted.Response.SubResponses).Status);
        DocumentState? expected = null;
        fixture.Store.AfterNextPreparationRead = async () =>
        {
            if (change == "revoke")
                await fixture.Store.TransitionAsync(fixture.Id, (current, now) => new StateTransition<bool>(
                    DocumentPermissionUpdates.Apply(current, now, current.Security with { Owner = "revoked" }), true));
            else
            {
                var competing = Request(accepted.State, DocumentPartitionKind.FileContents, "newer accepted revision");
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
    [InlineData(false, DocumentPartitionKind.FileContents)]
    [InlineData(true, DocumentPartitionKind.FileContents)]
    [InlineData(false, DocumentPartitionKind.Metadata)]
    [InlineData(true, DocumentPartitionKind.Metadata)]
    public async Task CompetingContentPublishedAfterPreparationCannotBeOverwritten(bool capability, DocumentPartitionKind kind)
    {
        var fixture = await SetupAsync(capability);
        await fixture.LeaseAsync(fixture.Store.UtcNow.AddMinutes(1));
        var before = await fixture.CurrentAsync();
        var stale = Request(before, kind, "stale content");
        var competing = Request(before, kind, "winning content");
        DocumentState? winner = null;
        fixture.Content.BeforeNextWrite = async () =>
        {
            var result = await fixture.ExecuteWithoutWriteHookAsync(competing, kind);
            Assert.False(Assert.Single(result.Response.SubResponses).Status);
            winner = result.State;
        };

        AssertRejected(await fixture.ExecuteAsync(stale, kind));

        Assert.NotNull(winner);
        Assert.Equal(JsonSerializer.Serialize(winner), JsonSerializer.Serialize(await fixture.CurrentAsync()));
        Assert.Single(winner.Receipts);
        Assert.Equal(2, winner.Revisions.Length);
    }

    private static FsshttpbCellRequest Request(DocumentState state, DocumentPartitionKind kind, string text)
    {
        if (kind == DocumentPartitionKind.FileContents)
            return RevisionHistoryTests.Request(state, MinimalDocx.Create(text));
        var indexNamespace = Guid.NewGuid();
        var graph = new GraphFixture(System.Text.Encoding.UTF8.GetBytes(text),
            identity: value => value == 1 ? new(value, indexNamespace) : GraphFixture.Id(value));
        return new()
        {
            DataElementPackage = new() { DataElements = graph.Elements.ToList() },
            SubRequests = { new(RequestTypes.PutChanges)
            {
                RequestId = 1,
                Data = new PutChangesSubRequestData { StorageIndex = graph.Index },
            } },
        };
    }

    private static void AssertRejected(CellExecution result)
    {
        Assert.Empty(result.AcceptedSaves);
        Assert.True(result.LockError is not null || result.Response.Status ||
            result.Response.SubResponses.Count != 0 && result.Response.SubResponses.All(r => r.Status));
    }

    private static async Task<Fixture> SetupAsync(bool capability)
    {
        var inner = new InMemoryStateStore();
        ControlledState store = capability ? new PreparationState(inner) : new ControlledState(inner);
        var rawContent = new InMemoryContentStore();
        var content = new WriteHook(rawContent);
        var service = new CellBridgeDocumentService(new(store, content));
        var state = (await service.CreateAsync("/optimistic.docx", MinimalDocx.Create(), TestActor.Value))!;
        return new(inner, store, rawContent, content, state.ResourceId);
    }

    private sealed record Fixture(InMemoryStateStore Inner, ControlledState Store, InMemoryContentStore RawContent,
        WriteHook Content, Guid Id)
    {
        public async Task<DocumentState> CurrentAsync() => (await Inner.FindByResourceIdAsync(Id))!;
        public ValueTask<bool> LeaseAsync(DateTime expires) => Store.TransitionAsync(Id, (state, _) =>
            new StateTransition<bool>(state with { Coordination = state.Coordination with
            { Exclusive = new(LockId, null, expires, 0, null, TestActor.Value.Identity.Subject) } }, true));
        public Task<CellExecution> ExecuteAsync(FsshttpbCellRequest request, DocumentPartitionKind kind, bool explicitToken = true) =>
            new CellBridgeDocumentService(new(Store, Content)).ExecuteAsync(Id, kind, request,
                explicitToken ? new Dictionary<string, string> { ["ExclusiveLockID"] = LockId, ["Timeout"] = "3600" } : [], TestActor.Value).AsTask();
        public Task<CellExecution> ExecuteWithoutWriteHookAsync(FsshttpbCellRequest request, DocumentPartitionKind kind) =>
            new CellBridgeDocumentService(new(Store, RawContent)).ExecuteAsync(Id, kind, request,
                new Dictionary<string, string> { ["ExclusiveLockID"] = LockId, ["Timeout"] = "3600" }, TestActor.Value).AsTask();
    }

    private class ControlledState(InMemoryStateStore inner) : IDocumentStateStore
    {
        public DateTime UtcNow = new(2040, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public int Transitions;
        public int Finds;
        public int PreparationReads;
        public Func<Task>? AfterNextFind;
        public Func<Task>? AfterNextPreparationRead;
        public bool Durable => false;
        public bool Shared => false;
        protected InMemoryStateStore Inner => inner;
        public async ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken ct = default)
        {
            Finds++;
            var state = await inner.FindByResourceIdAsync(id, ct);
            if (AfterNextFind is { } hook) { AfterNextFind = null; await hook(); }
            return state;
        }
        public ValueTask<DocumentState?> FindByPathKeyAsync(string key, CancellationToken ct = default) => inner.FindByPathKeyAsync(key, ct);
        public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken ct = default) => inner.ListAsync(offset, limit, ct);
        public ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken ct = default) => inner.TryCreateAsync(state, ct);
        public ValueTask CheckHealthAsync(CancellationToken ct = default) => inner.CheckHealthAsync(ct);
        public ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> callback, CancellationToken ct = default)
        {
            Transitions++;
            return inner.TransitionAsync(id, (state, _) => callback(state, UtcNow), ct);
        }
    }

    private sealed class PreparationState(InMemoryStateStore inner) : ControlledState(inner), IDocumentPreparationStore
    {
        public async ValueTask<DocumentPreparationRead?> ReadPreparationAsync(Guid resourceId, CancellationToken cancellationToken = default)
        {
            PreparationReads++;
            var state = await Inner.FindByResourceIdAsync(resourceId, cancellationToken);
            var view = state is null ? null : new DocumentPreparationRead(state, UtcNow);
            if (AfterNextPreparationRead is { } hook) { AfterNextPreparationRead = null; await hook(); }
            return view;
        }
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
