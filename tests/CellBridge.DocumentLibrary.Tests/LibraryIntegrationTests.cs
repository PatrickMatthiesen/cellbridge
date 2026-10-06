using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.DocumentLibrary.Tests;

public sealed class LibraryIntegrationTests
{
    [Fact]
    public async Task ConcurrentPermissionChangesPreserveEarlierRevocation()
    {
        using var files = new TemporaryDirectory();
        var faults = new PauseFirstPermissionPreparationFaults();
        await using var harness = new DocumentLibraryHarness(files.Path, destinationFaults: faults);
        var state = await harness.UploadAsync(TestOfficeFile.Docx("baseline"));

        var revokeEditor = harness.Library.SetPermissionAsync(state.ResourceId,
            DocumentLibraryService.EditorSubject, DocumentAccess.None);
        Task<long>? revokeReader = null;
        try
        {
            await faults.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            revokeReader = harness.Library.SetPermissionAsync(state.ResourceId,
                DocumentLibraryService.ReaderSubject, DocumentAccess.None);
            Assert.False(revokeReader.IsCompleted);
        }
        finally
        {
            faults.Release();
        }
        Assert.NotNull(revokeReader);
        await Task.WhenAll(revokeEditor, revokeReader);

        var manifest = await harness.Destination.GetAsync(state.ResourceId);
        var decisions = manifest.PermissionSnapshots[manifest.DesiredPermissionRevision].Decisions;
        Assert.Equal(DocumentAccess.None, decisions[DocumentLibraryService.EditorSubject]);
        Assert.Equal(DocumentAccess.None, decisions[DocumentLibraryService.ReaderSubject]);
        var committed = (await harness.Provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal(manifest.DesiredPermissionRevision, committed.Security.AuthorizationPolicy!.Revision);
    }

    [Fact]
    public async Task CellBridgeSaveChangesImmutableDestinationFileAndHistory()
    {
        using var files = new TemporaryDirectory();
        await using var harness = new DocumentLibraryHarness(files.Path);
        var baseline = TestOfficeFile.Docx("baseline");
        var changed = TestOfficeFile.Docx("saved through CellBridge");
        var state = await harness.UploadAsync(baseline);
        var request = DocumentLibraryHarness.SaveRequest(state, changed);

        var committed = await harness.Documents.ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(),
            DocumentLibraryHarness.Owner);

        Assert.False(Assert.Single(committed.Response.SubResponses).Status);
        Assert.Single(committed.AcceptedSaves);
        Assert.Single(committed.State.Publication!.Pending);
        Assert.Equal(baseline, await harness.Destination.ReadCurrentBytesAsync(state.ResourceId));

        Assert.Equal(1, await harness.Worker.PublishPendingOnceAsync());
        Assert.Equal(changed, await harness.Destination.ReadCurrentBytesAsync(state.ResourceId));
        var delivered = (await harness.Provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Empty(delivered.Publication!.Pending);
        Assert.Equal(2, (await harness.Library.HistoryAsync(state.ResourceId,
            DocumentLibraryHarness.Owner)).Count);
    }

    [Fact]
    public async Task PermissionDenialRevocationAndPreparedWriteFencingUseCellBridgeAuthority()
    {
        using var files = new TemporaryDirectory();
        await using var harness = new DocumentLibraryHarness(files.Path);
        var state = await harness.UploadAsync(TestOfficeFile.Docx("baseline"));
        var request = DocumentLibraryHarness.SaveRequest(state, TestOfficeFile.Docx("denied"));
        var readerDenied = await harness.Documents.ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(),
            DocumentLibraryHarness.Reader);
        Assert.True(Assert.Single(readerDenied.Response.SubResponses).Status);
        Assert.Empty(readerDenied.AcceptedSaves);

        var locks = new SharedDocumentLocks(harness.Documents);
        var acquired = await locks.ApplyAsync(state.ResourceId, HostLockOperation.Acquire,
            "editor-lock", DocumentLibraryHarness.Editor);
        Assert.True(acquired.Success);
        var beforeRevocation = (await harness.Provider.State.FindByResourceIdAsync(state.ResourceId))!;

        await harness.Library.SetPermissionAsync(state.ResourceId,
            DocumentLibraryService.EditorSubject, DocumentAccess.None);
        var committed = await locks.TryCommitAsync(acquired.WriteToken!, beforeRevocation.StateVersion,
            DocumentLibraryHarness.Editor, (current, _) => current, Guid.NewGuid());
        Assert.False(committed);

        var editorDenied = await harness.Documents.ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(),
            DocumentLibraryHarness.Editor);
        Assert.True(Assert.Single(editorDenied.Response.SubResponses).Status);
        Assert.Empty((await harness.Provider.State.FindByResourceIdAsync(state.ResourceId))!.Publication!.Pending);
    }

    [Fact]
    public async Task HostLockConflictsWithCompetingHostAndFsshttpSave()
    {
        using var files = new TemporaryDirectory();
        await using var harness = new DocumentLibraryHarness(files.Path);
        var baseline = TestOfficeFile.Docx("baseline");
        var state = await harness.UploadAsync(baseline);
        var locks = new SharedDocumentLocks(harness.Documents);
        var acquired = await locks.ApplyAsync(state.ResourceId, HostLockOperation.Acquire,
            "OwnerToken", DocumentLibraryHarness.Owner);
        Assert.True(acquired.Success);
        var competing = await locks.ApplyAsync(state.ResourceId, HostLockOperation.Acquire,
            "EditorToken", DocumentLibraryHarness.Editor);
        Assert.False(competing.Success);
        Assert.Equal("OwnerToken", competing.CurrentLock);

        var request = DocumentLibraryHarness.SaveRequest(
            (await harness.Provider.State.FindByResourceIdAsync(state.ResourceId))!,
            TestOfficeFile.Docx("blocked"));
        var blocked = await harness.Documents.ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(),
            DocumentLibraryHarness.Editor);
        Assert.NotNull(blocked.LockError);
        Assert.Empty(blocked.Response.SubResponses);
        Assert.Empty(blocked.AcceptedSaves);
        Assert.Equal(baseline, await harness.Destination.ReadCurrentBytesAsync(state.ResourceId));
    }

    private sealed class PauseFirstPermissionPreparationFaults : IDestinationFaults
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task Entered => _entered.Task;

        public async ValueTask BeforePermissionPreparationAsync(Guid resourceId,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) != 1) return;
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        public ValueTask AfterManifestReplacementAsync(LibraryManifest manifest,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void Release() => _release.TrySetResult();
    }
}
