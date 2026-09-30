namespace CellBridge.FssHttpB;

/// <summary>
/// A variable-length compact encoding of unsigned 64-bit integers.
/// [MS-FSSHTTPB] section 2.2.1.2.
/// The number of leading zero bits determines the encoded width:
/// 0 leading zeros = 7-bit value (1 byte), 1 = 14-bit (2 bytes), ... 7 = 64-bit (9 bytes).
/// A value of 0 is encoded as a single zero byte.
/// </summary>
public sealed class Compact64bitInt
{
    /// <summary>Creates a compact integer wrapping the given value.</summary>
    public Compact64bitInt(ulong value)
    {
        Value = value;
    }

    /// <summary>The decoded value.</summary>
    public ulong Value { get; set; }

    /// <summary>Implicitly converts an ulong to a Compact64bitInt.</summary>
    public static implicit operator Compact64bitInt(ulong value) => new(value);

    /// <summary>Implicitly converts a Compact64bitInt to an ulong.</summary>
    public static implicit operator ulong(Compact64bitInt c) => c.Value;

    /// <summary>
    /// Serializes the compact integer to the writer.
    /// The type prefix is k zero bits followed by a 1 bit (i.e. the type value
    /// 2^k written in k+1 bits), followed by the value in 7*(k+1) bits
    /// (64 bits for k=7). A value of 0 is encoded as 8 zero bits.
    /// </summary>
    public void Serialize(BinaryWriterEx writer)
    {
        var bits = new BitWriter(writer);
        ulong v = Value;

        if (v == 0)
        {
            bits.WriteBits(0, 8);
        }
        else if (v <= 0x7F)
        {
            bits.WriteBits(1, 1);
            bits.WriteBits((uint)v, 7);
        }
        else if (v <= 0x3FFF)
        {
            bits.WriteBits(0, 1);
            bits.WriteBits(1, 1);
            bits.WriteBits((uint)(v & 0x3FFF), 14);
        }
        else if (v <= 0x1FFFFF)
        {
            bits.WriteBits(0, 2);
            bits.WriteBits(1, 1);
            bits.WriteBits((uint)(v & 0x1FFFFF), 21);
        }
        else if (v <= 0xFFFFFFF)
        {
            bits.WriteBits(0, 3);
            bits.WriteBits(1, 1);
            bits.WriteBits((uint)(v & 0xFFFFFFF), 28);
        }
        else if (v <= 0x7FFFFFFFF)
        {
            bits.WriteBits(0, 4);
            bits.WriteBits(1, 1);
            bits.WriteBits(v, 35);
        }
        else if (v <= 0x3FFFFFFFFFF)
        {
            bits.WriteBits(0, 5);
            bits.WriteBits(1, 1);
            bits.WriteBits(v, 42);
        }
        else if (v <= 0x1FFFFFFFFFFFF)
        {
            bits.WriteBits(0, 6);
            bits.WriteBits(1, 1);
            bits.WriteBits(v, 49);
        }
        else
        {
            bits.WriteBits(0, 7);
            bits.WriteBits(1, 1);
            bits.WriteBits(v, 64);
        }

        bits.Flush();
    }

    /// <summary>
    /// Deserializes a compact integer from the reader.
    /// </summary>
    public static Compact64bitInt Deserialize(BinaryReaderEx reader)
    {
        var bits = new BitReader(reader);

        // Count leading zeros; the terminating 1 bit completes the type prefix.
        int leadingZeros = 0;
        while (leadingZeros < 8 && bits.ReadBit() == 0)
        {
            leadingZeros++;
        }

        // leadingZeros == 8 means the whole byte was zeros: value 0.
        // Otherwise leadingZeros == k means type value 2^k and 7*(k+1) value bits follow.
        ulong value = leadingZeros switch
        {
            8 => 0,
            0 => bits.ReadBits(7),
            1 => bits.ReadBits(14),
            2 => bits.ReadBits(21),
            3 => bits.ReadBits(28),
            4 => bits.ReadBits64(35),
            5 => bits.ReadBits64(42),
            6 => bits.ReadBits64(49),
            _ => bits.ReadBits64(64),
        };

        return new Compact64bitInt(value);
    }

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}
