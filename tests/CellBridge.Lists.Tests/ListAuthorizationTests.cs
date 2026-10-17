using CellBridge.Lists;

namespace CellBridge.Lists.Tests;

public class ListAuthorizationTests
{
    private const string Domain = "customer-permissions/v1";
    private const string Subject = "user:17";
    private static readonly Guid WorkspaceA = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceB = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid ListA = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid ListB = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly ListActor Actor = new(Subject);

    [Fact]
    public void Snapshot_copies_overrides_and_preserves_exact_access_flags()
    {
        var source = new Dictionary<Guid, ListAccess>
        {
            [ListA] = ListAccess.None,
            [ListB] = ListAccess.Write
        };
        var snapshot = new ListAuthorizationSnapshot(
            Domain,
            Subject,
            WorkspaceA,
            ListWorkspaceAccess.UseLists | ListWorkspaceAccess.CreateLists,
            ListAccess.Read,
            source,
            "revision-3");

        source[ListA] = ListAccess.Design;
        source[Guid.NewGuid()] = ListAccess.Read;

        Assert.Equal(Domain, snapshot.PolicyDomain);
        Assert.Equal(Subject, snapshot.Subject);
        Assert.Equal(WorkspaceA, snapshot.WorkspaceId);
        Assert.Equal(ListWorkspaceAccess.UseLists | ListWorkspaceAccess.CreateLists, snapshot.WorkspaceAccess);
        Assert.Equal(ListAccess.Read, snapshot.DefaultListAccess);
        Assert.Equal("revision-3", snapshot.Revision);
        Assert.Equal(2, snapshot.ListOverrides.Count);
        Assert.Equal(ListAccess.None, snapshot.AccessFor(ListA));
        Assert.Equal(ListAccess.Write, snapshot.AccessFor(ListB));
        Assert.Equal(ListAccess.Read, snapshot.AccessFor(Guid.NewGuid()));

        var denied = new ListAuthorizationSnapshot(
            Domain, Subject, WorkspaceA, ListWorkspaceAccess.None, ListAccess.Design);
        Assert.Equal(ListAccess.None, denied.AccessFor(ListA));
    }

    [Fact]
    public void Snapshot_rejects_invalid_identity_ids_and_flags()
    {
        Assert.Throws<ArgumentException>(() => Snapshot(policyDomain: " "));
        Assert.Throws<ArgumentException>(() => Snapshot(policyDomain: new string('x', 201)));
        Assert.Throws<ArgumentException>(() => Snapshot(subject: "bad\0subject"));
        Assert.Throws<ArgumentException>(() => Snapshot(workspaceId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Snapshot(workspaceAccess: ListWorkspaceAccess.CreateLists));
        Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(workspaceAccess: (ListWorkspaceAccess)16));
        Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(defaultAccess: (ListAccess)8));
        Assert.Throws<ArgumentException>(() => Snapshot(overrides: new Dictionary<Guid, ListAccess>
        {
            [Guid.Empty] = ListAccess.Read
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(overrides: new Dictionary<Guid, ListAccess>
        {
            [ListA] = (ListAccess)8
        }));
        Assert.Throws<ArgumentException>(() => Snapshot(revision: "\ud800"));
        Assert.Throws<ArgumentException>(() => Snapshot().AccessFor(Guid.Empty));
    }

    [Fact]
    public async Task Workspace_writes_overlap_across_workspaces_and_serialize_within_one_workspace()
    {
        var provider = Provider();
        var firstA = await provider.AcquireWriteAsync(WorkspaceA);

        var secondATask = provider.AcquireWriteAsync(WorkspaceA).AsTask();
        Assert.False(secondATask.IsCompleted);

        var firstBTask = provider.AcquireWriteAsync(WorkspaceB).AsTask();
        var firstB = await firstBTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(secondATask.IsCompleted);

        await firstA.DisposeAsync();
        var secondA = await secondATask.WaitAsync(TimeSpan.FromSeconds(5));
        await secondA.DisposeAsync();
        await secondA.DisposeAsync();
        await firstB.DisposeAsync();
    }

    [Fact]
    public async Task Waiting_workspace_writer_blocks_later_readers()
    {
        var provider = Provider();
        var firstReader = await provider.AcquireReadAsync(Actor, WorkspaceA);

        var writerTask = provider.AcquireWriteAsync(WorkspaceA).AsTask();
        Assert.False(writerTask.IsCompleted);
        var laterReaderTask = provider.AcquireReadAsync(Actor, WorkspaceA).AsTask();
        Assert.False(laterReaderTask.IsCompleted);

        await firstReader.DisposeAsync();
        var writer = await writerTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(laterReaderTask.IsCompleted);

        await writer.DisposeAsync();
        var laterReader = await laterReaderTask.WaitAsync(TimeSpan.FromSeconds(5));
        await laterReader.DisposeAsync();
    }

    [Fact]
    public async Task Global_writer_waits_for_active_work_and_blocks_new_workspace_work()
    {
        var provider = Provider();
        var active = await provider.AcquireWriteAsync(WorkspaceA);

        var globalTask = provider.AcquireGlobalWriteAsync().AsTask();
        Assert.False(globalTask.IsCompleted);
        var laterWorkspaceTask = provider.AcquireWriteAsync(WorkspaceB).AsTask();
        Assert.False(laterWorkspaceTask.IsCompleted);

        await active.DisposeAsync();
        var global = await globalTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(laterWorkspaceTask.IsCompleted);

        await global.DisposeAsync();
        var laterWorkspace = await laterWorkspaceTask.WaitAsync(TimeSpan.FromSeconds(5));
        await laterWorkspace.DisposeAsync();
    }

    [Fact]
    public async Task Canceled_waiters_leave_no_guard_and_canceling_after_return_does_not_release_a_guard()
    {
        var provider = Provider();
        var heldWriter = await provider.AcquireWriteAsync(WorkspaceA);
        using var waitingCancellation = new CancellationTokenSource();
        var canceledReadTask = provider.AcquireReadAsync(Actor, WorkspaceA, waitingCancellation.Token).AsTask();
        Assert.False(canceledReadTask.IsCompleted);

        waitingCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledReadTask);
        await heldWriter.DisposeAsync();

        using var acquiredCancellation = new CancellationTokenSource();
        var read = await provider.AcquireReadAsync(Actor, WorkspaceA, acquiredCancellation.Token);
        acquiredCancellation.Cancel();
        var nextWriterTask = provider.AcquireWriteAsync(WorkspaceA).AsTask();
        Assert.False(nextWriterTask.IsCompleted);

        await read.DisposeAsync();
        var nextWriter = await nextWriterTask.WaitAsync(TimeSpan.FromSeconds(5));
        await nextWriter.DisposeAsync();
    }

    [Fact]
    public async Task Loader_failures_and_identity_mismatches_fail_closed_and_release_guards()
    {
        var wrongDomain = Snapshot(policyDomain: "other-domain");
        var wrongSubject = Snapshot(subject: "user:18");
        var wrongWorkspace = Snapshot(workspaceId: WorkspaceB);
        Func<ListActor, Guid, CancellationToken, ValueTask<ListAuthorizationSnapshot?>>[] loaders =
        [
            static (_, _, _) => ValueTask.FromResult<ListAuthorizationSnapshot?>(null),
            static (_, _, _) => ValueTask.FromException<ListAuthorizationSnapshot?>(new IOException("private backend detail")),
            (_, _, _) => ValueTask.FromResult<ListAuthorizationSnapshot?>(wrongDomain),
            (_, _, _) => ValueTask.FromResult<ListAuthorizationSnapshot?>(wrongSubject),
            (_, _, _) => ValueTask.FromResult<ListAuthorizationSnapshot?>(wrongWorkspace)
        ];

        foreach (var loader in loaders)
        {
            var provider = new SingleProcessListAuthorizationProvider(Domain, loader);
            var failure = await Assert.ThrowsAsync<ListException>(() =>
                provider.AcquireReadAsync(Actor, WorkspaceA).AsTask());
            Assert.Equal(ListFailure.Unavailable, failure.Failure);
            Assert.DoesNotContain("private backend detail", failure.Message, StringComparison.Ordinal);

            var write = await provider.AcquireWriteAsync(WorkspaceA).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await write.DisposeAsync();
        }
    }

    [Fact]
    public async Task Failed_authorization_churn_reclaims_workspace_gates()
    {
        var provider = new SingleProcessListAuthorizationProvider(
            Domain,
            static (_, _, _) => ValueTask.FromResult<ListAuthorizationSnapshot?>(null));

        for (var i = 0; i < 200; i++)
        {
            var workspaceId = Guid.NewGuid();
            var failure = await Assert.ThrowsAsync<ListException>(() =>
                provider.AcquireReadAsync(Actor, workspaceId).AsTask());
            Assert.Equal(ListFailure.Unavailable, failure.Failure);
        }

        Assert.Equal(0, WorkspaceRegistryCount(provider));
    }

    [Fact]
    public async Task Workspace_gate_is_not_replaced_while_a_waiter_or_holder_references_it()
    {
        var loaderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new SingleProcessListAuthorizationProvider(Domain, async (_, workspaceId, ct) =>
        {
            loaderEntered.TrySetResult();
            await releaseLoader.Task.WaitAsync(ct);
            return Snapshot(workspaceId: workspaceId);
        });
        var firstWriter = await provider.AcquireWriteAsync(WorkspaceA);

        var readerTask = provider.AcquireReadAsync(Actor, WorkspaceA).AsTask();
        Assert.False(readerTask.IsCompleted);
        await firstWriter.DisposeAsync();
        await loaderEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondWriterTask = provider.AcquireWriteAsync(WorkspaceA).AsTask();
        Assert.False(secondWriterTask.IsCompleted);
        releaseLoader.TrySetResult();
        var reader = await readerTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(secondWriterTask.IsCompleted);

        await reader.DisposeAsync();
        var secondWriter = await secondWriterTask.WaitAsync(TimeSpan.FromSeconds(5));
        await secondWriter.DisposeAsync();
        Assert.Equal(0, WorkspaceRegistryCount(provider));
    }

    [Fact]
    public async Task A_valid_deny_snapshot_is_returned_as_authoritative_state()
    {
        var deny = Snapshot(workspaceAccess: ListWorkspaceAccess.None, defaultAccess: ListAccess.Design);
        var provider = new SingleProcessListAuthorizationProvider(
            Domain,
            (_, _, _) => ValueTask.FromResult<ListAuthorizationSnapshot?>(deny));

        await using var lease = await provider.AcquireReadAsync(Actor, WorkspaceA);
        Assert.Same(deny, lease.Snapshot);
        Assert.Equal(ListAccess.None, lease.Snapshot.AccessFor(ListA));
    }

    private static SingleProcessListAuthorizationProvider Provider() =>
        new(Domain, (_, workspaceId, _) => ValueTask.FromResult<ListAuthorizationSnapshot?>(Snapshot(workspaceId: workspaceId)));

    private static int WorkspaceRegistryCount(SingleProcessListAuthorizationProvider provider)
    {
        var field = typeof(SingleProcessListAuthorizationProvider).GetField(
            "_workspaces",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return Assert.IsAssignableFrom<System.Collections.IDictionary>(field!.GetValue(provider)).Count;
    }

    private static ListAuthorizationSnapshot Snapshot(
        string policyDomain = Domain,
        string subject = Subject,
        Guid? workspaceId = null,
        ListWorkspaceAccess workspaceAccess = ListWorkspaceAccess.UseLists,
        ListAccess defaultAccess = ListAccess.Read,
        IReadOnlyDictionary<Guid, ListAccess>? overrides = null,
        string? revision = null) =>
        new(policyDomain, subject, workspaceId ?? WorkspaceA, workspaceAccess, defaultAccess, overrides, revision);
}
