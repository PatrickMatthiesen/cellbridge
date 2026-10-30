using System.Collections.Immutable;
using System.Security.Cryptography;
using CellBridge.FssHttpB;

namespace CellBridge.Storage.Abstractions;

public static partial class IndexedDocumentGraph
{
    /// <summary>Extracts metadata for one newly admitted immutable element, without loading its ancestors.</summary>
    public static IndexedElementMetadata ReadMetadata(IndexedGraphScope scope, DataElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var id = Capture(element.DataElementExtendedGuid);
        switch (element.DataElementType)
        {
            case DataElementType.StorageIndexDataElementData:
                var mappings = StorageIndexMappingSerials.ReadMappings(element.Data ?? []).Select(mapping =>
                    new IndexedStorageMapping(scope, id, new(
                        mapping.Type == StreamObjectTypeHeaderStart.StorageIndexManifestMapping ? IndexedMappingKind.Manifest :
                        mapping.Type == StreamObjectTypeHeaderStart.StorageIndexCellMapping ? IndexedMappingKind.Cell : IndexedMappingKind.Revision,
                        mapping.Cell is null ? null : Capture(mapping.Cell),
                        mapping.Revision is null ? null : Capture(mapping.Revision)), Capture(mapping.Target),
                        new SerialId(mapping.Serial.Guid, mapping.Serial.Value))).ToImmutableArray();
                if (mappings.Select(m => m.Key).Distinct().Count() != mappings.Length)
                    throw new InvalidDataException("Duplicate storage-index mapping key.");
                return new() { Mappings = mappings };
            case DataElementType.StorageManifestDataElementData:
                var storage = IndexedGraphRecords.ReadStorageManifest(element);
                return new() { StorageManifest = new(scope, id, storage.Schema,
                    storage.Roots.Select(root => new IndexedStorageRoot(Capture(root.Root), Capture(root.Cell))).ToImmutableArray()) };
            case DataElementType.CellManifestDataElementData:
                return new() { CellManifest = new(scope, id, Capture(IndexedGraphRecords.ReadCellRevision(element))) };
            case DataElementType.RevisionManifestDataElementData:
                var revision = IndexedGraphRecords.ReadRevision(element);
                return new() { Revision = new(scope, id, Capture(revision.Revision), Capture(revision.BaseRevision),
                    revision.Roots.Select(root => new IndexedRevisionRoot(Capture(root.Root), Capture(root.Object))).ToImmutableArray(),
                    revision.ObjectGroups.Select(Capture).ToImmutableArray()) };
            case DataElementType.ObjectGroupDataElementData:
                var objects = ObjectGroupDataElement.ParseOpaque(element).Objects;
                return new() { ObjectGroupElement = id, Objects = objects.Select((obj, ordinal) => new IndexedObjectDefinition(scope, id,
                    ordinal, Capture(obj.ObjectGuid), obj.Declaration.PartitionId,
                    obj.ObjectReferences.Select(Capture).ToImmutableArray(), obj.CellReferences.Select(Capture).ToImmutableArray(),
                    obj.Declaration.BlobReference is { } blob ? Capture(blob) : null)
                    { DefinitionDigest = ObjectDigest(obj) }).ToImmutableArray() };
            case DataElementType.ObjectDataBLOBDataElementData:
                _ = IndexedGraphRecords.ReadBlob(element);
                return new();
            default:
                return new();
        }
    }

    /// <summary>Immutable protocol identities include type, element serial, mapping serials and payload integrity.</summary>
    public static void ValidateIdentity(GraphElementState existing, GraphElementState incoming)
    {
        if (existing.Id != incoming.Id || existing.Type != incoming.Type || existing.Serial != incoming.Serial ||
            existing.Payload.Length != incoming.Payload.Length || existing.Payload.Sha256 != incoming.Payload.Sha256 ||
            existing.MappingSerials.IsDefault || incoming.MappingSerials.IsDefault ||
            !existing.MappingSerials.AsSpan().SequenceEqual(incoming.MappingSerials.AsSpan()))
            throw new InvalidDataException("An immutable data-element identity was reused with different content or serials.");
    }

    public static ExtendedId Capture(ExGuid id) => new(id.Value, id.Guid);
    public static ExGuid Restore(ExtendedId id) => new(id.Value, id.Guid);
    public static IndexedCellId Capture(CellId cell) => new(Capture(cell.LongId), Capture(cell.ShortId));
    public static CellId Restore(IndexedCellId cell) => new(Restore(cell.LongId), Restore(cell.ShortId));
    internal static bool IsNull(ExtendedId id) => id.Value == 0 && id.Guid == Guid.Empty;

    private static string ObjectDigest(ObjectGroupObject obj)
    {
        var writer = new BinaryWriterEx();
        obj.ObjectGuid.Serialize(writer);
        writer.WriteUInt64(obj.Declaration.PartitionId);
        writer.WriteUInt64(obj.Declaration.DataSize);
        writer.WriteUInt64(obj.Declaration.ObjectReferencesCount);
        writer.WriteUInt64(obj.Declaration.CellReferencesCount);
        writer.WriteByte(obj.Declaration.BlobReference is null ? (byte)0 : (byte)1);
        obj.Declaration.BlobReference?.Serialize(writer);
        foreach (var reference in obj.ObjectReferences) reference.Serialize(writer);
        foreach (var reference in obj.CellReferences) reference.Serialize(writer);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(writer.ToArray());
        hash.AppendData(obj.Content);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
