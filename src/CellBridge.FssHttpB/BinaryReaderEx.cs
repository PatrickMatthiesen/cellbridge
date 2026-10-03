namespace CellBridge.FssHttpB;

/// <summary>
/// A binary reader over a byte buffer that tracks position and supports the
/// primitive types used by MS-FSSHTTPB. All multi-byte integers are
/// little-endian per the spec.
/// </summary>
public sealed class BinaryReaderEx
{
    private readonly ReadOnlyMemory<byte> _buffer;
    private readonly int _start;
    private int _position;
    private int _end;

    public BinaryReaderEx(byte[] buffer)
        : this((ReadOnlyMemory<byte>)(buffer ?? throw new ArgumentNullException(nameof(buffer)))) { }

    public BinaryReaderEx(ReadOnlyMemory<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
        _end = buffer.Length;
    }

    /// <summary>
    /// Initializes a reader over a region of a byte array.
    /// </summary>
    public BinaryReaderEx(byte[] buffer, int offset, int count)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        _buffer = buffer;
        _start = offset;
        _position = offset;
        _end = offset + count;
    }

    public int Length => _end;
    public int Remaining => _end - _position;

    /// <summary>Gets or sets the reader position within the buffer.</summary>
    public int Position
    {
        get => _position;
        set
        {
            if (value < _start || value > _end)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            _position = value;
        }
    }

    public byte ReadByte()
    {
        Ensure(1);
        return _buffer.Span[_position++];
    }

    public sbyte ReadSByte()
    {
        Ensure(1);
        return unchecked((sbyte)_buffer.Span[_position++]);
    }

    public ushort ReadUInt16()
    {
        Ensure(2);
        var span = _buffer.Span;
        ushort v = (ushort)(span[_position] | (span[_position + 1] << 8));
        _position += 2;
        return v;
    }

    public short ReadInt16()
    {
        return unchecked((short)ReadUInt16());
    }

    public uint ReadUInt32()
    {
        Ensure(4);
        var span = _buffer.Span;
        uint v = (uint)(span[_position]
            | (span[_position + 1] << 8)
            | (span[_position + 2] << 16)
            | (span[_position + 3] << 24));
        _position += 4;
        return v;
    }

    public int ReadInt32()
    {
        return unchecked((int)ReadUInt32());
    }

    public ulong ReadUInt64()
    {
        Ensure(8);
        ulong lo = ReadUInt32();
        ulong hi = ReadUInt32();
        return lo | (hi << 32);
    }

    public long ReadInt64()
    {
        return unchecked((long)ReadUInt64());
    }

    public byte[] ReadBytes(int count)
    {
        Ensure(count);
        var result = _buffer.Slice(_position, count).ToArray();
        _position += count;
        return result;
    }

    public void Skip(int count)
    {
        Ensure(count);
        _position += count;
    }

    public ReadOnlyMemory<byte> ReadMemory(int count)
    {
        Ensure(count);
        var result = _buffer.Slice(_position, count);
        _position += count;
        return result;
    }

    private void Ensure(int count)
    {
        if (count < 0 || count > _end - _position)
        {
            throw new EndOfStreamException(
                $"Attempted to read {count} bytes at position {_position} but buffer length is {_end}.");
        }
    }
}
