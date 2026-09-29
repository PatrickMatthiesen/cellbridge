namespace OfficeCollabServer.FssHttpB;

/// <summary>
/// A binary writer that appends primitive types in little-endian order as
/// required by MS-FSSHTTPB.
/// </summary>
public sealed class BinaryWriterEx
{
    private readonly List<byte> _buffer = new();

    public int Length => _buffer.Count;

    public void WriteByte(byte value) => _buffer.Add(value);

    public void WriteSByte(sbyte value) => _buffer.Add(unchecked((byte)value));

    public void WriteUInt16(ushort value)
    {
        _buffer.Add((byte)(value & 0xFF));
        _buffer.Add((byte)((value >> 8) & 0xFF));
    }

    public void WriteInt16(short value) => WriteUInt16(unchecked((ushort)value));

    public void WriteUInt32(uint value)
    {
        _buffer.Add((byte)(value & 0xFF));
        _buffer.Add((byte)((value >> 8) & 0xFF));
        _buffer.Add((byte)((value >> 16) & 0xFF));
        _buffer.Add((byte)((value >> 24) & 0xFF));
    }

    public void WriteInt32(int value) => WriteUInt32(unchecked((uint)value));

    public void WriteUInt64(ulong value)
    {
        WriteUInt32((uint)(value & 0xFFFFFFFF));
        WriteUInt32((uint)((value >> 32) & 0xFFFFFFFF));
    }

    public void WriteInt64(long value) => WriteUInt64(unchecked((ulong)value));

    public void WriteBytes(ReadOnlySpan<byte> bytes) => _buffer.AddRange(bytes.ToArray());

    public byte[] ToArray() => _buffer.ToArray();
}
