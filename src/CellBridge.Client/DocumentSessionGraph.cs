using System.Security.Cryptography;
using CellBridge.FssHttpB;

namespace CellBridge.Client;

internal sealed class DocumentSessionGraph
{
    private readonly Dictionary<ExGuid, DataElement> _elements;

    private DocumentSessionGraph(Dictionary<ExGuid, DataElement> elements, DataElement index, ClientKnowledge knowledge,
        long size, bool provisionalIndex = false)
    {
        _elements = elements;
        Index = index;
        Knowledge = knowledge;
        Size = size;
        _provisionalIndex = provisionalIndex;
    }

    private readonly bool _provisionalIndex;
    internal DataElement Index { get; }
    internal ClientKnowledge Knowledge { get; }
    internal long Size { get; }

    internal PartitionGraphSnapshot ReadFile() =>
        PartitionGraphSnapshot.Create(_elements.Values, Index.DataElementExtendedGuid);

    internal sealed class QueryPages(DocumentSessionGraph? current, long maxBytes)
    {
        private readonly Dictionary<ExGuid, DataElement> _elements = current is null ? [] : new(current._elements);
        private long _size = current?.Size ?? 0;
        private bool _provisionalIndex = current?._provisionalIndex ?? false;

        internal bool Stage(IEnumerable<DataElement> elements)
        {
            bool progress = false;
            foreach (var element in elements)
            {
                if (_provisionalIndex && current is not null &&
                    element.DataElementExtendedGuid.Equals(current.Index.DataElementExtendedGuid))
                {
                    // The prior save returned a delta. A full query can contain
                    // mappings for other cells that changed while the save ran.
                    if (element.DataElementType != DataElementType.StorageIndexDataElementData ||
                        !element.SerialNumber.Equals(current.Index.SerialNumber))
                        throw new InvalidDataException("Query changed the type or serial of the applied storage index.");
                    StorageIndexPatch.Read(element);
                    _elements[element.DataElementExtendedGuid] = element;
                    _size += ElementSize(element) - ElementSize(current.Index);
                    CheckSize(_size, maxBytes);
                    _provisionalIndex = false;
                    progress = true;
                    continue;
                }

                if (!AddElement(_elements, element))
                    continue;
                _size += ElementSize(element);
                CheckSize(_size, maxBytes);
                progress = true;
            }
            return progress;
        }

        internal DocumentSessionGraph Complete(ExGuid selected, ClientKnowledge knowledge)
        {
            if (selected.IsNull || !_elements.TryGetValue(selected, out var index) ||
                index.DataElementType != DataElementType.StorageIndexDataElementData)
                throw new InvalidDataException("Query omitted the selected storage index.");

            // Only the selected index is needed for the next query or PutChanges.
            // Non-index elements may be advertised by knowledge or reused by a save.
            foreach (var other in _elements.Values.Where(element =>
                element.DataElementType == DataElementType.StorageIndexDataElementData &&
                !element.DataElementExtendedGuid.Equals(selected)).ToArray())
            {
                _elements.Remove(other.DataElementExtendedGuid);
                _size -= ElementSize(other);
            }
            return new(_elements, index, knowledge, _size, _provisionalIndex && ReferenceEquals(current?.Index, index));
        }
    }

    internal FsshttpbCellRequest CreateSave(CellId cell, byte[] package, long maxBytes)
    {
        var updateId = Guid.NewGuid();
        var indexId = new ExGuid(1, updateId);
        var revision = new ExGuid(2, updateId);
        var cellManifest = new ExGuid(3, updateId);
        var revisionManifest = new ExGuid(4, updateId);
        var rootGroup = new ExGuid(5, updateId);
        var rootObject = new ExGuid(6, updateId);
        var mappings = StorageIndexPatch.Read(Index);
        var manifest = mappings.Single(mapping => mapping.Type == StreamObjectTypeHeaderStart.StorageIndexManifestMapping);
        var updates = new StorageIndexMapping[]
        {
            manifest,
            new(StreamObjectTypeHeaderStart.StorageIndexCellMapping, null, cell, cellManifest, new(updateId, 1)),
            new(StreamObjectTypeHeaderStart.StorageIndexRevisionMapping, revision, null, revisionManifest, new(updateId, 2)),
        };

        var elements = new DataElementPackage();
        var groups = new HashSet<ExGuid>();
        var leaves = new List<ExGuid>();
        foreach (var chunk in FsshttpdChunker.Chunk(package))
        {
            // The group identity includes a format domain so it identifies the framed
            // objects as well as the bytes they represent.
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData("CellBridge client chunk v1"u8);
            hash.AppendData(chunk.Content);
            var chunkId = new Guid(hash.GetHashAndReset().AsSpan(0, 16));
            var group = new ExGuid(1, chunkId);
            var leaf = new ExGuid(2, chunkId);
            var bytes = new ExGuid(3, chunkId);
            leaves.Add(leaf);
            if (groups.Add(group) && !_elements.ContainsKey(group))
            {
                elements.DataElements.Add(BuildGroup(group,
                    new(leaf, FsshttpdNodeSerializer.SerializeSimpleLeaf(chunk), [bytes]),
                    new(bytes, chunk.Content, [])));
            }
        }
        elements.DataElements.Add(BuildGroup(rootGroup,
            new FileObject(rootObject, FsshttpdNodeSerializer.SerializeSimpleRoot((ulong)package.Length), leaves)));
        elements.DataElements.Add(StorageManifestBuilder.BuildCellManifestDataElement(cellManifest, SerialNumber.Null, revision));
        elements.DataElements.Add(StorageManifestBuilder.BuildRevisionManifestDataElement(revisionManifest, SerialNumber.Null,
            revision, ExGuid.Null, new(2, StorageManifestBuilder.RootExtendedGuid), rootObject, groups.Prepend(rootGroup)));
        var applied = new DataElement(DataElementType.StorageIndexDataElementData, indexId, new(updateId, 1))
        {
            Data = StorageIndexPatch.Merge(mappings, updates),
        };
        // A server-assigned serial takes 24 more bytes than a null serial.
        CheckSize(Size - ElementSize(Index) + ElementSize(applied) +
            elements.DataElements.Sum(element => ElementSize(element) + 24), maxBytes);
        elements.DataElements.Add(new(DataElementType.StorageIndexDataElementData, indexId, SerialNumber.Null)
        {
            Data = StorageIndexPatch.Serialize(updates),
        });
        elements.DataElements.Add(Index);

        return new()
        {
            DataElementPackage = elements,
            SubRequests =
            {
                new(RequestTypes.PutChanges)
                {
                    RequestId = 1,
                    Data = new PutChangesSubRequestData
                    {
                        StorageIndex = indexId,
                        ExpectedStorageIndex = Index.DataElementExtendedGuid,
                        HasAdditionalFlags = true,
                        AdditionalFlagsBits = 1,
                    },
                },
            },
        };
    }

    internal DocumentSessionGraph ApplySave(FsshttpbCellRequest request, FsshttpbResponse response,
        PutChangesSubResponseData save, long maxBytes)
    {
        var indexId = save.PutChangesResponse?.AppliedStorageIndexID;
        if (indexId is null || indexId.IsNull)
            throw new InvalidDataException("Save omitted its applied storage index identifier.");
        var delta = response.DataElementPackage?.DataElements.SingleOrDefault(element =>
            element.DataElementExtendedGuid.Equals(indexId) && element.DataElementType == DataElementType.StorageIndexDataElementData)
            ?? throw new InvalidDataException("Save omitted its applied storage index element.");

        var mappings = StorageIndexPatch.Read(Index).ToDictionary(MappingKey);
        foreach (var mapping in StorageIndexPatch.Read(delta))
            mappings[MappingKey(mapping)] = mapping;
        var index = new DataElement(DataElementType.StorageIndexDataElementData, indexId, delta.SerialNumber)
        {
            Data = StorageIndexPatch.Serialize(mappings.Values),
        };

        var serials = ReadAssignments(save.SerialNumberReassignAllBytes);
        var elements = new Dictionary<ExGuid, DataElement>(_elements);
        elements.Remove(Index.DataElementExtendedGuid);
        long size = Size - ElementSize(Index) + ElementSize(index);
        foreach (var element in request.DataElementPackage!.DataElements)
        {
            if (element.DataElementType == DataElementType.StorageIndexDataElementData)
                continue;
            if (!serials.TryGetValue(element.DataElementExtendedGuid, out var serial) || serial.IsNull)
                throw new InvalidDataException("Save omitted a data element serial reassignment.");
            var admitted = new DataElement(element.DataElementType, element.DataElementExtendedGuid, serial) { Data = element.Data };
            if (AddElement(elements, admitted))
                size += ElementSize(admitted);
        }
        CheckSize(size, maxBytes);
        AddElement(elements, index);
        return new(elements, index, ReadKnowledge(save.KnowledgeBytes), size, provisionalIndex: true);
    }

    internal static ClientKnowledge ReadKnowledge(byte[]? bytes) =>
        bytes is { Length: > 0 } ? ClientKnowledge.Deserialize(new(bytes)) :
            throw new InvalidDataException("Cell response omitted its knowledge.");

    private static (StreamObjectTypeHeaderStart, ExGuid?, CellId?) MappingKey(StorageIndexMapping mapping) =>
        (mapping.Type, mapping.Revision, mapping.Cell);

    private static long ElementSize(DataElement element) => element.GetSerializedLength();

    private static void CheckSize(long size, long maxBytes)
    {
        if (size > maxBytes)
            throw new InvalidDataException("The document session graph exceeds its configured byte limit.");
    }

    private static bool AddElement(Dictionary<ExGuid, DataElement> elements, DataElement incoming)
    {
        if (elements.TryAdd(incoming.DataElementExtendedGuid, incoming))
            return true;

        var existing = elements[incoming.DataElementExtendedGuid];
        if (existing.DataElementType != incoming.DataElementType || !existing.SerialNumber.Equals(incoming.SerialNumber))
            throw new InvalidDataException("A data element identifier was reused with a different type or serial.");

        // A query can serialize an already applied mapping set in a different order.
        bool equivalent = incoming.DataElementType == DataElementType.StorageIndexDataElementData
            ? StorageIndexPatch.Read(existing).ToHashSet().SetEquals(StorageIndexPatch.Read(incoming))
            : existing.Data.AsSpan().SequenceEqual(incoming.Data);
        if (!equivalent)
            throw new InvalidDataException("A data element identifier was reused with different content.");
        return false;
    }

    private static Dictionary<ExGuid, SerialNumber> ReadAssignments(byte[]? data)
    {
        var assigned = new Dictionary<ExGuid, SerialNumber>();
        var reader = new BinaryReaderEx(data ?? throw new InvalidDataException("Save omitted serial assignments."));
        var header = StreamObjectHeaderStart.Parse(reader);
        var body = new BinaryReaderEx(reader.ReadMemory(header.Length));
        if (header.Type != StreamObjectTypeHeaderStart.PutChangesResponseSerialNumberReassignAll ||
            !SerialNumber.Deserialize(body).IsNull || body.Remaining != 0)
            throw new InvalidDataException("Save returned an unsupported shared serial assignment.");

        while (reader.Remaining > 0)
        {
            header = StreamObjectHeaderStart.Parse(reader);
            if (header.Type != StreamObjectTypeHeaderStart.PutChangesResponseSerialNumberReassign)
                throw new InvalidDataException("Save returned an unknown serial assignment object.");
            body = new BinaryReaderEx(reader.ReadMemory(header.Length));
            var id = ExGuid.Deserialize(body);
            var serial = SerialNumber.Deserialize(body);
            if (body.Remaining != 0 || !assigned.TryAdd(id, serial))
                throw new InvalidDataException("Save returned a malformed serial assignment.");
        }
        return assigned;
    }

    private static DataElement BuildGroup(ExGuid group, params FileObject[] objects)
    {
        var writer = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.ObjectGroupDeclarations, 0).Serialize(writer);
        foreach (var item in objects)
        {
            var body = new BinaryWriterEx();
            item.Id.Serialize(body);
            new Compact64bitInt(1).Serialize(body);
            new Compact64bitInt((ulong)item.Content.Length).Serialize(body);
            new Compact64bitInt((ulong)item.References.Count).Serialize(body);
            new Compact64bitInt(0).Serialize(body);
            WriteObject(writer, StreamObjectTypeHeaderStart.ObjectGroupObjectDeclare, body);
        }
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupDeclarations).Serialize(writer);
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.ObjectGroupData, 0).Serialize(writer);
        foreach (var item in objects)
        {
            var body = new BinaryWriterEx();
            new Compact64bitInt((ulong)item.References.Count).Serialize(body);
            foreach (var reference in item.References)
                reference.Serialize(body);
            new Compact64bitInt(0).Serialize(body);
            new BinaryItem(item.Content).Serialize(body);
            WriteObject(writer, StreamObjectTypeHeaderStart.ObjectGroupObjectData, body);
        }
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupData).Serialize(writer);
        return new(DataElementType.ObjectGroupDataElementData, group, SerialNumber.Null) { Data = writer.ToArray() };
    }

    private static void WriteObject(BinaryWriterEx writer, StreamObjectTypeHeaderStart type, BinaryWriterEx body)
    {
        new StreamObjectHeaderStart32Bit(type, body.Length).Serialize(writer);
        writer.WriteBytes(body.ToArray());
    }

    private sealed record FileObject(ExGuid Id, byte[] Content, IReadOnlyList<ExGuid> References);
}
