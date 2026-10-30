using System.Collections.Immutable;
using System.Text.Json;
using System.Security.Cryptography;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.Tests;

/// <summary>Shared behavior exercised against real normalized relational schemas.</summary>
public static class NormalizedProviderScenarios
{
    private static readonly CellBridgeActor Owner = new(new("normalized:owner", "owner", "Owner"), CanCreate: true);
    private static readonly ImmutableDictionary<string, string> NoLocks = ImmutableDictionary<string, string>.Empty;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static async Task<NormalizedDocumentSnapshot> CreateSaveMetadataRestoreAsync(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var repository = provider.Documents;
        var beforeBytes = MinimalDocx.Create("normalized original");
        var created = await service.CreateAsync("/normalized-" + Guid.NewGuid().ToString("N") + ".docx", beforeBytes, Owner)
            ?? throw new InvalidOperationException("Create did not return a document.");
        var initialCatalog = (await repository.ReadPartitionAsync(created.ResourceId, new(0) { IncludeCatalog = true }))!;
        await CheckSnapshotAsync(repository, created.ResourceId);
        var fixture = new GraphFixture("metadata payload"u8.ToArray());
        var request = new FsshttpbCellRequest { DataElementPackage = new(), SubRequests =
            { new(RequestTypes.PutChanges) { RequestId = 1, Data = new PutChangesSubRequestData { StorageIndex = fixture.Index } } } };
        request.DataElementPackage.DataElements.AddRange(fixture.Elements);
        var metadata = await service.ExecuteAsync(created.ResourceId, DocumentPartitionKind.Metadata, request, NoLocks, Owner);
        Require(metadata.Response.SubResponses.All(response => !response.Status) && metadata.AcceptedSaves.IsEmpty, "Metadata save failed or emitted a file delivery notification.");
        var afterMetadata = (await repository.ReadControlAsync(created.ResourceId))!.Head;
        Require(afterMetadata.Counters.SaveReceipts == 1, "Metadata save lost its protocol receipt.");
        Require(afterMetadata.ContentVersion == created.ContentVersion && afterMetadata.ModifiedUtc == created.ModifiedUtc &&
            afterMetadata.Security.ModifiedBy == created.Security.ModifiedBy && afterMetadata.Package.Root == created.Package.Root &&
            afterMetadata.LatestRevisionNumber == 2, "Metadata save changed file properties or lost history.");
        Require((await ReadAsync(provider, afterMetadata.Package)).SequenceEqual(beforeBytes), "Metadata save changed file download.");
        var metadataBeforeRestore = await QueryAsync(DocumentPartitionKind.Metadata);
        var knownMetadata = ClientKnowledge.FromElements(metadataBeforeRestore);
        var knownKeys = StorageIndexPatch.Read(metadataBeforeRestore.Single(element => element.DataElementType == DataElementType.StorageIndexDataElementData)).Count;
        await CheckSnapshotAsync(repository, created.ResourceId);
        var locks = new SharedDocumentLocks(service);
        Require((await locks.ApplyAsync(created.ResourceId, HostLockOperation.Acquire, "host", Owner)).Success, "Host lease was not acquired.");
        var bytes = MinimalDocx.Create("normalized replacement");
        using var stream = new MemoryStream(bytes);
        var accepted = await service.SaveWholeFileAsync(created.ResourceId, stream, "whole-file", Owner, "host");
        Require(!accepted.IsReplay && accepted.Revision.ContentVersion == created.ContentVersion + 1, "Whole-file save failed.");
        Require((await ReadAsync(provider, accepted.Revision.Package)).SequenceEqual(bytes), "New package bytes differ.");
        Require((await ReadAsync(provider, created.Package)).SequenceEqual(beforeBytes), "Historical package bytes changed.");
        var currentCatalog = (await repository.ReadPartitionAsync(created.ResourceId, new(0) { IncludeCatalog = true }))!;
        var added = currentCatalog.Elements.First(element => !initialCatalog.Elements.Any(before => before.Id == element.Id));
        var scope = created.Package.Root.Scope;
        Require((await repository.ReadVisibleElementIdsAsync(scope, currentCatalog.Partition.PublicationSequence, [added.Id])).SequenceEqual([added.Id]),
            "Targeted current visibility omitted an active element.");
        Require((await repository.ReadVisibleElementIdsAsync(scope, initialCatalog.Partition.PublicationSequence, [added.Id])).IsEmpty &&
            (await repository.ReadVisibleElementIdsAsync(scope, initialCatalog.Partition.PublicationSequence, [initialCatalog.Elements[0].Id])).SequenceEqual([initialCatalog.Elements[0].Id]),
            "Targeted historical visibility did not use the selected publication interval.");
        Require((await repository.ReadVisibleElementIdsAsync(scope, currentCatalog.Partition.PublicationSequence, [new(1, Guid.NewGuid())])).IsEmpty &&
            (await repository.ReadVisibleElementIdsAsync(scope, currentCatalog.Partition.PublicationSequence + 1, [added.Id])).IsEmpty,
            "Missing or future visibility returned an element.");
        using var replayStream = new MemoryStream(bytes);
        var replay = await service.SaveWholeFileAsync(created.ResourceId, replayStream, "whole-file", Owner, "host");
        Require(replay.IsReplay && JsonSerializer.Serialize(replay.Revision) == JsonSerializer.Serialize(accepted.Revision), "Accepted receipt did not replay.");
        Require((await locks.ApplyAsync(created.ResourceId, HostLockOperation.Release, "host", Owner)).Success, "Host lease was not released.");
        var restored = await service.RestoreRevisionAsync(created.ResourceId, 1, accepted.Revision.RevisionNumber, "restore", Owner);
        Require(!restored.IsReplay && restored.Revision.ContentVersion == accepted.Revision.ContentVersion + 1, "Restore did not append a file revision.");
        Require((await ReadAsync(provider, restored.Revision.Package)).SequenceEqual(beforeBytes), "Restore package bytes differ.");
        var warmMetadata = await QueryAsync(DocumentPartitionKind.Metadata, knownMetadata);
        var cleared = StorageIndexPatch.Read(warmMetadata.Single(element => element.DataElementType == DataElementType.StorageIndexDataElementData));
        Require(cleared.Count >= knownKeys && cleared.All(mapping => mapping.Target.IsNull && !mapping.Serial.IsNull),
            "A warm metadata client did not learn all restored-to-empty mapping keys.");
        var fileQuery = await QueryAsync(DocumentPartitionKind.FileContents);
        Require(PartitionGraphSnapshot.Create(fileQuery, fileQuery.Single(element => element.DataElementType == DataElementType.StorageIndexDataElementData).DataElementExtendedGuid)
            .Materialize().SequenceEqual(beforeBytes), "File QueryChanges does not reconstruct restored bytes.");
        var current = (await repository.ReadControlAsync(created.ResourceId))!.Head;
        var renamed = "/renamed-" + Guid.NewGuid().ToString("N") + ".docx";
        var changed = await repository.ChangePathAsync(new(created.ResourceId, Admission(current), renamed, StorageIds.PathKey(renamed), NoLocks));
        Require(changed.Changed && await repository.FindAsync(new DocumentIdentity.Path(created.PathKey)) is null,
            "Rename did not retire its original path.");
        return await CheckSnapshotAsync(repository, created.ResourceId);

        async Task<List<DataElement>> QueryAsync(DocumentPartitionKind kind, ClientKnowledge? knowledge = null)
        {
            var query = new FsshttpbCellRequest();
            query.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 1,
                Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true, Knowledge = knowledge } });
            var result = await service.ExecuteAsync(created.ResourceId, kind, query, NoLocks, Owner);
            Require(result.Response.SubResponses.All(response => !response.Status), "QueryChanges failed.");
            return result.Response.DataElementPackage?.DataElements ?? throw new InvalidOperationException("QueryChanges omitted its package.");
        }
    }
    public static async Task<NormalizedDocumentSnapshot> CheckSnapshotAsync(IDocumentRepository repository, Guid id)
    {
        var snapshot = await repository.ExportDocumentAsync(id) ?? throw new InvalidOperationException("Normalized export is absent.");
        NormalizedDocumentRecovery.Validate(snapshot, new());
        Require(snapshot.Current.Head.Counters.StoredBytes == NormalizedDocumentRecovery.StoredBytes(snapshot), "Incremental accounting differs from exported records.");
        return snapshot;
    }
    public static async Task OutboxBlocksLifecycleAsync(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var head = (await service.CreateAsync("/outbox-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create("before"), Owner))!;
        Require((await provider.Documents.UpdateDeliveryAsync(new DocumentDeliveryCommand.Bind(head.ResourceId, head.LifecycleGeneration,
            head.StateVersion, new(Guid.NewGuid(), "destination", "remote-0", 1)))).Applied, "Delivery binding failed.");
        var locks = new SharedDocumentLocks(service);
        Require((await locks.ApplyAsync(head.ResourceId, HostLockOperation.Acquire, "host", Owner)).Success, "Outbox host lease failed.");
        using var stream = new MemoryStream(MinimalDocx.Create("delivered"));
        await service.SaveWholeFileAsync(head.ResourceId, stream, "delivery", Owner, "host");
        var next = await provider.Documents.ReadNextDeliveryAsync(head.ResourceId)
            ?? throw new InvalidOperationException("File save lost its outbox entry.");
        Require(next.Current.Counters.PendingDeliveries == 1, "File save lost its outbox entry.");
        Require(!(await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(head.ResourceId, next.Current.LifecycleGeneration,
            next.Current.StateVersion))).Applied, "Delete stranded an accepted delivery.");
        Require((await provider.Documents.UpdateDeliveryAsync(new DocumentDeliveryCommand.Block(head.ResourceId, next, "blocked"))).Applied, "Block failed.");
        Require(await provider.Documents.ReadNextDeliveryAsync(head.ResourceId) is null &&
            !(await provider.Documents.ListPendingDeliveriesAsync(int.MaxValue)).Contains(head.ResourceId), "Blocked destination remained eligible.");
        Require((await provider.Documents.UpdateDeliveryAsync(new DocumentDeliveryCommand.Retry(head.ResourceId, next.Binding.BindingId, next.Revision.OperationId))).Applied, "Retry failed.");
        next = (await provider.Documents.ReadNextDeliveryAsync(head.ResourceId))!;
        Require((await provider.Documents.UpdateDeliveryAsync(new DocumentDeliveryCommand.Complete(head.ResourceId, next, "remote-1"))).Applied, "Complete failed.");
        Require(await provider.Documents.ReadNextDeliveryAsync(head.ResourceId) is null, "Completed outbox entry remained eligible.");
        await CheckSnapshotAsync(provider.Documents, head.ResourceId);
    }
    public static async Task RecoverAsync(NormalizedDocumentSnapshot snapshot, StorageProvider target)
    {
        var source = new ProviderSnapshot([snapshot], []);
        var receipt = new RecoveryReceipt(Guid.NewGuid(), new string('a', 64), 1, 1);
        Require(await target.Documents.ImportRecoveryAsync(source, receipt) == receipt, "Recovery import failed.");
        Require(await target.Documents.ImportRecoveryAsync(source, receipt) == receipt, "Recovery receipt did not replay.");
        var imported = await CheckSnapshotAsync(target.Documents, snapshot.Current.Head.ResourceId);
        Require(JsonSerializer.Serialize(imported.Current.Head) == JsonSerializer.Serialize(snapshot.Current.Head), "Recovery changed the document head.");
        Require((await ReadAsync(target, imported.Current.Head.Package)).Length == snapshot.Current.Head.Package.Length, "Recovered download failed.");
    }
    public static async Task LimitsCancellationAndIndependentDocumentsAsync(StorageProvider provider,
        Func<StorageLimits, StorageProvider> withLimits, Func<IInterceptor[], StorageProvider> withInterceptors)
    {
        var service = new CellBridgeDocumentService(provider);
        var first = (await service.CreateAsync("/guard-first-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create("first"), Owner))!;
        var second = (await service.CreateAsync("/guard-second-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create("second"), Owner))!;
        var pause = new NormalizedHeadPause(first.ResourceId);
        var held = withInterceptors([pause]);
        using var cancellation = new CancellationTokenSource();
        var proposedPath = "/cancelled-" + Guid.NewGuid().ToString("N") + ".docx";
        var pending = held.Documents.ChangePathAsync(new(first.ResourceId, Admission(first), proposedPath, StorageIds.PathKey(proposedPath), NoLocks), cancellation.Token).AsTask();
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var unrelated = "/independent-" + Guid.NewGuid().ToString("N") + ".docx";
            var changed = await provider.Documents.ChangePathAsync(new(second.ResourceId, Admission(second), unrelated, StorageIds.PathKey(unrelated), NoLocks)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Require(changed.Changed, "An unrelated document mutation could not commit while the first document was guarded.");
            cancellation.Cancel();
            try { await pending; throw new InvalidOperationException("A cancelled guarded mutation committed."); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        finally { pause.Continue.TrySetResult(); }
        var afterCancellation = (await provider.Documents.ReadControlAsync(first.ResourceId))!.Head;
        Require(JsonSerializer.Serialize(afterCancellation) == JsonSerializer.Serialize(first), "Cancellation published head or counters.");
        Require(await provider.Documents.FindAsync(new DocumentIdentity.Path(StorageIds.PathKey(proposedPath))) is null, "Cancellation published its filename claim.");
        var locks = new SharedDocumentLocks(service);
        Require((await locks.ApplyAsync(first.ResourceId, HostLockOperation.Acquire, "host", Owner)).Success, "Limit test lease failed.");
        var beforeLimit = (await provider.Documents.ReadControlAsync(first.ResourceId))!.Head;
        var restricted = new CellBridgeDocumentService(withLimits(new() { MaxHistoryRevisions = 1 }));
        try
        {
            using var replacement = new MemoryStream(MinimalDocx.Create("over history limit"));
            await restricted.SaveWholeFileAsync(first.ResourceId, replacement, "over-limit", Owner, "host");
            throw new InvalidOperationException("The per-document revision limit was bypassed.");
        }
        catch (StorageQuotaExceededException) { }
        var afterLimit = (await provider.Documents.ReadControlAsync(first.ResourceId))!.Head;
        Require(JsonSerializer.Serialize(beforeLimit) == JsonSerializer.Serialize(afterLimit), "Rejected quota publication changed the head or counters.");
        var lostCommands = new NormalizedHeadPause(first.ResourceId) { Pause = false };
        var uncertain = new CellBridgeDocumentService(withInterceptors([lostCommands, new NormalizedLostCommitReply(lostCommands)]));
        var finalBytes = MinimalDocx.Create("lost acknowledgement");
        using var finalInput = new MemoryStream(finalBytes);
        var resolved = await uncertain.SaveWholeFileAsync(first.ResourceId, finalInput, "uncertain", Owner, "host");
        Require(resolved.IsReplay && resolved.Revision.RevisionNumber == 2, "An uncertain real commit was not resolved through its receipt.");
        var exported = await CheckSnapshotAsync(provider.Documents, first.ResourceId);
        Require(exported.Current.Head.Counters.Revisions == 2 && exported.Current.Head.Counters.WholeFileSaveReceipts == 1,
            "Cancelled, rejected, or uncertain work duplicated publication records.");
    }
    public static async Task PhysicalCreationAsync(Func<IInterceptor[], IContentStore> contentFactory, Func<Task<string>> inventory)
    {
        var duplicate = RandomNumberGenerator.GetBytes(4 * 65536 + 17);
        var writes = Enumerable.Range(0, 8).Select(async _ =>
        { using var input = new MemoryStream(duplicate); return await contentFactory([]).WriteAsync(input); }).ToArray();
        var handles = await Task.WhenAll(writes);
        Require(handles.All(handle => handle == handles[0]), "Concurrent duplicate creation changed physical identity.");
        var other = RandomNumberGenerator.GetBytes(65536 + 1);
        var batches = Enumerable.Range(0, 8).Select(async index =>
        {
            var writer = contentFactory([]) as IContentBatchWriter ?? throw new InvalidOperationException("Shipping content store omitted batch writes.");
            var payloads = index % 2 == 0 ? new ReadOnlyMemory<byte>[] { duplicate, other } : new ReadOnlyMemory<byte>[] { other, duplicate };
            var accepted = await writer.WriteManyAsync(payloads);
            for (var i = 0; i < accepted.Count; i++)
                Require((await contentFactory([]).ReadVerifiedAsync(accepted[i])).SequenceEqual(payloads[i].ToArray()), "Concurrent overlapping batch stored incorrect bytes.");
        }).ToArray();
        await Task.WhenAll(batches);
        for (var mode = 0; mode < 2; mode++)
        {
            var before = await inventory();
            using var cancellation = new CancellationTokenSource();
            var content = contentFactory([new NormalizedChunkCancellation(cancellation)]);
            var bytes = RandomNumberGenerator.GetBytes(17 * 65536);
            try
            {
                if (mode == 0) { using var input = new MemoryStream(bytes); await content.WriteAsync(input, cancellation.Token); }
                else await ((IContentBatchWriter)content).WriteManyAsync(new ReadOnlyMemory<byte>[] { bytes, RandomNumberGenerator.GetBytes(100) }, cancellation.Token);
                throw new InvalidOperationException("Cancellation after an actual chunk batch committed content.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Require(before == await inventory(), "Cancelled chunk creation left object metadata or chunks.");
        }
    }
    public static async Task AcceptedPublicationExpiresControlsAtomicallyAsync(StorageProvider provider, Func<StorageLimits, StorageProvider> withLimits)
    {
        var now = DateTime.UtcNow;
        var seed = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), "/expiry-" + Guid.NewGuid().ToString("N") + ".docx",
            MinimalDocx.Create("expiry"), DocumentSecurity.Create(Owner.Identity), provider.Content, provider.Limits);
        seed = seed with
        {
            Coordination = seed.Coordination with { Generation = 7, Exclusive = new("old", "old-client", now.AddMinutes(-1), 0, null, Owner.Identity.Subject),
                HostLock = new("old-host", Owner.Identity.Subject, now.AddMinutes(-1)) },
            Editors = [new(Guid.NewGuid(), "expired", now.AddMinutes(-3), now.AddMinutes(-2), now.AddMinutes(-1), 60, true, 1,
                ImmutableDictionary<string, ImmutableArray<byte>>.Empty, Owner.Identity)],
        };
        Require((await provider.Documents.CreateAsync(seed)).Created, "The expiry fixture was not created.");
        var before = (await provider.Documents.ReadControlAsync(seed.Head.ResourceId))!;
        var cleanup = DocumentControlReducer.ExpireForPublication(before);
        Require(cleanup.Changed, "The expiry fixture has no expired controls.");
        var plan = new PreparedDocumentPublication(before.Head.ResourceId, Admission(before.Head),
            new(before.Head.LifecycleGeneration, before.Head.AuthorityGeneration, before.Coordination.Generation, before.Head.StateVersion, before.Head.ContentVersion, [])
                { PreparedCoordination = cleanup.Current.Coordination },
            new(new(before.Head.LifecycleGeneration, SaveOperationFamily.Protocol, 0, "cleanup"), new string('c', 64), Owner.Identity.Subject,
                before.Head.ContentVersion, before.Head.LatestRevisionNumber, null, null), []);
        try
        {
            await withLimits(new() { MaxGraphElements = 1 }).Documents.PublishAsync(plan);
            throw new InvalidOperationException("A quota failure published expiry cleanup.");
        }
        catch (StorageQuotaExceededException) { }
        var rejected = (await provider.Documents.ReadControlAsync(before.Head.ResourceId))!;
        Require(JsonSerializer.Serialize(before with { UtcNow = DateTime.UnixEpoch }) == JsonSerializer.Serialize(rejected with { UtcNow = DateTime.UnixEpoch }),
            "A rejected publication persisted expiry cleanup.");
        Require((await provider.Documents.PublishAsync(plan)).Status == PublicationStatus.Accepted, "A valid cleanup publication was rejected.");
        var accepted = (await provider.Documents.ReadControlAsync(before.Head.ResourceId))!;
        Require(accepted.Head.StateVersion == before.Head.StateVersion + 1 && accepted.Editors.IsEmpty && accepted.Coordination.Exclusive is null &&
            accepted.Coordination.HostLock is null && accepted.Coordination.Generation > before.Coordination.Generation &&
            accepted.Head.CoordinationGeneration == accepted.Coordination.Generation && accepted.Head.NextEditorExpiryUtc is null &&
            accepted.Partitions.Single(partition => partition.Kind == 2).Knowledge > before.Partitions.Single(partition => partition.Kind == 2).Knowledge,
            "Accepted publication did not persist controls, editor knowledge and scalar generations once.");
        Require((await provider.Documents.PublishAsync(plan with { Admission = Admission(accepted.Head) })).Status == PublicationStatus.Replay,
            "Accepted cleanup receipt did not replay.");
        await CheckSnapshotAsync(provider.Documents, before.Head.ResourceId);
    }
    public static DocumentActorAdmission Admission(DocumentHead head) => new(Owner.Identity, head.ResourceId, head.LifecycleGeneration, head.Security.AuthorizationPolicy, DocumentAccess.Read | DocumentAccess.Write)
        { AuthorityGeneration = head.AuthorityGeneration, StateVersion = head.StateVersion };
    public static async Task CopyReferencesAsync(NormalizedDocumentSnapshot snapshot, IContentStore source, IContentStore target)
    {
        foreach (var reference in snapshot.ContentReferences)
        {
            await using var stream = await source.OpenReadAsync(reference.Handle);
            Require(await target.WriteAsync(stream) == reference.Handle, "Physical content copy changed its identity.");
        }
    }
    private static async Task<byte[]> ReadAsync(StorageProvider provider, DocumentPackage package)
    {
        await using var stream = await provider.Packages.OpenReadAsync(package);
        using var output = new MemoryStream(); await stream.CopyToAsync(output); return output.ToArray();
    }
}
