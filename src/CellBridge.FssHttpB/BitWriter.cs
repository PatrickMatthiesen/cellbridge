namespace CellBridge.FssHttpB;

/// <summary>
/// A bit-level writer that appends bits in LSB-first order and flushes them
/// into a <see cref="BinaryWriterEx"/> as complete bytes.
/// </summary>
/// <remarks>
/// <para>
/// The MS-FSSHTTPB protocol packs multiple fields into single bytes using
/// LSB-first bit order. This writer accumulates bits and emits them to the
/// underlying <see cref="BinaryWriterEx"/> as soon as a full byte is available.
/// </para>
/// <para>
/// All bit-level structures in the protocol are byte-aligned, so callers must
/// ensure the total number of bits written is a multiple of 8 before reading
/// the output. <see cref="Flush"/> pads the final partial byte with zero bits.
/// </para>
/// </remarks>
public sealed class BitWriter
{
    private readonly BinaryWriterEx _writer;
    private uint _buffer;
    private int _bitsAccumulated;

    /// <summary>
    /// Initializes a new instance of the <see cref="BitWriter"/> class.
    /// </summary>
    /// <param name="writer">The underlying byte-level writer.</param>
    public BitWriter(BinaryWriterEx writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _buffer = 0;
        _bitsAccumulated = 0;
    }

    /// <summary>
    /// Writes the specified number of bits from <paramref name="value"/>,
    /// LSB-first. Bit 0 of <paramref name="value"/> is written first.
    /// </summary>
    /// <param name="value">The value whose bits are written.</param>
    /// <param name="count">The number of bits to write (0–32).</param>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="count"/> is negative or greater than 32.</exception>
    public void WriteBits(uint value, int count)
    {
        if (count < 0 || count > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Bit count must be between 0 and 32.");
        }

        for (int i = 0; i < count; i++)
        {
            if ((value & (1u << i)) != 0)
            {
                _buffer |= 1u << _bitsAccumulated;
            }

            _bitsAccumulated++;

            if (_bitsAccumulated == 8)
            {
                _writer.WriteByte((byte)_buffer);
                _buffer = 0;
                _bitsAccumulated = 0;
            }
        }
    }

    /// <summary>
    /// Writes up to 64 bits of a <see cref="ulong"/>, LSB-first, by splitting
    /// it into two 32-bit halves.
    /// </summary>
    /// <param name="value">The value whose bits are written.</param>
    /// <param name="count">The number of bits to write (0–64).</param>
    public void WriteBits(ulong value, int count)
    {
        if (count <= 32)
        {
            WriteBits((uint)value, count);
            return;
        }

        WriteBits((uint)(value & 0xFFFFFFFF), 32);
        WriteBits((uint)(value >> 32), count - 32);
    }

    /// <summary>
    /// Writes a single bit.
    /// </summary>
    /// <param name="value">0 or 1.</param>
    public void WriteBit(int value)
    {
        WriteBits((uint)(value & 1), 1);
    }

    /// <summary>
    /// Flushes any remaining partial byte, padding with zero bits.
    /// </summary>
    public void Flush()
    {
        if (_bitsAccumulated > 0)
        {
            _writer.WriteByte((byte)_buffer);
            _buffer = 0;
            _bitsAccumulated = 0;
        }
    }
}
