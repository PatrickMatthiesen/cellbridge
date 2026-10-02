using System.IO.Compression;
using CellBridge.FssHttpB;
using CellBridge.Storage;

namespace CellBridge.Web;

/// <summary>Validates and commits a complete file graph, including retained delta objects.</summary>
public static class FilePartitionSaveHandler
{
    public static FsshttpbSubResponse Apply(StoredDocument document, DocumentPartition partition,
        FsshttpbCellSubRequest subRequest, DataElementPackage? package)
    {
        if (partition.Kind != DocumentPartitionKind.FileContents ||
            subRequest.Data is not PutChangesSubRequestData put || package is null)
            return Failure(subRequest.RequestId, CellErrorCode.RequestNotSupported,
                "A file partition PutChanges request requires a data element package.");
        // MS-FSSHTTPB 2.2.2.1.4: abort-on-failure and the legacy content-version
        // item are ignored. 2.2.2.1.4.1 also requires ignoring reserved bits.
        // Excel sets reserved bit 15; it does not request a partial upload.
        if ((put.Flags & ~0x59) != 0 || (put.AdditionalFlagsBits & 0x38) != 0)
            return Failure(subRequest.RequestId, CellErrorCode.RequestNotSupported,
                "Partial, multi-request and alternate coherency modes are not implemented.");

        lock (document)
        {
            try
            {
                var current = partition.FileGraph;
                bool repeat = put.StorageIndex.Equals(current.StorageIndex);
                if (!repeat && !current.MatchesPutChanges(
                    current.StorageIndexes.Concat(package.DataElements), put.StorageIndex, put.ExpectedStorageIndex,
                    (put.Flags & 1) != 0))
                    return Failure(subRequest.RequestId, CellErrorCode.CoherencyFailure,
                        "The expected storage index is no longer current.");
                var existing = current.ElementMetadata.ToDictionary(x => x.DataElementExtendedGuid);
                foreach (var element in package.DataElements)
                    if (current.ConflictsWith(element))
                        return Failure(subRequest.RequestId, CellErrorCode.InvalidObject,
                            "A data element identifier was reused for different content.");
                ulong sequence = partition.KnowledgeSequence;
                var serialHints = ReadMappingSerials(package.DataElements);
                var accepted = package.DataElements.Select(element =>
                {
                    var serial = existing.TryGetValue(element.DataElementExtendedGuid, out var stored)
                        ? stored.SerialNumber
                        : element.SerialNumber.IsNull
                            ? serialHints.GetValueOrDefault(element.DataElementExtendedGuid)
                              ?? new SerialNumber(partition.ProtocolIdentity.SerialGuid, checked(++sequence))
                            : element.SerialNumber;
                    return new DataElement(element.DataElementType, element.DataElementExtendedGuid, serial) { Data = element.Data };
                }).ToArray();
                if (!repeat && sequence == partition.KnowledgeSequence) sequence = checked(sequence + 1);
                var next = current.Merge(accepted, put.StorageIndex);
                var bytes = next.Materialize();
                ValidateDocument(bytes, document.Url);

                // Prepare the response before changing visible document state.
                var knowledge = CreateQueryData(partition, next, sequence);
                var writer = new BinaryWriterEx();
                knowledge.SerializeKnowledge(writer);
                var applied = new PutChangesResponse { AppliedStorageIndexID = next.StorageIndex };
                if ((put.AdditionalFlagsBits & 0x02) != 0)
                    applied.DataElementAdded.AddRange(accepted.Where(x => !existing.ContainsKey(x.DataElementExtendedGuid))
                        .Select(x => x.DataElementExtendedGuid));
                var result = new FsshttpbSubResponse
                {
                    RequestId = subRequest.RequestId,
                    RequestType = RequestTypes.PutChanges,
                    Data = new PutChangesSubResponseData
                    {
                        PutChangesResponse = applied,
                        SerialNumberReassignAllBytes = SerialReassignments(accepted),
                        KnowledgeBytes = writer.ToArray(),
                    },
                };
                if (!repeat) document.CommitFileRevision(next, bytes, sequence);
                return result;
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException or OverflowException)
            {
                return Failure(subRequest.RequestId, CellErrorCode.InvalidObject, ex.Message);
            }
        }
    }

    internal static QueryChangesSubResponseData CreateQueryData(DocumentPartition partition,
        PartitionGraphSnapshot graph, ulong sequence) => new()
    {
        StorageIndexExtendedGuid = graph.StorageIndex,
        CellKnowledgeCellGuid = partition.ProtocolIdentity.CellId.LongId.Guid,
        CellKnowledgeTo = sequence,
        WaterlineCellStorageExtendedGuid = partition.ProtocolIdentity.CellId.ShortId,
        Waterline = sequence,
        KnowledgeBytes = BinaryKnowledgeBuilder.FromElements(graph.ElementMetadata,
            partition.ProtocolIdentity.CellId.ShortId, sequence),
    };

    private static void ValidateDocument(byte[] content, string documentUrl)
    {
        using var archive = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
        var mainPart = Path.GetExtension(documentUrl).ToLowerInvariant() switch
        {
            ".docx" => "word/document.xml",
            ".xlsx" => "xl/workbook.xml",
            ".pptx" => "ppt/presentation.xml",
            _ => throw new InvalidDataException("The proposed file type is not supported."),
        };
        if (archive.GetEntry("[Content_Types].xml") is null || archive.GetEntry(mainPart) is null)
            throw new InvalidDataException("The proposed package does not match the document's file type.");
        // Read all parts before committing so truncated ZIP members cannot become stored content.
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            stream.CopyTo(Stream.Null);
        }
    }

    private static Dictionary<ExGuid, SerialNumber> ReadMappingSerials(IEnumerable<DataElement> elements)
    {
        var result = new Dictionary<ExGuid, SerialNumber>();
        foreach (var element in elements.Where(x => x.DataElementType == DataElementType.StorageIndexDataElementData))
        {
            var reader = new BinaryReaderEx(element.Data ?? []);
            while (reader.Remaining > 0)
            {
                var header = StreamObjectHeaderStart.Parse(reader);
                var payload = new BinaryReaderEx(reader.ReadBytes(header.Length));
                if (header.Type == StreamObjectTypeHeaderStart.StorageIndexCellMapping) CellId.Deserialize(payload);
                else if (header.Type == StreamObjectTypeHeaderStart.StorageIndexRevisionMapping) ExGuid.Deserialize(payload);
                else if (header.Type != StreamObjectTypeHeaderStart.StorageIndexManifestMapping) continue;
                if (payload.Remaining == 0) continue;
                var id = ExGuid.Deserialize(payload);
                var serial = SerialNumber.Deserialize(payload);
                if (!id.IsNull && !serial.IsNull)
                {
                    if (result.TryGetValue(id, out var previous) && !previous.Equals(serial))
                        throw new InvalidDataException("Storage indices disagree on a referenced data element's serial number.");
                    result[id] = serial;
                }
            }
        }
        return result;
    }

    private static byte[] SerialReassignments(IEnumerable<DataElement> elements)
    {
        var writer = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.PutChangesResponseSerialNumberReassignAll, 1).Serialize(writer);
        SerialNumber.Null.Serialize(writer);
        foreach (var element in elements)
        {
            var body = new BinaryWriterEx();
            element.DataElementExtendedGuid.Serialize(body);
            element.SerialNumber.Serialize(body);
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.PutChangesResponseSerialNumberReassign, body.Length).Serialize(writer);
            writer.WriteBytes(body.ToArray());
        }
        return writer.ToArray();
    }

    private static FsshttpbSubResponse Failure(ulong id, CellErrorCode code, string message) => new()
    {
        RequestId = id,
        RequestType = RequestTypes.PutChanges,
        Status = true,
        Error = new ResponseError(ErrorType.Cell, (ulong)code, message),
    };
}
