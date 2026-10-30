using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public sealed record RestoreRevisionResult(DocumentRevisionRecord Revision, bool IsReplay);

public sealed partial class CellBridgeDocumentService
{
    public async ValueTask<IReadOnlyList<DocumentRevisionRecord>> ListRevisionsAsync(Guid resourceId, CellBridgeActor actor,
        CancellationToken cancellationToken = default)
    {
        actor = await AuthorizeResourceActorAsync(actor, resourceId, DocumentAccess.Read, cancellationToken);
        var revisions = new List<DocumentRevisionRecord>();
        ulong? beforeRevision = null;
        long? lifecycle = null;
        while (true)
        {
            var page = await ReadRevisionPageAsync(resourceId, 0, 256, actor, beforeRevision, cancellationToken);
            lifecycle ??= page.Current.LifecycleGeneration;
            if (page.Current.LifecycleGeneration != lifecycle)
                throw new UnauthorizedAccessException("The document lifecycle changed.");
            revisions.AddRange(page.Revisions);
            if (page.Revisions.IsEmpty || page.NextOffset is null) return revisions;
            beforeRevision = page.Revisions[^1].RevisionNumber;
        }
    }

    public ValueTask<DocumentRevisionRecord> GetRevisionAsync(Guid resourceId, ulong revisionNumber, CellBridgeActor actor,
        CancellationToken cancellationToken = default) => GetRevisionSummaryAsync(resourceId, revisionNumber, actor, cancellationToken);

    public async ValueTask<RestoreRevisionResult> RestoreRevisionAsync(Guid resourceId, ulong revisionNumber,
        ulong expectedRevision, string operationKey, CellBridgeActor actor,
        IReadOnlyDictionary<string, string>? lockAttributes = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        if (operationKey.Length > 256) throw new ArgumentException("The restore key exceeds 256 characters.", nameof(operationKey));
        var attributes = lockAttributes ?? ImmutableDictionary<string, string>.Empty;
        var head = await CurrentAsync(resourceId, cancellationToken);
        var operation = new SaveOperationIdentity(head.LifecycleGeneration, SaveOperationFamily.Restore, 0, operationKey);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{revisionNumber}:{expectedRevision}"))));
        actor = await AuthorizeActorAsync(actor, head, DocumentAccess.Write, cancellationToken);
        Demand(actor, head, DocumentAccess.Write);
        if (await RestoreReplayAsync(cancellationToken) is { } existingReplay) return existingReplay;
        var selected = await GetRevisionAsync(resourceId, revisionNumber, actor, cancellationToken);
        // Verify the selected canonical file before any publication. The stream is temporary.
        await using (var verified = await provider.Packages.OpenReadAsync(selected.Package, cancellationToken)) { }
        CoordinationState? originalAuthority = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var current = await provider.Documents.ReadControlAsync(resourceId, cancellationToken) ?? throw MissingDocument();
            actor = await AuthorizeActorAsync(actor, current.Head, DocumentAccess.Write, cancellationToken);
            Demand(actor, current.Head, DocumentAccess.Write);
            if (current.Head.LifecycleGeneration != operation.LifecycleGeneration) throw new InvalidOperationException("The document lifecycle changed.");
            if (await RestoreReplayAsync(cancellationToken) is { } replay) return replay;
            if (current.Head.LatestRevisionNumber != expectedRevision) throw new InvalidOperationException("The current revision changed.");
            if (DocumentControlReducer.ValidateWrite(current, Admission(actor, current.Head), attributes) is { } error)
                throw new DocumentOperationLockException(error);
            var authority = DocumentControlReducer.ExpireForPublication(current).Current.Coordination;
            originalAuthority ??= authority;
            if (CoordinationFencing.Capture(originalAuthority with { Generation = 0 }, authority with { Generation = 0 }).Generation != 0 ||
                current.Coordination.Generation != head.CoordinationGeneration)
                throw new DocumentOperationLockException("InvalidCoauthSession");
            using var stagingContent = new DocumentStagingContent(provider.Documents, provider.Content, Admission(actor, current.Head), provider.Limits);
            var plans = ImmutableArray.CreateBuilder<PreparedPartitionPublication>();
            DocumentPackage? package = null;
            foreach (var historical in selected.Partitions)
            {
                var before = current.Partitions.Single(partition => partition.Kind == historical.Kind);
                if (historical.StorageIndex is null)
                {
                    var inlineScope = new IndexedGraphScope(resourceId, head.LifecycleGeneration, before.Kind);
                    var sequence = checked(before.PublicationSequence + 1);
                    var existing = await provider.Documents.ReadPartitionAsync(resourceId,
                        new(before.Kind) { IncludeCatalog = true, IncludeBindings = true }, cancellationToken) ?? throw MissingDocument();
                    var inlineMappings = before.StorageIndex is null ? [] :
                        await provider.Documents.ReadMappingsAsync(inlineScope, before.PublicationSequence, cancellationToken);
                    var knowledge = checked(before.Knowledge + 1);
                    var changes = new List<IndexedMappingChange>();
                    var logicalIndex = before.StorageIndex is null ? null : new ExtendedId(1, Guid.NewGuid());
                    var indexSerial = logicalIndex is null ? null : new SerialId(before.Identity.SerialGuid, knowledge);
                    var claims = ImmutableArray<IndexedStorageMapping>.Empty;
                    if (logicalIndex is not null)
                    {
                        var nil = new ExtendedId(0, Guid.Empty);
                        foreach (var mappingKey in inlineMappings.Select(mapping => mapping.Key)
                            .Append(new IndexedMappingKey(IndexedMappingKind.Manifest)).Distinct())
                            changes.Add(new(mappingKey, new(nil, new(before.Identity.SerialGuid, checked(++knowledge)))));
                        // Keep serial-bearing nil mappings so a client that has the
                        // previous map can observe every removed key in QueryChanges.
                        claims = changes.Select(change => new IndexedStorageMapping(inlineScope, logicalIndex,
                            change.Key, change.Value!.Target, change.Value.Serial)).ToImmutableArray();
                    }
                    plans.Add(new(before, before with { Knowledge = knowledge, PublicationSequence = sequence,
                        StorageIndex = logicalIndex, StorageIndexSerial = indexSerial, ElementCount = 0, ElementBytes = 0,
                        InlineContent = historical.Kind == 1 ? Encoding.UTF8.GetBytes(FormattableString.Invariant(
                            $"<Metadata ContentVersion=\"{checked(current.Head.ContentVersion + 1)}\" Modified=\"{current.UtcNow.Ticks}\" />")).ToImmutableArray() : historical.InlineContent }, [], existing.Elements.Select(element => element.Id).ToImmutableArray(), claims, [], [],
                        IndexedDocumentGraph.Difference(inlineScope, before.PublicationSequence, sequence, existing.Bindings, []))
                    {
                        MappingChanges = changes.ToImmutableArray(),
                        Checkpoint = IndexedDocumentGraph.Checkpoint(inlineScope, sequence, []),
                    });
                    continue;
                }
                var scope = new IndexedGraphScope(resourceId, head.LifecycleGeneration, before.Kind);
                var oldMappings = await provider.Documents.ReadMappingsAsync(scope, historical.PublicationSequence, cancellationToken);
                var currentMappings = await provider.Documents.ReadMappingsAsync(scope, before.PublicationSequence, cancellationToken);
                ulong serial = before.Knowledge;
                foreach (var mapping in oldMappings.Concat(currentMappings))
                    if (mapping.Serial.Guid == before.Identity.SerialGuid) serial = Math.Max(serial, mapping.Serial.Value);
                SerialNumber NextSerial() => new(before.Identity.SerialGuid, checked(++serial));
                ExGuid NextId() => new(1, Guid.NewGuid());
                var updateMappings = oldMappings.Select(RestoreMapping).ToList();
                var incoming = new DataElementPackage();
                var oldCells = oldMappings.Where(mapping => mapping.Key.Kind == IndexedMappingKind.Cell && !IsNull(mapping.Target)).ToArray();
                foreach (var cell in oldCells)
                {
                    var descriptor = (await provider.Documents.ReadElementsAsync(scope, [cell.Target], cancellationToken)).Single();
                    var element = new DataElement((DataElementType)descriptor.Type, StorageIds.Restore(descriptor.Id), new(descriptor.Serial.Guid, descriptor.Serial.Value))
                    { Data = await provider.Content.ReadVerifiedAsync(descriptor.Payload, provider.Limits.MaxObjectBytes, cancellationToken) };
                    var oldRevision = IndexedDocumentGraph.Capture(IndexedGraphRecords.ReadCellRevision(element));
                    var mappedRevision = oldMappings.Single(mapping => mapping.Key.Kind == IndexedMappingKind.Revision && mapping.Key.Revision == oldRevision);
                    var manifest = await provider.Documents.ReadRevisionAsync(scope, mappedRevision.Target, cancellationToken)
                        ?? throw new StorageCorruptionException("Missing historical revision manifest.");
                    var revision = NextId();
                    var revisionElement = NextId();
                    incoming.DataElements.Add(HistoricalGraphRestorer.BuildInheritedRevision(revisionElement, NextSerial(), revision,
                        StorageIds.Restore(oldRevision), manifest.Roots.Select(root =>
                            new RevisionRootReference(StorageIds.Restore(root.Root), StorageIds.Restore(root.Object)))));
                    var cellElement = StorageManifestBuilder.BuildCellManifestDataElement(NextId(), NextSerial(), revision);
                    incoming.DataElements.Add(cellElement);
                    updateMappings.RemoveAll(mapping => mapping.Type == StreamObjectTypeHeaderStart.StorageIndexCellMapping &&
                        IndexedDocumentGraph.Capture(mapping.Cell!) == cell.Key.Cell);
                    updateMappings.Add(new(StreamObjectTypeHeaderStart.StorageIndexCellMapping, null,
                        new(StorageIds.Restore(cell.Key.Cell!.LongId), StorageIds.Restore(cell.Key.Cell.ShortId)), cellElement.DataElementExtendedGuid, NextSerial()));
                    updateMappings.Add(new(StreamObjectTypeHeaderStart.StorageIndexRevisionMapping, revision, null, revisionElement, NextSerial()));
                }
                // Restore the complete historical map. Keys introduced since that
                // revision need explicit nil values for clients that know them.
                var restoredKeys = updateMappings.Select(mapping => (mapping.Type, mapping.Revision, mapping.Cell)).ToHashSet();
                foreach (var obsolete in currentMappings.Select(RestoreMapping))
                    if (!restoredKeys.Contains((obsolete.Type, obsolete.Revision, obsolete.Cell)))
                        updateMappings.Add(new(obsolete.Type, obsolete.Revision, obsolete.Cell, ExGuid.Null, NextSerial()));
                var proposedIndex = NextId();
                var expectedIndex = NextId();
                incoming.DataElements.Add(new(DataElementType.StorageIndexDataElementData, proposedIndex, NextSerial())
                { Data = StorageIndexPatch.Serialize(updateMappings) });
                incoming.DataElements.Add(new(DataElementType.StorageIndexDataElementData, expectedIndex, NextSerial())
                { Data = StorageIndexPatch.Serialize(currentMappings.Select(RestoreMapping)) });
                var keys = updateMappings.Select(mapping => new IndexedMappingKey(
                    mapping.Type == StreamObjectTypeHeaderStart.StorageIndexManifestMapping ? IndexedMappingKind.Manifest :
                    mapping.Type == StreamObjectTypeHeaderStart.StorageIndexCellMapping ? IndexedMappingKind.Cell : IndexedMappingKind.Revision,
                    mapping.Cell is null ? null : IndexedDocumentGraph.Capture(mapping.Cell),
                    mapping.Revision is null ? null : IndexedDocumentGraph.Capture(mapping.Revision))).ToImmutableArray();
                var preparation = await provider.Documents.PrepareSaveAsync(resourceId,
                    new(before.Kind, operation, keys) { IncludeBindings = true, IncludeCatalog = true }, cancellationToken) ?? throw MissingDocument();
                if (preparation.Control.Head.StateVersion != current.Head.StateVersion) { plans.Clear(); break; }
                var request = new FsshttpbCellSubRequest(RequestTypes.PutChanges)
                { RequestId = 1, Data = new PutChangesSubRequestData { StorageIndex = proposedIndex, ExpectedStorageIndex = expectedIndex } };
                var admission = Admission(actor, current.Head);
                IndexedPreparedSave prepared;
                try
                {
                    prepared = await IndexedPartitionSavePreparer.PrepareAsync(preparation, request, incoming, admission,
                        attributes, provider.Documents, stagingContent,
                        provider.Limits, cancellationToken, new DocumentWriteContext(operation, digest) { ExpectedRevisionNumber = expectedRevision });
                }
                catch (StagingAdmissionChangedException) { plans.Clear(); break; }
                if (prepared.ErrorCode is not null || prepared.Publication is null)
                {
                    // Another copy can publish after the coherent preparation
                    // read. Resolve its receipt before classifying stale input.
                    if (await RestoreReplayAsync(cancellationToken) is { } racedReplay) return racedReplay;
                    var latest = await CurrentAsync(resourceId, cancellationToken);
                    if (latest.StateVersion != current.Head.StateVersion) { plans.Clear(); break; }
                    if (prepared.ErrorCode is { } lockError) throw new DocumentOperationLockException(lockError);
                    throw new InvalidOperationException("Historical graph preparation failed: " +
                        prepared.Response.SubResponses.FirstOrDefault()?.Error?.ErrorMessage);
                }
                var publication = prepared.Publication;
                plans.AddRange(publication.Partitions);
                if (before.Kind == 0) package = publication.Package;
            }
            if (plans.Count != selected.Partitions.Length || package is null) continue;
            if (package.Length != selected.Package.Length || package.Sha256 != selected.Package.Sha256)
                throw new StorageCorruptionException("Restored graph does not reproduce the selected historical file.");
            var number = checked(current.Head.LatestRevisionNumber + 1);
            var revisionRecord = new DocumentRevisionRecord(resourceId, head.LifecycleGeneration, number,
                checked(current.Head.ContentVersion + 1), current.UtcNow, actor.Identity, package,
                current.Partitions.Where(partition => partition.Kind != 2)
                    .Select(partition => plans.Single(plan => plan.After.Kind == partition.Kind).After).ToImmutableArray());
            var receipt = new DocumentOperationReceipt(operation, digest, actor.Identity.Subject, revisionRecord.ContentVersion, number, null, null);
            var combined = new PreparedDocumentPublication(resourceId, Admission(actor, current.Head),
                new(head.LifecycleGeneration, current.Head.AuthorityGeneration, current.Coordination.Generation,
                    current.Head.StateVersion, current.Head.ContentVersion, []) { ExpectedRevisionNumber = expectedRevision, PreparedCoordination = originalAuthority }, receipt, plans.ToImmutable())
            {
                Package = package, Revision = revisionRecord, LockAttributes = attributes.ToImmutableDictionary(StringComparer.Ordinal),
                RequestedModifiedUtc = current.UtcNow, DeliveryOperationId = Guid.NewGuid(),
            };
            try
            {
                var result = await stagingContent.PublishAsync(combined, cancellationToken);
                if (result.Status == PublicationStatus.Reprepare) continue;
                if (result.Status == PublicationStatus.Replay) return await RestoreReplayAsync(cancellationToken) ?? throw new StorageCorruptionException("Missing restore receipt.");
                if (result.Status == PublicationStatus.ProtocolError) throw new DocumentOperationLockException(result.ErrorCode ?? "CoherencyFailure");
                return new(revisionRecord, false);
            }
            catch (Exception exception) when (exception is StorageUnavailableException || exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                if (await RestoreReplayAsync(CancellationToken.None) is { } accepted) return accepted;
                throw;
            }
        }
        throw new InvalidOperationException("The document changed while preparing restore.");

        async ValueTask<RestoreRevisionResult?> RestoreReplayAsync(CancellationToken ct)
        {
            var read = await provider.Documents.ReadReceiptAsync(resourceId, operation, ct) ?? throw MissingDocument();
            Demand(actor, read.Current, DocumentAccess.Write);
            if (read.Receipt is not { } accepted) return null;
            if (accepted.OwnerSubject != actor.Identity.Subject || accepted.Digest != digest)
                throw new InvalidOperationException("The restore key was reused with different arguments or actor.");
            return new(await GetRevisionAsync(resourceId, accepted.RevisionNumber, actor, ct), true);
        }
    }


}

public sealed class DocumentOperationLockException(string errorCode) : InvalidOperationException(errorCode)
{
    public string ErrorCode { get; } = errorCode;
}
