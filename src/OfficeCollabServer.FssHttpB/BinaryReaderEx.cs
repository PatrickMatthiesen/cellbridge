namespace OfficeCollabServer.FssHttpB;

/// <summary>
/// A binary reader over a byte buffer that tracks position and supports the
/// primitive types used by MS-FSSHTTPB. All multi-byte integers are
/// little-endian per the spec.
/// </summary>
public sealed class BinaryReaderEx
{
    private readonly byte[] _buffer;
    private int _position;
    private int _end;

    public BinaryReaderEx(byte[] buffer)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _position = 0;
        _end = buffer.Length;
    }

    /// <summary>
    /// Initializes a reader over a region of a byte array.
    /// </summary>
    public BinaryReaderEx(byte[] buffer, int offset, int count)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        _buffer = buffer;
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
            if (value < 0 || value > _end)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            _position = value;
        }
    }

    public byte ReadByte()
    {
        Ensure(1);
        return _buffer[_position++];
    }

    public sbyte ReadSByte()
    {
        Ensure(1);
        return unchecked((sbyte)_buffer[_position++]);
    }

    public ushort ReadUInt16()
    {
        Ensure(2);
        ushort v = (ushort)(_buffer[_position] | (_buffer[_position + 1] << 8));
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
        uint v = (uint)(_buffer[_position]
            | (_buffer[_position + 1] << 8)
            | (_buffer[_position + 2] << 16)
            | (_buffer[_position + 3] << 24));
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
        var result = new byte[count];
        Buffer.BlockCopy(_buffer, _position, result, 0, count);
        _position += count;
        return result;
    }

    public void Skip(int count)
    {
        Ensure(count);
        _position += count;
    }

    private void Ensure(int count)
    {
        if (_position + count > _end)
        {
            throw new EndOfStreamException(
                $"Attempted to read {count} bytes at position {_position} but buffer length is {_end}.");
        }
    }
}
