using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.DocumentLibrary;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using Microsoft.Extensions.Configuration;

namespace CellBridge.DocumentLibrary.Tests;

public sealed class AuthorizationRecoveryTests
{
    [Theory]
    [InlineData("matching")]
    [InlineData("content")]
    [InlineData("publication")]
    public async Task LegacyAuthorizationIsRejectedWithoutChangingRecoveryState(string recoveryState)
    {
        using var files = new TemporaryDirectory();
        await using var harness = new DocumentLibraryHarness(files.Path);
        var baseline = TestOfficeFile.Docx("legacy baseline");
        var changed = TestOfficeFile.Docx("legacy saved revision");
        var manifest = await harness.Destination.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), "legacy.docx",
            "/shared/legacy.docx", new MemoryStream(baseline),
            ImmutableDictionary<string, DocumentAccess>.Empty.Add(DocumentLibraryService.OwnerSubject, DocumentAccess.Write));
        var oldPolicy = new LegacyPolicy();
        var oldService = new CellBridgeDocumentService(harness.Provider, authorizationPolicy: oldPolicy);
        var imported = (await oldService.ImportAsync(manifest.ResourceId, manifest.CellBridgePath, baseline,
            DocumentLibraryHarness.Owner.Identity, DocumentLibraryHarness.Owner))!;
        if (recoveryState != "content")
        {
            Assert.True(await harness.Publisher.BindAsync(imported.ResourceId, imported.LifecycleGeneration, imported.StateVersion,
                manifest.BindingId, manifest.DestinationId, manifest.CurrentRevision));
            await harness.Destination.MarkCellBridgeBoundAsync(imported.ResourceId);
        }
        imported = (await harness.Provider.State.FindByResourceIdAsync(imported.ResourceId))!;
        var saved = await oldService.ExecuteAsync(imported.ResourceId, DocumentPartitionKind.FileContents,
            DocumentLibraryHarness.SaveRequest(imported, changed), new Dictionary<string, string>(), DocumentLibraryHarness.Owner);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        Assert.Equal(recoveryState == "content" ? 0 : 1, await harness.Worker.PublishPendingOnceAsync());
        if (recoveryState == "publication")
            await harness.Provider.State.TransitionAsync(imported.ResourceId, (current, _) =>
                new StateTransition<bool>(current with
                { Publication = current.Publication! with { BindingId = Guid.NewGuid() } }, true));
        var locks = new SharedDocumentLocks(oldService);
        Assert.True((await locks.ApplyAsync(imported.ResourceId, HostLockOperation.Acquire, "old-lock", DocumentLibraryHarness.Owner)).Success);
        var before = (await harness.Provider.State.FindByResourceIdAsync(imported.ResourceId))!;
        var manifestPath = Assert.Single(Directory.GetFiles(files.Path, "manifest.json", SearchOption.AllDirectories));
        var manifestBefore = await File.ReadAllBytesAsync(manifestPath);
        var destinationBefore = await harness.Destination.ReadCurrentBytesAsync(imported.ResourceId);
        var revisions = await oldService.ListRevisionsAsync(imported.ResourceId, DocumentLibraryHarness.Owner);
        Assert.True(revisions.Count > 1);
        Assert.NotEmpty(before.Receipts);
        Assert.NotNull(before.Coordination.HostLock);
        // Stale local configuration cannot opt this example into mutating legacy state.
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DocumentLibrary:MigrateVersionedAuthorization"] = "true"
        }).Build().GetSection("DocumentLibrary").Bind(harness.Options);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Library.InitializeAsync());

        Assert.Equal(before, await harness.Provider.State.FindByResourceIdAsync(imported.ResourceId));
        Assert.Equal(manifestBefore, await File.ReadAllBytesAsync(manifestPath));
        Assert.Equal(destinationBefore, await harness.Destination.ReadCurrentBytesAsync(imported.ResourceId));
        Assert.Equal(revisions, await oldService.ListRevisionsAsync(imported.ResourceId, DocumentLibraryHarness.Owner));
        Assert.Contains("does not migrate existing state", refused.Message);
        Assert.Contains("fresh example database and destination root", refused.Message);
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
    public async Task StartupCannotTakeOverNativeDocumentsOrAnotherScope(bool native)
    {
        using var files = new TemporaryDirectory();
        await using var harness = new DocumentLibraryHarness(files.Path);
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

    private sealed class LegacyPolicy : ICellBridgeAuthorizationPolicy
    {
        public string PolicyDomain => DocumentLibraryPermissionProvider.Domain;
        public DocumentAuthorizationBinding BindNewDocument(Guid resourceId) => new(PolicyDomain, 1, 1);
        public ICellBridgeAuthorizationSnapshot? Resolve(DocumentState state) =>
            state.Security.AuthorizationPolicy == BindNewDocument(state.ResourceId)
                ? new LegacySnapshot(state.ResourceId, state.Security.AuthorizationPolicy!) : null;
        private sealed record LegacySnapshot(Guid ResourceId, DocumentAuthorizationBinding Binding)
            : ICellBridgeAuthorizationSnapshot
        {
            public DocumentAccess Evaluate(string subject) => subject == DocumentLibraryService.OwnerSubject
                ? DocumentAccess.Read | DocumentAccess.Write : DocumentAccess.None;
        }
    }
}
