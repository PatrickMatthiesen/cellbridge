using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

internal static partial class FilePartitionSaveHandler
{
    // The catalog describes all history. Selected contains only fully populated elements
    // needed for this file revision, verified indexes and incoming identity checks.
    internal sealed record SaveCatalog(IReadOnlyCollection<DataElement> Metadata,
        IReadOnlyCollection<DataElement> Indexes, ExGuid StorageIndex, long PayloadBytes,
        IReadOnlyDictionary<ExGuid, DataElement> Verified, PartitionGraphSnapshot Selected)
    {
        internal bool ConflictsWith(DataElement element) => Verified.TryGetValue(element.DataElementExtendedGuid, out var old) &&
            (old.DataElementType != element.DataElementType || !(old.Data ?? []).SequenceEqual(element.Data ?? []));
    }

    internal static async ValueTask<PreparedFileSave> PrepareAsync(DocumentState state, StoredDocument document,
        FsshttpbCellSubRequest request, DataElementPackage package, IContentStore content, StorageLimits limits,
        CancellationToken cancellationToken, bool forceFullGraph = false)
    {
        var catalog = forceFullGraph ? null : await TryReadCatalogAsync(state, request, package, content, limits, cancellationToken);
        if (catalog is not null)
            return Prepare(document, document.FilePartition, request, package, limits, catalog: catalog);
        var graph = await DocumentPartition.RestoreFileGraphAsync(state, content, limits, cancellationToken);
        return Prepare(document, document.FilePartition, request, package, limits, graph);
    }

    private static async ValueTask<SaveCatalog?> TryReadCatalogAsync(DocumentState state,
        FsshttpbCellSubRequest request, DataElementPackage package, IContentStore content, StorageLimits limits,
        CancellationToken cancellationToken)
    {
        if (request.Data is not PutChangesSubRequestData put) return null;
        var source = state.Partitions.Single(p => p.Kind == 0);
        if (source.StorageIndex is null || StorageIds.Restore(source.StorageIndex).Equals(put.StorageIndex)) return null;
        IReadOnlyCollection<ExGuid>? groups;
        try
        {
            // Incoming serials are reassigned by Prepare, including duplicate IDs.
            groups = PartitionGraphSnapshot.ReadFileSaveObjectGroups(package.DataElements.Select(e =>
                new DataElement(e.DataElementType, e.DataElementExtendedGuid, SerialNumber.Null) { Data = e.Data }), put.StorageIndex);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException or OverflowException)
        { return null; }
        if (groups is null) return null;
        limits.CheckDocument(state);
        _ = ContentStoreReader.UniqueHandles(StorageReferences.Handles(state));
        if (state.Content.Length != source.Content.Length || state.Content.Sha256 != source.Content.Sha256)
            throw new StorageCorruptionException("Document and file partition content disagree.");
        var incomingIds = package.DataElements.Select(e => e.DataElementExtendedGuid).ToHashSet();
        var groupIds = groups.ToHashSet();
        var selected = source.Elements.Where(e => e.Type == (uint)DataElementType.StorageIndexDataElementData ||
            incomingIds.Contains(StorageIds.Restore(e.Id)) || groupIds.Contains(StorageIds.Restore(e.Id))).ToArray();
        var payloads = await content.ReadManyVerifiedAsync(selected.Select(e => e.Payload), limits.MaxObjectBytes, cancellationToken);
        var verified = selected.Select(e => new DataElement((DataElementType)e.Type, StorageIds.Restore(e.Id),
            new SerialNumber(e.Serial.Guid, e.Serial.Value)) { Data = payloads[e.Payload.Key] })
            .ToDictionary(e => e.DataElementExtendedGuid);
        var indexes = verified.Values.Where(e => e.DataElementType == DataElementType.StorageIndexDataElementData).ToArray();
        if (!indexes.Any(e => e.DataElementExtendedGuid.Equals(StorageIds.Restore(source.StorageIndex)))) return null;
        // Read actual index bytes, rather than relying on cached mapping-serial metadata.
        var existing = source.Elements.ToDictionary(e => StorageIds.Restore(e.Id));
        try
        {
            var elements = new Dictionary<ExGuid, DataElement>(verified);
            foreach (var incoming in package.DataElements)
            {
                if (verified.TryGetValue(incoming.DataElementExtendedGuid, out var old) &&
                    (old.DataElementType != incoming.DataElementType || !(old.Data ?? []).SequenceEqual(incoming.Data ?? []))) return null;
                elements[incoming.DataElementExtendedGuid] = new DataElement(incoming.DataElementType,
                    incoming.DataElementExtendedGuid, existing.TryGetValue(incoming.DataElementExtendedGuid, out var stored)
                        ? new SerialNumber(stored.Serial.Guid, stored.Serial.Value) : SerialNumber.Null) { Data = incoming.Data };
            }
            var graph = PartitionGraphSnapshot.Create(elements.Values, put.StorageIndex);
            if (!graph.IsSelfContainedFileGraph()) return null;
            return new SaveCatalog(source.Elements.Select(e => new DataElement((DataElementType)e.Type,
                    StorageIds.Restore(e.Id), new SerialNumber(e.Serial.Guid, e.Serial.Value))).ToArray(),
                indexes, StorageIds.Restore(source.StorageIndex), source.Elements.Sum(e => e.Payload.Length), verified, graph);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException or OverflowException)
        { return null; }
    }
}
