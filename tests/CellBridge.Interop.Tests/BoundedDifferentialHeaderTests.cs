using Xunit.Abstractions;
using Server = CellBridge.FssHttpB;
using Reference = Microsoft.Protocols.TestSuites.SharedAdapter;

namespace CellBridge.Interop.Tests;

public sealed class BoundedDifferentialHeaderTests(ITestOutputHelper output)
{
    [Fact]
    public void MicrosoftAndCellBridgeAgreeOnRepresentableCompoundHeaderWidthsAndPreambles()
    {
        // Compare framing only. The full Microsoft parser does not promise to
        // accept arbitrary optional unknown extension objects.
        foreach (var (type, compound) in new[] { (0x02, false), (0x10, true), (0x5D, true) })
        foreach (int length in new[] { 0, 3, 127, 128, 32766, 32767 })
        foreach (bool narrow in new[] { false, true })
        {
            if (narrow && (type > 63 || length > 127)) continue;
            Reference.StreamObjectHeaderStart reference = narrow
                ? new Reference.StreamObjectHeaderStart16bit((Reference.StreamObjectTypeHeaderStart)type, length)
                : new Reference.StreamObjectHeaderStart32bit((Reference.StreamObjectTypeHeaderStart)type, length);
            reference.Compound = compound ? 1 : 0;
            byte[] header = ((Reference.IFSSHTTPBSerializable)reference).SerializeToByteList().ToArray();
            var writer = new Server.BinaryWriterEx();
            if (narrow) new Server.StreamObjectHeaderStart16Bit((Server.StreamObjectTypeHeaderStart)type, length)
                { Compound = compound ? 1 : 0 }.Serialize(writer);
            else new Server.StreamObjectHeaderStart32Bit((Server.StreamObjectTypeHeaderStart)type, length)
                { Compound = compound ? 1 : 0 }.Serialize(writer);
            Assert.Equal(header, writer.ToArray());
            var reader = new Server.BinaryReaderEx(header);
            var server = Server.StreamObjectHeaderStart.Parse(reader);
            Assert.Equal(0, reader.Remaining);
            Assert.Equal(length, server.Length); Assert.Equal(compound ? 1 : 0, server.Compound);
            int consumed = Reference.StreamObjectHeaderStart.TryParse(writer.ToArray(), 0, out var parsed);
            Assert.Equal(header.Length, consumed); Assert.Equal(type, (int)parsed.Type);
            int decodedLength = parsed is Reference.StreamObjectHeaderStart32bit wide && wide.LargeLength != null
                ? checked((int)wide.LargeLength.DecodedValue) : parsed.Length;
            Assert.Equal(length, decodedLength); Assert.Equal(server.Compound, parsed.Compound);
            if (compound)
            {
                foreach (bool shortEnd in new[] { false, true })
                {
                    if (shortEnd && type > 63) continue;
                    byte[] end = shortEnd
                        ? new Reference.StreamObjectHeaderEnd8bit(type).SerializeToByteList().ToArray()
                        : new Reference.StreamObjectHeaderEnd16bit(type).SerializeToByteList().ToArray();
                    var endReader = new Server.BinaryReaderEx(end);
                    Assert.Equal(type, (int)Server.StreamObjectHeaderEnd.Parse(endReader).Type);
                    Assert.Equal(0, endReader.Remaining);
                }
            }
            output.WriteLine($"type={type:X} length={length} compound={compound} narrow={narrow} header-bytes={header.Length}");
        }
    }
}
