namespace CellBridge.FssHttpB;

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

    /// <summary>Protocol schema version, defined values 12, 13 and 14.</summary>
    public ushort ProtocolVersion { get; set; }

    /// <summary>Oldest compatible schema version (MUST be 11).</summary>
    public ushort MinimumVersion { get; set; }

    /// <summary>Constant request signature (0x9B069439F329CF9C).</summary>
    public ulong Signature { get; set; }

    /// <summary>User agent GUID.</summary>
    public Guid UserAgentGuid { get; set; }

    /// <summary>Optional client/platform identity, replacing the GUID when serialized.</summary>
    public UserAgentClientAndPlatform? UserAgentClientAndPlatform { get; set; }

    /// <summary>User agent version.</summary>
    public uint UserAgentVersionValue { get; set; }

    /// <summary>Optional request-wide schema-1 hash negotiation.</summary>
    public RequestHashOptions? HashOptions { get; set; }

    /// <summary>Checks the advertised request envelope before any operation executes.</summary>
    public bool HasValidEnvelope => ProtocolVersion is 12 or 13 or 14 && MinimumVersion == 11 &&
        Signature == RequestSignature && (HashOptions is null || HashOptions.Schema == 1);

    /// <summary>Validates model-level fields before any operation executes.</summary>
    public bool TryGetValidationError(out CellErrorCode code, out string message)
    {
        if (ProtocolVersion is not (12 or 13 or 14) || MinimumVersion != 11)
        {
            code = CellErrorCode.IncompatibleProtocolVersion;
            message = "The request protocol version is incompatible.";
            return true;
        }
        if (Signature != RequestSignature || HashOptions is { Schema: not 1 } || SubRequests.Count == 0)
        {
            code = CellErrorCode.RequestStreamSchemaError;
            message = "The cell request envelope is malformed.";
            return true;
        }

        var ids = new HashSet<ulong>();
        foreach (var subRequest in SubRequests)
        {
            if (subRequest.RequestId >= uint.MaxValue || !ids.Add(subRequest.RequestId) ||
                subRequest.RequestType switch
                {
                    RequestTypes.QueryAccess => subRequest.Data is not null and not QueryAccessSubRequestData,
                    RequestTypes.QueryChanges => subRequest.Data is not QueryChangesSubRequestData,
                    RequestTypes.PutChanges => subRequest.Data is not PutChangesSubRequestData,
                    RequestTypes.AllocateExtendedGuidRange => subRequest.Data is not AllocateExtendedGuidRangeSubRequestData,
                    _ => false,
                })
            {
                code = CellErrorCode.RequestStreamSchemaError;
                message = "A subrequest has a duplicate or out-of-range ID, or invalid operation data.";
                return true;
            }
        }
        code = default;
        message = string.Empty;
        return false;
    }

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

        if (UserAgentClientAndPlatform is { } identity)
            identity.Serialize(writer);
        else
        {
            var userAgentGuidHeader = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.UserAgentGUID, 16);
            userAgentGuidHeader.Serialize(writer);
            ExGuid.WriteGuid(writer, UserAgentGuid);
        }

        var userAgentVersionHeader = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.UserAgentVersion, 4);
        userAgentVersionHeader.Serialize(writer);
        writer.WriteUInt32(UserAgentVersionValue);

        var userAgentEnd = new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.UserAgent);
        userAgentEnd.Serialize(writer);

        HashOptions?.Serialize(writer);

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
    private static void SkipObject(BinaryReaderEx reader, StreamObjectHeaderStart start, int depth = 0)
    {
        if (depth > 32) throw new InvalidDataException("Binary object nesting limit exceeded.");
        reader.Skip(start.Length);
        if (start.Compound != 1)
        {
            return;
        }

        StreamObjectTypeHeaderEnd expectedEnd = (StreamObjectTypeHeaderEnd)(int)start.Type;
        while (!IsHeaderEnd(reader))
        {
            SkipObject(reader, StreamObjectHeaderStart.Parse(reader), depth + 1);
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
        if (requestStart.Type != StreamObjectTypeHeaderStart.Request ||
            requestStart.HeaderType != StreamObjectHeaderStart.HeaderType32Bit || requestStart.Compound != 1 || requestStart.Length != 0)
        {
            throw new InvalidDataException("Expected a zero-length 32-bit compound Request header.");
        }

        // Compatibility: captured Word inline QueryAccess omits UserAgent.
        int userAgentPosition = reader.Position;
        var userAgentStart = StreamObjectHeaderStart.Parse(reader);
        if (userAgentStart.Type == StreamObjectTypeHeaderStart.UserAgent)
        {
            if (userAgentStart.HeaderType != StreamObjectHeaderStart.HeaderType32Bit ||
                userAgentStart.Compound != 1 || userAgentStart.Length != 0)
                throw new InvalidDataException("Expected a zero-length 32-bit compound UserAgent header.");
            bool hasUserAgentGuid = false;
            bool hasUserAgentVersion = false;
            while (!IsHeaderEnd(reader))
            {
                var header = StreamObjectHeaderStart.Parse(reader);
                if (header.Type == StreamObjectTypeHeaderStart.UserAgentGUID)
                {
                    if (hasUserAgentGuid || header.HeaderType != StreamObjectHeaderStart.HeaderType32Bit ||
                        header.Length != 16 || header.Compound != 0)
                        throw new InvalidDataException("Invalid or duplicate user agent GUID.");
                    request.UserAgentGuid = ExGuid.ReadGuid(reader);
                    hasUserAgentGuid = true;
                }
                else if (header.Type == StreamObjectTypeHeaderStart.UserAgentClientandPlatform)
                {
                    if (request.UserAgentClientAndPlatform is not null)
                        throw new InvalidDataException("Duplicate user agent client and platform.");
                    request.UserAgentClientAndPlatform = UserAgentClientAndPlatform.Deserialize(reader, header);
                }
                else if (header.Type == StreamObjectTypeHeaderStart.UserAgentVersion)
                {
                    if (hasUserAgentVersion || header.HeaderType != StreamObjectHeaderStart.HeaderType32Bit ||
                        header.Length != 4 || header.Compound != 0)
                        throw new InvalidDataException("Invalid or duplicate user agent version.");
                    request.UserAgentVersionValue = reader.ReadUInt32();
                    hasUserAgentVersion = true;
                }
                else
                {
                    SkipObject(reader, header);
                }
            }

            if ((!hasUserAgentGuid && request.UserAgentClientAndPlatform is null) || !hasUserAgentVersion)
            {
                throw new InvalidDataException("UserAgent is missing its identity or version field.");
            }

            SkipToCompoundEnd(reader, StreamObjectTypeHeaderEnd.UserAgent);
        }
        else
        {
            reader.Position = userAgentPosition;
        }

        // Sub-requests until the Request end header.
        request.SubRequests = new List<FsshttpbCellSubRequest>();
        bool hasRoundtripOptions = false;
        bool hasDataElementPackage = false;
        while (reader.Remaining > 0 && !IsHeaderEnd(reader))
        {
            var header = StreamObjectHeaderStart.Parse(reader);
            if (header.Type == StreamObjectTypeHeaderStart.SubRequest)
            {
                if (hasDataElementPackage)
                    throw new InvalidDataException("Subrequests must precede the data element package.");
                if (request.SubRequests.Count >= 1024) throw new InvalidDataException("Binary subrequest limit exceeded.");
                request.SubRequests.Add(FsshttpbCellSubRequest.Deserialize(reader, header));
            }
            else if (header.Type == StreamObjectTypeHeaderStart.DataElementPackage)
            {
                if (hasDataElementPackage) throw new InvalidDataException("Duplicate data element package.");
                hasDataElementPackage = true;
                reader.Position -= header.HeaderSize;
                request.DataElementPackage = DataElementPackage.Deserialize(reader);
            }
            else if (header.Type == StreamObjectTypeHeaderStart.RequestHashOptions)
            {
                if (request.HashOptions is not null || hasRoundtripOptions || request.SubRequests.Count != 0 || request.DataElementPackage is not null)
                    throw new InvalidDataException("Duplicate or misplaced Request Hashing Options.");
                request.HashOptions = RequestHashOptions.Deserialize(reader, header);
            }
            else if (header.Type == StreamObjectTypeHeaderStart.CellRoundtripOptions)
            {
                if (hasRoundtripOptions || request.SubRequests.Count != 0 || request.DataElementPackage is not null)
                    throw new InvalidDataException("Duplicate or misplaced Cell Roundtrip Options.");
                hasRoundtripOptions = true;
                SkipObject(reader, header);
            }
            else
            {
                // Ignore optional request-level client extensions such as
                // CellRoundtripOptions (0x8D) while preserving alignment.
                SkipObject(reader, header);
            }
        }

        SkipToCompoundEnd(reader, StreamObjectTypeHeaderEnd.Request);

        if (request.TryGetValidationError(out var validationCode, out var validationMessage) &&
            validationCode != CellErrorCode.IncompatibleProtocolVersion)
            throw new InvalidDataException(validationMessage);

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

    /// <summary>Optional partition selector preceding this operation's data.</summary>
    public Guid? TargetPartitionId { get; set; }

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
        if (TargetPartitionId is { } target)
        {
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.TargetPartitionId, 16).Serialize(writer);
            ExGuid.WriteGuid(writer, target);
        }
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
        if (start.Type != StreamObjectTypeHeaderStart.SubRequest ||
            start.HeaderType != StreamObjectHeaderStart.HeaderType32Bit || start.Compound != 1)
        {
            throw new InvalidDataException("Expected a 32-bit compound SubRequest header.");
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
        if (requestId >= uint.MaxValue)
            throw new InvalidDataException("Subrequest ID must be less than 0xFFFFFFFF.");

        Guid? targetPartition = null;
        if (!IsHeaderEnd(reader))
        {
            int position = reader.Position;
            var targetHeader = StreamObjectHeaderStart.Parse(reader);
            if (targetHeader.Type == StreamObjectTypeHeaderStart.TargetPartitionId)
            {
                if (targetHeader.HeaderType != StreamObjectHeaderStart.HeaderType32Bit ||
                    targetHeader.Compound != 0 || targetHeader.Length != 16)
                    throw new InvalidDataException("Target partition must contain one GUID in a 32-bit noncompound object.");
                targetPartition = ExGuid.ReadGuid(reader);
            }
            else reader.Position = position;
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
            RequestTypes.AllocateExtendedGuidRange => new FsshttpbCellSubRequest(requestType)
            {
                RequestId = requestId,
                Priority = priority,
                Data = AllocateExtendedGuidRangeSubRequestData.Deserialize(reader),
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

        subRequest.TargetPartitionId = targetPartition;
        SkipToSubRequestEnd(reader, subRequest);

        return subRequest;
    }

    private static void SkipToSubRequestEnd(BinaryReaderEx reader, FsshttpbCellSubRequest subRequest)
    {
        bool hasLockId = false;
        bool hasAdditionalFlags = false;
        while (!IsHeaderEnd(reader))
        {
            var header = StreamObjectHeaderStart.Parse(reader);
            if (header.Type == StreamObjectTypeHeaderStart.TargetPartitionId)
                throw new InvalidDataException("Duplicate or misplaced target partition.");
            if (header.Type is StreamObjectTypeHeaderStart.QueryChangesRequest or
                StreamObjectTypeHeaderStart.PutChangesRequest or
                StreamObjectTypeHeaderStart.AllocateExtendedGUIDRangeRequest)
                throw new InvalidDataException("Duplicate or misplaced operation data.");
            if (header.Type == StreamObjectTypeHeaderStart.PutChangesLockId && subRequest.Data is PutChangesSubRequestData put)
            {
                if (hasLockId || header.HeaderType != StreamObjectHeaderStart.HeaderType32Bit ||
                    header.Length != 16 || header.Compound != 0)
                    throw new InvalidDataException("Put Changes lock ID must contain one GUID.");
                hasLockId = true;
                put.LockId = ExGuid.ReadGuid(reader);
            }
            else if (header.Type == StreamObjectTypeHeaderStart.AdditionalFlags && subRequest.Data is PutChangesSubRequestData additionalPut)
            {
                if (hasAdditionalFlags || header.HeaderType != StreamObjectHeaderStart.HeaderType32Bit ||
                    header.Compound != 0 || header.Length < 2)
                    throw new InvalidDataException("AdditionalFlags must contain at least two flag bytes.");
                hasAdditionalFlags = true;
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

    private static void SkipObject(BinaryReaderEx reader, StreamObjectHeaderStart start, int depth = 0)
    {
        if (depth > 32) throw new InvalidDataException("Binary object nesting limit exceeded.");
        reader.Skip(start.Length);
        if (start.Compound != 1)
        {
            return;
        }

        StreamObjectTypeHeaderEnd expectedEnd = (StreamObjectTypeHeaderEnd)(int)start.Type;
        while (!IsHeaderEnd(reader))
        {
            SkipObject(reader, StreamObjectHeaderStart.Parse(reader), depth + 1);
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
    public ClientKnowledge? Knowledge { get; set; }
    public bool IncludeFilteredOutDataElementsInKnowledge { get; set; }
    public bool RoundKnowledgeToWholeCellChanges { get; set; }
    public bool HasUnsupportedQueryControls { get; private set; }
    /// <summary>The SharePoint 2010/2013 profile ignores this extension; it never selects a historical revision.</summary>
    public bool IgnoredQueryChangesVersioning { get; private set; }
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

    /// <summary>Legacy field. Waterline selectors are unsupported; the selected profile ignores the bounded QueryChangesVersioning extension.</summary>
    public ulong? Waterline { get; set; }

    /// <summary>Serializes the payload to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        if (Waterline is not null)
            throw new NotSupportedException("A waterline is knowledge, not a Query Changes version token.");
        // Flags are payload bytes, not the stream object's declared length.
        int requestFlags = (AllowFragments ? 0x02 : 0) | (ReturnFileHash ? 0x40 : 0)
            | (IncludeFilteredOutDataElementsInKnowledge ? 0x08 : 0)
            | (RoundKnowledgeToWholeCellChanges ? 0x20 : 0);
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

        if (MaxDataElements is not null)
        {
            var constraint = new BinaryWriterEx();
            new Compact64bitInt(MaxDataElements.Value).Serialize(constraint);
            var constraintHeader = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.QueryChangesDataConstraint, constraint.Length);
            constraintHeader.Serialize(writer);
            writer.WriteBytes(constraint.ToArray());
        }

        Knowledge?.Serialize(writer);
    }

    /// <summary>Deserializes the QueryChanges header and its optional request objects.</summary>
    public static QueryChangesSubRequestData Deserialize(BinaryReaderEx reader)
    {
        var data = new QueryChangesSubRequestData();

        var requestHeader = StreamObjectHeaderStart.Parse(reader);
        if (requestHeader.Type != StreamObjectTypeHeaderStart.QueryChangesRequest ||
            requestHeader.HeaderType != StreamObjectHeaderStart.HeaderType32Bit || requestHeader.Compound != 0 ||
            requestHeader.Length is < 1 or > 2)
        {
            throw new InvalidDataException("Expected a one- or two-byte 32-bit Query Changes request.");
        }

        byte[] requestPayload = reader.ReadBytes(requestHeader.Length);
        int requestFlags = requestPayload.Length > 0 ? requestPayload[0] : 0;
        data.AllowFragments = (requestFlags & 0x02) != 0 || (requestFlags & 0x10) != 0;
        data.ReturnFileHash = (requestFlags & 0x40) != 0;
        data.IncludeFilteredOutDataElementsInKnowledge = (requestFlags & 0x08) != 0;
        data.RoundKnowledgeToWholeCellChanges = (requestFlags & 0x20) != 0;

        bool precedingUnsupportedFilter = false;
        bool hasArguments = false;
        bool hasConstraint = false;
        bool hasVersioning = false;
        while (!IsHeaderEnd(reader))
        {
            var header = StreamObjectHeaderStart.Parse(reader);
            if (header.Type != StreamObjectTypeHeaderStart.QueryChangesFilterFlags)
                precedingUnsupportedFilter = false;
            switch (header.Type)
            {
                case StreamObjectTypeHeaderStart.QueryChangesRequest:
                case StreamObjectTypeHeaderStart.PutChangesRequest:
                case StreamObjectTypeHeaderStart.AllocateExtendedGUIDRangeRequest:
                    throw new InvalidDataException("Duplicate or misplaced operation data.");

                case StreamObjectTypeHeaderStart.TargetPartitionId:
                    throw new InvalidDataException("Target partition must precede the query data.");
                case StreamObjectTypeHeaderStart.QueryChangesFilter:
                    // Returning the full set is the required fallback for unsupported
                    // filters unless the following flags request failure.
                    SkipObject(reader, header);
                    precedingUnsupportedFilter = true;
                    break;

                case StreamObjectTypeHeaderStart.QueryChangesFilterFlags:
                    if (!precedingUnsupportedFilter || header.HeaderType != StreamObjectHeaderStart.HeaderType32Bit ||
                        header.Compound != 0 || header.Length != 1)
                        throw new InvalidDataException("Invalid Query Changes filter flags.");
                    if ((reader.ReadByte() & 1) != 0) data.HasUnsupportedQueryControls = true;
                    precedingUnsupportedFilter = false;
                    break;
                case StreamObjectTypeHeaderStart.QueryChangesRequestArguments:
                {
                    if (hasArguments || header.HeaderType != StreamObjectHeaderStart.HeaderType32Bit ||
                        header.Compound != 0 || header.Length < 3)
                        throw new InvalidDataException("Invalid or duplicate Query Changes request arguments.");
                    hasArguments = true;
                    byte[] argumentsPayload = reader.ReadBytes(header.Length);
                    data.IncludeStorageManifest = (argumentsPayload[0] & 0x01) != 0;
                    data.IncludeCellChanges = (argumentsPayload[0] & 0x02) != 0;
                    var argumentsReader = new BinaryReaderEx(argumentsPayload, 1, argumentsPayload.Length - 1);
                    data.CellId = CellId.Deserialize(argumentsReader);
                    if (argumentsReader.Remaining != 0)
                        throw new InvalidDataException("Query Changes request arguments contain trailing bytes.");
                    break;
                }

                case StreamObjectTypeHeaderStart.QueryChangesDataConstraint:
                {
                    if (hasConstraint || header.HeaderType != StreamObjectHeaderStart.HeaderType32Bit ||
                        header.Compound != 0 || header.Length == 0)
                        throw new InvalidDataException("Invalid or duplicate Query Changes data constraint.");
                    hasConstraint = true;
                    byte[] constraintPayload = reader.ReadBytes(header.Length);
                    var constraintReader = new BinaryReaderEx(constraintPayload);
                    data.MaxDataElements = Compact64bitInt.Deserialize(constraintReader).Value;
                    if (constraintReader.Remaining != 0)
                        throw new InvalidDataException("Query Changes data constraint contains trailing bytes.");
                    break;
                }

                case StreamObjectTypeHeaderStart.QueryChangesVersioning:
                    if (hasVersioning) throw new InvalidDataException("Duplicate Query Changes versioning object.");
                    hasVersioning = true;
                    // MS-FSSHTTPB product behavior note 17: SharePoint 2010/2013
                    // ignore this field. This server follows that versioning profile.
                    data.IgnoredQueryChangesVersioning = true;
                    SkipObject(reader, header);
                    break;

                case StreamObjectTypeHeaderStart.Knowledge:
                    if (data.Knowledge is not null) throw new InvalidDataException("Duplicate client knowledge.");
                    reader.Position -= header.HeaderSize;
                    data.Knowledge = ClientKnowledge.Deserialize(reader);
                    break;

                default:
                    data.HasUnsupportedQueryControls = true;
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

    private static void SkipObject(BinaryReaderEx reader, StreamObjectHeaderStart start, int depth = 0)
    {
        if (depth > 32) throw new InvalidDataException("Binary object nesting limit exceeded.");
        reader.Skip(start.Length);
        if (start.Compound != 1)
        {
            return;
        }

        StreamObjectTypeHeaderEnd expectedEnd = (StreamObjectTypeHeaderEnd)(int)start.Type;
        while (!IsHeaderEnd(reader))
        {
            SkipObject(reader, StreamObjectHeaderStart.Parse(reader), depth + 1);
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
        if (putHeader.Type != StreamObjectTypeHeaderStart.PutChangesRequest ||
            putHeader.HeaderType != StreamObjectHeaderStart.HeaderType32Bit || putHeader.Compound != 0)
        {
            throw new InvalidDataException("Expected a 32-bit noncompound Put Changes request.");
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
