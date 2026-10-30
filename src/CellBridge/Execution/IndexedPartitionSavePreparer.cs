using CellBridge.Diagnostics;
using System.Collections.Immutable;
using System.Security.Cryptography;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public sealed record IndexedPreparedSave(FsshttpbResponse Response, PreparedDocumentPublication? Publication = null,
    string? ErrorCode = null);

/// <summary>Trusted host write identity, supplied by whole-file and restore entry points.</summary>
public sealed record DocumentWriteContext(SaveOperationIdentity Operation, string Digest, string? HostLockToken = null)
{
    public ulong? ExpectedRevisionNumber { get; init; }
}

/// <summary>Prepares one typed publication from current bindings and targeted immutable graph reads.</summary>
public static class IndexedPartitionSavePreparer
{
    public static (SaveOperationIdentity Operation, string Digest) Identify(long lifecycle, int partitionKind,
        FsshttpbCellSubRequest operation, DataElementPackage package)
    {
        if (operation.Data is not PutChangesSubRequestData put) throw new InvalidDataException("Expected PutChanges data.");
        var key = (partitionKind == 1 ? "metadata:" : "") + $"{put.StorageIndex.Value}:{put.StorageIndex.Guid:D}";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var writer = new BinaryWriterEx();
        put.StorageIndex.Serialize(writer); put.ExpectedStorageIndex.Serialize(writer);
        writer.WriteByte((byte)(put.Flags & 1)); writer.WriteUInt16((ushort)(put.AdditionalFlagsBits & 7));
        hash.AppendData(writer.ToArray());
        foreach (var element in OwnElements(package).Values.OrderBy(element => element.DataElementExtendedGuid.Guid)
                     .ThenBy(element => element.DataElementExtendedGuid.Value))
        {
            writer = new(); element.DataElementExtendedGuid.Serialize(writer);
            writer.WriteUInt32((uint)element.DataElementType); element.SerialNumber.Serialize(writer);
            writer.WriteUInt64((ulong)element.Data!.LongLength);
            hash.AppendData(writer.ToArray()); hash.AppendData(element.Data);
        }
        return (new(lifecycle, SaveOperationFamily.Protocol, partitionKind, key), Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    public static async ValueTask<IndexedPreparedSave> PrepareAsync(SavePreparationRead preparation,
        FsshttpbCellSubRequest operation, DataElementPackage package, DocumentActorAdmission admission,
        IReadOnlyDictionary<string, string> attributes, IIndexedSaveSource source, IContentStore content,
        StorageLimits limits, CancellationToken cancellationToken = default, DocumentWriteContext? writeContext = null,
        Func<DataElementPackage?, bool>? canAppend = null)
    {
        using var timing = CellBridgeMetrics.Measure(CellBridgePhase.GraphPreparation);
        var control = preparation.Control;
        var before = preparation.Partition.Partition;
        var head = control.Head;
        if (before.Kind is not (0 or 1) || operation.Data is not PutChangesSubRequestData put || put.StorageIndex.IsNull ||
            (put.Flags & ~0x79) != 0 || (put.AdditionalFlagsBits & 0x38) != 0 ||
            before.Kind != 0 && CellSubRequestDataValidation.TryGetBoolean(attributes, "Coalesce", out bool coalesce) && coalesce)
            return Reject(operation.RequestId, CellErrorCode.RequestNotSupported, "Only complete file and application metadata uploads are supported.");
        var writeError = writeContext?.Operation.Family == SaveOperationFamily.WholeFile
            ? DocumentControlReducer.ValidateWholeFile(control, admission, writeContext.HostLockToken)
            : DocumentControlReducer.ValidateWrite(control, admission, attributes);
        if (writeError is { } lockError)
            return new(new(), ErrorCode: lockError);
        try
        {
            limits.Validate();
            var incoming = OwnElements(package);
            if (incoming.Values.Any(element => element.DataElementType is not (DataElementType.StorageIndexDataElementData or
                DataElementType.StorageManifestDataElementData or DataElementType.CellManifestDataElementData or
                DataElementType.RevisionManifestDataElementData or DataElementType.ObjectGroupDataElementData or
                DataElementType.ObjectDataBLOBDataElementData)))
                return Reject(operation.RequestId, CellErrorCode.RequestNotSupported, "Partial or unknown graph elements are not supported.");
            var identity = writeContext is null ? Identify(head.LifecycleGeneration, before.Kind, operation, package)
                : (Operation: writeContext.Operation, Digest: writeContext.Digest);
            if (identity.Operation.LifecycleGeneration != head.LifecycleGeneration || identity.Operation.Family != SaveOperationFamily.Restore && identity.Operation.PartitionKind != before.Kind)
                throw new ArgumentException("The trusted write identity belongs to another document incarnation or partition.");
            var scope = new IndexedGraphScope(head.ResourceId, head.LifecycleGeneration, before.Kind);
            var currentSource = new PreparedGraphSource(scope, before.StorageIndex, preparation.Partition.Mappings, source);
            var descriptors = await source.ReadElementsAsync(scope, incoming.Keys.ToArray(), cancellationToken);
            var existing = descriptors.ToDictionary(element => element.Id);
            var loader = new Loader(scope, source, content, limits, incoming, descriptors);
            if (!incoming.TryGetValue(IndexedDocumentGraph.Capture(put.StorageIndex), out var proposed))
                return Reject(operation.RequestId, CellErrorCode.ReferencedDataElementNotFound, "Missing uploaded storage index.");
            var updates = StorageIndexPatch.Read(proposed);
            var currentMappings = new List<StorageIndexMapping>();
            foreach (var update in updates)
                if (before.StorageIndex is { } currentIndex &&
                    await currentSource.FindMappingAsync(scope, currentIndex, Key(update), cancellationToken) is { } current)
                    currentMappings.Add(Restore(current));
            IReadOnlyList<StorageIndexMapping> expected = [];
            if (!put.ExpectedStorageIndex.IsNull)
            {
                var expectedId = IndexedDocumentGraph.Capture(put.ExpectedStorageIndex);
                if (!incoming.TryGetValue(expectedId, out var expectedElement))
                    return Reject(operation.RequestId, CellErrorCode.ReferencedDataElementNotFound, "Missing expected storage index in request package.");
                expected = StorageIndexPatch.Read(expectedElement);
            }
            if (!StorageIndexPatch.IsCoherent(currentMappings, updates, expected, (put.Flags & 1) != 0))
                return Reject(operation.RequestId, CellErrorCode.CoherencyFailure, "An affected storage-index mapping is no longer coherent.");
            var addedIds = incoming.Where(pair => pair.Value.DataElementType != DataElementType.StorageIndexDataElementData &&
                !existing.ContainsKey(pair.Key)).Select(pair => pair.Key).ToArray();
            StorageLimits.Check("graph elements", head.Counters.GraphElements + (long)addedIds.Length, limits.MaxGraphElements);
            StorageLimits.Check("graph bytes", head.Counters.GraphBytes + addedIds.Sum(id => incoming[id].Data!.LongLength), limits.MaxGraphBytes);
            // MS-FSSHTTPB 2.2.2.1.4 applies only the selected index. Expected mappings
            // constrain target coherency; they do not introduce durable serial claims.
            var claims = await source.ReadMappingClaimsAsync(scope, updates.Where(mapping => !mapping.Serial.IsNull)
                .Select(mapping => new SerialId(mapping.Serial.Guid, mapping.Serial.Value)).Distinct().ToArray(), cancellationToken);
            ValidateMappingClaims(updates, claims);
            ulong knowledge = before.Knowledge;
            foreach (var serial in incoming.Values.Select(element => element.SerialNumber).Concat(updates.Select(mapping => mapping.Serial)))
                if (serial.Guid == before.Identity.SerialGuid) knowledge = Math.Max(knowledge, serial.Value);
            var admitted = new List<DataElement>();
            foreach (var (id, element) in incoming.ToArray())
            {
                StorageLimits.Check("graph object bytes", element.Data!.LongLength, limits.MaxObjectBytes);
                SerialNumber serial;
                if (existing.TryGetValue(id, out var old))
                {
                    if (old.Type != (uint)element.DataElementType || old.Payload.Length != element.Data.LongLength ||
                        old.Payload.Sha256 != Convert.ToHexStringLower(SHA256.HashData(element.Data)))
                        return Reject(operation.RequestId, CellErrorCode.InvalidObject, "An immutable element identity was reused for different content.");
                    serial = new(old.Serial.Guid, old.Serial.Value);
                }
                else serial = new(before.Identity.SerialGuid, checked(++knowledge));
                incoming[id] = WithSerial(element, serial);
                admitted.Add(incoming[id]);
            }
            if (writeContext is null && addedIds.Length == 0 && before.StorageIndex == IndexedDocumentGraph.Capture(put.StorageIndex) &&
                updates.All(update => currentMappings.FirstOrDefault(mapping => Key(mapping) == Key(update)) is { } current &&
                    current.Target.Equals(update.Target) && current.Serial.Equals(update.Serial)))
            {
                // A client can resend the current complete index before it has a receipt.
                // Validate immutable identities and mapping claims above, then preserve the current version.
                var unchanged = new DataElement(DataElementType.StorageIndexDataElementData, put.StorageIndex,
                    before.StorageIndexSerial is { } existingSerial ? new(existingSerial.Guid, existingSerial.Value) : SerialNumber.Null)
                    { Data = proposed.Data!.ToArray() };
                var known = existing.Values.Select(element => element.Serial).Concat(currentMappings.Select(mapping => new SerialId(mapping.Serial.Guid,mapping.Serial.Value)))
                    .Append(new(unchanged.SerialNumber.Guid,unchanged.SerialNumber.Value)).Where(serial => serial.Guid != Guid.Empty)
                    .Distinct().Select(serial => new GraphSerialRange(serial.Guid,serial.Value,serial.Value)).ToImmutableArray();
                return new(Response(operation.RequestId,before,put,unchanged,[],[],known,before.Knowledge));
            }
            var proposedWithSerial = incoming[IndexedDocumentGraph.Capture(put.StorageIndex)];
            // Request envelope identities belong to retry receipts. Every accepted map has a server-owned logical token.
            var selected = new DataElement(DataElementType.StorageIndexDataElementData, new(1, Guid.NewGuid()),
                proposedWithSerial.SerialNumber) { Data = proposedWithSerial.Data };
            var index = IndexedDocumentGraph.Capture(selected.DataElementExtendedGuid);
            var records = incoming.Values.Where(element => element.DataElementType != DataElementType.StorageIndexDataElementData ||
                element.DataElementExtendedGuid.Equals(put.StorageIndex))
                .Select(element => IndexedDocumentGraph.ReadMetadata(scope, element)).ToArray();
            var overlay = new CandidateSource(scope, index, before.StorageIndex, updates,
                new IndexedGraphOverlaySource(scope, records, currentSource), currentSource);
            var manifestMapping = await overlay.FindMappingAsync(scope, index, new(IndexedMappingKind.Manifest), cancellationToken);
            ProtocolStorageManifestRecord? storageManifest = null;
            if (manifestMapping is not null && !IsNull(manifestMapping.Target))
                storageManifest = IndexedGraphRecords.ReadStorageManifest(await loader.ReadAsync(manifestMapping.Target, cancellationToken));
            if (storageManifest is null)
            {
                if (before.Kind == 0) throw new InvalidDataException("Missing storage manifest.");
                var retained = before.StorageIndex is null ? [] :
                    await source.ReadMappingsAsync(scope, before.PublicationSequence, cancellationToken);
                var effectiveMappings = retained.ToDictionary(mapping => mapping.Key, mapping => mapping.Target);
                foreach (var update in updates) effectiveMappings[Key(update)] = IndexedDocumentGraph.Capture(update.Target);
                if (effectiveMappings.Any(mapping => mapping.Key.Kind != IndexedMappingKind.Manifest && !IsNull(mapping.Value)))
                    throw new InvalidDataException("A graph without a storage manifest cannot contain active cell or revision mappings.");
            }
            var cells = preparation.Partition.Bindings.Select(binding => binding.Key.Cell)
                .Concat(updates.Where(mapping => mapping.Cell is not null).Select(mapping => IndexedDocumentGraph.Capture(mapping.Cell!)))
                .Concat(storageManifest?.Roots.Select(root => IndexedDocumentGraph.Capture(root.Cell)) ?? []).Distinct().ToArray();
            // A changed existing revision mapping may alter an ancestor of any current cell.
            // New revision keys cannot alter an already resolved base. Only redirections need a cold replay.
            var redirectsRevision = updates.Any(update => update.Revision is not null &&
                currentMappings.FirstOrDefault(mapping => Key(mapping) == Key(update)) is { } old &&
                !old.Target.IsNull && !old.Target.Equals(update.Target));
            var bindings = ImmutableArray.CreateBuilder<IndexedObjectBinding>();
            var resolvedCells = new Dictionary<IndexedCellId, IndexedResolvedCell>();
            using (CellBridgeMetrics.Measure(CellBridgePhase.GraphResolve))
            {
                foreach (var cell in cells)
                {
                    var mapping = await overlay.FindMappingAsync(scope, index, new(IndexedMappingKind.Cell, Cell: cell), cancellationToken);
                    if (mapping is null || IsNull(mapping.Target)) continue;
                    var revision = IndexedDocumentGraph.Capture(IndexedGraphRecords.ReadCellRevision(
                        await loader.ReadAsync(mapping.Target, cancellationToken)));
                    var currentCell = before.StorageIndex is { } previousIndex
                        ? await currentSource.FindMappingAsync(scope, previousIndex, new(IndexedMappingKind.Cell, Cell: cell), cancellationToken) : null;
                    IndexedResolvedCell resolved;
                    if (!redirectsRevision && currentCell is not null && !IsNull(currentCell.Target))
                    {
                        var oldRevision = IndexedDocumentGraph.Capture(IndexedGraphRecords.ReadCellRevision(
                            await loader.ReadStoredAsync(currentCell.Target, cancellationToken)));
                        var baseline = new IndexedResolvedCell(scope, cell, oldRevision, [],
                            preparation.Partition.Bindings.Where(binding => binding.Key.Cell == cell).ToImmutableArray());
                        resolved = await IndexedDocumentGraph.ResolveCellWithCurrentAsync(scope, index, cell, revision, overlay, baseline,
                            before.Kind == 0, limits.GraphTraversalLimits(), cancellationToken);
                    }
                    else resolved = before.Kind == 0
                        ? await IndexedDocumentGraph.ResolveFileCellAsync(scope, index, cell, revision, overlay,
                            limits.GraphTraversalLimits(), cancellationToken)
                        : await IndexedDocumentGraph.ResolveCellAsync(scope, index, cell, revision, overlay,
                            limits.GraphTraversalLimits(), cancellationToken);
                    resolvedCells.Add(cell, resolved); bindings.AddRange(resolved.Bindings);
                }
                if (storageManifest is not null)
                    foreach (var storageRoot in storageManifest.Roots)
                    {
                        var rootCell = IndexedDocumentGraph.Capture(storageRoot.Cell);
                        if (!resolvedCells.TryGetValue(rootCell, out var rootRevision) ||
                            !rootRevision.Roots.Any(root => root.Root == IndexedDocumentGraph.Capture(storageRoot.Root)))
                            throw new InvalidDataException("A storage root does not resolve to a declared cell revision root.");
                    }
                await ValidateNewReferencesAsync(scope, index, records, updates, addedIds, loader, overlay, before.Kind == 1, cancellationToken);
            }
            var effectiveBindings = bindings.ToImmutable();
            DocumentPackage? packageDescriptor = null;
            if (before.Kind == 0)
            {
                using var materializationTiming = CellBridgeMetrics.Measure(CellBridgePhase.PackageMaterialization);
                if (storageManifest is null || storageManifest.Schema != StorageManifestBuilder.StorageManifestSchemaGuid)
                    throw new InvalidDataException("Missing or unsupported file storage manifest.");
                var storageRoot = storageManifest.Roots.FirstOrDefault(root =>
                    root.Root.Guid == StorageManifestBuilder.RootExtendedGuid && root.Root.Value == 2)
                    ?? throw new InvalidDataException("Missing distinguished file storage root.");
                var fileCell = IndexedDocumentGraph.Capture(storageRoot.Cell);
                if (!resolvedCells.TryGetValue(fileCell, out var resolved)) throw new InvalidDataException("Unmapped file cell.");
                var root = resolved.Roots.Single(root => root.Root == IndexedDocumentGraph.Capture(storageRoot.Root));
                var packageRoot = new DocumentPackageRoot(scope, index, fileCell, resolved.Revision, root.Object);
                var file = await IndexedDocumentGraph.LoadFileAsync(packageRoot, effectiveBindings,
                    (_, id, ct) => loader.ReadAsync(id, ct), cancellationToken);
                await using var scratch = OpenScratch();
                try { await file.MaterializeToAsync(scratch, limits.MaxDocumentBytes, cancellationToken); }
                catch (GraphMaterializationLimitException)
                { throw new StorageQuotaExceededException("document bytes", checked(limits.MaxDocumentBytes + 1), limits.MaxDocumentBytes); }
                await OfficePackageValidation.ValidateDocumentAsync(scratch, head.Path, limits.MaxDocumentBytes, cancellationToken);
                scratch.Position = 0;
                packageDescriptor = new(head.ResourceId, head.LifecycleGeneration,
                    checked(head.LatestRevisionNumber + 1), 1, packageRoot, scratch.Length,
                    Convert.ToHexStringLower(await SHA256.HashDataAsync(scratch, cancellationToken)));
            }
            var required = new HashSet<ExtendedId>();
            if (writeContext?.Operation.Family == SaveOperationFamily.Restore && storageManifest is not null)
            {
                // Restore is an explicit cold operation. Include every wrapper in its selected
                // historical ancestry, even when intervening inline metadata retired that catalog.
                foreach (var cell in resolvedCells.Values)
                    required.UnionWith(await IndexedDocumentGraph.RequiredElementsAsync(scope,index,cell,overlay,
                        (_,id,ct) => loader.ReadAsync(id,ct),limits.GraphTraversalLimits(),cancellationToken));
            }
            // Ordinary saves reuse IDs visited while resolving and validating their candidate.
            // This does not walk older base chains already represented by current bindings.
            foreach (var group in overlay.ReferencedGroups.Concat(effectiveBindings.Select(binding => binding.GroupElement)).Distinct().ToArray())
                _ = await overlay.ReadGroupAsync(scope,group,cancellationToken);
            required.UnionWith(overlay.ReferencedElements);
            required.UnionWith(loader.LoadedIds);
            required.UnionWith(incoming.Where(pair => pair.Value.DataElementType != DataElementType.StorageIndexDataElementData).Select(pair => pair.Key));
            required.Remove(index);
            var visibleIds = (await source.ReadVisibleElementIdsAsync(scope,before.PublicationSequence,required.ToArray(),cancellationToken)).ToHashSet();
            var activationIds = required.Where(id => !visibleIds.Contains(id) && !incoming.ContainsKey(id)).ToArray();
            var reactivated = await source.ReadElementsAsync(scope,activationIds,cancellationToken);
            if (reactivated.Length != activationIds.Length) throw new InvalidDataException("Missing retained candidate closure.");
            var retainedRecords = new List<IndexedElementMetadata>();
            foreach (var descriptor in reactivated)
            {
                existing.Add(descriptor.Id,descriptor);
                retainedRecords.Add(IndexedDocumentGraph.ReadMetadata(scope,await loader.ReadAsync(descriptor.Id,cancellationToken)));
            }
            records = records.Concat(retainedRecords).ToArray();
            // Non-coalescing PutChanges returns conservative knowledge of accepted records only.
            // The protocol prohibits complete knowledge here; whole QueryChanges builds its own current knowledge.
            var ranges = IndexedDocumentGraph.NormalizeRanges(reactivated.Select(element => element.Serial)
                .Concat(incoming.Values.Where(element => element.DataElementType != DataElementType.StorageIndexDataElementData)
                    .Select(element => new SerialId(element.SerialNumber.Guid, element.SerialNumber.Value)))
                .Select(serial => new GraphSerialRange(serial.Guid, serial.Value, serial.Value))
                .Concat(updates.Where(mapping => !mapping.Serial.IsNull).Select(mapping => new GraphSerialRange(mapping.Serial.Guid, mapping.Serial.Value, mapping.Serial.Value)))
                .Append(new(selected.SerialNumber.Guid, selected.SerialNumber.Value, selected.SerialNumber.Value)));
            var response = Response(operation.RequestId, before, put, selected, admitted, addedIds, ranges, knowledge);
            if (canAppend is not null && !canAppend(response.DataElementPackage))
                return Reject(operation.RequestId, CellErrorCode.RequestNotSupported, "The save response exceeds the combined response budget.");
            var responseBytes = response.ToByteArray();
            StorageLimits.Check("save receipt bytes", responseBytes.LongLength, limits.MaxObjectBytes);
            var modified = control.UtcNow;
            if (attributes.ContainsKey("LastModifiedTime") && !CellSubRequestDataValidation.TryGetLastModifiedTime(attributes, out modified))
                return new(response, ErrorCode: "InvalidArgument");
            // Reject known publication limits before reserving or writing physical content.
            StorageLimits.Check("history revisions", head.Counters.Revisions + 1L, limits.MaxHistoryRevisions);
            switch (identity.Operation.Family)
            {
                case SaveOperationFamily.Protocol:
                    StorageLimits.Check("save receipts", head.Counters.SaveReceipts + 1L, limits.MaxSaveReceipts);
                    break;
                case SaveOperationFamily.WholeFile:
                    StorageLimits.Check("whole-file save receipts", head.Counters.WholeFileSaveReceipts + 1L, limits.MaxWholeFileSaveReceipts);
                    break;
                case SaveOperationFamily.Restore:
                    StorageLimits.Check("restore receipts", head.Counters.RestoreReceipts + 1L, limits.MaxRestoreReceipts);
                    break;
            }
            if (before.Kind == 0 && head.Delivery is not null)
            {
                StorageLimits.Check("pending external revisions", head.Counters.PendingDeliveries + 1L, limits.MaxPendingExternalRevisions);
                StorageLimits.Check("pending external bytes", checked(head.Counters.PendingDeliveryBytes + packageDescriptor!.Length), limits.MaxPendingExternalBytes);
            }
            // These arrays are owned by this preparation. Validate the candidate before staging,
            // then let the content writer bound each physical transaction independently of publication.
            var newElements = incoming.Where(pair => pair.Value.DataElementType != DataElementType.StorageIndexDataElementData &&
                !existing.ContainsKey(pair.Key)).ToArray();
            var payloads = newElements.Select(pair => (ReadOnlyMemory<byte>)pair.Value.Data!).ToList();
            var storesResponse = identity.Operation.Family == SaveOperationFamily.Protocol;
            if (storesResponse) payloads.Add(responseBytes);
            IReadOnlyList<ContentHandle> newHandles;
            using (CellBridgeMetrics.Measure(CellBridgePhase.ContentStaging))
                newHandles = await content.WriteManyAsync(payloads, cancellationToken);
            // Protocol acknowledgements share staging with canonical elements. Trusted whole-file
            // and restore operations retain their typed receipt without a binary response object.
            ContentHandle? responseHandle = storesResponse ? newHandles[^1] : null;
            var handlesById = newElements.Select((pair, ordinal) => (pair.Key, Handle: newHandles[ordinal]))
                .ToDictionary(pair => pair.Key, pair => pair.Handle);
            var staged = ImmutableArray.CreateBuilder<GraphElementState>();
            staged.AddRange(reactivated);
            foreach (var (id, element) in incoming.Where(pair => pair.Value.DataElementType != DataElementType.StorageIndexDataElementData))
            {
                var handle = existing.TryGetValue(id, out var old) ? old.Payload : handlesById[id];
                staged.Add(new(id, (uint)element.DataElementType, new(element.SerialNumber.Guid, element.SerialNumber.Value), handle)
                {
                    MappingSerials = element.DataElementType == DataElementType.StorageIndexDataElementData
                        ? StorageIndexMappingSerials.Read(element.Data!).Select(serial => new SerialId(serial.Guid, serial.Value)).ToImmutableArray() : [],
                });
            }
            var stagedElements = staged.ToImmutable();
            var sequence = checked(before.PublicationSequence + 1);
            var newlyStored = stagedElements.Where(element => !existing.ContainsKey(element.Id)).ToArray();
            var newlyVisible = stagedElements.Where(element => !visibleIds.Contains(element.Id)).ToArray();
            var after = before with { StorageIndex = index, StorageIndexSerial = new(selected.SerialNumber.Guid, selected.SerialNumber.Value), Knowledge = knowledge, PublicationSequence = sequence, InlineContent = [],
                ElementCount = checked(before.ElementCount + newlyVisible.Length),
                ElementBytes = checked(before.ElementBytes + newlyVisible.Sum(element => element.Payload.Length)) };
            var delta = IndexedDocumentGraph.Difference(scope, before.PublicationSequence, sequence,
                preparation.Partition.Bindings, effectiveBindings);
            var partition = new PreparedPartitionPublication(before, after, stagedElements, [],
                records.SelectMany(record => record.Mappings).ToImmutableArray(),
                records.Where(record => record.Revision is not null).Select(record => record.Revision!).ToImmutableArray(),
                records.SelectMany(record => record.Objects).ToImmutableArray(), delta)
            {
                Cells = records.Where(record => record.CellManifest is not null).Select(record => record.CellManifest!).ToImmutableArray(),
                StorageManifests = records.Where(record => record.StorageManifest is not null).Select(record => record.StorageManifest!).ToImmutableArray(),
                MappingChanges = updates.Select(mapping => new IndexedMappingChange(Key(mapping), new(IndexedDocumentGraph.Capture(mapping.Target), new(mapping.Serial.Guid, mapping.Serial.Value)))).ToImmutableArray(),
                Checkpoint = IndexedDocumentGraph.RequiresCheckpoint(preparation.ReplayPosition, delta, IndexedCheckpointPolicy.Default)
                    ? IndexedDocumentGraph.Checkpoint(scope, sequence, effectiveBindings) : null,
            };
            var revisionNumber = checked(head.LatestRevisionNumber + 1);
            var version = before.Kind == 0 ? checked(head.ContentVersion + 1) : head.ContentVersion;
            var packageForRevision = packageDescriptor ?? head.Package with { RevisionNumber = revisionNumber };
            var changedPartitions = ImmutableArray.Create(partition);
            if (before.Kind == 0 && writeContext?.Operation.Family != SaveOperationFamily.Restore &&
                control.Partitions.Single(value => value.Kind == 1) is { StorageIndex: null } metadata)
            {
                var metadataScope = scope with { TargetPartition = 1 };
                var metadataSequence = checked(metadata.PublicationSequence + 1);
                var inline = System.Text.Encoding.UTF8.GetBytes(FormattableString.Invariant(
                    $"<Metadata ContentVersion=\"{version}\" Modified=\"{modified.Ticks}\" />")).ToImmutableArray();
                StorageLimits.Check("metadata partition bytes", inline.Length, limits.MaxObjectBytes);
                var metadataAfter = metadata with { InlineContent = inline, Knowledge = checked(metadata.Knowledge + 1),
                    PublicationSequence = metadataSequence };
                changedPartitions = changedPartitions.Add(new(metadata,metadataAfter,[],[],[],[],[],
                    IndexedDocumentGraph.Difference(metadataScope,metadata.PublicationSequence,metadataSequence,[],[]))
                    { Checkpoint = IndexedDocumentGraph.Checkpoint(metadataScope,metadataSequence,[]) });
            }
            var application = new DocumentRevisionRecord(head.ResourceId, head.LifecycleGeneration, revisionNumber,
                version, modified, admission.Actor, packageForRevision,
                control.Partitions.Where(value => value.Kind != 2).Select(value => changedPartitions.SingleOrDefault(change => change.After.Kind == value.Kind)?.After ?? value).ToImmutableArray());
            var expectedKeys = expected.Select(Key).ToHashSet();
            var expectations = updates.Select(update => new MappingExpectation(Key(update),
                currentMappings.FirstOrDefault(mapping => Key(mapping) == Key(update)) is { } actual
                    ? Capture(scope, before.StorageIndex!, actual) : null)
                { EnforceCoherency = expectedKeys.Contains(Key(update)) || (put.Flags & 1) != 0 }).ToImmutableArray();
            var fence = new DocumentPublicationFence(head.LifecycleGeneration, head.AuthorityGeneration, control.Coordination.Generation,
                head.StateVersion, head.ContentVersion, expectations) { ExpectedEtag = attributes.GetValueOrDefault("Etag"), ExpectedRevisionNumber = writeContext?.ExpectedRevisionNumber,
                PreparedCoordination = DocumentControlReducer.ExpireForPublication(control).Current.Coordination };
            var receipt = new DocumentOperationReceipt(identity.Operation, identity.Digest, admission.Actor.Subject,
                version, revisionNumber, before.Kind == 1 ? index : null, responseHandle);
            return new(response, new(head.ResourceId, admission, fence, receipt, changedPartitions)
            {
                Package = packageForRevision, Revision = application, RequestedModifiedUtc = modified,
                LockAttributes = attributes.ToImmutableDictionary(StringComparer.Ordinal),
                HostLockToken = writeContext?.HostLockToken,
                NewDocumentReferences = stagedElements.Select(element => element.Payload).Concat(responseHandle is null ? [] : new[] { responseHandle }).DistinctBy(handle => handle.Key).ToImmutableArray(),
                CounterChange = new() { GraphElements = newlyStored.Length, GraphBytes = newlyStored.Sum(element => element.Payload.Length) },
                DeliveryOperationId = before.Kind == 0 ? Guid.NewGuid() : null,
            });
        }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or ArgumentException or OverflowException)
        { return Reject(operation.RequestId, CellErrorCode.InvalidObject, error.Message); }
    }

    private static Dictionary<ExtendedId, DataElement> OwnElements(DataElementPackage package)
    {
        var result = new Dictionary<ExtendedId, DataElement>();
        foreach (var element in package.DataElements)
        {
            if (element.DataElementExtendedGuid.IsNull || element.Data is null) throw new InvalidDataException("Graph elements need an identifier and payload.");
            var id = IndexedDocumentGraph.Capture(element.DataElementExtendedGuid);
            if (result.TryGetValue(id, out var old))
            {
                if (old.DataElementType != element.DataElementType || !old.SerialNumber.Equals(element.SerialNumber) || !old.Data!.SequenceEqual(element.Data))
                    throw new InvalidDataException("Conflicting duplicate graph elements.");
                continue;
            }
            result.Add(id, new(element.DataElementType, IndexedDocumentGraph.Restore(id),
                new(element.SerialNumber.Guid, element.SerialNumber.Value)) { Data = element.Data.ToArray() });
        }
        return result;
    }

    private static DataElement WithSerial(DataElement element, SerialNumber serial) =>
        new(element.DataElementType, element.DataElementExtendedGuid, serial) { Data = element.Data };

    private static IndexedMappingKey Key(StorageIndexMapping mapping) => new(
        mapping.Type == StreamObjectTypeHeaderStart.StorageIndexManifestMapping ? IndexedMappingKind.Manifest :
        mapping.Type == StreamObjectTypeHeaderStart.StorageIndexCellMapping ? IndexedMappingKind.Cell : IndexedMappingKind.Revision,
        mapping.Cell is null ? null : IndexedDocumentGraph.Capture(mapping.Cell),
        mapping.Revision is null ? null : IndexedDocumentGraph.Capture(mapping.Revision));
    private static IndexedStorageMapping Capture(IndexedGraphScope scope, ExtendedId index, StorageIndexMapping mapping) =>
        new(scope, index, Key(mapping), IndexedDocumentGraph.Capture(mapping.Target), new(mapping.Serial.Guid, mapping.Serial.Value));

    private static bool IsNull(ExtendedId id) => id.Guid == Guid.Empty && id.Value == 0;
    private static StorageIndexMapping Restore(IndexedStorageMapping mapping) => new(
        mapping.Key.Kind == IndexedMappingKind.Manifest ? StreamObjectTypeHeaderStart.StorageIndexManifestMapping :
        mapping.Key.Kind == IndexedMappingKind.Cell ? StreamObjectTypeHeaderStart.StorageIndexCellMapping : StreamObjectTypeHeaderStart.StorageIndexRevisionMapping,
        mapping.Key.Revision is { } revision ? IndexedDocumentGraph.Restore(revision) : null,
        mapping.Key.Cell is { } cell ? IndexedDocumentGraph.Restore(cell) : null,
        IndexedDocumentGraph.Restore(mapping.Target), new(mapping.Serial.Guid, mapping.Serial.Value));

    // Preparation contains only affected keys. Missing entries still require a provider lookup;
    // successful lookups (including absence) are stable under this immutable logical index.
    private sealed class PreparedGraphSource(IndexedGraphScope scope, ExtendedId? index,
        ImmutableArray<IndexedStorageMapping> prepared, IIndexedGraphSource source) : IIndexedGraphSource
    {
        private readonly Dictionary<IndexedMappingKey, IndexedStorageMapping?> mappings = prepared
            .Where(mapping => mapping.Scope == scope && mapping.StorageIndex == index)
            .ToDictionary(mapping => mapping.Key, mapping => (IndexedStorageMapping?)mapping);

        public async ValueTask<IndexedStorageMapping?> FindMappingAsync(IndexedGraphScope requestedScope, ExtendedId selectedIndex,
            IndexedMappingKey key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requestedScope != scope || selectedIndex != index)
                return await source.FindMappingAsync(requestedScope, selectedIndex, key, cancellationToken);
            if (mappings.TryGetValue(key, out var value)) return value;
            value = await source.FindMappingAsync(scope, selectedIndex, key, cancellationToken);
            mappings.Add(key, value);
            return value;
        }

        // Successful immutable metadata can be shared by resolution and closure validation.
        // Keep the provider's arrays directly; absent records are never memoized.
        private Dictionary<ExtendedId, IndexedRevisionManifest>? revisions;
        private Dictionary<ExtendedId, ImmutableArray<IndexedObjectDefinition>>? groups;

        public async ValueTask<IndexedRevisionManifest?> ReadRevisionAsync(IndexedGraphScope requestedScope, ExtendedId manifest,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requestedScope != scope)
                return await source.ReadRevisionAsync(requestedScope, manifest, cancellationToken);
            if (revisions is not null && revisions.TryGetValue(manifest, out var cached)) return cached;
            var value = await source.ReadRevisionAsync(scope, manifest, cancellationToken);
            if (value is not null) (revisions ??= []).Add(manifest, value);
            return value;
        }
        public async ValueTask<ImmutableArray<IndexedObjectDefinition>> ReadGroupAsync(IndexedGraphScope requestedScope, ExtendedId group,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requestedScope != scope)
                return await source.ReadGroupAsync(requestedScope, group, cancellationToken);
            if (groups is not null && groups.TryGetValue(group, out var cached)) return cached;
            var value = await source.ReadGroupAsync(scope, group, cancellationToken);
            if (!value.IsDefault) (groups ??= []).Add(group, value);
            return value;
        }
    }

    private sealed class CandidateSource(IndexedGraphScope scope, ExtendedId index, ExtendedId? previousIndex,
        IReadOnlyList<StorageIndexMapping> updates, IIndexedGraphSource metadata, IIndexedGraphSource current) : IIndexedGraphSource
    {
        private readonly Dictionary<IndexedMappingKey, StorageIndexMapping> changed = updates.ToDictionary(Key);
        public HashSet<ExtendedId> ReferencedElements { get; } = [];
        public HashSet<ExtendedId> ReferencedGroups { get; } = [];
        public async ValueTask<IndexedStorageMapping?> FindMappingAsync(IndexedGraphScope requestedScope, ExtendedId selectedIndex,
            IndexedMappingKey key, CancellationToken cancellationToken = default)
        {
            if (requestedScope != scope || selectedIndex != index)
                return await current.FindMappingAsync(requestedScope, selectedIndex, key, cancellationToken);
            if (changed.TryGetValue(key, out var update)) return update.Target.IsNull ? null : Capture(scope, index, update);
            var value = previousIndex is null ? null : await current.FindMappingAsync(scope, previousIndex, key, cancellationToken);
            return value is null ? null : value with { StorageIndex = index };
        }
        public async ValueTask<IndexedRevisionManifest?> ReadRevisionAsync(IndexedGraphScope scope, ExtendedId manifest, CancellationToken cancellationToken = default)
        {
            var revision = await metadata.ReadRevisionAsync(scope,manifest,cancellationToken);
            if (revision is not null)
            {
                ReferencedElements.Add(revision.ElementId);
                ReferencedElements.UnionWith(revision.ObjectGroups);
                ReferencedGroups.UnionWith(revision.ObjectGroups);
            }
            return revision;
        }
        public async ValueTask<ImmutableArray<IndexedObjectDefinition>> ReadGroupAsync(IndexedGraphScope scope, ExtendedId group, CancellationToken cancellationToken = default)
        {
            var objects = await metadata.ReadGroupAsync(scope,group,cancellationToken);
            ReferencedElements.Add(group);
            ReferencedGroups.Add(group);
            if (!objects.IsDefault)
                foreach (var obj in objects) if (obj.BlobElement is { } blob) ReferencedElements.Add(blob);
            return objects;
        }
    }

    private static void ValidateMappingClaims(IEnumerable<StorageIndexMapping> incoming, IEnumerable<IndexedStorageMapping> stored)
    {
        var meanings = new Dictionary<SerialId, (IndexedMappingKey Key, ExtendedId Target)>();
        foreach (var mapping in stored)
        {
            var meaning = (mapping.Key, mapping.Target);
            if (meanings.TryGetValue(mapping.Serial, out var prior) && prior != meaning)
                throw new InvalidDataException("Persisted mapping serial identities disagree.");
            meanings[mapping.Serial] = meaning;
        }
        foreach (var mapping in incoming.Where(mapping => !mapping.Serial.IsNull))
        {
            var serial = new SerialId(mapping.Serial.Guid, mapping.Serial.Value);
            var meaning = (Key(mapping), IndexedDocumentGraph.Capture(mapping.Target));
            if (meanings.TryGetValue(serial, out var previous) && previous != meaning)
                throw new InvalidDataException("A mapping serial was reused for a different mapping.");
            meanings[serial] = meaning;
        }
    }

    private static async ValueTask ValidateNewReferencesAsync(IndexedGraphScope scope, ExtendedId index,
        IndexedElementMetadata[] records, IReadOnlyList<StorageIndexMapping> mappings, ExtendedId[] addedIds,
        Loader loader, IIndexedGraphSource source, bool complete, CancellationToken cancellationToken)
    {
        var rooted = mappings.Where(mapping => !mapping.Target.IsNull).Select(mapping => IndexedDocumentGraph.Capture(mapping.Target)).ToHashSet();
        foreach (var mapping in mappings.Where(mapping => mapping.Revision is not null && !mapping.Target.IsNull))
        {
            var target = IndexedDocumentGraph.Capture(mapping.Target);
            var revision = await source.ReadRevisionAsync(scope, target, cancellationToken);
            if (revision is null || revision.Scope != scope || revision.ElementId != target ||
                revision.RevisionId != IndexedDocumentGraph.Capture(mapping.Revision!))
                throw new InvalidDataException("A revision mapping does not match its immutable target.");
        }
        foreach (var record in records.Where(record => record.Revision is not null))
        {
            var revision = record.Revision!;
            var mapping = await source.FindMappingAsync(scope, index, new(IndexedMappingKind.Revision, Revision: revision.RevisionId), cancellationToken);
            if (mapping?.Target != revision.ElementId) continue;
            rooted.Add(revision.ElementId);
            foreach (var group in revision.ObjectGroups)
            {
                rooted.Add(group);
                var objects = await source.ReadGroupAsync(scope, group, cancellationToken);
                if (objects.IsDefault) throw new InvalidDataException("Missing revision object group.");
                foreach (var obj in objects)
                    if (obj.BlobElement is { } blob)
                    {
                        rooted.Add(blob);
                        _ = IndexedGraphRecords.ReadBlob(await loader.ReadAsync(blob, cancellationToken));
                    }
            }
        }
        if (complete && addedIds.Any(id => !rooted.Contains(id)))
            throw new InvalidDataException("A complete metadata upload contains an unrooted new element.");
    }

    private static FsshttpbResponse Response(ulong requestId, PartitionHead partition, PutChangesSubRequestData put,
        DataElement selected, IEnumerable<DataElement> admitted, IEnumerable<ExtendedId> added,
        ImmutableArray<GraphSerialRange> ranges, ulong knowledge)
    {
        var applied = new PutChangesResponse { AppliedStorageIndexID = (put.AdditionalFlagsBits & 1) != 0
            ? selected.DataElementExtendedGuid : ExGuid.Null };
        applied.DataElementAdded.AddRange(added.Select(IndexedDocumentGraph.Restore));
        var knowledgeBytes = BinaryKnowledgeBuilder.FromRanges(ranges.Select(range => (range.Namespace, range.First, range.Last)),
            IndexedDocumentGraph.Restore(partition.Identity.CellShort), 0);
        var response = new FsshttpbResponse
        {
            SubResponses = { new() { RequestId = requestId, RequestType = RequestTypes.PutChanges,
                Data = new PutChangesSubResponseData { PutChangesResponse = applied,
                    SerialNumberReassignAllBytes = PutChangesSerialReassignments.Serialize(admitted), KnowledgeBytes = knowledgeBytes } } },
        };
        if ((put.AdditionalFlagsBits & 1) != 0)
        {
            response.DataElementPackage = new();
            response.DataElementPackage.DataElements.Add(selected);
        }
        return response;
    }

    private static IndexedPreparedSave Reject(ulong requestId, CellErrorCode code, string message) => new(new()
    { SubResponses = { new() { RequestId = requestId, RequestType = RequestTypes.PutChanges, Status = true,
        Error = new(ErrorType.Cell, (ulong)code, message) } } });

    private static FileStream OpenScratch()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite,
            Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose, BufferSize = 65536 };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new(Path.Combine(Path.GetTempPath(), "cellbridge-indexed-save-" + Guid.NewGuid().ToString("N")), options);
    }

    private sealed class Loader(IndexedGraphScope scope, IIndexedSaveSource source, IContentStore content,
        StorageLimits limits, Dictionary<ExtendedId, DataElement> incoming, IEnumerable<GraphElementState> known)
    {
        private readonly Dictionary<ExtendedId, GraphElementState> descriptors = known.ToDictionary(element => element.Id);
        private readonly Dictionary<ExtendedId, DataElement> loaded = [];
        public IEnumerable<ExtendedId> LoadedIds => loaded.Keys;
        public ValueTask<DataElement> ReadAsync(ExtendedId id, CancellationToken ct) => incoming.TryGetValue(id, out var element)
            ? ValueTask.FromResult(element) : ReadStoredAsync(id, ct);
        public async ValueTask<DataElement> ReadStoredAsync(ExtendedId id, CancellationToken ct)
        {
            if (loaded.TryGetValue(id, out var element)) return element;
            if (!descriptors.TryGetValue(id, out var descriptor))
            {
                descriptor = (await source.ReadElementsAsync(scope, [id], ct)).SingleOrDefault(value => value.Id == id)
                    ?? throw new InvalidDataException("Missing referenced immutable graph element.");
                descriptors.Add(id, descriptor);
            }
            var bytes = await content.ReadVerifiedAsync(descriptor.Payload, limits.MaxObjectBytes, ct);
            element = new((DataElementType)descriptor.Type, IndexedDocumentGraph.Restore(id),
                new(descriptor.Serial.Guid, descriptor.Serial.Value)) { Data = bytes };
            loaded.Add(id, element);
            return element;
        }
    }
}
