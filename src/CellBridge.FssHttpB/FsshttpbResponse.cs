namespace CellBridge.FssHttpB;

/// <summary>
/// The FSSHTTPB response — the binary payload of the SOAP Cell subresponse.
/// [MS-FSSHTTPB] section 2.2.2.2.1.
/// Wire layout: ProtocolVersion (2), MinimumVersion (2), Signature (8),
/// ResponseStart header, Status byte, optional DataElementPackage,
/// sub-responses, ResponseEnd header.
/// </summary>
public sealed class FsshttpbResponse
{
    /// <summary>Constant response signature.</summary>
    public const ulong ResponseSignature = 0x9B069439F329CF9D;

    /// <summary>Creates an empty response with the required header values.</summary>
    public FsshttpbResponse()
    {
        ProtocolVersion = FsshttpbCellRequest.CurrentProtocolVersion;
        MinimumVersion = FsshttpbCellRequest.MinimumProtocolVersion;
        Signature = ResponseSignature;
        SubResponses = new List<FsshttpbSubResponse>();
    }

    /// <summary>Protocol schema version (MUST be 12).</summary>
    public ushort ProtocolVersion { get; set; }

    /// <summary>Oldest compatible schema version (MUST be 11).</summary>
    public ushort MinimumVersion { get; set; }

    /// <summary>Constant response signature (0x9B069439F329CF9D).</summary>
    public ulong Signature { get; set; }

    /// <summary>True when the whole response failed and a ResponseError follows.</summary>
    public bool Status { get; set; }

    /// <summary>The response error when <see cref="Status"/> is true.</summary>
    public ResponseError? Error { get; set; }

    /// <summary>Optional data element package carrying data for Query Changes responses.</summary>
    public DataElementPackage? DataElementPackage { get; set; }

    /// <summary>The sub-responses, one per sub-request.</summary>
    public List<FsshttpbSubResponse> SubResponses { get; set; }

    /// <summary>Serializes the response to the writer.</summary>
    public void Serialize(BinaryWriterEx writer) => Serialize(writer, FsshttpbSerializationProfile.Current);

    /// <summary>Serializes the response using the selected wire profile.</summary>
    public void Serialize(BinaryWriterEx writer, FsshttpbSerializationProfile profile)
    {
        writer.WriteUInt16(profile.ProtocolVersion());
        writer.WriteUInt16(profile.MinimumVersion());
        writer.WriteUInt64(Signature);

        // Response compound object start (Length 1 = the status byte).
        var responseStart = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.FsshttpbResponse, 1);
        responseStart.Serialize(writer);

        // Status byte: bit 0 = status, bits 1-7 reserved.
        writer.WriteByte(Status ? (byte)0x01 : (byte)0x00);

        if (Status)
        {
            if (Error is null)
            {
                throw new InvalidOperationException("A failed response MUST include a ResponseError.");
            }

            Error.Serialize(writer);
        }
        else
        {
            DataElementPackage?.Serialize(writer, profile);

            foreach (var subResponse in SubResponses)
            {
                subResponse.Serialize(writer);
            }
        }

        // Response compound object end.
        var responseEnd = new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.Response);
        responseEnd.Serialize(writer);
    }

    /// <summary>Determines whether the next header at this position is a header end (vs a start).</summary>
    private static bool IsHeaderEnd(BinaryReaderEx reader)
    {
        int pos = reader.Position;
        int discriminator = reader.ReadByte() & 0x03;
        reader.Position = pos;
        return discriminator == 0x1 || discriminator == 0x3;
    }

    /// <summary>Serializes the response to a byte array.</summary>
    public byte[] ToByteArray()
    {
        var writer = new BinaryWriterEx();
        Serialize(writer);
        return writer.ToArray();
    }

    /// <summary>Serializes the response using the selected wire profile.</summary>
    public byte[] ToByteArray(FsshttpbSerializationProfile profile)
    {
        var writer = new BinaryWriterEx();
        Serialize(writer, profile);
        return writer.ToArray();
    }

    /// <summary>Serializes the response to a base64 string (as used in the SOAP payload).</summary>
    public string ToBase64() => Convert.ToBase64String(ToByteArray());

    /// <summary>Serializes the response as base64 using the selected profile.</summary>
    public string ToBase64(FsshttpbSerializationProfile profile) =>
        Convert.ToBase64String(ToByteArray(profile));

    /// <summary>Deserializes a response from the reader.</summary>
    public static FsshttpbResponse Deserialize(BinaryReaderEx reader)
    {
        var response = new FsshttpbResponse
        {
            ProtocolVersion = reader.ReadUInt16(),
            MinimumVersion = reader.ReadUInt16(),
            Signature = reader.ReadUInt64(),
        };

        if (response.Signature != ResponseSignature)
        {
            throw new InvalidDataException(
                $"Invalid response signature 0x{response.Signature:X16}; expected 0x{ResponseSignature:X16}.");
        }

        var responseStart = StreamObjectHeaderStart.Parse(reader);
        if (responseStart.Type != StreamObjectTypeHeaderStart.FsshttpbResponse)
        {
            throw new InvalidDataException($"Expected FsshttpbResponse header, got {responseStart.Type}.");
        }

        byte statusByte = reader.ReadByte();
        response.Status = (statusByte & 0x1) == 0x1;

        if (response.Status)
        {
            response.Error = ResponseError.Deserialize(reader);
        }
        else
        {
            // Peek for an optional data element package.
            if (!IsHeaderEnd(reader))
            {
                int pos = reader.Position;
                var header = StreamObjectHeaderStart.Parse(reader);
                reader.Position = pos;

                if (header.Type == StreamObjectTypeHeaderStart.DataElementPackage)
                {
                    response.DataElementPackage = DataElementPackage.Deserialize(reader);
                }
            }

            response.SubResponses = new List<FsshttpbSubResponse>();
            while (reader.Remaining > 0 && !IsHeaderEnd(reader))
            {
                int pos = reader.Position;
                var header = StreamObjectHeaderStart.Parse(reader);
                reader.Position = pos;

                if (header.Type == StreamObjectTypeHeaderStart.FsshttpbSubResponse)
                {
                    response.SubResponses.Add(FsshttpbSubResponse.Deserialize(reader));
                }
                else
                {
                    break;
                }
            }
        }

        var end = StreamObjectHeaderEnd.Parse(reader);
        if (end.Type != StreamObjectTypeHeaderEnd.Response)
        {
            throw new InvalidDataException($"Expected Response end header, got {end.Type}.");
        }

        return response;
    }
}

/// <summary>
/// A sub-response within an FSSHTTPB response, one per sub-request.
/// [MS-FSSHTTPB] section 2.2.2.2.1.1.
/// </summary>
public sealed class FsshttpbSubResponse
{
    /// <summary>The ID of the sub-request this response is for.</summary>
    public ulong RequestId { get; set; }

    /// <summary>The request type this response matches.</summary>
    public RequestTypes RequestType { get; set; }

    /// <summary>True when the sub-request failed and an error follows.</summary>
    public bool Status { get; set; }

    /// <summary>The error when <see cref="Status"/> is true.</summary>
    public ResponseError? Error { get; set; }

    /// <summary>The type-specific response data when <see cref="Status"/> is false.</summary>
    public ISubResponseData? Data { get; set; }

    /// <summary>Serializes the sub-response to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        var payloadWriter = new BinaryWriterEx();
        new Compact64bitInt(RequestId).Serialize(payloadWriter);
        new Compact64bitInt((ulong)RequestType).Serialize(payloadWriter);
        payloadWriter.WriteByte(Status ? (byte)0x01 : (byte)0x00);
        byte[] payload = payloadWriter.ToArray();

        // The compound sub-response header length covers only the fixed
        // request ID, request type, and status fields. The ResponseError or
        // type-specific response data is a child stream object after that
        // fixed payload, not part of the declared length.
        var start = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.FsshttpbSubResponse, payload.Length);
        start.Serialize(writer);
        writer.WriteBytes(payload);

        if (Status)
        {
            Error!.Serialize(writer);
        }
        else
        {
            Data?.Serialize(writer);
        }

        var end = new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.SubResponse);
        end.Serialize(writer);
    }

    /// <summary>Deserializes a sub-response from the reader.</summary>
    public static FsshttpbSubResponse Deserialize(BinaryReaderEx reader)
    {
        var start = StreamObjectHeaderStart.Parse(reader);
        if (start.Type != StreamObjectTypeHeaderStart.FsshttpbSubResponse)
        {
            throw new InvalidDataException($"Expected FsshttpbSubResponse header, got {start.Type}.");
        }

        int fixedPayloadEnd = reader.Position + start.Length;

        var subResponse = new FsshttpbSubResponse
        {
            RequestId = Compact64bitInt.Deserialize(reader).Value,
            RequestType = (RequestTypes)Compact64bitInt.Deserialize(reader).Value,
        };

        byte statusByte = reader.ReadByte();
        subResponse.Status = (statusByte & 0x1) == 0x1;

        if (reader.Position != fixedPayloadEnd)
        {
            throw new InvalidDataException(
                $"FsshttpbSubResponse fixed payload over/under-read: expected {fixedPayloadEnd}, got {reader.Position}.");
        }

        if (subResponse.Status)
        {
            subResponse.Error = ResponseError.Deserialize(reader);
        }
        else
        {
            subResponse.Data = SubResponseDataFactory.Create(subResponse.RequestType, reader);
        }

        var end = StreamObjectHeaderEnd.Parse(reader);
        if (end.Type != StreamObjectTypeHeaderEnd.SubResponse)
        {
            throw new InvalidDataException($"Expected SubResponse end header, got {end.Type}.");
        }

        return subResponse;
    }
}

/// <summary>
/// Marker interface for sub-response data payloads.
/// </summary>
public interface ISubResponseData
{
    /// <summary>Serializes the payload to the writer.</summary>
    void Serialize(BinaryWriterEx writer);
}

/// <summary>
/// Creates the type-specific sub-response data for a request type.
/// </summary>
public static class SubResponseDataFactory
{
    /// <summary>Creates and deserializes the sub-response data for the given request type.</summary>
    public static ISubResponseData Create(RequestTypes requestType, BinaryReaderEx reader) => requestType switch
    {
        RequestTypes.QueryAccess => QueryAccessSubResponseData.Deserialize(reader),
        RequestTypes.QueryChanges => QueryChangesSubResponseData.Deserialize(reader),
        RequestTypes.PutChanges => PutChangesSubResponseData.Deserialize(reader),
        RequestTypes.AllocateExtendedGuidRange => AllocateExtendedGuidRangeSubResponseData.Deserialize(reader),
        _ => new UnknownSubResponseData(),
    };
}

/// <summary>
/// Query Access sub-response data. [MS-FSSHTTPB] section 2.2.2.2.2.
/// Wire layout: ReadAccessResponse header (length 0), mandatory ResponseError
/// child, WriteAccessResponse header (length 0), and mandatory ResponseError
/// child. HRESULT 0 indicates that the requested access was granted.
/// </summary>
public sealed class QueryAccessSubResponseData : ISubResponseData
{
    /// <summary>Read access error (null = access granted).</summary>
    public ResponseError? ReadAccessError { get; set; }

    /// <summary>Write access error (null = access granted).</summary>
    public ResponseError? WriteAccessError { get; set; }

    /// <summary>Serializes the payload to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.ReadAccessResponse, 0)
            .Serialize(writer);
        (ReadAccessError ?? new ResponseError(ErrorType.HResult, 0)).Serialize(writer);
        new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.ReadAccessResponse).Serialize(writer);

        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.WriteAccessResponse, 0)
            .Serialize(writer);
        (WriteAccessError ?? new ResponseError(ErrorType.HResult, 0)).Serialize(writer);
        new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.WriteAccessResponse).Serialize(writer);
    }

    /// <summary>Deserializes the payload from the reader.</summary>
    public static QueryAccessSubResponseData Deserialize(BinaryReaderEx reader)
    {
        var data = new QueryAccessSubResponseData();

        var readHeader = StreamObjectHeaderStart.Parse(reader);
        if (readHeader.Type != StreamObjectTypeHeaderStart.ReadAccessResponse)
        {
            throw new InvalidDataException($"Expected ReadAccessResponse header, got {readHeader.Type}.");
        }

        if (readHeader.Length != 0)
            throw new InvalidDataException($"ReadAccessResponse must have a zero fixed payload length, got {readHeader.Length}.");

        var readError = ResponseError.Deserialize(reader);
        data.ReadAccessError = readError.Type == ErrorType.HResult && readError.ErrorCode == 0 ? null : readError;
        var readEnd = StreamObjectHeaderEnd.Parse(reader);
        if (readEnd.Type != StreamObjectTypeHeaderEnd.ReadAccessResponse)
            throw new InvalidDataException($"Expected ReadAccessResponse end, got {readEnd.Type}.");

        var writeHeader = StreamObjectHeaderStart.Parse(reader);
        if (writeHeader.Type != StreamObjectTypeHeaderStart.WriteAccessResponse)
        {
            throw new InvalidDataException($"Expected WriteAccessResponse header, got {writeHeader.Type}.");
        }

        if (writeHeader.Length != 0)
            throw new InvalidDataException($"WriteAccessResponse must have a zero fixed payload length, got {writeHeader.Length}.");

        var writeError = ResponseError.Deserialize(reader);
        data.WriteAccessError = writeError.Type == ErrorType.HResult && writeError.ErrorCode == 0 ? null : writeError;
        var writeEnd = StreamObjectHeaderEnd.Parse(reader);
        if (writeEnd.Type != StreamObjectTypeHeaderEnd.WriteAccessResponse)
            throw new InvalidDataException($"Expected WriteAccessResponse end, got {writeEnd.Type}.");

        return data;
    }
}

/// <summary>
/// Query Changes sub-response data. [MS-FSSHTTPB] sections 2.2.2.2.3 and
/// 2.2.1.13.
/// The data element package carrying the manifest is a sibling of this
/// sub-response; it is not declared inside the QueryChanges payload.
/// </summary>
public sealed class QueryChangesSubResponseData : ISubResponseData
{
    private static readonly Guid CellKnowledgeGuid = new("327A35F6-0761-4414-9686-51E900667A4D");
    private static readonly Guid WaterlineKnowledgeGuid = new("3A76E90E-8032-4D0C-B9DD-F3C65029433E");

    /// <summary>The cell storage whose waterline is returned.</summary>
    public ExGuid StorageIndexExtendedGuid { get; set; } = ExGuid.Null;

    /// <summary>Whether the query result is partial.</summary>
    public bool PartialResult { get; set; }

    /// <summary>The cell storage identifier represented in cell knowledge.</summary>
    public Guid CellKnowledgeCellGuid { get; set; }

    /// <summary>The highest cell knowledge sequence returned.</summary>
    public ulong CellKnowledgeTo { get; set; }

    /// <summary>Whether to emit the optional CellKnowledge block.</summary>
    public bool IncludeCellKnowledge { get; set; } = true;

    /// <summary>Optional complete Knowledge object to write verbatim.</summary>
    public byte[]? KnowledgeBytes { get; set; }

    /// <summary>The cell storage identifier used by the Waterline entry.</summary>
    public ExGuid? WaterlineCellStorageExtendedGuid { get; set; }

    /// <summary>The server waterline for the cell storage.</summary>
    public ulong Waterline { get; set; }

    /// <summary>Serializes the payload to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        // ExGuid uses a variable-width index, so the QueryChangesResponse
        // payload length must follow the encoded value rather than assume a
        // 5-bit index.
        var payload = new BinaryWriterEx();
        StorageIndexExtendedGuid.Serialize(payload);
        payload.WriteByte(PartialResult ? (byte)1 : (byte)0);
        var bytes = payload.ToArray();
        var header = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.QueryChangesResponse, bytes.Length);
        header.Serialize(writer);
        writer.WriteBytes(bytes);

        SerializeKnowledge(writer);
    }

    /// <summary>Writes the shared Knowledge structure used by query and put responses.</summary>
    public void SerializeKnowledge(BinaryWriterEx writer)
    {
        if (KnowledgeBytes is { Length: > 0 })
        {
            writer.WriteBytes(KnowledgeBytes);
            return;
        }

        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.Knowledge, 0).Serialize(writer);

        if (IncludeCellKnowledge)
        {
            var cell = new BinaryWriterEx();
            ExGuid.WriteGuid(cell, CellKnowledgeGuid);
            new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.CellKnowledge, 0).Serialize(cell);
            var range = new BinaryWriterEx();
            ExGuid.WriteGuid(range, CellKnowledgeCellGuid);
            new Compact64bitInt(0).Serialize(range);
            new Compact64bitInt(CellKnowledgeTo).Serialize(range);
            byte[] rangeBytes = range.ToArray();
            new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.CellKnowledgeRange, rangeBytes.Length).Serialize(cell);
            cell.WriteBytes(rangeBytes);
            new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.CellKnowledge).Serialize(cell);
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.SpecializedKnowledge, 16).Serialize(writer);
            writer.WriteBytes(cell.ToArray());
            new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.SpecializedKnowledge).Serialize(writer);
        }

        var waterline = new BinaryWriterEx();
        ExGuid.WriteGuid(waterline, WaterlineKnowledgeGuid);
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.WaterlineKnowledge, 0).Serialize(waterline);
        var entry = new BinaryWriterEx();
        (WaterlineCellStorageExtendedGuid ?? StorageIndexExtendedGuid).Serialize(entry);
        new Compact64bitInt(Waterline).Serialize(entry);
        new Compact64bitInt(0).Serialize(entry);
        byte[] entryBytes = entry.ToArray();
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.WaterlineKnowledgeEntry, entryBytes.Length).Serialize(waterline);
        waterline.WriteBytes(entryBytes);
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.WaterlineKnowledge).Serialize(waterline);
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.SpecializedKnowledge, 16).Serialize(writer);
        writer.WriteBytes(waterline.ToArray());
        new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.SpecializedKnowledge).Serialize(writer);
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.Knowledge).Serialize(writer);
    }

    /// <summary>Deserializes the payload from the reader (minimal parsing).</summary>
    public static QueryChangesSubResponseData Deserialize(BinaryReaderEx reader)
    {
        var data = new QueryChangesSubResponseData();

        var header = StreamObjectHeaderStart.Parse(reader);
        if (header.Type != StreamObjectTypeHeaderStart.QueryChangesResponse)
        {
            throw new InvalidDataException($"Expected QueryChangesResponse header, got {header.Type}.");
        }

        int payloadStart = reader.Position;
        data.StorageIndexExtendedGuid = ExGuid.Deserialize(reader);
        data.PartialResult = (reader.ReadByte() & 1) != 0;
        int payloadLength = reader.Position - payloadStart;
        if (header.Length != payloadLength)
        {
            throw new InvalidDataException($"QueryChangesResponse length {header.Length} does not match encoded payload length {payloadLength}.");
        }

        var knowledge = StreamObjectHeaderStart.Parse(reader);
        if (knowledge.Type != StreamObjectTypeHeaderStart.Knowledge)
        {
            throw new InvalidDataException($"Expected Knowledge header, got {knowledge.Type}.");
        }

        while (!IsHeaderEnd(reader))
        {
            var specialized = StreamObjectHeaderStart.Parse(reader);
            if (specialized.Type != StreamObjectTypeHeaderStart.SpecializedKnowledge)
            {
                throw new InvalidDataException($"Expected SpecializedKnowledge header, got {specialized.Type}.");
            }

            var specializedGuid = ExGuid.ReadGuid(reader);
            if (specializedGuid == CellKnowledgeGuid)
            {
                ParseCellKnowledge(reader, data);
            }
            else if (specializedGuid == WaterlineKnowledgeGuid)
            {
                ParseWaterlineKnowledge(reader, data);
            }
            else
            {
                throw new InvalidDataException($"Unsupported specialized knowledge GUID {specializedGuid}.");
            }

            var specializedEnd = StreamObjectHeaderEnd.Parse(reader);
            if (specializedEnd.Type != StreamObjectTypeHeaderEnd.SpecializedKnowledge)
            {
                throw new InvalidDataException($"Expected SpecializedKnowledge end, got {specializedEnd.Type}.");
            }
        }

        var knowledgeEnd = StreamObjectHeaderEnd.Parse(reader);
        if (knowledgeEnd.Type != StreamObjectTypeHeaderEnd.Knowledge)
        {
            throw new InvalidDataException($"Expected Knowledge end, got {knowledgeEnd.Type}.");
        }

        return data;
    }

    private static bool IsHeaderEnd(BinaryReaderEx reader)
    {
        int position = reader.Position;
        int discriminator = reader.ReadByte() & 3;
        reader.Position = position;
        return discriminator is 1 or 3;
    }

    private static void ParseCellKnowledge(BinaryReaderEx reader, QueryChangesSubResponseData data)
    {
        var cellKnowledge = StreamObjectHeaderStart.Parse(reader);
        if (cellKnowledge.Type != StreamObjectTypeHeaderStart.CellKnowledge)
        {
            throw new InvalidDataException($"Expected CellKnowledge header, got {cellKnowledge.Type}.");
        }

        while (!IsHeaderEnd(reader))
        {
            var range = StreamObjectHeaderStart.Parse(reader);
            if (range.Type != StreamObjectTypeHeaderStart.CellKnowledgeRange)
            {
                throw new InvalidDataException("Expected a CellKnowledgeRange.");
            }

            int rangePayloadStart = reader.Position;
            data.CellKnowledgeCellGuid = ExGuid.ReadGuid(reader);
            _ = Compact64bitInt.Deserialize(reader);
            data.CellKnowledgeTo = Compact64bitInt.Deserialize(reader).Value;
            if (reader.Position - rangePayloadStart != range.Length)
            {
                throw new InvalidDataException($"CellKnowledgeRange length {range.Length} does not match encoded payload length {reader.Position - rangePayloadStart}.");
            }
        }

        var cellEnd = StreamObjectHeaderEnd.Parse(reader);
        if (cellEnd.Type != StreamObjectTypeHeaderEnd.CellKnowledge)
        {
            throw new InvalidDataException($"Expected CellKnowledge end, got {cellEnd.Type}.");
        }
    }

    private static void ParseWaterlineKnowledge(BinaryReaderEx reader, QueryChangesSubResponseData data)
    {
        var waterlineKnowledge = StreamObjectHeaderStart.Parse(reader);
        if (waterlineKnowledge.Type != StreamObjectTypeHeaderStart.WaterlineKnowledge)
        {
            throw new InvalidDataException($"Expected WaterlineKnowledge header, got {waterlineKnowledge.Type}.");
        }

        var entry = StreamObjectHeaderStart.Parse(reader);
        if (entry.Type != StreamObjectTypeHeaderStart.WaterlineKnowledgeEntry)
        {
            throw new InvalidDataException("Expected a WaterlineKnowledgeEntry.");
        }

        int entryPayloadStart = reader.Position;
        data.WaterlineCellStorageExtendedGuid = ExGuid.Deserialize(reader);
        data.Waterline = Compact64bitInt.Deserialize(reader).Value;
        _ = Compact64bitInt.Deserialize(reader);
        if (reader.Position - entryPayloadStart != entry.Length)
        {
            throw new InvalidDataException($"WaterlineKnowledgeEntry length {entry.Length} does not match encoded payload length {reader.Position - entryPayloadStart}.");
        }
        var waterlineEnd = StreamObjectHeaderEnd.Parse(reader);
        if (waterlineEnd.Type != StreamObjectTypeHeaderEnd.WaterlineKnowledge)
        {
            throw new InvalidDataException($"Expected WaterlineKnowledge end, got {waterlineEnd.Type}.");
        }
    }
}

/// <summary>
/// Put Changes sub-response data. [MS-FSSHTTPB] section 2.2.2.2.4.
/// </summary>
/// <remarks>
/// The response is a sequence, rather than the fabricated flags/root object
/// that this type used to emit.  The sequence is an optional serial-number
/// reassignment object, an optional <see cref="PutChangesResponse"/>, the
/// mandatory Knowledge object, and an optional diagnostic output object.
/// Knowledge can contain server-specific specializations, so it is retained
/// as its complete framed stream object until a typed model is justified by a
/// capture or the specification.
/// </remarks>
public sealed class PutChangesSubResponseData : ISubResponseData
{
    /// <summary>Raw optional PutChangesResponseSerialNumberReassignAll object.</summary>
    public byte[]? SerialNumberReassignAllBytes { get; set; }

    /// <summary>The optional applied-index and added-data-element response.</summary>
    public PutChangesResponse? PutChangesResponse { get; set; }

    /// <summary>
    /// The mandatory Knowledge stream object, including its start and end
    /// headers.  It is intentionally opaque because the wire allows several
    /// specialization graphs.
    /// </summary>
    public byte[]? KnowledgeBytes { get; set; }

    /// <summary>The optional diagnostic request option output.</summary>
    public DiagnosticRequestOptionOutput? DiagnosticRequestOptionOutput { get; set; }

    /// <summary>Serializes the response sequence to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (SerialNumberReassignAllBytes is { Length: > 0 })
            writer.WriteBytes(SerialNumberReassignAllBytes);

        PutChangesResponse?.Serialize(writer);

        if (KnowledgeBytes is not { Length: > 0 })
        {
            throw new InvalidOperationException(
                "A PutChanges response MUST include the server Knowledge stream object; " +
                "provide KnowledgeBytes from a decoded or captured response.");
        }

        writer.WriteBytes(KnowledgeBytes);
        DiagnosticRequestOptionOutput?.Serialize(writer);
    }

    /// <summary>Deserializes the response sequence from the reader.</summary>
    public static PutChangesSubResponseData Deserialize(BinaryReaderEx reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var data = new PutChangesSubResponseData();

        while (reader.Remaining > 0)
        {
            var type = PeekStartType(reader);
            if (type is null)
                break;

            switch (type.Value)
            {
                case StreamObjectTypeHeaderStart.PutChangesResponseSerialNumberReassignAll:
                    data.SerialNumberReassignAllBytes = ReadFramedObject(reader, type.Value);
                    break;

                case StreamObjectTypeHeaderStart.PutChangesResponseSerialNumberReassign:
                    if (data.SerialNumberReassignAllBytes is null)
                        throw new InvalidDataException("Serial reassignment entry has no reassignment header.");
                    data.SerialNumberReassignAllBytes = [..data.SerialNumberReassignAllBytes, ..ReadFramedObject(reader, type.Value)];
                    break;

                case StreamObjectTypeHeaderStart.PutChangesResponse:
                    data.PutChangesResponse = PutChangesResponse.Deserialize(reader);
                    break;

                case StreamObjectTypeHeaderStart.Knowledge:
                    data.KnowledgeBytes = ReadFramedObject(reader, type.Value);
                    break;

                case StreamObjectTypeHeaderStart.DiagnosticRequestOptionOutput:
                    data.DiagnosticRequestOptionOutput = DiagnosticRequestOptionOutput.Deserialize(reader);
                    break;

                default:
                    throw new InvalidDataException(
                        $"Unexpected PutChanges response object {type.Value} ({(int)type.Value}).");
            }
        }

        if (data.KnowledgeBytes is null)
        {
            throw new InvalidDataException(
                "PutChangesSubResponseData is missing its mandatory Knowledge object.");
        }

        return data;
    }

    private static StreamObjectTypeHeaderStart? PeekStartType(BinaryReaderEx reader)
    {
        if (reader.Remaining <= 0)
            return null;

        int position = reader.Position;
        try
        {
            return StreamObjectHeaderStart.Parse(reader).Type;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException)
        {
            return null;
        }
        finally
        {
            reader.Position = position;
        }
    }

    /// <summary>
    /// Reads one complete framed object, recursively consuming children of a
    /// compound object.  This keeps unknown Knowledge specializations intact.
    /// </summary>
    private static byte[] ReadFramedObject(
        BinaryReaderEx reader,
        StreamObjectTypeHeaderStart expectedType)
    {
        int start = reader.Position;
        var header = StreamObjectHeaderStart.Parse(reader);
        if (header.Type != expectedType)
        {
            throw new InvalidDataException(
                $"Expected {expectedType}, got {header.Type}.");
        }

        if (header.Length > reader.Remaining)
            throw new EndOfStreamException("Stream object payload exceeds the response boundary.");
        reader.Position += header.Length;

        if (header.Compound == 1)
        {
            while (true)
            {
                if (reader.Remaining <= 0)
                    throw new EndOfStreamException(
                        $"Compound stream object {header.Type} has no end header.");

                if (IsMatchingEnd(reader, header.Type))
                {
                    var end = StreamObjectHeaderEnd.Parse(reader);
                    if ((int)end.Type != (int)header.Type)
                    {
                        throw new InvalidDataException(
                            $"Expected {header.Type} end header, got {end.Type}.");
                    }
                    break;
                }

                // The child is consumed for framing only.  Its bytes remain
                // in the outer slice returned below.
                ReadAnyFramedObject(reader, 1);
            }
        }

        int endPosition = reader.Position;
        reader.Position = start;
        byte[] result = reader.ReadBytes(endPosition - start);
        reader.Position = endPosition;
        return result;
    }

    private static void ReadAnyFramedObject(BinaryReaderEx reader, int depth)
    {
        if (depth > 32)
            throw new InvalidDataException("Response object nesting limit exceeded.");
        var header = StreamObjectHeaderStart.Parse(reader);
        if (header.Length > reader.Remaining)
            throw new EndOfStreamException("Stream object payload exceeds the response boundary.");
        reader.Position += header.Length;

        if (header.Compound == 1)
        {
            while (true)
            {
                if (reader.Remaining <= 0)
                    throw new EndOfStreamException(
                        $"Compound stream object {header.Type} has no end header.");
                if (IsMatchingEnd(reader, header.Type))
                {
                    StreamObjectHeaderEnd.Parse(reader);
                    break;
                }
                ReadAnyFramedObject(reader, depth + 1);
            }
        }
    }

    private static bool IsMatchingEnd(
        BinaryReaderEx reader,
        StreamObjectTypeHeaderStart startType)
    {
        int position = reader.Position;
        try
        {
            int discriminator = reader.ReadByte() & 0x03;
            reader.Position = position;
            if (discriminator is not (0x1 or 0x3))
                return false;

            var end = StreamObjectHeaderEnd.Parse(reader);
            return (int)end.Type == (int)startType;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException)
        {
            return false;
        }
        finally
        {
            reader.Position = position;
        }
    }
}

/// <summary>
/// The optional PutChangesResponse stream object.  It contains the applied
/// storage-index identifier and an ExGUID array of added data elements.
/// </summary>
public sealed class PutChangesResponse
{
    /// <summary>The storage index applied by the server, or null.</summary>
    public ExGuid AppliedStorageIndexID { get; set; } = ExGuid.Null;

    /// <summary>Data-element identifiers returned by the server.</summary>
    public List<ExGuid> DataElementAdded { get; } = [];

    /// <summary>
    /// Any bytes after the decoded ExGUID array inside the declared object
    /// payload.  SharePoint emits these in the captured save response; keep
    /// them so a capture can be replayed byte-for-byte.
    /// </summary>
    public byte[] TrailingPayloadBytes { get; private set; } = [];

    /// <summary>The original framed object, when this value was decoded.</summary>
    public byte[]? RawBytes { get; private set; }

    /// <summary>Serializes the object, retaining original framing when available.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (RawBytes is { Length: > 0 })
        {
            writer.WriteBytes(RawBytes);
            return;
        }

        var payload = new BinaryWriterEx();
        AppliedStorageIndexID.Serialize(payload);
        new Compact64bitInt((ulong)DataElementAdded.Count).Serialize(payload);
        foreach (var id in DataElementAdded)
            id.Serialize(payload);
        payload.WriteBytes(TrailingPayloadBytes);

        byte[] bytes = payload.ToArray();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.PutChangesResponse, bytes.Length)
            .Serialize(writer);
        writer.WriteBytes(bytes);
    }

    /// <summary>Deserializes one PutChangesResponse stream object.</summary>
    public static PutChangesResponse Deserialize(BinaryReaderEx reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        int start = reader.Position;
        var header = StreamObjectHeaderStart.Parse(reader);
        if (header.Type != StreamObjectTypeHeaderStart.PutChangesResponse)
            throw new InvalidDataException($"Expected PutChangesResponse header, got {header.Type}.");

        int payloadStart = reader.Position;
        int payloadEnd = checked(payloadStart + header.Length);
        if (payloadEnd > reader.Length)
            throw new EndOfStreamException("PutChangesResponse payload exceeds the response boundary.");

        reader.Position = start;
        byte[] raw = reader.ReadBytes(payloadEnd - start);
        reader.Position = payloadStart;

        var result = new PutChangesResponse { RawBytes = raw };
        try
        {
            result.AppliedStorageIndexID = ExGuid.Deserialize(reader);
            ulong count = Compact64bitInt.Deserialize(reader).Value;
            if (count > int.MaxValue)
                throw new InvalidDataException($"PutChangesResponse ExGUID array is too large: {count}.");
            for (ulong i = 0; i < count; i++)
                result.DataElementAdded.Add(ExGuid.Deserialize(reader));
            result.TrailingPayloadBytes = reader.ReadBytes(payloadEnd - reader.Position);
        }
        catch
        {
            // Retain the complete framed object for replay even if a future
            // server adds fields we do not yet understand.
            reader.Position = payloadEnd;
            throw;
        }

        reader.Position = payloadEnd;
        return result;
    }
}

/// <summary>The optional DiagnosticRequestOptionOutput stream object.</summary>
public sealed class DiagnosticRequestOptionOutput
{
    /// <summary>Whether diagnostic output was requested.</summary>
    public bool IsDiagnosticRequestOptionOutput { get; set; }

    /// <summary>Reserved bits, retained for round-trip fidelity.</summary>
    public byte Reserved { get; set; }

    /// <summary>Serializes the one-byte diagnostic option object.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        byte flags = (byte)((IsDiagnosticRequestOptionOutput ? 1 : 0) | (Reserved & 0xFE));
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.DiagnosticRequestOptionOutput, 1)
            .Serialize(writer);
        writer.WriteByte(flags);
    }

    /// <summary>Deserializes the one-byte diagnostic option object.</summary>
    public static DiagnosticRequestOptionOutput Deserialize(BinaryReaderEx reader)
    {
        var header = StreamObjectHeaderStart.Parse(reader);
        if (header.Type != StreamObjectTypeHeaderStart.DiagnosticRequestOptionOutput)
            throw new InvalidDataException($"Expected DiagnosticRequestOptionOutput header, got {header.Type}.");
        if (header.Length != 1)
            throw new InvalidDataException($"DiagnosticRequestOptionOutput length must be 1, got {header.Length}.");

        byte flags = reader.ReadByte();
        return new DiagnosticRequestOptionOutput
        {
            IsDiagnosticRequestOptionOutput = (flags & 1) != 0,
            Reserved = (byte)(flags & 0xFE),
        };
    }
}

/// <summary>
/// Placeholder for sub-response data types not yet implemented.
/// </summary>
public sealed class UnknownSubResponseData : ISubResponseData
{
    /// <summary>Serializes nothing to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
    }
}
