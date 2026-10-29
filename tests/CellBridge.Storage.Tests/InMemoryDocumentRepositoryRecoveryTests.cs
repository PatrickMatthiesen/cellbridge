using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;
using CellBridge.Tests;

public sealed partial class InMemoryDocumentRepositoryTests
{
    [Fact]
    public async Task JsonRoundTrippedCreatesAndRecreationsComparePartitionHeadsByValue()
    {
        var content = new InMemoryContentStore(); var repository = new InMemoryDocumentRepository(content);
        var bytes = MinimalDocx.Create("round trip");
        var original = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), "/round-trip.docx", bytes,
            DocumentSecurity.Create(Actor), content, new());
        PreparedDocumentCreate RoundTrip(PreparedDocumentCreate create) =>
            System.Text.Json.JsonSerializer.Deserialize<PreparedDocumentCreate>(System.Text.Json.JsonSerializer.Serialize(create))!;
        var created = (await repository.CreateAsync(RoundTrip(original))).Current!;
        Assert.True((await repository.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(created.ResourceId,
            created.LifecycleGeneration, created.StateVersion))).Applied);
        var retired = (await repository.FindAsync(new DocumentIdentity.Resource(created.ResourceId, true)))!;
        var replacement = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), retired.Path, bytes,
            retired.Security, content, new(), lifecycleGeneration: retired.LifecycleGeneration + 1);
        var recreated = await repository.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(retired.ResourceId,
            retired.LifecycleGeneration, retired.StateVersion, RoundTrip(replacement)));
        Assert.True(recreated.Applied);
        Assert.Equal(replacement.Head.ResourceId, recreated.Current!.ResourceId);
        await using var stream = await new StorageProvider(repository, content).Packages.OpenReadAsync(recreated.Current.Package);
        using var output = new MemoryStream(); await stream.CopyToAsync(output);
        Assert.Equal(bytes, output.ToArray());
        NormalizedDocumentRecovery.Validate((await repository.ExportDocumentAsync(recreated.Current.ResourceId))!, new());
    }

    [Fact]
    public async Task NormalizedRecoveryRetainsHistoryReceiptsDeletedDocumentsAndIncrementalAccounting()
    {
        var content = new InMemoryContentStore();
        var source = new InMemoryDocumentRepository(content);
        var service = new CellBridgeDocumentService(new(source, content));
        var actor = new CellBridgeActor(Actor, CanCreate: true);
        var created = (await service.CreateAsync("/before-recovery.docx", MinimalDocx.Create("before"), actor))!;
        var locks = new SharedDocumentLocks(service);
        Assert.True((await locks.ApplyAsync(created.ResourceId, HostLockOperation.Acquire, "host", actor)).Success);
        var savedBytes = MinimalDocx.Create("saved");
        using var replacement = new MemoryStream(savedBytes);
        var accepted = await service.SaveWholeFileAsync(created.ResourceId, replacement, "saved", actor, "host");
        Assert.True((await locks.ApplyAsync(created.ResourceId, HostLockOperation.Release, "host", actor)).Success);
        var current = (await source.ReadControlAsync(created.ResourceId))!.Head;
        Assert.True((await source.ChangePathAsync(new(created.ResourceId, Admission(current), "/after-recovery.docx", StorageIds.PathKey("/after-recovery.docx"),
            ImmutableDictionary<string, string>.Empty))).Changed);
        var retired = (await service.CreateAsync("/deleted.docx", MinimalDocx.Create("deleted"), actor))!;
        Assert.True((await source.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(retired.ResourceId, 1, retired.StateVersion))).Applied);
        var snapshot = await source.CaptureRecoveryAsync();
        Assert.Equal(2, snapshot.Snapshots.Length);
        Assert.All(snapshot.Snapshots, document => NormalizedDocumentRecovery.Validate(document, new()));
        var operation = new RecoveryReceipt(Guid.NewGuid(), new string('a', 64), 2, 2);
        var target = new InMemoryDocumentRepository(content);
        await target.CheckRecoveryAsync();
        Assert.Empty((await target.CaptureRecoveryAsync()).Snapshots);
        Assert.Null(await target.FindRecoveryReceiptAsync(operation.OperationId));
        Assert.Equal(operation, await target.ImportRecoveryAsync(snapshot, operation));
        Assert.Equal(operation, await target.ImportRecoveryAsync(snapshot, operation));
        Assert.Equal(operation, await target.FindRecoveryReceiptAsync(operation.OperationId));
        var restored = (await target.FindAsync(new DocumentIdentity.Resource(created.ResourceId)))!;
        Assert.Equal("/after-recovery.docx", restored.Path);
        Assert.Null(await target.FindAsync(new DocumentIdentity.Path(StorageIds.PathKey("/before-recovery.docx"))));
        Assert.Null(await target.FindAsync(new DocumentIdentity.Resource(retired.ResourceId)));
        Assert.True((await target.FindAsync(new DocumentIdentity.Resource(retired.ResourceId, true)))!.IsDeleted);
        var recoveredService = new CellBridgeDocumentService(new(target, content));
        using var retry = new MemoryStream(savedBytes);
        Assert.Equal(accepted.Revision, (await recoveredService.SaveWholeFileAsync(created.ResourceId, retry, "saved", actor, "host")).Revision);
        Assert.True((await new SharedDocumentLocks(recoveredService).ApplyAsync(created.ResourceId, HostLockOperation.Acquire, "new-host", actor)).Success);
        using var newer = new MemoryStream(MinimalDocx.Create("after import"));
        await recoveredService.SaveWholeFileAsync(created.ResourceId, newer, "after-import", actor, "new-host");
        var exported = await target.ExportDocumentAsync(created.ResourceId);
        Assert.NotNull(exported);
        NormalizedDocumentRecovery.Validate(exported!, new());
        Assert.Equal(3, exported!.Current.Head.Counters.Revisions);
        Assert.Equal(2, exported.Current.Head.Counters.WholeFileSaveReceipts);
        Assert.Equal(exported.Current.Head.Counters.StoredBytes, NormalizedDocumentRecovery.StoredBytes(exported));
    }

    [Fact]
    public async Task InvalidRecoveryBatchPublishesNeitherDocumentsNorItsReceiptAndCanBeRetried()
    {
        var content = new InMemoryContentStore();
        var source = new InMemoryDocumentRepository(content);
        var first = await SeedAsync(content, "/recovery-one.docx");
        var second = await SeedAsync(content, "/recovery-two.docx");
        Assert.True((await source.CreateAsync(first)).Created);
        Assert.True((await source.CreateAsync(second)).Created);
        var valid = await source.CaptureRecoveryAsync();
        var bad = valid.Snapshots[1];
        bad = bad with { Current = bad.Current with { Head = bad.Current.Head with
            { Counters = bad.Current.Head.Counters with { GraphElements = bad.Current.Head.Counters.GraphElements + 1 } } } };
        var invalid = valid with { Snapshots = [valid.Snapshots[0], bad] };
        var target = new InMemoryDocumentRepository(content);
        var receipt = new RecoveryReceipt(Guid.NewGuid(), new string('b', 64), 2, 2);
        await Assert.ThrowsAsync<StorageCorruptionException>(async () => await target.ImportRecoveryAsync(invalid, receipt));
        Assert.Empty((await target.CaptureRecoveryAsync()).Snapshots);
        Assert.Null(await target.FindRecoveryReceiptAsync(receipt.OperationId));
        Assert.Equal(receipt, await target.ImportRecoveryAsync(valid, receipt));
    }

    [Fact]
    public async Task ARepositoryThatAdmittedNormalOperationsRejectsStartupImport()
    {
        var content = new InMemoryContentStore();
        var source = new InMemoryDocumentRepository(content);
        Assert.True((await source.CreateAsync(await SeedAsync(content, "/startup.docx"))).Created);
        var snapshot = await source.CaptureRecoveryAsync();
        var target = new InMemoryDocumentRepository(content);
        Assert.Null(await target.FindAsync(new DocumentIdentity.Resource(Guid.NewGuid())));
        var receipt = new RecoveryReceipt(Guid.NewGuid(), new string('c', 64), 1, 1);
        await Assert.ThrowsAsync<RecoveryConflictException>(async () => await target.ImportRecoveryAsync(snapshot, receipt));
        Assert.Empty((await target.CaptureRecoveryAsync()).Snapshots);
        Assert.Null(await target.FindRecoveryReceiptAsync(receipt.OperationId));
    }

    [Fact]
    public async Task MetadataPublicationAppendsApplicationHistoryWhilePreservingTheCurrentFileAndOutbox()
    {
        var content = new InMemoryContentStore();
        var repository = new InMemoryDocumentRepository(content);
        var provider = new StorageProvider(repository, content);
        var owner = new CellBridgeActor(Actor, CanCreate: true);
        var service = new CellBridgeDocumentService(provider, accessEvaluator: new GrantAll());
        var bytes = MinimalDocx.Create("file remains unchanged");
        var original = (await service.CreateAsync("/metadata.docx", bytes, owner))!;
        Assert.True((await repository.UpdateDeliveryAsync(new DocumentDeliveryCommand.Bind(original.ResourceId, 1, original.StateVersion,
            new(Guid.NewGuid(), "external", "baseline", 1)))).Applied);
        var fixture = new GraphFixture("application metadata"u8.ToArray());
        var request = new FsshttpbCellRequest { DataElementPackage = new(), SubRequests =
            { new(RequestTypes.PutChanges) { RequestId = 1, Data = new PutChangesSubRequestData { StorageIndex = fixture.Index } } } };
        request.DataElementPackage.DataElements.AddRange(fixture.Elements);
        var metadataAuthor = new CellBridgeActor(new("metadata-author", "metadata-author", "Metadata author"));
        var saved = await service.ExecuteAsync(original.ResourceId, DocumentPartitionKind.Metadata, request,
            ImmutableDictionary<string, string>.Empty, metadataAuthor);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        Assert.Empty(saved.AcceptedSaves);
        Assert.Equal(1, saved.State.Counters.SaveReceipts);
        var replay = await service.ExecuteAsync(original.ResourceId, DocumentPartitionKind.Metadata, request,
            ImmutableDictionary<string, string>.Empty, metadataAuthor);
        Assert.False(Assert.Single(replay.Response.SubResponses).Status);
        Assert.Empty(replay.AcceptedSaves);
        Assert.Equal(saved.State.StateVersion, replay.State.StateVersion);
        var current = (await repository.ReadControlAsync(original.ResourceId))!.Head;
        Assert.Equal(original.ContentVersion, current.ContentVersion);
        Assert.Equal(original.ModifiedUtc, current.ModifiedUtc);
        Assert.Equal(original.Security.ModifiedBy, current.Security.ModifiedBy);
        Assert.Equal(original.Package.Root, current.Package.Root);
        Assert.Equal(2UL, current.LatestRevisionNumber);
        Assert.Equal(0, current.Counters.PendingDeliveries);
        var selected = (await repository.ReadRevisionAsync(original.ResourceId, 2))!.Revision!;
        Assert.Equal(metadataAuthor.Identity, selected.Author);
        await using var download = await provider.Packages.OpenReadAsync(current.Package);
        using var result = new MemoryStream();
        await download.CopyToAsync(result);
        Assert.Equal(bytes, result.ToArray());
        Assert.Empty((await repository.ReadPartitionAsync(original.ResourceId, new(0) { IncludeBindings = true }))!.Mappings);
        Assert.NotNull(await repository.ExportDocumentAsync(original.ResourceId));
    }
}
