namespace CellBridge.FssHttpB.Tests;

public sealed class BinaryWriterTests
{
    [Fact]
    public void MixedWritesPreserveLittleEndianValuesAcrossGrowthAndOwnTheirBytes()
    {
        var writer = new BinaryWriterEx();
        writer.WriteSByte(sbyte.MinValue);
        writer.WriteInt16(short.MinValue);
        writer.WriteUInt16(ushort.MaxValue);
        writer.WriteInt32(int.MinValue);
        writer.WriteUInt32(uint.MaxValue);
        writer.WriteInt64(long.MinValue);
        writer.WriteUInt64(ulong.MaxValue);
        byte[] prefix = [0x80, 0, 0x80, 0xff, 0xff, 0, 0, 0, 0x80, 0xff, 0xff, 0xff, 0xff,
            0, 0, 0, 0, 0, 0, 0, 0x80, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff];
        Assert.Equal(prefix, writer.ToArray());
        var snapshot = writer.ToArray();
        var source = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
        writer.WriteBytes(source.AsSpan(1, 4000));
        writer.WriteBytes([]);
        source.AsSpan().Clear();
        Assert.Equal(prefix.Length + 4000, writer.Length);
        Assert.Equal(prefix, snapshot);
        snapshot[0] = 0;
        Assert.Equal(prefix.Concat(Enumerable.Range(1, 4000).Select(i => (byte)i)), writer.ToArray());
    }

    [Fact]
    public void LargeSpanWriteAllocatesOnlyItsOwnedBuffer()
    {
        var source = new byte[4 * 1024 * 1024];
        new BinaryWriterEx().WriteBytes([1]);
        var writer = new BinaryWriterEx();
        long before = GC.GetAllocatedBytesForCurrentThread();
        writer.WriteBytes(source);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, source.Length, source.Length + 4096L);
        Assert.Equal(source.Length, writer.Length);
    }
}
