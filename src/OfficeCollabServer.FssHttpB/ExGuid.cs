namespace OfficeCollabServer.FssHttpB;

/// <summary>
/// An Extended GUID: a variable-length encoded (index value, GUID) pair.
/// [MS-FSSHTTPB] section 2.2.1.4.
/// The number of leading zero bits determines the encoded width of the index value:
/// 3 leading zeros = 5-bit value, 6 = 10-bit, 7 = 17-bit, 8 = 32-bit.
/// A null GUID (Guid.Empty) is encoded as a single zero byte.
/// </summary>
public sealed class ExGuid
{
    /// <summary>Creates an ExGuid with the given index value and GUID.</summary>
    public ExGuid(uint value, Guid guid)
    {
        Value = value;
        Guid = guid;
    }

    /// <summary>The index value.</summary>
    public uint Value { get; set; }

    /// <summary>The GUID. Must not be Guid.Empty on the wire (encoded as null).</summary>
    public Guid Guid { get; set; }

    /// <summary>Creates a null ExGuid (Guid.Empty).</summary>
    public static ExGuid Null => new(0, System.Guid.Empty);

    /// <summary>Whether this ExGuid represents the null encoding.</summary>
    public bool IsNull => Guid == System.Guid.Empty;

    /// <summary>Serializes the ExGuid to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        var bits = new BitWriter(writer);

        if (Guid == System.Guid.Empty)
        {
            bits.WriteBits(0, 8);
            bits.Flush();
            return;
        }

        uint v = Value;
        if (v <= 0x1F)
        {
            // 2 zero bits then a 1 (type value 4 in 3 bits), then a 5-bit value.
            bits.WriteBits(0, 2);
            bits.WriteBits(1, 1);
            bits.WriteBits(v, 5);
        }
        else if (v <= 0x3FF)
        {
            // 5 zero bits then a 1 (type value 32 in 6 bits), then a 10-bit value.
            bits.WriteBits(0, 5);
            bits.WriteBits(1, 1);
            bits.WriteBits(v, 10);
        }
        else if (v <= 0x1FFFF)
        {
            // 6 zero bits then a 1 (type value 64 in 7 bits), then a 17-bit value.
            bits.WriteBits(0, 6);
            bits.WriteBits(1, 1);
            bits.WriteBits(v, 17);
        }
        else
        {
            // 7 zero bits then a 1 (type value 128 in 8 bits), then a 32-bit value.
            bits.WriteBits(0, 7);
            bits.WriteBits(1, 1);
            bits.WriteBits(v, 32);
        }

        bits.Flush();
        WriteGuid(writer, Guid);
    }

    /// <summary>Deserializes an ExGuid from the reader.</summary>
    public static ExGuid Deserialize(BinaryReaderEx reader)
    {
        var bits = new BitReader(reader);

        int leadingZeros = 0;
        while (leadingZeros < 8 && bits.ReadBit() == 0)
        {
            leadingZeros++;
        }

        switch (leadingZeros)
        {
            case 8:
                // Null encoding: 8 zero bits.
                return Null;
            case 2:
                // Type value 8 (3 bits): 5-bit index value.
                return new ExGuid(bits.ReadBits(5), ReadGuid(reader));
            case 5:
                // Type value 32 (6 bits): 10-bit index value.
                return new ExGuid(bits.ReadBits(10), ReadGuid(reader));
            case 6:
                // Type value 64 (7 bits): 17-bit index value.
                return new ExGuid(bits.ReadBits(17), ReadGuid(reader));
            case 7:
                // Type value 128 (8 bits): 32-bit index value.
                return new ExGuid(bits.ReadBits(32), ReadGuid(reader));
            default:
                throw new InvalidDataException(
                    $"Invalid ExGuid type encoding: {leadingZeros} leading zero bits.");
        }
    }

    /// <summary>Writes a GUID in the MS-FSSHTTPB wire order (mixed-endian).</summary>
    internal static void WriteGuid(BinaryWriterEx writer, Guid guid)
    {
        byte[] bytes = guid.ToByteArray();
        // .NET Guid.ToByteArray is already in the mixed-endian order the
        // protocol uses (3 little-endian uints followed by 8 big-endian bytes).
        writer.WriteBytes(bytes);
    }

    /// <summary>Reads a GUID in the MS-FSSHTTPB wire order.</summary>
    internal static Guid ReadGuid(BinaryReaderEx reader)
    {
        byte[] bytes = reader.ReadBytes(16);
        return new Guid(bytes);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is ExGuid other && other.Value == Value && other.Guid == Guid;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Value, Guid);

    /// <inheritdoc />
    public override string ToString() => IsNull ? "null" : $"{Value}:{Guid}";
}
