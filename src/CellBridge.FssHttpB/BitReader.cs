namespace CellBridge.FssHttpB;

/// <summary>
/// A bit-level reader that wraps a <see cref="BinaryReaderEx"/> and provides
/// LSB-first bit reads. Bits are read from the least-significant bit of each
/// byte, matching the MS-FSSHTTPB wire format.
/// </summary>
/// <remarks>
/// <para>
/// The MS-FSSHTTPB protocol packs multiple fields into single bytes using
/// LSB-first bit order. This reader accumulates bytes from the underlying
/// <see cref="BinaryReaderEx"/> and serves individual bits on demand.
/// </para>
/// <para>
/// All bit-level structures in the protocol (stream object headers, compact
/// integers, extended GUIDs, serial numbers, and flag bytes) are byte-aligned,
/// meaning a bit read sequence always starts and ends on a byte boundary.
/// </para>
/// </remarks>
public sealed class BitReader
{
    private readonly BinaryReaderEx _reader;
    private uint _buffer;
    private int _bitsRemaining;

    /// <summary>
    /// Initializes a new instance of the <see cref="BitReader"/> class.
    /// </summary>
    /// <param name="reader">The underlying byte-level reader.</param>
    public BitReader(BinaryReaderEx reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _buffer = 0;
        _bitsRemaining = 0;
    }

    /// <summary>
    /// Reads the specified number of bits, LSB-first, and returns them as an
    /// unsigned integer where bit 0 is the first bit read.
    /// </summary>
    /// <param name="count">The number of bits to read (0–32).</param>
    /// <returns>The value of the read bits.</returns>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="count"/> is negative or greater than 32.</exception>
    public uint ReadBits(int count)
    {
        if (count < 0 || count > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Bit count must be between 0 and 32.");
        }

        uint result = 0;
        for (int i = 0; i < count; i++)
        {
            if (_bitsRemaining == 0)
            {
                _buffer = _reader.ReadByte();
                _bitsRemaining = 8;
            }

            if ((_buffer & 1u) != 0)
            {
                result |= 1u << i;
            }

            _buffer >>= 1;
            _bitsRemaining--;
        }

        return result;
    }

    /// <summary>
    /// Reads up to 64 bits, LSB-first, by splitting into two 32-bit reads.
    /// </summary>
    /// <param name="count">The number of bits to read (0–64).</param>
    /// <returns>The value of the read bits.</returns>
    public ulong ReadBits64(int count)
    {
        if (count <= 32)
        {
            return ReadBits(count);
        }

        ulong lo = ReadBits(32);
        ulong hi = ReadBits(count - 32);
        return lo | (hi << 32);
    }

    /// <summary>
    /// Reads a single bit and returns it as 0 or 1.
    /// </summary>
    /// <returns>0 or 1.</returns>
    public int ReadBit()
    {
        return (int)ReadBits(1);
    }
}
