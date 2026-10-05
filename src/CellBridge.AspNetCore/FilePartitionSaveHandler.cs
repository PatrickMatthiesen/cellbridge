using System.IO.Compression;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

/// <summary>Validates and commits a complete file graph, including retained delta objects.</summary>
public static class FilePartitionSaveHandler
{
    public static FsshttpbSubResponse Apply(StoredDocument document, DocumentPartition partition,
        FsshttpbCellSubRequest subRequest, DataElementPackage? package, StorageLimits? limits = null)
        => ApplyResponse(document, partition, subRequest, package, limits).SubResponses[0];

    /// <summary>Applies a complete save and returns its mandatory applied-index payload when requested.</summary>
    public static FsshttpbResponse ApplyResponse(StoredDocument document, DocumentPartition partition,
        FsshttpbCellSubRequest subRequest, DataElementPackage? package, StorageLimits? limits = null)
        => ApplyResponse(document, partition, subRequest, package, limits, _ => true);

    internal static FsshttpbResponse ApplyResponse(StoredDocument document, DocumentPartition partition,
        FsshttpbCellSubRequest subRequest, DataElementPackage? package, StorageLimits? limits,
        Func<DataElementPackage?, bool> canAppend)
    {
        limits ??= new StorageLimits();
        lock (document)
        {
            var prepared = Prepare(document, partition, subRequest, package, limits);
            if (prepared.Graph is null) return Wrap(prepared.Response);
            if (!canAppend(prepared.ResponsePackage))
                return Wrap(Failure(subRequest.RequestId, CellErrorCode.RequestNotSupported,
                    "The mandatory save payload exceeds the response budget or conflicts with earlier results."));
            try
            {
                byte[] bytes;
                try { bytes = prepared.Graph.Materialize(limits.MaxDocumentBytes); }
                catch (GraphMaterializationLimitException)
                { throw new StorageQuotaExceededException("document bytes", limits.MaxDocumentBytes + 1, limits.MaxDocumentBytes); }
                using var stream = new MemoryStream(bytes, writable: false);
                ValidateDocument(stream, document.Url, limits.MaxDocumentBytes);
                if (!prepared.Repeat) document.CommitFileRevision(prepared.Graph, bytes, prepared.KnowledgeSequence);
                return Wrap(prepared.Response, prepared.ResponsePackage);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException or OverflowException)
            {
                return Wrap(Failure(subRequest.RequestId, CellErrorCode.InvalidObject, ex.Message));
            }
        }
    }

    internal sealed record PreparedFileSave(FsshttpbSubResponse Response,
        PartitionGraphSnapshot? Graph = null, ulong KnowledgeSequence = 0, bool Repeat = false,
        DataElementPackage? ResponsePackage = null);

    private static FsshttpbResponse Wrap(FsshttpbSubResponse response, DataElementPackage? package = null)
        => new() { SubResponses = { response }, DataElementPackage = package };

    private static PreparedFileSave Reject(ulong id, CellErrorCode code, string message)
        => new(Failure(id, code, message));

    internal static PreparedFileSave Prepare(StoredDocument document, DocumentPartition partition,
        FsshttpbCellSubRequest subRequest, DataElementPackage? package, StorageLimits? limits = null,
        PartitionGraphSnapshot? currentGraph = null)
    {
        if (!CellBinaryRequestExecutor.MatchesTarget(subRequest, partition.Kind) ||
            partition.Kind != DocumentPartitionKind.FileContents ||
            subRequest.Data is not PutChangesSubRequestData put || package is null)
            return Reject(subRequest.RequestId, CellErrorCode.RequestNotSupported,
                "A file partition PutChanges request requires a data element package.");
        // MS-FSSHTTPB 2.2.2.1.4: abort-on-failure and the legacy content-version
        // item are ignored. 2.2.2.1.4.1 also requires ignoring reserved bits.
        // Excel sets reserved bit 15; it does not request a partial upload.
        if ((put.Flags & ~0x79) != 0 || (put.AdditionalFlagsBits & 0x38) != 0)
            return Reject(subRequest.RequestId, CellErrorCode.RequestNotSupported,
                "Partial uploads and alternate coherency modes are not implemented.");

        lock (document)
        {
            try
            {
                var current = currentGraph ?? partition.FileGraph;
                bool repeat = put.StorageIndex.Equals(current.StorageIndex);
                if (!repeat && !current.MatchesPutChanges(
                    current.StorageIndexes.Concat(package.DataElements), put.StorageIndex, put.ExpectedStorageIndex,
                    (put.Flags & 1) != 0))
                    return Reject(subRequest.RequestId, CellErrorCode.CoherencyFailure,
                        "The expected storage index is no longer current.");
                var existing = current.ElementMetadata.ToDictionary(x => x.DataElementExtendedGuid);
                limits ??= new StorageLimits();
                var additions = package.DataElements.Where(e => !existing.ContainsKey(e.DataElementExtendedGuid))
                    .DistinctBy(e => e.DataElementExtendedGuid).ToArray();
                if (repeat && additions.Length != 0)
                    return Reject(subRequest.RequestId, CellErrorCode.InvalidObject,
                        "An accepted storage index cannot introduce new data elements.");
                StorageLimits.Check("graph elements", existing.Count + (long)additions.Length, limits.MaxGraphElements);
                StorageLimits.Check("graph bytes", current.PayloadBytes + additions.Sum(e => (long)(e.Data?.Length ?? 0)), limits.MaxGraphBytes);
                foreach (var element in package.DataElements)
                    if (current.ConflictsWith(element))
                        return Reject(subRequest.RequestId, CellErrorCode.InvalidObject,
                            "A data element identifier was reused for different content.");
                ulong sequence = partition.KnowledgeSequence;
                var serialGuid = partition.ProtocolIdentity.SerialGuid;
                var incomingMappings = package.DataElements.Where(e => e.DataElementType == DataElementType.StorageIndexDataElementData)
                    .SelectMany(e => StorageIndexMappingSerials.ReadMappings(e.Data ?? [])).ToArray();
                current.ValidateMappingSerials(incomingMappings);
                // Mapping serials identify mappings, independently of target DE serials.
                // Never recycle a server serial after a restore or a client upload.
                foreach (var serial in current.ElementMetadata.Select(e => e.SerialNumber)
                    .Concat(package.DataElements.Select(e => e.SerialNumber))
                    .Concat(current.MappingSerials.Values.SelectMany(s => s))
                    .Concat(incomingMappings.Select(m => m.Serial)))
                    if (serial.Guid == serialGuid) sequence = Math.Max(sequence, serial.Value);
                var assigned = new Dictionary<ExGuid, SerialNumber>();
                var accepted = package.DataElements.Select(element =>
                {
                    if (!assigned.TryGetValue(element.DataElementExtendedGuid, out var serial))
                    {
                        serial = existing.TryGetValue(element.DataElementExtendedGuid, out var stored)
                            ? stored.SerialNumber : new SerialNumber(serialGuid, checked(++sequence));
                        assigned.Add(element.DataElementExtendedGuid, serial);
                    }
                    return new DataElement(element.DataElementType, element.DataElementExtendedGuid, serial) { Data = element.Data };
                }).ToArray();
                if (!repeat && sequence == partition.KnowledgeSequence) sequence = checked(sequence + 1);
                var next = current.Merge(accepted, put.StorageIndex);
                StorageLimits.Check("graph elements", next.ElementCount, limits.MaxGraphElements);
                StorageLimits.Check("graph bytes", next.PayloadBytes, limits.MaxGraphBytes);
                // Prepare the response before changing visible document state.
                var knowledge = CreateQueryData(partition, next, sequence);
                var writer = new BinaryWriterEx();
                knowledge.SerializeKnowledge(writer);
                var applied = new PutChangesResponse
                {
                    AppliedStorageIndexID = (put.AdditionalFlagsBits & 0x01) != 0 ? next.StorageIndex : ExGuid.Null,
                };
                // SharePoint's captured Word saves return added IDs even with bit B
                // clear. Report only newly admitted, unique IDs, including for that
                // profile; accepted also contains retained or duplicated elements.
                applied.DataElementAdded.AddRange(additions.Select(x => x.DataElementExtendedGuid));
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
                DataElementPackage? responsePackage = null;
                if ((put.AdditionalFlagsBits & 1) != 0)
                {
                    responsePackage = new();
                    responsePackage.DataElements.AddRange(next.SelectElements(e => e.DataElementExtendedGuid.Equals(next.StorageIndex)));
                }
                return new(result, next, sequence, repeat, responsePackage);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException or OverflowException)
            {
                return Reject(subRequest.RequestId, CellErrorCode.InvalidObject, ex.Message);
            }
        }
    }

    internal static QueryChangesSubResponseData CreateQueryData(DocumentPartition partition,
        PartitionGraphSnapshot graph, ulong sequence) => new()
    {
        StorageIndexExtendedGuid = graph.StorageIndex,
        CellKnowledgeCellGuid = graph.FileCell.LongId.Guid,
        CellKnowledgeTo = sequence,
        WaterlineCellStorageExtendedGuid = graph.FileCell.ShortId,
        Waterline = sequence,
        KnowledgeBytes = BinaryKnowledgeBuilder.FromElements(graph.ElementMetadata,
            graph.FileCell.ShortId, sequence,
            mappingSerials: graph.MappingSerials.Values.SelectMany(s => s)),
    };

    private static void ValidateDocument(Stream content, string documentUrl, long maxExpandedBytes)
    {
        using var archive = OpenDocumentArchive(content, documentUrl);
        // Read all parts before committing so truncated ZIP members cannot become stored content.
        long expanded = 0;
        var buffer = new byte[65536];
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > maxExpandedBytes - expanded)
                throw new StorageQuotaExceededException("expanded package bytes", checked(expanded + entry.Length), maxExpandedBytes);
            using var stream = entry.Open();
            int read;
            while ((read = stream.Read(buffer)) != 0)
            {
                expanded = checked(expanded + read);
                StorageLimits.Check("expanded package bytes", expanded, maxExpandedBytes);
            }
        }
    }

    private static ZipArchive OpenDocumentArchive(Stream content, string documentUrl)
    {
        content.Position = 0;
        var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
        try
        {
            var mainPart = Path.GetExtension(documentUrl).ToLowerInvariant() switch
            {
                ".docx" => "word/document.xml",
                ".xlsx" => "xl/workbook.xml",
                ".pptx" => "ppt/presentation.xml",
                _ => throw new InvalidDataException("The proposed file type is not supported."),
            };
            if (archive.GetEntry("[Content_Types].xml") is null || archive.GetEntry(mainPart) is null)
                throw new InvalidDataException("The proposed package does not match the document's file type.");
            return archive;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    internal static async ValueTask ValidateDocumentAsync(Stream content, string documentUrl,
        long maxExpandedBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = OpenDocumentArchive(content, documentUrl);
        long expanded = 0;
        var buffer = new byte[65536];
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Length > maxExpandedBytes - expanded)
                throw new StorageQuotaExceededException("expanded package bytes", checked(expanded + entry.Length), maxExpandedBytes);
            await using var stream = entry.Open();
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                expanded = checked(expanded + read);
                StorageLimits.Check("expanded package bytes", expanded, maxExpandedBytes);
            }
        }
    }

    internal static byte[] SerialReassignments(IEnumerable<DataElement> elements)
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
