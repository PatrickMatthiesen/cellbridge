using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.DocumentLibrary;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.DocumentLibrary.Tests;

public sealed class AuthorizationMigrationTests
{
    [Fact]
    public async Task VersionedDocumentsRequireExplicitMigrationAndKeepHistoryAndDeliveryReceipts()
    {
        using var files = new TemporaryDirectory();
        await using var harness = new DocumentLibraryHarness(files.Path);
        var baseline = TestOfficeFile.Docx("legacy baseline");
        var changed = TestOfficeFile.Docx("legacy saved revision");
        var manifest = await harness.Destination.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), "legacy.docx",
            "/shared/legacy.docx", new MemoryStream(baseline),
            ImmutableDictionary<string, DocumentAccess>.Empty.Add(DocumentLibraryService.OwnerSubject, DocumentAccess.Write));
        var oldPolicy = new DocumentLibraryPermissionPolicy(harness.Destination);
        oldPolicy.Install(manifest);
        var oldService = new CellBridgeDocumentService(harness.Provider, authorizationPolicy: oldPolicy);
        var imported = (await oldService.ImportAsync(manifest.ResourceId, manifest.CellBridgePath, baseline,
            DocumentLibraryHarness.Owner.Identity, DocumentLibraryHarness.Owner))!;
        Assert.True(await harness.Publisher.BindAsync(imported.ResourceId, imported.LifecycleGeneration, imported.StateVersion,
            manifest.BindingId, manifest.DestinationId, manifest.CurrentRevision));
        await harness.Destination.MarkCellBridgeBoundAsync(imported.ResourceId);
        imported = (await harness.Provider.State.FindByResourceIdAsync(imported.ResourceId))!;
        var saved = await oldService.ExecuteAsync(imported.ResourceId, DocumentPartitionKind.FileContents,
            DocumentLibraryHarness.SaveRequest(imported, changed), new Dictionary<string, string>(), DocumentLibraryHarness.Owner);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        Assert.Equal(1, await harness.Worker.PublishPendingOnceAsync());
        var locks = new SharedDocumentLocks(oldService);
        Assert.True((await locks.ApplyAsync(imported.ResourceId, HostLockOperation.Acquire, "old-lock", DocumentLibraryHarness.Owner)).Success);
        var before = (await harness.Provider.State.FindByResourceIdAsync(imported.ResourceId))!;
        var beforeManifest = await harness.Destination.GetAsync(imported.ResourceId);
        var revisions = await oldService.ListRevisionsAsync(imported.ResourceId, DocumentLibraryHarness.Owner);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Library.InitializeAsync());
        Assert.Contains("MigrateVersionedAuthorization=true", refused.Message);
        Assert.Equal(before, await harness.Provider.State.FindByResourceIdAsync(imported.ResourceId));
        harness.Options.MigrateVersionedAuthorization = true;
        await harness.Library.InitializeAsync();
        var after = (await harness.Provider.State.FindByResourceIdAsync(imported.ResourceId))!;
        Assert.Equal(2, after.Security.AuthorizationPolicy!.ContractVersion);
        Assert.Equal(DocumentLibraryPermissionProvider.Scope.TenantId, after.Security.AuthorizationPolicy.TenantId);
        Assert.Equal(DocumentLibraryPermissionProvider.Scope.WorkspaceId, after.Security.AuthorizationPolicy.WorkspaceId);
        Assert.Equal(before.Content, after.Content);
        Assert.Equal(before.ContentVersion, after.ContentVersion);
        Assert.Equal(before.LifecycleGeneration, after.LifecycleGeneration);
        Assert.Null(after.Coordination.HostLock);
        Assert.True(after.Coordination.Generation > before.Coordination.Generation);
        Assert.Equal(revisions, await harness.Documents.ListRevisionsAsync(imported.ResourceId, DocumentLibraryHarness.Owner));
        Assert.Equal(changed, await harness.Destination.ReadCurrentBytesAsync(imported.ResourceId));
        var afterManifest = await harness.Destination.GetAsync(imported.ResourceId);
        Assert.Equal(beforeManifest.CurrentRevision, afterManifest.CurrentRevision);
        Assert.Equal(beforeManifest.Receipts, afterManifest.Receipts);
        Assert.Equal(beforeManifest.PermissionSnapshots.Keys, afterManifest.PermissionSnapshots.Keys);
        foreach (var snapshot in beforeManifest.PermissionSnapshots.Values)
        {
            var retained = afterManifest.PermissionSnapshots[snapshot.Revision];
            Assert.Equal(snapshot.Revision, retained.Revision);
            Assert.Equal(snapshot.Decisions.OrderBy(x => x.Key), retained.Decisions.OrderBy(x => x.Key));
        }

        // Subsequent ordinary permission changes neither publish v1 snapshots nor migrate again.
        await harness.Library.SetPermissionAsync(imported.ResourceId, DocumentLibraryService.ReaderSubject, DocumentAccess.Read);
        await harness.Library.SetPermissionAsync(imported.ResourceId, DocumentLibraryService.ReaderSubject, DocumentAccess.None);
        await harness.Library.InitializeAsync();
        var current = (await harness.Provider.State.FindByResourceIdAsync(imported.ResourceId))!;
        Assert.Equal(after.Security.AuthorizationPolicy, current.Security.AuthorizationPolicy);
        Assert.Single((await harness.Destination.GetAsync(imported.ResourceId)).PermissionSnapshots);
    }

    [Fact]
    public async Task RecoveryCannotImportAnInteractiveUploadWhoseCreateCommitIsMissing()
    {
        using var files = new TemporaryDirectory();
        await using var harness = new DocumentLibraryHarness(files.Path);
        var manifest = await harness.Destination.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), "pending.docx",
            "/shared/pending.docx", new MemoryStream(TestOfficeFile.Docx("pending")),
            ImmutableDictionary<string, DocumentAccess>.Empty.Add(DocumentLibraryService.OwnerSubject, DocumentAccess.Write),
            pendingCreation: true);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Library.InitializeAsync());
        Assert.Contains("cannot be imported automatically", refused.Message);
        Assert.Null(await harness.Provider.State.FindByResourceIdAsync(manifest.ResourceId));
    }

    [Fact]
    public async Task RecoveryCompletesPublicationBindingAfterAuthorizedCreate()
    {
        using var files = new TemporaryDirectory();
        await using var harness = new DocumentLibraryHarness(files.Path);
        var bytes = TestOfficeFile.Docx("accepted pending publication");
        var manifest = await harness.Destination.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), "pending.docx",
            "/shared/pending.docx", new MemoryStream(bytes),
            ImmutableDictionary<string, DocumentAccess>.Empty.Add(DocumentLibraryService.OwnerSubject, DocumentAccess.Write),
            pendingCreation: true);
        var accepted = await harness.Documents.CreateExternalAsync(manifest.ResourceId, manifest.CellBridgePath, bytes,
            DocumentLibraryPermissionProvider.Scope, DocumentLibraryHarness.Owner);
        Assert.NotNull(accepted);
        await harness.Library.InitializeAsync();
        var recovered = (await harness.Provider.State.FindByResourceIdAsync(manifest.ResourceId))!;
        Assert.NotNull(recovered.Publication);
        var delivered = await harness.Destination.GetAsync(manifest.ResourceId);
        Assert.True(delivered.CellBridgeBound);
        Assert.False(delivered.PendingCreation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigrationOptInCannotTakeOverNativeDocumentsOrAnotherScope(bool native)
    {
        using var files = new TemporaryDirectory();
        await using var harness = new DocumentLibraryHarness(files.Path);
        harness.Options.MigrateVersionedAuthorization = true;
        var bytes = TestOfficeFile.Docx("unrelated authority");
        var manifest = await harness.Destination.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), "other.docx",
            "/shared/other.docx", new MemoryStream(bytes),
            ImmutableDictionary<string, DocumentAccess>.Empty.Add(DocumentLibraryService.OwnerSubject, DocumentAccess.Write));
        var state = native
            ? await new CellBridgeDocumentService(harness.Provider).ImportAsync(manifest.ResourceId, manifest.CellBridgePath,
                bytes, DocumentLibraryHarness.Owner.Identity, DocumentLibraryHarness.Owner)
            : await harness.Documents.ImportExternalAsync(manifest.ResourceId, manifest.CellBridgePath, bytes,
                new("another-tenant", "another-workspace"), DocumentLibraryHarness.Owner.Identity, DocumentLibraryHarness.Owner);
        Assert.NotNull(state);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Library.InitializeAsync());
        Assert.Contains("does not match this host's authority", refused.Message);
        Assert.Equal(state, await harness.Provider.State.FindByResourceIdAsync(manifest.ResourceId));
    }
}
