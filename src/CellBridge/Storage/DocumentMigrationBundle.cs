using System.Collections.Immutable;
using System.Security.Cryptography;
using CellBridge.FssHttpB;

namespace CellBridge.Storage.Abstractions;

public sealed record MigratedDocumentRevision(DocumentRevisionRecord Revision,
    ImmutableArray<PreparedPartitionPublication> Partitions);

/// <summary>An explicit offline conversion. Providers retain old recovery snapshots separately.</summary>
public sealed record DocumentMigrationBundle(PreparedDocumentCreate Current,
    ImmutableArray<MigratedDocumentRevision> Revisions,
    ImmutableArray<DocumentOperationReceipt> Receipts,
    ImmutableArray<DocumentDelivery> Deliveries,
    ImmutableArray<string> RetiredPathKeys)
{
    public ImmutableArray<ContentHandle> PhysicalReferences { get; init; } = [];
    /// <summary>Preserve these existing objects during activation; ordinary downloads use canonical graph roots.</summary>
    public ImmutableArray<ContentHandle> LegacyPackageReferences { get; init; } = [];
}

/// <summary>Backfills normalized records from a validated aggregate with services stopped.</summary>
public static class LegacyDocumentProjection
{
    public static async ValueTask<DocumentMigrationBundle> PrepareAsync(DocumentState state, IContentStore content,
        StorageLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(content);
        limits.Validate();
        limits.CheckDocument(state);
        if (state.FormatVersion != DocumentState.CurrentFormat || state.ResourceId == Guid.Empty || state.LifecycleGeneration <= 0)
            throw new StorageCorruptionException("Unsupported legacy document identity or format.");
        var history = state.Revisions.IsEmpty
            ? ImmutableArray.Create(new DocumentRevision(state.ResourceId, state.LifecycleGeneration,
                Math.Max(1UL, state.ContentVersion), state.ContentVersion, state.ModifiedUtc,
                state.Security.ModifiedBy ?? state.Security.CreatedBy ?? new("unknown", "unknown", "unknown"),
                state.Content, state.Partitions.Where(partition => partition.Kind != 2).ToImmutableArray()))
            : state.Revisions;
        if (history.Select(revision => revision.RevisionNumber).Distinct().Count() != history.Length ||
            history.Any(revision => revision.ResourceId != state.ResourceId || revision.LifecycleGeneration != state.LifecycleGeneration || revision.RevisionNumber == 0) ||
            !history.Select(revision => revision.RevisionNumber).SequenceEqual(history.Select(revision => revision.RevisionNumber).Order()))
            throw new StorageCorruptionException("Legacy application revision identities are inconsistent.");
        var converter = new Converter(state.ResourceId, state.LifecycleGeneration, content, limits, cancellationToken);
        var revisions = ImmutableArray.CreateBuilder<MigratedDocumentRevision>();
        long sequence = 0;
        foreach (var revision in history)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prepared = await converter.PartitionsAsync(revision.Partitions, checked(++sequence));
            var package = await converter.PackageAsync(revision.RevisionNumber, revision.Content, prepared);
            var record = new DocumentRevisionRecord(revision.ResourceId, revision.LifecycleGeneration, revision.RevisionNumber,
                revision.ContentVersion, revision.CreatedUtc, revision.Author, package,
                prepared.Select(partition => partition.After).ToImmutableArray());
            revisions.Add(new(record, prepared));
        }
        var currentPartitions = await converter.PartitionsAsync(state.Partitions, checked(++sequence));
        var currentPackage = await converter.PackageAsync(history[^1].RevisionNumber, state.Content, currentPartitions);
        if (history[^1].ContentVersion != state.ContentVersion || history[^1].Content.Length != state.Content.Length ||
            history[^1].Content.Sha256 != state.Content.Sha256)
            throw new StorageCorruptionException("Current file metadata disagrees with the latest application revision.");
        // Current protocol metadata can have a later import publication token after control changes.
        // Downloads remain bound to the one immutable package recorded by the latest application revision.
        var latestPackage = revisions[^1].Revision.Package;
        if (currentPackage.Root.Cell != latestPackage.Root.Cell)
            throw new StorageCorruptionException("Current file cell disagrees with the latest immutable application revision.");
        currentPackage = latestPackage;

        var receipts = ImmutableArray.CreateBuilder<DocumentOperationReceipt>();
        foreach (var receipt in state.Receipts)
        {
            var accepted = history.FirstOrDefault(revision => revision.ContentVersion == receipt.ContentVersion &&
                (receipt.PartitionKind != 1 || receipt.AcceptedStorageIndex is null ||
                 revision.Partitions.Any(partition => partition.Kind == 1 && partition.StorageIndex == receipt.AcceptedStorageIndex)));
            if (accepted is null) throw new StorageCorruptionException("Legacy save receipt has no retained application revision.");
            var acceptedRoot = receipt.AcceptedStorageIndex;
            if (receipt.PartitionKind == 1 && acceptedRoot is not null)
            {
                var acceptedPartitions = state.Partitions.Any(partition => partition.Kind == 1 && partition.StorageIndex == acceptedRoot)
                    ? currentPartitions
                    : revisions.Single(revision => revision.Revision.RevisionNumber == accepted.RevisionNumber).Partitions;
                acceptedRoot = acceptedPartitions.Single(partition => partition.After.Kind == 1).After.StorageIndex;
            }
            receipts.Add(new(new(receipt.LifecycleGeneration, SaveOperationFamily.Protocol, receipt.PartitionKind, receipt.OperationKey),
                receipt.Digest, receipt.OwnerSubject, receipt.ContentVersion, accepted.RevisionNumber,
                acceptedRoot, receipt.Response));
            if (receipt.Response is { } response) await converter.VerifyPhysicalAsync(response);
        }
        foreach (var receipt in state.RestoreReceipts)
        {
            var accepted = history.SingleOrDefault(revision => revision.RevisionNumber == receipt.RevisionNumber)
                ?? throw new StorageCorruptionException("Legacy restore receipt has no retained application revision.");
            receipts.Add(new(new(receipt.LifecycleGeneration, SaveOperationFamily.Restore, 0, receipt.OperationKey),
                receipt.Digest, receipt.OwnerSubject, accepted.ContentVersion, accepted.RevisionNumber, null, null));
        }
        foreach (var receipt in state.WholeFileSaveReceipts)
        {
            var accepted = history.SingleOrDefault(revision => revision.RevisionNumber == receipt.RevisionNumber)
                ?? throw new StorageCorruptionException("Legacy whole-file receipt has no retained application revision.");
            receipts.Add(new(new(receipt.LifecycleGeneration, SaveOperationFamily.WholeFile, 0, receipt.OperationKey),
                receipt.Digest, receipt.OwnerSubject, accepted.ContentVersion, accepted.RevisionNumber, null, null));
        }
        var deliveries = ImmutableArray.CreateBuilder<DocumentDelivery>();
        if (state.Publication is { } publication)
            foreach (var delivery in publication.Pending)
            {
                var accepted = revisions.FirstOrDefault(candidate => candidate.Revision.ContentVersion == delivery.ContentVersion &&
                    candidate.Revision.Package.Length == delivery.Content.Length && candidate.Revision.Package.Sha256 == delivery.Content.Sha256)
                    ?? throw new StorageCorruptionException("Legacy delivery has no retained application revision.");
                if (delivery.ResourceId != state.ResourceId || delivery.LifecycleGeneration != state.LifecycleGeneration)
                    throw new StorageCorruptionException("Legacy delivery identity disagrees with its document.");
                deliveries.Add(new(delivery.OperationId, delivery.ResourceId, delivery.LifecycleGeneration, delivery.Sequence,
                    delivery.ContentVersion, accepted.Revision.Package));
            }
        var retiredPathKeys = state.RetiredPathKeys.Where(key => key != state.PathKey)
            .Distinct(StringComparer.Ordinal).ToImmutableArray();
        var counters = new DocumentCounters
        {
            GraphElements = currentPartitions.Sum(partition => partition.After.ElementCount),
            GraphBytes = currentPartitions.Sum(partition => partition.After.ElementBytes),
            Revisions = revisions.Count, SaveReceipts = state.Receipts.Length,
            RestoreReceipts = state.RestoreReceipts.Length, WholeFileSaveReceipts = state.WholeFileSaveReceipts.Length,
            RetiredPaths = retiredPathKeys.Length, PendingDeliveries = deliveries.Count,
            PendingDeliveryBytes = deliveries.Sum(delivery => delivery.Package.Length),
            // Canonical physical references are exact. Providers add their normalized metadata-row bytes
            // while importing; obsolete aggregate/package objects are preserved outside this counter.
            StoredBytes = converter.PhysicalReferences.Values.Sum(handle => handle.Length),
        };
        var head = new DocumentHead(state.ResourceId, state.Path, state.PathKey, state.CreatedUtc, state.ModifiedUtc,
            state.ContentVersion, state.StateVersion, state.LifecycleGeneration, state.IsDeleted, currentPackage,
            state.Security, history[^1].RevisionNumber, counters)
        {
            AuthorityGeneration = state.Coordination.Generation, CoordinationGeneration = state.Coordination.Generation,
            DeletedFromStateVersion = state.DeletedFromStateVersion, ReplacedBy = state.ReplacedBy,
            NextEditorExpiryUtc = state.Editors.IsEmpty ? null : state.Editors.Min(editor => editor.ExpiresUtc),
            Delivery = state.Publication is { } binding ? new(binding.BindingId, binding.Destination,
                binding.ExpectedRevision, binding.NextSequence, binding.BlockedReason) : null,
        };
        return new(new(head, state.Coordination, state.Editors, currentPartitions, revisions[0].Revision),
            revisions.ToImmutable(), receipts.ToImmutable(), deliveries.ToImmutable(), retiredPathKeys)
        {
            PhysicalReferences = converter.PhysicalReferences.Values.ToImmutableArray(),
            LegacyPackageReferences = converter.LegacyPackages.Values.ToImmutableArray(),
        };
    }

    private sealed class Converter(Guid resourceId, long lifecycle, IContentStore content, StorageLimits limits,
        CancellationToken cancellationToken)
    {
        private readonly Dictionary<(int Partition, ExtendedId Id), GraphElementState> identities = [];
        private readonly Dictionary<(int Partition, ExtendedId Id), IndexedElementMetadata> metadata = [];
        private readonly Dictionary<string, byte[]> payloads = new(StringComparer.Ordinal);
        private readonly Dictionary<ExtendedId, ExtendedId> legacyIndexes = [];
        public Dictionary<string, ContentHandle> PhysicalReferences { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ContentHandle> LegacyPackages { get; } = new(StringComparer.Ordinal);

        public async ValueTask<ImmutableArray<PreparedPartitionPublication>> PartitionsAsync(
            ImmutableArray<PartitionState> partitions, long sequence)
        {
            if (partitions.Select(partition => partition.Kind).Distinct().Count() != partitions.Length ||
                !partitions.Any(partition => partition.Kind == 0))
                throw new StorageCorruptionException("Legacy partition identities are inconsistent.");
            var result = ImmutableArray.CreateBuilder<PreparedPartitionPublication>();
            foreach (var partition in partitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scope = new IndexedGraphScope(resourceId, lifecycle, partition.Kind);
                var legacyElements = partition.Elements.ToImmutableArray();
                var elements = legacyElements.Where(element => element.Type != (uint)DataElementType.StorageIndexDataElementData).ToImmutableArray();
                var records = new List<IndexedElementMetadata>();
                foreach (var element in legacyElements)
                {
                    var key = (partition.Kind, element.Id);
                    if (identities.TryGetValue(key, out var existing)) IndexedDocumentGraph.ValidateIdentity(existing, element);
                    else identities.Add(key, element);
                    if (!metadata.TryGetValue(key, out var record))
                    {
                        var bytes = await BytesAsync(element.Payload);
                        record = IndexedDocumentGraph.ReadMetadata(scope, new((DataElementType)element.Type,
                            IndexedDocumentGraph.Restore(element.Id), new(element.Serial.Guid, element.Serial.Value)) { Data = bytes });
                        metadata.Add(key, record);
                        if (element.Type == (uint)DataElementType.StorageIndexDataElementData) PhysicalReferences.Remove(element.Payload.Key);
                    }
                    records.Add(record);
                }
                var source = new Source(records);
                var bindings = ImmutableArray.CreateBuilder<IndexedObjectBinding>();
                if (partition.StorageIndex is { } index)
                {
                    var selectedMappings = source.Mappings.Where(mapping => mapping.StorageIndex == index).ToArray();
                    foreach (var mapping in selectedMappings.Where(mapping => mapping.Key.Kind == IndexedMappingKind.Cell && !IsNull(mapping.Target)))
                    {
                        if (mapping.Key.Cell is not { } cell || !source.Cells.TryGetValue(mapping.Target, out var manifest))
                            throw new StorageCorruptionException("A legacy cell mapping is unresolved.");
                        var resolved = partition.Kind == 0
                            ? await IndexedDocumentGraph.ResolveFileCellAsync(scope, index, cell, manifest.RevisionId, source,
                                new(MaxElements: limits.MaxGraphElements, MaxPayloadBytes: limits.MaxGraphBytes), cancellationToken)
                            : await IndexedDocumentGraph.ResolveCellAsync(scope, index, cell, manifest.RevisionId, source,
                                new(MaxElements: limits.MaxGraphElements, MaxPayloadBytes: limits.MaxGraphBytes), cancellationToken);
                        bindings.AddRange(resolved.Bindings);
                    }
                }
                else if (!elements.IsEmpty) throw new StorageCorruptionException("A legacy graph has no selected storage index.");
                ExtendedId? logicalIndex = partition.StorageIndex is null ? null : new(1, Guid.NewGuid());
                if (logicalIndex is not null) legacyIndexes.Add(logicalIndex, partition.StorageIndex!);
                var head = new PartitionHead(partition.Kind, partition.Identity, partition.Knowledge,
                    logicalIndex, sequence, partition.InlineContent.IsDefault ? [] : partition.InlineContent)
                { StorageIndexSerial = partition.StorageIndex is { } selectedIndex ? legacyElements.Single(element => element.Id == selectedIndex).Serial : null,
                    ElementCount = elements.Length, ElementBytes = elements.Sum(element => element.Payload.Length) };
                var resolvedBindings = bindings.ToImmutable();
                result.Add(new(head with { StorageIndex = null, PublicationSequence = 0, Knowledge = 0, ElementCount = 0, ElementBytes = 0, InlineContent = [] },
                    head, elements, [], records.SelectMany(record => record.Mappings).ToImmutableArray(),
                    records.Where(record => record.Revision is not null).Select(record => record.Revision!).ToImmutableArray(),
                    records.SelectMany(record => record.Objects).ToImmutableArray(),
                    IndexedDocumentGraph.Difference(scope, 0, sequence, [], resolvedBindings))
                {
                    Checkpoint = IndexedDocumentGraph.Checkpoint(scope, sequence, resolvedBindings),
                    Cells = records.Where(record => record.CellManifest is not null).Select(record => record.CellManifest!).ToImmutableArray(),
                    StorageManifests = records.Where(record => record.StorageManifest is not null).Select(record => record.StorageManifest!).ToImmutableArray(),
                    MappingChanges = records.SelectMany(record => record.Mappings).Where(mapping => mapping.StorageIndex == partition.StorageIndex)
                        .Select(mapping => new IndexedMappingChange(mapping.Key, IsNull(mapping.Target) ? null : new(mapping.Target, mapping.Serial))).ToImmutableArray(),
                });
            }
            return result.ToImmutable();
        }

        public async ValueTask<DocumentPackage> PackageAsync(ulong number, ContentHandle original,
            ImmutableArray<PreparedPartitionPublication> partitions)
        {
            var file = partitions.Single(partition => partition.After.Kind == 0);
            if (file.After.StorageIndex is not { } index) throw new StorageCorruptionException("Missing legacy file storage index.");
            var fileElements = file.AddedElements.Select(element => new DataElement((DataElementType)element.Type,
                IndexedDocumentGraph.Restore(element.Id), new(element.Serial.Guid, element.Serial.Value))
                { Data = payloads[element.Payload.Key] }).ToArray();
            var legacyRawIndex = legacyIndexes[index];
            var legacyIndex = identities[(0, legacyRawIndex)];
            var indexElement = new DataElement(DataElementType.StorageIndexDataElementData, IndexedDocumentGraph.Restore(legacyRawIndex),
                new(legacyIndex.Serial.Guid, legacyIndex.Serial.Value)) { Data = payloads[legacyIndex.Payload.Key] };
            var graph = PartitionGraphSnapshot.Create(fileElements.Append(indexElement), IndexedDocumentGraph.Restore(legacyRawIndex));
            var root = new DocumentPackageRoot(new(resourceId, lifecycle, 0), index,
                IndexedDocumentGraph.Capture(graph.FileCell), IndexedDocumentGraph.Capture(graph.Revision ??
                    throw new StorageCorruptionException("Missing legacy file revision.")),
                IndexedDocumentGraph.Capture(graph.RootObject ?? throw new StorageCorruptionException("Missing legacy file root.")));
            StorageLimits.Check("document bytes", original.Length, limits.MaxDocumentBytes);
            // Hash the authoritative existing materializer's output without retaining another package buffer.
            await using (var verified = new HashSink(limits.MaxDocumentBytes))
            {
                await graph.MaterializeToAsync(verified, limits.MaxDocumentBytes, cancellationToken);
                verified.Verify(original);
            }
            var resolvedFile = await IndexedDocumentGraph.LoadFileAsync(root, file.Checkpoint!.Bindings,
                ReadElementAsync, cancellationToken);
            await using (var verified = new HashSink(limits.MaxDocumentBytes))
            {
                await resolvedFile.MaterializeToAsync(verified, limits.MaxDocumentBytes, cancellationToken);
                verified.Verify(original);
            }
            if (!LegacyPackages.TryGetValue(original.Key, out var known))
            {
                await using var stored = await content.OpenReadAsync(original, cancellationToken);
                await using var verified = new HashSink(limits.MaxDocumentBytes);
                await stored.CopyToAsync(verified, cancellationToken);
                verified.Verify(original);
                LegacyPackages.Add(original.Key, original);
            }
            else if (known != original) throw new StorageCorruptionException("Legacy package handle identity disagrees.");
            return new(resourceId, lifecycle, number, DocumentPackage.CurrentReconstructionVersion, root,
                original.Length, original.Sha256);

            ValueTask<DataElement> ReadElementAsync(IndexedGraphScope scope, ExtendedId id, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                if (scope != root.Scope) throw new StorageCorruptionException("Cross-scope migration payload lookup.");
                return ValueTask.FromResult(fileElements.Single(element => IndexedDocumentGraph.Capture(element.DataElementExtendedGuid) == id));
            }
        }

        public async ValueTask VerifyPhysicalAsync(ContentHandle handle) => _ = await BytesAsync(handle);

        private async ValueTask<byte[]> BytesAsync(ContentHandle handle)
        {
            if (PhysicalReferences.TryGetValue(handle.Key, out var known) && known != handle)
                throw new StorageCorruptionException("Physical payload handle identity disagrees.");
            if (!payloads.TryGetValue(handle.Key, out var bytes))
            {
                bytes = await content.ReadVerifiedAsync(handle, limits.MaxObjectBytes, cancellationToken);
                payloads.Add(handle.Key, bytes);
                PhysicalReferences.Add(handle.Key, handle);
            }
            return bytes;
        }
        private static bool IsNull(ExtendedId id) => id.Value == 0 && id.Guid == Guid.Empty;
    }

    private sealed class Source(IEnumerable<IndexedElementMetadata> records) : IIndexedGraphSource
    {
        public ImmutableArray<IndexedStorageMapping> Mappings { get; } = records.SelectMany(record => record.Mappings).ToImmutableArray();
        public Dictionary<ExtendedId, IndexedCellManifest> Cells { get; } = records.Where(record => record.CellManifest is not null)
            .Select(record => record.CellManifest!).ToDictionary(record => record.ElementId);
        private readonly Dictionary<ExtendedId, IndexedRevisionManifest> revisions = records.Where(record => record.Revision is not null)
            .Select(record => record.Revision!).ToDictionary(record => record.ElementId);
        private readonly Dictionary<ExtendedId, ImmutableArray<IndexedObjectDefinition>> objects = records.Where(record => record.ObjectGroupElement is not null)
            .ToDictionary(record => record.ObjectGroupElement!, record => record.Objects);
        public ValueTask<IndexedStorageMapping?> FindMappingAsync(IndexedGraphScope scope, ExtendedId storageIndex,
            IndexedMappingKey key, CancellationToken cancellationToken = default) => ValueTask.FromResult(Mappings.SingleOrDefault(
                mapping => mapping.Scope == scope && mapping.StorageIndex == storageIndex && mapping.Key == key));
        public ValueTask<IndexedRevisionManifest?> ReadRevisionAsync(IndexedGraphScope scope, ExtendedId manifest,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(revisions.GetValueOrDefault(manifest));
        public ValueTask<ImmutableArray<IndexedObjectDefinition>> ReadGroupAsync(IndexedGraphScope scope, ExtendedId group,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(objects.GetValueOrDefault(group));
    }

    private sealed class HashSink(long limit) : Stream
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long length;
        public void Verify(ContentHandle expected)
        {
            if (length != expected.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != expected.Sha256)
                throw new StorageCorruptionException("Legacy graph or package bytes disagree with the recorded length or digest.");
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > limit - length) throw new StorageCorruptionException("Legacy package exceeds its byte limit.");
            hash.AppendData(buffer); length += buffer.Length;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
        protected override void Dispose(bool disposing) { if (disposing) hash.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get => length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
