namespace OfficeCollabServer.FssHttpB;

/// <summary>
/// The FSSHTTPB cell request � the binary payload of the SOAP Cell subrequest.
/// [MS-FSSHTTPB] section 2.2.2.1.1.
/// Wire layout: ProtocolVersion (2), MinimumVersion (2), Signature (8),
/// RequestStart header, UserAgent compound object, optional RequestHashOptions,
/// sub-requests, optional DataElementPackage, RequestEnd header.
/// </summary>
public sealed class FsshttpbCellRequest
{
    /// <summary>Constant request signature.</summary>
    public const ulong RequestSignature = 0x9B069439F329CF9C;

    /// <summary>Current protocol schema version.</summary>
    public const ushort CurrentProtocolVersion = 12;

    /// <summary>Minimum compatible protocol schema version.</summary>
    public const ushort MinimumProtocolVersion = 11;

    /// <summary>Creates an empty cell request with the required header values.</summary>
    public FsshttpbCellRequest()
    {
        ProtocolVersion = CurrentProtocolVersion;
        MinimumVersion = MinimumProtocolVersion;
        Signature = RequestSignature;
        UserAgentGuid = Guid.Empty;
        UserAgentVersionValue = 0;
        SubRequests = new List<FsshttpbCellSubRequest>();
    }

    /// <summary>Protocol schema version (MUST be 12).</summary>
    public ushort ProtocolVersion { get; set; }

    /// <summary>Oldest compatible schema version (MUST be 11).</summary>
    public ushort MinimumVersion { get; set; }

    /// <summary>Constant request signature (0x9B069439F329CF9C).</summary>
    public ulong Signature { get; set; }

    /// <summary>User agent GUID.</summary>
    public Guid UserAgentGuid { get; set; }

    /// <summary>User agent version.</summary>
    public uint UserAgentVersionValue { get; set; }

    /// <summary>The sub-requests (MUST contain at least one).</summary>
    public List<FsshttpbCellSubRequest> SubRequests { get; set; }

    /// <summary>Optional data element package carrying data for Put Changes.</summary>
    public DataElementPackage? DataElementPackage { get; set; }

    /// <summary>Serializes the cell request to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        writer.WriteUInt16(ProtocolVersion);
        writer.WriteUInt16(MinimumVersion);
        writer.WriteUInt64(Signature);

        // Request compound object start.
        var requestStart = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.Request, 0);
        requestStart.Serialize(writer);

        // User Agent compound object.
        var userAgentStart = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.UserAgent, 0);
        userAgentStart.Serialize(writer);

        var userAgentGuidHeader = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.UserAgentGUID, 16);
        userAgentGuidHeader.Serialize(writer);
        ExGuid.WriteGuid(writer, UserAgentGuid);

        var userAgentVersionHeader = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.UserAgentVersion, 4);
        userAgentVersionHeader.Serialize(writer);
        writer.WriteUInt32(UserAgentVersionValue);

        var userAgentEnd = new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.UserAgent);
        userAgentEnd.Serialize(writer);

        // Sub-requests.
        if (SubRequests.Count == 0)
        {
            throw new InvalidOperationException("An FSSHTTPB cell request MUST contain at least one sub-request.");
        }

        foreach (var subRequest in SubRequests)
        {
            subRequest.Serialize(writer);
        }

        // Optional data element package.
        DataElementPackage?.Serialize(writer);

        // Request compound object end.
        var requestEnd = new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.Request);
        requestEnd.Serialize(writer);
    }

    /// <summary>Determines whether the next header at this position is a header end (vs a start).</summary>
    private static bool IsHeaderEnd(BinaryReaderEx reader)
    {
        int pos = reader.Position;
        int discriminator = reader.ReadByte() & 0x03;
        reader.Position = pos;
        return discriminator == 0x1 || discriminator == 0x3;
    }

    /// <summary>
    /// Skips an object after its start header has been consumed. A compound
    /// object's declared length is its fixed preamble; child stream objects
    /// follow the preamble and terminate with the matching end header.
    /// </summary>
    private static void SkipObject(BinaryReaderEx reader, StreamObjectHeaderStart start)
    {
        reader.Skip(start.Length);
        if (start.Compound != 1)
        {
            return;
        }

        StreamObjectTypeHeaderEnd expectedEnd = (StreamObjectTypeHeaderEnd)(int)start.Type;
        while (!IsHeaderEnd(reader))
        {
            SkipObject(reader, StreamObjectHeaderStart.Parse(reader));
        }

        var end = StreamObjectHeaderEnd.Parse(reader);
        if (end.Type != expectedEnd)
        {
            throw new InvalidDataException($"Expected {expectedEnd} end header, got {end.Type}.");
        }
    }

    /// <summary>Skips remaining children of a compound object and its end header.</summary>
    private static void SkipToCompoundEnd(BinaryReaderEx reader, StreamObjectTypeHeaderEnd expectedEnd)
    {
        while (!IsHeaderEnd(reader))
        {
            SkipObject(reader, StreamObjectHeaderStart.Parse(reader));
        }

        var end = StreamObjectHeaderEnd.Parse(reader);
        if (end.Type != expectedEnd)
        {
            throw new InvalidDataException($"Expected {expectedEnd} end header, got {end.Type}.");
        }
    }

    /// <summary>Serializes the cell request to a byte array.</summary>
    public byte[] ToByteArray()
    {
        var writer = new BinaryWriterEx();
        Serialize(writer);
        return writer.ToArray();
    }

    /// <summary>Serializes the cell request to a base64 string (as used in the SOAP payload).</summary>
    public string ToBase64() => Convert.ToBase64String(ToByteArray());

    /// <summary>
    /// Deserializes a cell request from the reader. The reader must be
    /// positioned at the Protocol Version field.
    /// </summary>
    public static FsshttpbCellRequest Deserialize(BinaryReaderEx reader)
    {
        var request = new FsshttpbCellRequest
        {
            ProtocolVersion = reader.ReadUInt16(),
            MinimumVersion = reader.ReadUInt16(),
            Signature = reader.ReadUInt64(),
        };

        if (request.Signature != RequestSignature)
        {
            throw new InvalidDataException(
                $"Invalid request signature 0x{request.Signature:X16}; expected 0x{RequestSignature:X16}.");
        }

        // Request start header.
        var requestStart = StreamObjectHeaderStart.Parse(reader);
        if (requestStart.Type != StreamObjectTypeHeaderStart.Request)
        {
            throw new InvalidDataException($"Expected Request header, got {requestStart.Type}.");
        }

        // Compatibility: captured Word inline QueryAccess omits UserAgent.
        int userAgentPosition = reader.Position;
        var userAgentStart = StreamObjectHeaderStart.Parse(reader);
        if (userAgentStart.Type == StreamObjectTypeHeaderStart.UserAgent)
        {
            bool hasUserAgentGuid = false;
            bool hasUserAgentVersion = false;
            while (!IsHeaderEnd(reader))
            {
                var header = StreamObjectHeaderStart.Parse(reader);
                if (header.Type == StreamObjectTypeHeaderStart.UserAgentGUID && header.Length == 16)
                {
                    request.UserAgentGuid = ExGuid.ReadGuid(reader);
                    hasUserAgentGuid = true;
                }
                else if (header.Type == StreamObjectTypeHeaderStart.UserAgentVersion && header.Length == 4)
                {
                    request.UserAgentVersionValue = reader.ReadUInt32();
                    hasUserAgentVersion = true;
                }
                else
                {
                    // Word includes UserAgentClientandPlatform (0x8B), which is
                    // not emitted by the minimal Interop-TestSuites serializer.
                    SkipObject(reader, header);
                }
            }

            if (!hasUserAgentGuid || !hasUserAgentVersion)
            {
                throw new InvalidDataException("UserAgent is missing its GUID or version field.");
            }

            SkipToCompoundEnd(reader, StreamObjectTypeHeaderEnd.UserAgent);
        }
        else
        {
            reader.Position = userAgentPosition;
        }

        // Sub-requests until the Request end header.
        request.SubRequests = new List<FsshttpbCellSubRequest>();
        while (reader.Remaining > 0 && !IsHeaderEnd(reader))
        {
            var header = StreamObjectHeaderStart.Parse(reader);
            if (header.Type == StreamObjectTypeHeaderStart.SubRequest)
            {
                request.SubRequests.Add(FsshttpbCellSubRequest.Deserialize(reader, header));
            }
            else if (header.Type == StreamObjectTypeHeaderStart.DataElementPackage)
            {
                reader.Position -= header.HeaderSize;
                request.DataElementPackage = DataElementPackage.Deserialize(reader);
            }
            else
            {
                // Ignore optional request-level client extensions such as
                // CellRoundtripOptions (0x8D) while preserving alignment.
                SkipObject(reader, header);
            }
        }

        SkipToCompoundEnd(reader, StreamObjectTypeHeaderEnd.Request);

        return request;
    }
}

/// <summary>
/// A cell sub-request within an FSSHTTPB cell request.
/// [MS-FSSHTTPB] section 2.2.2.1.1.1.
/// Wire layout: SubRequestStart header, RequestType compact uint, SubRequestData,
/// SubRequestEnd header.
/// </summary>
public sealed class FsshttpbCellSubRequest
{
    /// <summary>Creates a sub-request with the given request type.</summary>
    public FsshttpbCellSubRequest(RequestTypes requestType)
    {
        RequestType = requestType;
    }

    /// <summary>The sub-request ID.</summary>
    public ulong RequestId { get; set; }

    /// <summary>The sub-request type.</summary>
    public RequestTypes RequestType { get; set; }

    /// <summary>The sub-request priority (compact uint, typically 0).</summary>
    public ulong Priority { get; set; }

    /// <summary>The sub-request data (type-specific payload).</summary>
    public ISubRequestData? Data { get; set; }

    /// <summary>Serializes the sub-request to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        // Serialize the payload first to learn its length.
        var payloadWriter = new BinaryWriterEx();
        new Compact64bitInt(RequestId).Serialize(payloadWriter);
        new Compact64bitInt((ulong)RequestType).Serialize(payloadWriter);
        new Compact64bitInt(Priority).Serialize(payloadWriter);
        byte[] payload = payloadWriter.ToArray();

        var start = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.SubRequest, payload.Length);
        start.Serialize(writer);
        writer.WriteBytes(payload);
        Data?.Serialize(writer);

        var end = new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.SubRequest);
        end.Serialize(writer);
    }

    /// <summary>Deserializes a sub-request from the reader.</summary>
    public static FsshttpbCellSubRequest Deserialize(BinaryReaderEx reader)
    {
        var start = StreamObjectHeaderStart.Parse(reader);
        return Deserialize(reader, start);
    }

    internal static FsshttpbCellSubRequest Deserialize(BinaryReaderEx reader, StreamObjectHeaderStart start)
    {
        if (start.Type != StreamObjectTypeHeaderStart.SubRequest)
        {
            throw new InvalidDataException($"Expected SubRequest header, got {start.Type}.");
        }

        int preambleStart = reader.Position;
        int preambleEnd = preambleStart + start.Length;
        var requestId = Compact64bitInt.Deserialize(reader).Value;
        var requestType = (RequestTypes)Compact64bitInt.Deserialize(reader).Value;
        var priority = Compact64bitInt.Deserialize(reader).Value;

        if (reader.Position != preambleEnd)
        {
            throw new InvalidDataException(
                $"SubRequest preamble consumed {reader.Position - preambleStart} bytes but declared {start.Length}.");
        }

        FsshttpbCellSubRequest subRequest = requestType switch
        {
            RequestTypes.QueryAccess => new FsshttpbCellSubRequest(requestType)
            {
                RequestId = requestId,
                Priority = priority,
                Data = QueryAccessSubRequestData.Deserialize(reader),
            },
            RequestTypes.QueryChanges => new FsshttpbCellSubRequest(requestType)
            {
                RequestId = requestId,
                Priority = priority,
                Data = QueryChangesSubRequestData.Deserialize(reader),
            },
            RequestTypes.PutChanges => new FsshttpbCellSubRequest(requestType)
            {
                RequestId = requestId,
                Priority = priority,
                Data = PutChangesSubRequestData.Deserialize(reader),
            },
            _ => new FsshttpbCellSubRequest(requestType)
            {
                RequestId = requestId,
                Priority = priority,
            },
        };

        SkipToSubRequestEnd(reader, subRequest);

        return subRequest;
    }

    private static void SkipToSubRequestEnd(BinaryReaderEx reader, FsshttpbCellSubRequest subRequest)
    {
        while (!IsHeaderEnd(reader))
        {
            var header = StreamObjectHeaderStart.Parse(reader);
            if (header.Type == StreamObjectTypeHeaderStart.PutChangesLockId && subRequest.Data is PutChangesSubRequestData put)
            {
                if (header.Length != 16 || header.Compound != 0)
                    throw new InvalidDataException("Put Changes lock ID must contain one GUID.");
                put.LockId = ExGuid.ReadGuid(reader);
            }
            else if (header.Type == StreamObjectTypeHeaderStart.AdditionalFlags && subRequest.Data is PutChangesSubRequestData additionalPut)
            {
                if (header.Compound != 0 || header.Length < 2)
                    throw new InvalidDataException("AdditionalFlags must contain at least two flag bytes.");
                byte[] bytes = reader.ReadBytes(header.Length);
                additionalPut.HasAdditionalFlags = true;
                additionalPut.AdditionalFlagsBits = (ushort)(bytes[0] | (bytes[1] << 8));
                additionalPut.AdditionalFlagsTrailingBytes = bytes.Length > 2 ? bytes[2..] : [];
            }
            else
            {
                SkipObject(reader, header);
            }
        }

        var end = StreamObjectHeaderEnd.Parse(reader);
        if (end.Type != StreamObjectTypeHeaderEnd.SubRequest)
        {
            throw new InvalidDataException($"Expected SubRequest end header, got {end.Type}.");
        }
    }

    private static bool IsHeaderEnd(BinaryReaderEx reader)
    {
        int position = reader.Position;
        int discriminator = reader.ReadByte() & 0x03;
        reader.Position = position;
        return discriminator is 0x1 or 0x3;
    }

    private static void SkipObject(BinaryReaderEx reader, StreamObjectHeaderStart start)
    {
        reader.Skip(start.Length);
        if (start.Compound != 1)
        {
            return;
        }

        StreamObjectTypeHeaderEnd expectedEnd = (StreamObjectTypeHeaderEnd)(int)start.Type;
        while (!IsHeaderEnd(reader))
        {
            SkipObject(reader, StreamObjectHeaderStart.Parse(reader));
        }

        var end = StreamObjectHeaderEnd.Parse(reader);
        if (end.Type != expectedEnd)
        {
            throw new InvalidDataException($"Expected {expectedEnd} end header, got {end.Type}.");
        }
    }
}

/// <summary>
/// Marker interface for sub-request data payloads.
/// </summary>
public interface ISubRequestData
{
    /// <summary>Serializes the payload to the writer.</summary>
    void Serialize(BinaryWriterEx writer);
}

/// <summary>
/// Query Access sub-request data (empty). [MS-FSSHTTPB] section 2.2.2.1.2.
/// </summary>
public sealed class QueryAccessSubRequestData : ISubRequestData
{
    /// <summary>Serializes the payload (nothing) to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
    }

    /// <summary>Deserializes the payload (nothing) from the reader.</summary>
    public static QueryAccessSubRequestData Deserialize(BinaryReaderEx reader) => new();
}

/// <summary>
/// Query Changes sub-request data. [MS-FSSHTTPB] section 2.2.2.1.3.
/// </summary>
public sealed class QueryChangesSubRequestData : ISubRequestData
{
    /// <summary>Whether the response should include the storage manifest.</summary>
    public bool IncludeStorageManifest { get; set; }

    /// <summary>Whether the response should include cell changes.</summary>
    public bool IncludeCellChanges { get; set; }

    /// <summary>Whether the request permits fragmented data elements.</summary>
    public bool AllowFragments { get; set; }

    /// <summary>Whether the client requested a file hash in the response.</summary>
    public bool ReturnFileHash { get; set; }

    /// <summary>Optional query changes request arguments header + storage manifest root.</summary>
    public ExGuid? StorageManifestRoot { get; set; }

    /// <summary>Optional cell ID scoping the query.</summary>
    public CellId? CellId { get; set; }

    /// <summary>Optional maximum serialized data-element size in bytes.</summary>
    public ulong? MaxDataElements { get; set; }

    /// <summary>Optional versioning header + waterline.</summary>
    public ulong? Waterline { get; set; }

    /// <summary>Serializes the payload to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        // Flags are payload bytes, not the stream object's declared length.
        int requestFlags = (AllowFragments ? 0x02 : 0) | (ReturnFileHash ? 0x40 : 0);
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.QueryChangesRequest, 1).Serialize(writer);
        writer.WriteByte((byte)requestFlags);

        if (IncludeStorageManifest || IncludeCellChanges || StorageManifestRoot is not null || CellId is not null)
        {
            int argumentFlags = (IncludeStorageManifest ? 0x01 : 0) | (IncludeCellChanges ? 0x02 : 0);
            var payload = new BinaryWriterEx();
            payload.WriteByte((byte)argumentFlags);
            (CellId ?? new CellId(ExGuid.Null, ExGuid.Null)).Serialize(payload);
            var args = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.QueryChangesRequestArguments, payload.Length);
            args.Serialize(writer);
            writer.WriteBytes(payload.ToArray());
        }

        if (CellId is not null)
        {
            var cellIdHeader = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.QueryChangesFilterCellID, 0);
            cellIdHeader.Serialize(writer);
            CellId.Serialize(writer);
        }

        if (MaxDataElements is not null)
        {
            var constraint = new BinaryWriterEx();
            new Compact64bitInt(MaxDataElements.Value).Serialize(constraint);
            var constraintHeader = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.QueryChangesDataConstraint, constraint.Length);
            constraintHeader.Serialize(writer);
            writer.WriteBytes(constraint.ToArray());
        }

        if (Waterline is not null)
        {
            var versioningHeader = new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.QueryChangesVersioning, 0);
            versioningHeader.Serialize(writer);
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.WaterlineKnowledge, 0).Serialize(writer);
            new Compact64bitInt(Waterline.Value).Serialize(writer);
        }
    }

    /// <summary>Deserializes the QueryChanges header and its optional request objects.</summary>
    public static QueryChangesSubRequestData Deserialize(BinaryReaderEx reader)
    {
        var data = new QueryChangesSubRequestData();

        var requestHeader = StreamObjectHeaderStart.Parse(reader);
        if (requestHeader.Type != StreamObjectTypeHeaderStart.QueryChangesRequest)
        {
            throw new InvalidDataException($"Expected QueryChangesRequest header, got {requestHeader.Type}.");
        }

        byte[] requestPayload = reader.ReadBytes(requestHeader.Length);
        int requestFlags = requestPayload.Length > 0 ? requestPayload[0] : 0;
        data.AllowFragments = (requestFlags & 0x02) != 0 || (requestFlags & 0x10) != 0;
        data.ReturnFileHash = (requestFlags & 0x40) != 0;

        while (!IsHeaderEnd(reader))
        {
            var header = StreamObjectHeaderStart.Parse(reader);
            switch (header.Type)
            {
                case StreamObjectTypeHeaderStart.QueryChangesRequestArguments:
                {
                    byte[] argumentsPayload = reader.ReadBytes(header.Length);
                    if (argumentsPayload.Length > 0)
                    {
                        data.IncludeStorageManifest = (argumentsPayload[0] & 0x01) != 0;
                        data.IncludeCellChanges = (argumentsPayload[0] & 0x02) != 0;
                    }

                    if (argumentsPayload.Length > 1)
                    {
                        data.CellId = CellId.Deserialize(new BinaryReaderEx(
                            argumentsPayload, 1, argumentsPayload.Length - 1));
                    }
                    break;
                }

                case StreamObjectTypeHeaderStart.QueryChangesDataConstraint:
                {
                    byte[] constraintPayload = reader.ReadBytes(header.Length);
                    if (constraintPayload.Length > 0)
                    {
                        data.MaxDataElements = Compact64bitInt.Deserialize(
                            new BinaryReaderEx(constraintPayload)).Value;
                    }
                    break;
                }

                case StreamObjectTypeHeaderStart.QueryChangesVersioning:
                    SkipObject(reader, header);
                    break;

                case StreamObjectTypeHeaderStart.Knowledge:
                    SkipObject(reader, header);
                    break;

                default:
                    SkipObject(reader, header);
                    break;
            }
        }

        return data;
    }

    private static bool IsHeaderEnd(BinaryReaderEx reader)
    {
        int position = reader.Position;
        int discriminator = reader.ReadByte() & 0x03;
        reader.Position = position;
        return discriminator is 0x1 or 0x3;
    }

    private static void SkipObject(BinaryReaderEx reader, StreamObjectHeaderStart start)
    {
        reader.Skip(start.Length);
        if (start.Compound != 1)
        {
            return;
        }

        StreamObjectTypeHeaderEnd expectedEnd = (StreamObjectTypeHeaderEnd)(int)start.Type;
        while (!IsHeaderEnd(reader))
        {
            SkipObject(reader, StreamObjectHeaderStart.Parse(reader));
        }

        var end = StreamObjectHeaderEnd.Parse(reader);
        if (end.Type != expectedEnd)
        {
            throw new InvalidDataException($"Expected {expectedEnd} end header, got {end.Type}.");
        }
    }
}

/// <summary>
/// Put Changes sub-request data. [MS-FSSHTTPB] section 2.2.2.1.4.
/// </summary>
public sealed class PutChangesSubRequestData : ISubRequestData
{
    /// <summary>The storage index containing the proposed mappings.</summary>
    public ExGuid StorageIndex { get; set; } = ExGuid.Null;

    /// <summary>The index against which the client computed its changes.</summary>
    public ExGuid ExpectedStorageIndex { get; set; } = ExGuid.Null;

    /// <summary>Put Changes flags from MS-FSSHTTPB 2.2.2.1.4.</summary>
    public byte Flags { get; set; }

    /// <summary>Opaque content-version coherency information.</summary>
    public byte[] ContentVersionCoherencyCheck { get; set; } = [];

    /// <summary>Author logins supplied by the client.</summary>
    public List<string> AuthorLogins { get; set; } = [];

    /// <summary>Optional lock ID GUID.</summary>
    public Guid? LockId { get; set; }

    /// <summary>Whether the optional AdditionalFlags object was present.</summary>
    public bool HasAdditionalFlags { get; set; }

    /// <summary>The defined AdditionalFlags bits in wire bit order.</summary>
    public ushort AdditionalFlagsBits { get; set; }

    /// <summary>Bytes following the two AdditionalFlags bit bytes.</summary>
    public byte[] AdditionalFlagsTrailingBytes { get; set; } = [];

    /// <summary>Whether the request asks the server to check reused IDs.</summary>
    public bool CheckForIdReuse => HasAdditionalFlags && (AdditionalFlagsBits & (1 << 2)) != 0;

    /// <summary>Serializes the payload to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        var payload = new BinaryWriterEx();
        StorageIndex.Serialize(payload);
        ExpectedStorageIndex.Serialize(payload);
        payload.WriteByte(Flags);
        new BinaryItem(ContentVersionCoherencyCheck).Serialize(payload);
        new Compact64bitInt((ulong)AuthorLogins.Count).Serialize(payload);
        foreach (var author in AuthorLogins) new StringItem(author).Serialize(payload);
        payload.WriteByte(0);
        var bytes = payload.ToArray();
        var putHeader = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.PutChangesRequest, bytes.Length);
        putHeader.Serialize(writer);
        writer.WriteBytes(bytes);

        if (HasAdditionalFlags)
        {
            byte[] additional = AdditionalFlagsTrailingBytes.Length == 0
                ? [(byte)(AdditionalFlagsBits & 0xFF), (byte)(AdditionalFlagsBits >> 8), 0]
                : [(byte)(AdditionalFlagsBits & 0xFF), (byte)(AdditionalFlagsBits >> 8), .. AdditionalFlagsTrailingBytes];
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.AdditionalFlags, additional.Length)
                .Serialize(writer);
            writer.WriteBytes(additional);
        }

        if (LockId is not null)
        {
            var lockHeader = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.PutChangesLockId, 16);
            lockHeader.Serialize(writer);
            ExGuid.WriteGuid(writer, LockId.Value);
        }

    }

    /// <summary>Deserializes the fixed Put Changes fields within their declared boundary.</summary>
    public static PutChangesSubRequestData Deserialize(BinaryReaderEx reader)
    {
        var data = new PutChangesSubRequestData();

        var putHeader = StreamObjectHeaderStart.Parse(reader);
        if (putHeader.Type != StreamObjectTypeHeaderStart.PutChangesRequest)
        {
            throw new InvalidDataException($"Expected PutChangesRequest header, got {putHeader.Type}.");
        }

        var payload = new BinaryReaderEx(reader.ReadBytes(putHeader.Length));
        data.StorageIndex = ExGuid.Deserialize(payload);
        data.ExpectedStorageIndex = ExGuid.Deserialize(payload);
        data.Flags = payload.ReadByte();
        data.ContentVersionCoherencyCheck = BinaryItem.Deserialize(payload).Content;
        var count = Compact64bitInt.Deserialize(payload).Value;
        if (count > (ulong)payload.Remaining)
            throw new InvalidDataException("Author login count exceeds the Put Changes payload.");
        for (ulong i = 0; i < count; i++)
            data.AuthorLogins.Add(StringItem.Deserialize(payload).Value);
        payload.ReadByte(); // Reserved; ignored on receipt.
        if (payload.Remaining != 0)
            throw new InvalidDataException("Unexpected trailing bytes in Put Changes fixed fields.");

        return data;
    }
}
