using CellBridge.OfficeInspectors;
using CellBridge.OfficeInspectors.Parsers;

namespace OfficeInspectors.Adapter;

public sealed class ObjectDataBlobTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OpaquePayloadsPreserveEveryFirstByte(bool wideElement, bool wideBlob)
    {
        for (int first = 0; first <= 255; first++)
        {
            byte[] payload = [(byte)first, 0x05, 0xff, 0x80];
            var wire = Element(payload, wideElement, wideBlob);
            using var stream = new MemoryStream([..wire, 0xa5]);
            var parsed = new ObjectDataBLOBDataElements();
            parsed.Parse(stream);
            Assert.Equal(payload, parsed.Data.Content);
            Assert.Equal((ulong)payload.Length, parsed.Data.Length.GetUint(parsed.Data.Length));
            Assert.Equal(wire.Length, stream.Position);
            Assert.Equal(0xa5, stream.ReadByte());
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(127, false)]
    [InlineData(128, true)]
    [InlineData(32766, true)]
    [InlineData(32767, true)]
    [InlineData(65536, true)]
    public void EmptyAndLargePayloadsWorkInBothProtocolVersions(int length, bool wideBlob)
    {
        byte[] payload = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        foreach (ushort version in new ushort[] { 12, 13 })
        foreach (bool wideElement in new[] { false, true })
        {
            var element = Element(payload, wideElement, wideBlob);
            // Current 12/11 uses the imported parser's supported 16-bit end form.
            if (version == 12)
                element = [..element[..^1], 7, 0];
            var result = OfficeInspector.ParseResponse(Response(element, version));
            Assert.True(result.Parsed, result.Error);
            var blob = Assert.IsType<ObjectDataBLOBDataElements>(Assert.Single(result.Response.DataElementPackage.DataElements));
            Assert.Equal(payload, blob.Data.Content);
        }
    }

    [Fact]
    public void TruncationAndInvalidFramingAreRejected()
    {
        var valid = Element([0xff, 0x00, 0x80], false, false);
        foreach (var framed in new[] { valid, Element([0xff, 0, 0x80], true, true), Element(new byte[32767], true, true) })
        {
            // Every prefix for small frames; header prefixes and payload/end boundaries for large ones.
            var lengths = framed.Length < 100 ? Enumerable.Range(0, framed.Length)
                : Enumerable.Range(0, 25).Concat(new[] { framed.Length - 2, framed.Length - 1 });
            foreach (int length in lengths)
                Reject(framed[..length]);
        }
        foreach (int offset in new[] { 0, 5 })
        {
            var wrongType = valid.ToArray();
            wrongType[offset] ^= 8;
            Reject(wrongType);
            var compound = valid.ToArray();
            compound[offset] ^= 4;
            Reject(compound);
        }
        foreach (byte lengthByte in new byte[] { 4, 8 })
        {
            var wrongMetadataLength = valid.ToArray();
            wrongMetadataLength[1] = lengthByte;
            Reject(wrongMetadataLength);
            var wrongBlobLength = valid.ToArray();
            wrongBlobLength[6] = lengthByte;
            Reject(wrongBlobLength);
        }
        var wrongElementType = valid.ToArray();
        wrongElementType[4] = 3;
        Reject(wrongElementType);
        foreach (byte end in new byte[] { 0, 1, 4, 0x55 })
            Reject([..valid[..^1], end]);
        Reject([..valid[..^1], 7, 1]); // Wrong 16-bit end type.
    }

    [Theory]
    [InlineData(0x7fffffffUL)]
    [InlineData(0x80000000UL)]
    [InlineData(0x100000003UL)]
    [InlineData(ulong.MaxValue)]
    public void OversizedDeclaredLengthIsRejectedBeforeAllocation(ulong length)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        Header(writer, 1, 3, false, true);
        writer.Write(new byte[] { 0, 0, 21 });
        Header(writer, 2, length, true, false);
        writer.Write(new byte[] { 0, 0, 0, 5 });
        Reject(stream.ToArray());
    }

    private static void Reject(byte[] wire)
    {
        using var stream = new MemoryStream(wire);
        var error = Record.Exception(() => new ObjectDataBLOBDataElements().Parse(stream));
        Assert.True(error is InvalidDataException or EndOfStreamException, error?.ToString() ?? "Accepted malformed BLOB.");
    }

    // Independent MS-FSSHTTPB framing, with null EXGUID/serial and type 0x0a.
    private static byte[] Element(byte[] data, bool wideElement, bool wideBlob)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        Header(writer, 1, 3, wideElement, true);
        writer.Write(new byte[] { 0, 0, 21 });
        Header(writer, 2, (ulong)data.Length, wideBlob, false);
        writer.Write(data);
        writer.Write((byte)5);
        return stream.ToArray();
    }

    private static byte[] Response(byte[] element, ushort version)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(version);
        writer.Write((ushort)11);
        writer.Write(0x9b069439f329cf9dUL);
        Header(writer, 0x62, 1, true, true);
        writer.Write((byte)0);
        Header(writer, 0x15, 1, false, true);
        writer.Write((byte)0);
        writer.Write(element);
        writer.Write((byte)0x55);
        writer.Write((ushort)0x18b);
        return stream.ToArray();
    }

    private static void Header(BinaryWriter writer, uint type, ulong length, bool wide, bool compound)
    {
        uint flags = compound ? 4U : 0;
        if (!wide)
            writer.Write((ushort)((length << 9) | (type << 3) | flags));
        else
        {
            writer.Write(((uint)Math.Min(length, 32767UL) << 17) | (type << 3) | flags | 2);
            if (length >= 32767)
            {
                writer.Write((byte)0x80);
                writer.Write(length);
            }
        }
    }
}
