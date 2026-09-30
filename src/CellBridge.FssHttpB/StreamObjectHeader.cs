namespace CellBridge.FssHttpB;

// Stream object type enums (StreamObjectTypeHeaderStart / StreamObjectTypeHeaderEnd)
// are defined in Enums.cs. This file contains the header framing classes.

/// <summary>
/// Base class for stream object header starts (16-bit or 32-bit).
/// [MS-FSSHTTPB] section 2.2.1.5.2.
/// </summary>
public abstract class StreamObjectHeaderStart
{
    /// <summary>Header type value for a 16-bit header start.</summary>
    public const int HeaderType16Bit = 0x0;

    /// <summary>Header type value for a 32-bit header start.</summary>
    public const int HeaderType32Bit = 0x2;

    /// <summary>Maximum payload length representable by a 16-bit header.</summary>
    public const int Max16BitLength = 127;

    /// <summary>Length sentinel in a 32-bit header indicating a Compact64bitInt LargeLength follows.</summary>
    public const int LargeLengthSentinel = 32767;

    /// <summary>2-bit header type discriminator (0 = 16-bit, 2 = 32-bit).</summary>
    public int HeaderType { get; set; }

    /// <summary>1 = compound object (terminated by a header end), 0 = single object.</summary>
    public int Compound { get; set; }

    /// <summary>The stream object type.</summary>
    public StreamObjectTypeHeaderStart Type { get; set; }

    /// <summary>Length in bytes of the additional data following the header.</summary>
    public int Length { get; set; }

    /// <summary>
    /// The size in bytes of this header itself (2 for 16-bit, 4 for 32-bit,
    /// plus the size of the LargeLength compact integer when present).
    /// </summary>
    public abstract int HeaderSize { get; }

    /// <summary>Returns the encoded byte size of a compact 64-bit integer value.</summary>
    protected static int Compact64bitIntEncodedSize(ulong v) => v switch
    {
        0 => 1,
        <= 0x7F => 1,
        <= 0x3FFF => 2,
        <= 0x1FFFFF => 3,
        <= 0xFFFFFFF => 4,
        <= 0x7FFFFFFFF => 5,
        <= 0x3FFFFFFFFFF => 6,
        <= 0x1FFFFFFFFFFFF => 7,
        _ => 9,
    };

    /// <summary>
    /// Set of stream object types that are compound (must have Compound = 1).
    /// </summary>
    public static readonly HashSet<StreamObjectTypeHeaderStart> CompoundTypes = new()
    {
        StreamObjectTypeHeaderStart.CellKnowledge,
        StreamObjectTypeHeaderStart.ContentTagKnowledge,
        StreamObjectTypeHeaderStart.DataElement,
        StreamObjectTypeHeaderStart.DataElementPackage,
        StreamObjectTypeHeaderStart.FragmentKnowledge,
        StreamObjectTypeHeaderStart.FsshttpbResponse,
        StreamObjectTypeHeaderStart.FsshttpbSubResponse,
        StreamObjectTypeHeaderStart.IntermediateNodeObject,
        StreamObjectTypeHeaderStart.Knowledge,
        StreamObjectTypeHeaderStart.LeafNodeObject,
        StreamObjectTypeHeaderStart.ObjectGroupData,
        StreamObjectTypeHeaderStart.ObjectGroupDeclarations,
        StreamObjectTypeHeaderStart.ObjectGroupMetadataDeclarations,
        StreamObjectTypeHeaderStart.QueryChangesFilter,
        StreamObjectTypeHeaderStart.ReadAccessResponse,
        StreamObjectTypeHeaderStart.Request,
        StreamObjectTypeHeaderStart.ResponseError,
        StreamObjectTypeHeaderStart.SpecializedKnowledge,
        StreamObjectTypeHeaderStart.SubRequest,
        StreamObjectTypeHeaderStart.UserAgent,
        StreamObjectTypeHeaderStart.WaterlineKnowledge,
        StreamObjectTypeHeaderStart.WriteAccessResponse,
    };

    /// <summary>
    /// Parses a stream object header start at the current reader position.
    /// Returns the parsed header; the reader is positioned after the header
    /// (including any LargeLength compact integer).
    /// </summary>
    public static StreamObjectHeaderStart Parse(BinaryReaderEx reader)
    {
        // Peek the 2-bit header type without consuming.
        int pos = reader.Position;
        int headerType = reader.ReadByte() & 0x03;
        reader.Position = pos;

        StreamObjectHeaderStart header = headerType switch
        {
            HeaderType16Bit => new StreamObjectHeaderStart16Bit(),
            HeaderType32Bit => new StreamObjectHeaderStart32Bit(),
            _ => throw new InvalidDataException(
                $"Invalid stream object header start type {headerType} at position {pos}."),
        };
        header.Deserialize(reader);
        return header;
    }

    /// <summary>Deserializes this header from the reader.</summary>
    public abstract void Deserialize(BinaryReaderEx reader);
}

/// <summary>
/// A 16-bit Stream Object Header Start. [MS-FSSHTTPB] section 2.2.1.5.2.1.
/// Bit layout (LSB-first): HeaderType (2), Compound (1), Type (6), Length (7).
/// </summary>
public sealed class StreamObjectHeaderStart16Bit : StreamObjectHeaderStart
{
    /// <summary>Creates a 16-bit header start for the given type and payload length.</summary>
    public StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart type, int length)
    {
        if (length > Max16BitLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length),
                "16-bit headers support lengths up to 127 bytes; use a 32-bit header.");
        }

        HeaderType = HeaderType16Bit;
        Type = type;
        Compound = CompoundTypes.Contains(type) ? 1 : 0;
        Length = length;
    }

    /// <summary>Creates a 16-bit header start with zero payload length.</summary>
    public StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart type)
        : this(type, 0)
    {
    }

    /// <summary>Default constructor for deserialization.</summary>
    public StreamObjectHeaderStart16Bit()
    {
    }

    /// <inheritdoc />
    public override int HeaderSize => 2;

    /// <inheritdoc />
    public override void Deserialize(BinaryReaderEx reader)
    {
        var bits = new BitReader(reader);
        HeaderType = (int)bits.ReadBits(2);
        if (HeaderType != HeaderType16Bit)
        {
            throw new InvalidDataException($"Expected 16-bit header start type {HeaderType16Bit}, got {HeaderType}.");
        }

        Compound = (int)bits.ReadBits(1);
        int typeValue = (int)bits.ReadBits(6);
        Type = (StreamObjectTypeHeaderStart)typeValue;
        Length = (int)bits.ReadBits(7);

        if (CompoundTypes.Contains(Type) && Compound != 1)
        {
            throw new InvalidDataException(
                $"Stream object type {Type} is compound but the header Compound bit is 0.");
        }
    }

    /// <summary>Serializes this header to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        var bits = new BitWriter(writer);
        bits.WriteBits((uint)HeaderType16Bit, 2);
        bits.WriteBits((uint)Compound, 1);
        bits.WriteBits((uint)Type, 6);
        bits.WriteBits((uint)Length, 7);
        bits.Flush();
    }

    /// <summary>Returns the header as a 2-byte little-endian ushort.</summary>
    public ushort ToUInt16()
    {
        var w = new BinaryWriterEx();
        Serialize(w);
        return BitConverter.ToUInt16(w.ToArray(), 0);
    }
}

/// <summary>
/// A 32-bit Stream Object Header Start. [MS-FSSHTTPB] section 2.2.1.5.2.2.
/// Bit layout (LSB-first): HeaderType (2), Compound (1), Type (14), Length (15),
/// optionally followed by a Compact64bitInt LargeLength when Length == 32767.
/// </summary>
public sealed class StreamObjectHeaderStart32Bit : StreamObjectHeaderStart
{
    /// <summary>Optional large length present when <see cref="StreamObjectHeaderStart.Length"/> is 32767.</summary>
    public Compact64bitInt? LargeLength { get; set; }

    /// <summary>Creates a 32-bit header start for the given type and payload length.</summary>
    public StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart type, int length)
    {
        HeaderType = HeaderType32Bit;
        Type = type;
        Compound = CompoundTypes.Contains(type) ? 1 : 0;

        if (length >= LargeLengthSentinel)
        {
            Length = LargeLengthSentinel;
            LargeLength = new Compact64bitInt((ulong)length);
        }
        else
        {
            Length = length;
            LargeLength = null;
        }
    }

    /// <summary>Creates a 32-bit header start with zero payload length.</summary>
    public StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart type)
        : this(type, 0)
    {
    }

    /// <summary>Default constructor for deserialization.</summary>
    public StreamObjectHeaderStart32Bit()
    {
    }

    /// <inheritdoc />
    public override int HeaderSize =>
        LargeLength is not null ? 4 + Compact64bitIntEncodedSize(LargeLength.Value) : 4;

    /// <inheritdoc />
    public override void Deserialize(BinaryReaderEx reader)
    {
        var bits = new BitReader(reader);
        HeaderType = (int)bits.ReadBits(2);
        if (HeaderType != HeaderType32Bit)
        {
            throw new InvalidDataException($"Expected 32-bit header start type {HeaderType32Bit}, got {HeaderType}.");
        }

        Compound = (int)bits.ReadBits(1);
        int typeValue = (int)bits.ReadBits(14);
        Type = (StreamObjectTypeHeaderStart)typeValue;
        Length = (int)bits.ReadBits(15);

        if (Length == LargeLengthSentinel)
        {
            LargeLength = Compact64bitInt.Deserialize(reader);
            Length = (int)LargeLength.Value;
        }
        else
        {
            LargeLength = null;
        }

        if (CompoundTypes.Contains(Type) && Compound != 1)
        {
            throw new InvalidDataException(
                $"Stream object type {Type} is compound but the header Compound bit is 0.");
        }
    }

    /// <summary>Serializes this header to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        var bits = new BitWriter(writer);
        bits.WriteBits((uint)HeaderType32Bit, 2);
        bits.WriteBits((uint)Compound, 1);
        bits.WriteBits((uint)Type, 14);
        bits.WriteBits((uint)Length, 15);
        bits.Flush();

        if (Length == LargeLengthSentinel && LargeLength is not null)
        {
            LargeLength.Serialize(writer);
        }
    }
}

/// <summary>
/// Base class for stream object header ends (8-bit or 16-bit).
/// [MS-FSSHTTPB] section 2.2.1.5.3.
/// </summary>
public abstract class StreamObjectHeaderEnd
{
    /// <summary>The stream object end type.</summary>
    public StreamObjectTypeHeaderEnd Type { get; set; }

    /// <summary>Parses a header end at the current reader position.</summary>
    public static StreamObjectHeaderEnd Parse(BinaryReaderEx reader)
    {
        int pos = reader.Position;
        int headerType = reader.ReadByte() & 0x03;
        reader.Position = pos;

        StreamObjectHeaderEnd end = headerType switch
        {
            0x1 => new StreamObjectHeaderEnd8Bit(),
            0x3 => new StreamObjectHeaderEnd16Bit(),
            _ => throw new InvalidDataException(
                $"Invalid stream object header end type {headerType} at position {pos}."),
        };
        end.Deserialize(reader);
        return end;
    }

    /// <summary>Deserializes this header end from the reader.</summary>
    public abstract void Deserialize(BinaryReaderEx reader);
}

/// <summary>
/// An 8-bit Stream Object Header End.
/// Bit layout (LSB-first): HeaderType (2, value 0x1), Type (6).
/// </summary>
public sealed class StreamObjectHeaderEnd8Bit : StreamObjectHeaderEnd
{
    /// <summary>Creates an 8-bit header end for the given type.</summary>
    public StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd type)
    {
        Type = type;
    }

    /// <summary>Default constructor for deserialization.</summary>
    public StreamObjectHeaderEnd8Bit()
    {
    }

    /// <inheritdoc />
    public override void Deserialize(BinaryReaderEx reader)
    {
        var bits = new BitReader(reader);
        int headerType = (int)bits.ReadBits(2);
        if (headerType != 0x1)
        {
            throw new InvalidDataException($"Expected 8-bit header end type 0x1, got {headerType}.");
        }

        Type = (StreamObjectTypeHeaderEnd)bits.ReadBits(6);
    }

    /// <summary>Serializes this header end to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        var bits = new BitWriter(writer);
        bits.WriteBits(0x1, 2);
        bits.WriteBits((uint)Type, 6);
        bits.Flush();
    }
}

/// <summary>
/// A 16-bit Stream Object Header End.
/// Bit layout (LSB-first): HeaderType (2, value 0x3), Type (14).
/// </summary>
public sealed class StreamObjectHeaderEnd16Bit : StreamObjectHeaderEnd
{
    /// <summary>Creates a 16-bit header end for the given type.</summary>
    public StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd type)
    {
        Type = type;
    }

    /// <summary>Default constructor for deserialization.</summary>
    public StreamObjectHeaderEnd16Bit()
    {
    }

    /// <inheritdoc />
    public override void Deserialize(BinaryReaderEx reader)
    {
        var bits = new BitReader(reader);
        int headerType = (int)bits.ReadBits(2);
        if (headerType != 0x3)
        {
            throw new InvalidDataException($"Expected 16-bit header end type 0x3, got {headerType}.");
        }

        Type = (StreamObjectTypeHeaderEnd)bits.ReadBits(14);
    }

    /// <summary>Serializes this header end to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        var bits = new BitWriter(writer);
        bits.WriteBits(0x3, 2);
        bits.WriteBits((uint)Type, 14);
        bits.Flush();
    }
}
