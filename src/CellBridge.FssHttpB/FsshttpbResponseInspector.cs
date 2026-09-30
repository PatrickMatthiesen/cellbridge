using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CellBridge.FssHttpB;

/// <summary>Inspects an FSSHTTPB response without requiring every payload type to be known.</summary>
/// <remarks>
/// This is deliberately a diagnostic parser.  A captured SharePoint response
/// can contain newer or implementation-specific stream objects; those objects
/// are retained as raw payloads and reported in <see cref="Issues"/> rather
/// than making the complete dump unusable.
/// </remarks>
public static class FsshttpbResponseInspector
{
    /// <summary>Inspects a binary response.</summary>
    public static FsshttpbResponseInspection Inspect(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new Parser(bytes).Parse();
    }

    /// <summary>Inspects base64 as carried by an FSSHTTP Cell subresponse.</summary>
    public static FsshttpbResponseInspection InspectBase64(string base64)
    {
        ArgumentNullException.ThrowIfNull(base64);
        return Inspect(Convert.FromBase64String(base64));
    }
}

/// <summary>Stable, serializable result of a response inspection.</summary>
public sealed class FsshttpbResponseInspection
{
    public ushort ProtocolVersion { get; internal set; }
    public ushort MinimumVersion { get; internal set; }
    public ulong Signature { get; internal set; }
    public bool Status { get; internal set; }
    public ResponseError? Error { get; internal set; }
    public int ByteLength { get; internal set; }
    public List<FsshttpbSubResponseInspection> SubResponses { get; } = [];
    public List<FsshttpbDataElementInspection> DataElements { get; } = [];
    public List<FsshttpbRelationship> Relationships { get; } = [];
    public List<string> Issues { get; } = [];

    /// <summary>Returns deterministic JSON suitable for checked-in golden dumps.</summary>
    public string ToJson(bool indented = true) => JsonSerializer.Serialize(this,
        new JsonSerializerOptions { WriteIndented = indented, Converters = { new JsonStringEnumConverter() } });

    /// <summary>Returns a compact deterministic line-oriented dump.</summary>
    public string ToCanonicalText()
    {
        var sb = new StringBuilder();
        sb.Append("response ").Append(ProtocolVersion).Append('/').Append(MinimumVersion)
            .Append(" signature=0x").Append(Signature.ToString("X16"))
            .Append(" status=").Append(Status ? "error" : "ok")
            .Append(" bytes=").Append(ByteLength).AppendLine();
        if (Error is not null) AppendError(sb, Error);
        foreach (var sub in SubResponses)
        {
            sb.Append("subresponse offset=").Append(sub.Offset).Append(" request=").Append(sub.RequestId)
                .Append(" type=").Append((int)sub.RequestType).Append(':').Append(sub.RequestType)
                .Append(" status=").Append(sub.Status ? "error" : "ok")
                .Append(" payload=").Append(sub.PayloadLength).AppendLine();
            if (sub.Error is not null) AppendError(sb, sub.Error);
            foreach (var item in sub.Objects) AppendObject(sb, item, "  ");
        }
        foreach (var element in DataElements)
        {
            sb.Append("element[").Append(element.Index).Append("] offset=").Append(element.Offset)
                .Append(" type=").Append((int)element.DataElementType).Append(':').Append(element.DataElementType)
                .Append(" id=").Append(element.ExtendedGuid).Append(" serial=").Append(element.SerialNumber)
                .Append(" payload=").Append(element.DataLength).Append(" sha256=").Append(element.DataSha256).AppendLine();
            foreach (var item in element.Objects)
                AppendObject(sb, item, "  ");
        }
        foreach (var relation in Relationships)
            sb.Append("edge ").Append(relation.Source).Append(" --").Append(relation.Kind).Append("--> ").AppendLine(relation.Target);
        foreach (var issue in Issues)
            sb.Append("issue ").AppendLine(issue);
        return sb.ToString();
    }

    private static void AppendError(StringBuilder sb, ResponseError error)
        => sb.Append("  error type=").Append(error.Type).Append(" code=").Append(error.ErrorCode)
            .Append(" message=").AppendLine(error.ErrorMessage);

    private static void AppendObject(StringBuilder sb, FsshttpbStreamObjectInspection item, string indent)
    {
        sb.Append(indent).Append("object offset=").Append(item.Offset).Append(" type=")
            .Append(item.TypeValue).Append(':').Append(item.Type).Append(" length=").Append(item.Length)
            .Append(" compound=").Append(item.Compound ? '1' : '0');
        if (!string.IsNullOrEmpty(item.PayloadSha256)) sb.Append(" sha256=").Append(item.PayloadSha256);
        if (item.Fields.Count != 0) sb.Append(" fields=").Append(string.Join(",", item.Fields));
        sb.AppendLine();
        foreach (var child in item.Children) AppendObject(sb, child, indent + "  ");
    }
}

public sealed class FsshttpbSubResponseInspection
{
    public int Offset { get; internal set; }
    public ulong RequestId { get; internal set; }
    public RequestTypes RequestType { get; internal set; }
    public bool Status { get; internal set; }
    public int PayloadLength { get; internal set; }
    public ResponseError? Error { get; internal set; }
    public List<FsshttpbStreamObjectInspection> Objects { get; } = [];
}

public sealed class FsshttpbDataElementInspection
{
    public int Index { get; internal set; }
    public int Offset { get; internal set; }
    public DataElementType DataElementType { get; internal set; }
    public string ExtendedGuid { get; internal set; } = "null";
    public string SerialNumber { get; internal set; } = "null";
    public int DataLength { get; internal set; }
    public string DataSha256 { get; internal set; } = "";
    public List<FsshttpbStreamObjectInspection> Objects { get; } = [];
}

public sealed class FsshttpbStreamObjectInspection
{
    public int Offset { get; internal set; }
    public int TypeValue { get; internal set; }
    public StreamObjectTypeHeaderStart Type { get; internal set; }
    public int Length { get; internal set; }
    public bool Compound { get; internal set; }
    public string PayloadSha256 { get; internal set; } = "";
    public List<string> Fields { get; } = [];
    public List<FsshttpbStreamObjectInspection> Children { get; } = [];
}

public sealed record FsshttpbRelationship(string Source, string Target, string Kind);

internal sealed class Parser
{
    private readonly byte[] _bytes;
    private readonly FsshttpbResponseInspection _result;
    private int _elementIndex;

    public Parser(byte[] bytes)
    {
        _bytes = bytes;
        _result = new FsshttpbResponseInspection { ByteLength = bytes.Length };
    }

    public FsshttpbResponseInspection Parse()
    {
        try
        {
            var reader = new BinaryReaderEx(_bytes);
            _result.ProtocolVersion = reader.ReadUInt16();
            _result.MinimumVersion = reader.ReadUInt16();
            _result.Signature = reader.ReadUInt64();
            var response = StreamObjectHeaderStart.Parse(reader);
            if (response.Type != StreamObjectTypeHeaderStart.FsshttpbResponse)
                throw new InvalidDataException($"Expected response header, got {response.Type}.");
            // The response header's immediate payload is the status byte,
            // which is consumed below.  Compound response children follow
            // it, so do not skip Length here.
            _result.Status = (reader.ReadByte() & 1) != 0;
            if (_result.Status)
            {
                _result.Error = ResponseError.Deserialize(reader);
            }
            else
            {
                while (reader.Remaining > 0 && !IsEnd(reader))
                {
                    int position = reader.Position;
                    var header = StreamObjectHeaderStart.Parse(reader);
                    reader.Position = position;
                    if (header.Type == StreamObjectTypeHeaderStart.DataElementPackage)
                        ParsePackage(reader);
                    else if (header.Type == StreamObjectTypeHeaderStart.FsshttpbSubResponse)
                        ParseSubResponse(reader);
                    else
                    {
                        _result.Issues.Add($"unknown response object at {position}: {header.Type} ({(int)header.Type})");
                        SkipObject(reader, null);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException)
        {
            _result.Issues.Add($"response parse stopped: {ex.Message}");
        }
        return _result;
    }

    private void ParsePackage(BinaryReaderEx reader)
    {
        int packageOffset = reader.Position;
        var start = StreamObjectHeaderStart.Parse(reader);
        int immediateEnd = CheckedEnd(reader.Position, start.Length);
        if (start.Length < 1) throw new InvalidDataException("DataElementPackage has no reserved byte.");
        reader.ReadByte();
        reader.Position = immediateEnd;
        while (reader.Remaining > 0 && !IsEnd(reader))
        {
            int position = reader.Position;
            var header = StreamObjectHeaderStart.Parse(reader);
            reader.Position = position;
            if (header.Type != StreamObjectTypeHeaderStart.DataElement)
            {
                _result.Issues.Add($"unexpected package object at {position}: {header.Type} ({(int)header.Type})");
                SkipObject(reader, null);
                continue;
            }
            ParseDataElement(reader);
        }
        if (reader.Remaining > 0) reader.Position += HeaderEndSize(reader);
        if (packageOffset < 0) throw new InvalidDataException("Invalid package offset.");
    }

    private void ParseDataElement(BinaryReaderEx reader)
    {
        int offset = reader.Position;
        var start = StreamObjectHeaderStart.Parse(reader);
        int metadataEnd = CheckedEnd(reader.Position, start.Length);
        int payloadStart = reader.Position;
        var item = new FsshttpbDataElementInspection { Index = _elementIndex++, Offset = offset };
        try
        {
            var id = ExGuid.Deserialize(reader);
            item.ExtendedGuid = Format(id);
            var serial = SerialNumber.Deserialize(reader);
            item.SerialNumber = Format(serial);
            item.DataElementType = (DataElementType)Compact64bitInt.Deserialize(reader).Value;
            if (reader.Position != metadataEnd)
                throw new InvalidDataException("DataElement metadata length mismatch.");
            int dataStart = reader.Position;
            int end = FindEndHeader(dataStart, StreamObjectTypeHeaderEnd.DataElement);
            item.DataLength = Math.Max(0, end - dataStart);
            item.DataSha256 = Sha256(_bytes, dataStart, item.DataLength);
            ParseDataPayload(item, dataStart, end);
            reader.Position = end;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException)
        {
            int end = FindEndHeader(Math.Min(reader.Position, _bytes.Length), StreamObjectTypeHeaderEnd.DataElement);
            item.DataLength = Math.Max(0, end - Math.Min(reader.Position, end));
            item.DataSha256 = Sha256(_bytes, payloadStart, Math.Max(0, end - payloadStart));
            _result.Issues.Add($"element[{item.Index}] at {offset}: {ex.Message}");
            reader.Position = end;
        }
        _result.DataElements.Add(item);
        if (reader.Remaining > 0) SkipEnd(reader, StreamObjectTypeHeaderEnd.DataElement);
    }

    private int FindEndHeader(int start, StreamObjectTypeHeaderEnd type)
    {
        var markers = new List<byte[]>();
        var shortMarkerWriter = new BinaryWriterEx();
        new StreamObjectHeaderEnd8Bit(type).Serialize(shortMarkerWriter);
        markers.Add(shortMarkerWriter.ToArray());
        var longMarkerWriter = new BinaryWriterEx();
        new StreamObjectHeaderEnd16Bit(type).Serialize(longMarkerWriter);
        markers.Add(longMarkerWriter.ToArray());
        for (int i = start; i < _bytes.Length; i++)
        {
            foreach (byte[] marker in markers)
            {
                if (i > _bytes.Length - marker.Length || !_bytes.AsSpan(i, marker.Length).SequenceEqual(marker))
                    continue;
                int next = i + marker.Length;
                if (next >= _bytes.Length) return i;
                try
                {
                    var probe = new BinaryReaderEx(_bytes) { Position = next };
                    int discriminator = _bytes[next] & 3;
                    if (discriminator is 0 or 2)
                    {
                        if (StreamObjectHeaderStart.Parse(probe).Type == StreamObjectTypeHeaderStart.DataElement) return i;
                    }
                    else if (discriminator is 1 or 3)
                    {
                        if (StreamObjectHeaderEnd.Parse(probe).Type == StreamObjectTypeHeaderEnd.DataElementPackage) return i;
                    }
                }
                catch (InvalidDataException) { }
            }
        }
        throw new InvalidDataException($"End header {type} was not found after {start}.");
    }

    private void ParseDataPayload(FsshttpbDataElementInspection item, int start, int end)
    {
        var reader = new BinaryReaderEx(_bytes) { Position = start };
        while (reader.Position < end)
        {
            int position = reader.Position;
            try { item.Objects.Add(ParseObject(reader, end)); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException)
            {
                _result.Issues.Add($"element[{item.Index}] object at {position}: {ex.Message}");
                break;
            }
        }
        ExtractRelationships(item, start, end);
    }

    private FsshttpbStreamObjectInspection ParseObject(BinaryReaderEx reader, int boundary)
    {
        int offset = reader.Position;
        var header = StreamObjectHeaderStart.Parse(reader);
        int immediateEnd = CheckedEnd(reader.Position, header.Length);
        if (immediateEnd > boundary) throw new InvalidDataException($"object {header.Type} at {offset} exceeds payload boundary {boundary}");
        var item = new FsshttpbStreamObjectInspection
        {
            Offset = offset, TypeValue = (int)header.Type, Type = header.Type,
            Length = header.Length, Compound = header.Compound == 1,
            PayloadSha256 = Sha256(_bytes, reader.Position, header.Length),
        };
        var fieldsReader = new BinaryReaderEx(_bytes, reader.Position, header.Length);
        DescribeImmediateFields(item, fieldsReader);
        int immediateStart = reader.Position;
        if (header.Compound == 1)
            TryParseEmbeddedChildren(item, immediateStart, immediateEnd);
        reader.Position = immediateEnd;
        if (header.Compound == 1)
        {
            while (reader.Position < boundary && !IsEnd(reader))
                item.Children.Add(ParseObject(reader, boundary));
            if (reader.Position >= boundary) throw new InvalidDataException($"compound {header.Type} has no end header");
            reader.Position += HeaderEndSize(reader);
        }
        return item;
    }

    // Older SharePoint/FSSHTTPB writers (and the current builder) put the
    // first child objects inside the compound header's declared payload.  The
    // newer grammar places them after the immediate payload.  Accept both
    // forms so a comparison dump remains useful across captures.
    private void TryParseEmbeddedChildren(FsshttpbStreamObjectInspection item, int start, int end)
    {
        if (start >= end) return;
        var reader = new BinaryReaderEx(_bytes) { Position = start };
        var children = new List<FsshttpbStreamObjectInspection>();
        try
        {
            while (reader.Position < end && !IsEnd(reader))
                children.Add(ParseObject(reader, end));
            if (reader.Position != end) return;
            item.Children.AddRange(children);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException)
        {
            // This is only a compatibility probe.  The payload remains in
            // PayloadSha256 and the normal child walk will still run.
        }
    }

    private void DescribeImmediateFields(FsshttpbStreamObjectInspection item, BinaryReaderEx reader)
    {
        try
        {
            switch (item.Type)
            {
                case StreamObjectTypeHeaderStart.StorageManifestSchemaGUID:
                    if (reader.Remaining >= 16) item.Fields.Add($"schema={ExGuid.ReadGuid(reader):D}");
                    break;
                case StreamObjectTypeHeaderStart.StorageManifestRootDeclare:
                    AddExGuidField(item, reader, "root");
                    AddCellField(item, reader);
                    break;
                case StreamObjectTypeHeaderStart.CellManifestCurrentRevision:
                    AddExGuidField(item, reader, "revision");
                    break;
                case StreamObjectTypeHeaderStart.RevisionManifest:
                    AddExGuidField(item, reader, "revision-id");
                    AddExGuidField(item, reader, "base-revision");
                    break;
                case StreamObjectTypeHeaderStart.RevisionManifestRootDeclare:
                    AddExGuidField(item, reader, "root");
                    AddExGuidField(item, reader, "object");
                    break;
                case StreamObjectTypeHeaderStart.RevisionManifestObjectGroupReferences:
                    AddExGuidField(item, reader, "object-group");
                    break;
                case StreamObjectTypeHeaderStart.ObjectGroupObjectDeclare:
                    AddExGuidField(item, reader, "object");
                    AddCompactField(item, reader, "partition");
                    AddCompactField(item, reader, "data-size");
                    AddCompactField(item, reader, "object-ref-count");
                    AddCompactField(item, reader, "cell-ref-count");
                    break;
                case StreamObjectTypeHeaderStart.ObjectGroupObjectDataBLOBReference:
                    AddExGuidField(item, reader, "blob");
                    break;
                case StreamObjectTypeHeaderStart.ObjectGroupObjectData:
                    AddCompactField(item, reader, "object-count");
                    // Object Group Object Data begins with an ExGuid array,
                    // followed by a Cell ID array and a Binary Item.  Decode
                    // the GUIDs because these are the edges that connect an
                    // FSSHTTPD node to its object declarations.
                    if (item.Fields.Count > 0 && TryLastCompact(item, out ulong objectCount))
                    {
                        for (ulong i = 0; i < objectCount && reader.Remaining > 0; i++)
                            AddExGuidField(item, reader, "object-ref");
                        if (reader.Remaining > 0)
                        {
                            ulong cellCount = Compact64bitInt.Deserialize(reader).Value;
                            item.Fields.Add("cell-count=" + cellCount);
                            for (ulong i = 0; i < cellCount && reader.Remaining > 0; i++)
                                AddCellField(item, reader);
                        }
                        if (reader.Remaining > 0)
                        {
                            ulong dataLength = Compact64bitInt.Deserialize(reader).Value;
                            item.Fields.Add("data-bytes=" + dataLength);
                        }
                    }
                    break;
                case StreamObjectTypeHeaderStart.ObjectDataBLOB:
                    item.Fields.Add($"data-bytes={reader.Remaining}");
                    break;
                case StreamObjectTypeHeaderStart.QueryChangesResponse:
                    AddExGuidField(item, reader, "storage-index");
                    if (reader.Remaining > 0) item.Fields.Add("partial=" + ((reader.ReadByte() & 1) != 0));
                    break;
                // MS-FSSHTTPD node objects are assigned these stream-object
                // values by the FSSHTTPB enum table.  Keep the numeric forms
                // here as well: captures can be inspected before the host
                // library has added symbolic enum names for FSSHTTPD.
                case (StreamObjectTypeHeaderStart)0x21: // SignatureObject
                    AddBinaryItemField(item, reader, "signature");
                    break;
                case (StreamObjectTypeHeaderStart)0x22: // DataSizeObject
                    if (reader.Remaining >= 8) item.Fields.Add("represented-size=" + reader.ReadUInt64());
                    break;
                case (StreamObjectTypeHeaderStart)0x2F: // DataHashObject
                    AddBinaryItemField(item, reader, "data-hash");
                    break;
                case StreamObjectTypeHeaderStart.LeafNodeObject:
                case StreamObjectTypeHeaderStart.IntermediateNodeObject:
                    item.Fields.Add($"node-immediate-bytes={reader.Remaining}");
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException)
        {
            item.Fields.Add("decode-error=" + ex.Message);
        }
    }

    private void ExtractRelationships(FsshttpbDataElementInspection item, int start, int end)
    {
        string source = item.ExtendedGuid;
        foreach (var obj in Flatten(item.Objects))
        {
            foreach (var field in obj.Fields)
            {
                int equals = field.IndexOf('=');
                if (equals < 0) continue;
                string name = field[..equals];
                string value = field[(equals + 1)..];
                if (name is "root" or "revision" or "revision-id" or "base-revision" or "object" or "object-ref" or "object-group" or "blob")
                    _result.Relationships.Add(new FsshttpbRelationship(source, value, name));
            }
        }
    }

    private static IEnumerable<FsshttpbStreamObjectInspection> Flatten(IEnumerable<FsshttpbStreamObjectInspection> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in Flatten(root.Children)) yield return child;
        }
    }

    private void ParseSubResponse(BinaryReaderEx reader)
    {
        int offset = reader.Position;
        var start = StreamObjectHeaderStart.Parse(reader);
        int fixedEnd = CheckedEnd(reader.Position, start.Length);
        var sub = new FsshttpbSubResponseInspection { Offset = offset, PayloadLength = start.Length };
        try
        {
            sub.RequestId = Compact64bitInt.Deserialize(reader).Value;
            sub.RequestType = (RequestTypes)Compact64bitInt.Deserialize(reader).Value;
            sub.Status = (reader.ReadByte() & 1) != 0;
            if (reader.Position != fixedEnd)
                throw new InvalidDataException("Subresponse fixed-field length mismatch.");
            if (sub.Status)
            {
                sub.Error = ResponseError.Deserialize(reader);
            }
            else
            {
                while (reader.Remaining > 0 && !IsEnd(reader))
                    sub.Objects.Add(ParseObject(reader, _bytes.Length));
                foreach (var obj in Flatten(sub.Objects))
                    foreach (var field in obj.Fields)
                        if (field.StartsWith("storage-index=", StringComparison.Ordinal))
                            _result.Relationships.Add(new FsshttpbRelationship(
                                $"subresponse:{sub.RequestId}", field[14..], "storage-index"));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException)
        { _result.Issues.Add($"subresponse at {offset}: {ex.Message}"); }
        _result.SubResponses.Add(sub);
        if (reader.Remaining > 0) SkipEnd(reader, StreamObjectTypeHeaderEnd.SubResponse);
    }

    private static void AddExGuidField(FsshttpbStreamObjectInspection item, BinaryReaderEx reader, string name)
        => item.Fields.Add(name + "=" + Format(ExGuid.Deserialize(reader)));

    private static void AddCellField(FsshttpbStreamObjectInspection item, BinaryReaderEx reader)
    {
        var cell = CellId.Deserialize(reader);
        item.Fields.Add("cell-long=" + Format(cell.LongId));
        item.Fields.Add("cell-short=" + Format(cell.ShortId));
    }

    private static void AddCompactField(FsshttpbStreamObjectInspection item, BinaryReaderEx reader, string name)
        => item.Fields.Add(name + "=" + Compact64bitInt.Deserialize(reader).Value);

    private static void AddBinaryItemField(FsshttpbStreamObjectInspection item, BinaryReaderEx reader, string name)
    {
        ulong length = Compact64bitInt.Deserialize(reader).Value;
        if (length > int.MaxValue || length > (ulong)reader.Remaining)
            throw new InvalidDataException($"{name} length {length} exceeds remaining payload");
        byte[] value = reader.ReadBytes((int)length);
        item.Fields.Add(name + "-bytes=" + length);
        item.Fields.Add(name + "-sha256=" + Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant());
    }

    private static bool TryLastCompact(FsshttpbStreamObjectInspection item, out ulong value)
    {
        const string prefix = "object-count=";
        string? field = item.Fields.LastOrDefault(f => f.StartsWith(prefix, StringComparison.Ordinal));
        value = 0;
        return field is not null && ulong.TryParse(field.AsSpan(prefix.Length), out value);
    }

    private static string Format(ExGuid value) => value.IsNull ? "null" : $"{value.Value}:{value.Guid:D}";
    private static string Format(SerialNumber value) => value.IsNull ? "null" : $"{value.Value}:{value.Guid:D}";
    private static int CheckedEnd(int position, int length)
    {
        if (length < 0 || position > int.MaxValue - length) throw new InvalidDataException("negative or overflowing length");
        return position + length;
    }

    private static bool IsEnd(BinaryReaderEx reader)
    {
        if (reader.Remaining == 0) return false;
        int position = reader.Position;
        bool result = (reader.ReadByte() & 3) is 1 or 3;
        reader.Position = position;
        return result;
    }

    private static int HeaderEndSize(BinaryReaderEx reader)
    {
        if (reader.Remaining == 0) throw new EndOfStreamException("Missing stream object end header.");
        int position = reader.Position;
        int size = (reader.ReadByte() & 3) == 1 ? 1 : 2;
        reader.Position = position;
        return size;
    }
    private static void SkipEnd(BinaryReaderEx reader, StreamObjectTypeHeaderEnd expected)
    {
        var end = StreamObjectHeaderEnd.Parse(reader);
        if (end.Type != expected) throw new InvalidDataException($"Expected {expected} end, got {end.Type}.");
    }
    private static void SkipObject(BinaryReaderEx reader, int? boundary)
    {
        var header = StreamObjectHeaderStart.Parse(reader);
        int end = CheckedEnd(reader.Position, header.Length);
        reader.Position = end;
        if (header.Compound == 1)
        {
            int max = boundary ?? reader.Length;
            while (reader.Position < max && !IsEnd(reader)) SkipObject(reader, max);
            if (reader.Position < max) reader.Position += HeaderEndSize(reader);
        }
    }
    private static string Sha256(byte[] bytes, int offset, int count)
        => Convert.ToHexString(SHA256.HashData(bytes.AsSpan(offset, count))).ToLowerInvariant();
}
