using Xunit.Abstractions;

namespace CellBridge.FssHttpB.Tests;

public sealed class BoundedNestedFramingTests(ITestOutputHelper output)
{
    private const int Seed = 0x18_34;

    [Theory]
    [InlineData("request")]
    [InlineData("user-agent")]
    [InlineData("subrequest")]
    [InlineData("element")]
    public void MixedNestedExtensionsPreserveSiblingsAndRejectMalformedEnds(string site)
    {
        var random = new Random(Seed);
        for (int sample = 0; sample < 16; sample++)
        {
            byte[] tree = Tree(random.Next(1, 6), sample);
            byte[] wire = Wrap(site, tree);
            Assert.InRange(wire.Length, 1, 1024);
            output.WriteLine($"seed={Seed} sample={sample} site={site} bytes={wire.Length} mutation=nested-headers/ends/prefix");
            Check(site, wire, tree);
            for (int length = 0; length < wire.Length; length++) Reject(site, wire[..length]);

            // The last end closes the extension, not its enclosing known object.
            byte[] mismatch = tree.ToArray(); mismatch[^1] ^= 0x04;
            Reject(site, Wrap(site, mismatch));
            byte[] noncompound = tree.ToArray(); noncompound[0] ^= 0x04;
            Reject(site, Wrap(site, noncompound));
            int endSize = sample % 2 != 0 || sample % 4 == 0 ? 2 : 1; // high type 0x1FE ends as FB 07.
            Reject(site, Wrap(site, tree[..^endSize]));
            var headerReader = new BinaryReaderEx(tree);
            var root = StreamObjectHeaderStart.Parse(headerReader);
            var oversized = new BinaryWriterEx();
            new StreamObjectHeaderStart32Bit(root.Type, tree.Length - headerReader.Position + 1)
                { Compound = 1 }.Serialize(oversized);
            oversized.WriteBytes(tree.AsSpan(headerReader.Position));
            Reject(site, Wrap(site, oversized.ToArray()));
            // A premature outer end replaces the entire extension and leaves its close behind.
            Reject(site, Wrap(site, [0x03, 0x01, ..tree])); // Request end.
        }
    }

    [Theory]
    [InlineData("request")]
    [InlineData("user-agent")]
    [InlineData("subrequest")]
    [InlineData("element")]
    public void TerminalLeafAtDepth32PassesAndDepth33Fails(string site)
    {
        // The outer skipped extension starts at zero. N compounds put its leaf at depth N.
        byte[] accepted = Tree(32, 0);
        Check(site, Wrap(site, accepted), accepted);
        var error = Assert.Throws<InvalidDataException>(() => Decode(site, Wrap(site, Tree(33, 0))));
        Assert.Contains("nesting limit", error.Message);
        output.WriteLine($"seed={Seed} site={site} accepted-leaf-depth=32 rejected-leaf-depth=33");
    }

    [Theory]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(32766)]
    [InlineData(32767)]
    public void LeafLengthTransitionsPreserveAlignmentAndTruncatedLargeLengthFails(int length)
    {
        var writer = new BinaryWriterEx();
        if (length <= 127) new StreamObjectHeaderStart16Bit((StreamObjectTypeHeaderStart)0x3E, length).Serialize(writer);
        else new StreamObjectHeaderStart32Bit((StreamObjectTypeHeaderStart)0x1FE, length).Serialize(writer);
        int headerSize = writer.Length;
        writer.WriteBytes(Enumerable.Repeat((byte)0x03, length).ToArray()); // Payload resembles end headers.
        byte[] leaf = writer.ToArray();
        foreach (string site in new[] { "request", "user-agent", "subrequest", "element" })
        {
            Check(site, Wrap(site, leaf), leaf);
            foreach (int cut in new[] { 1, headerSize - 1, headerSize, leaf.Length - 1 })
                Reject(site, Wrap(site, leaf[..cut]));
        }
        output.WriteLine($"seed={Seed} leaf-length={length} header-size={headerSize} mutation=length-transition");
    }

    private static byte[] Tree(int compounds, int sample)
    {
        var writer = new BinaryWriterEx();
        for (int depth = 0; depth < compounds; depth++)
        {
            bool wide = (depth + sample) % 2 != 0;
            int type = wide ? 0x1FE : 0x3E;
            if (wide) new StreamObjectHeaderStart32Bit((StreamObjectTypeHeaderStart)type, 3) { Compound = 1 }.Serialize(writer);
            else new StreamObjectHeaderStart16Bit((StreamObjectTypeHeaderStart)type, 3) { Compound = 1 }.Serialize(writer);
            writer.WriteBytes([0x03, 0x01, 0xFF]); // Fixed preamble, not child objects.
            new StreamObjectHeaderStart16Bit((StreamObjectTypeHeaderStart)0x3D, 1).Serialize(writer);
            writer.WriteByte((byte)depth); // Sibling before the nested child.
        }
        new StreamObjectHeaderStart32Bit((StreamObjectTypeHeaderStart)0x1FD, 2).Serialize(writer);
        writer.WriteBytes([0x01, 0x03]);
        for (int depth = compounds - 1; depth >= 0; depth--)
        {
            new StreamObjectHeaderStart16Bit((StreamObjectTypeHeaderStart)0x3D, 0).Serialize(writer);
            int type = (depth + sample) % 2 != 0 ? 0x1FE : 0x3E;
            if (type > 63 || (depth + sample) % 4 == 0)
                new StreamObjectHeaderEnd16Bit((StreamObjectTypeHeaderEnd)type).Serialize(writer);
            else new StreamObjectHeaderEnd8Bit((StreamObjectTypeHeaderEnd)type).Serialize(writer);
        }
        return writer.ToArray();
    }

    private static byte[] Wrap(string site, byte[] extension)
    {
        if (site == "element")
        {
            // Raw type-specific bytes are retained by the envelope parser.
            var element = new DataElement(DataElementType.None, new(5, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")),
                SerialNumber.Null) { Data = [..Leaf(7), ..extension, ..Leaf(9)] };
            var writer = new BinaryWriterEx(); element.Serialize(writer);
            return writer.ToArray();
        }
        var request = new FsshttpbCellRequest
        {
            UserAgentVersionValue = 123,
            SubRequests = { new(RequestTypes.QueryAccess) { RequestId = 7 }, new(RequestTypes.QueryAccess) { RequestId = 9 } },
        };
        byte[] wire = request.ToByteArray();
        int offset;
        if (site == "request")
        {
            // Place the extension between two known operations.
            int end = wire.Length - 2;
            var last = new BinaryWriterEx(); request.SubRequests[1].Serialize(last);
            offset = end - last.Length;
        }
        else if (site == "subrequest")
        {
            var first = new BinaryWriterEx(); request.SubRequests[0].Serialize(first);
            offset = 50 + first.Length - 2; // First subrequest's end follows the fixed UserAgent.
        }
        else offset = 40; // Between GUID payload and UserAgentVersion header.
        return [..wire[..offset], ..extension, ..wire[offset..]];
    }

    private static byte[] Leaf(byte value)
    {
        var writer = new BinaryWriterEx();
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.ObjectDataBLOB, 1).Serialize(writer);
        writer.WriteByte(value); return writer.ToArray();
    }

    private static object Decode(string site, byte[] wire)
    {
        var reader = new BinaryReaderEx(wire);
        object parsed = site == "element" ? DataElement.Deserialize(reader) : FsshttpbCellRequest.Deserialize(reader);
        // The host requires one complete payload. The stream decoder can return at
        // an early matching outer end, so trailing bytes are a framing failure here.
        if (reader.Remaining != 0) throw new InvalidDataException("Trailing payload bytes.");
        return parsed;
    }

    private static void Check(string site, byte[] wire, byte[] extension)
    {
        var parsed = Decode(site, wire);
        if (parsed is DataElement element) Assert.Equal((byte[])[..Leaf(7), ..extension, ..Leaf(9)], element.Data);
        else
        {
            var request = Assert.IsType<FsshttpbCellRequest>(parsed);
            Assert.Equal(123u, request.UserAgentVersionValue);
            Assert.Equal(new ulong[] { 7, 9 }, request.SubRequests.Select(s => s.RequestId));
            Assert.All(request.SubRequests, s => Assert.Equal(RequestTypes.QueryAccess, s.RequestType));
        }
    }

    private static void Reject(string site, byte[] wire)
    {
        var error = Record.Exception(() => Decode(site, wire));
        Assert.True(error is EndOfStreamException or InvalidDataException, $"site={site} bytes={wire.Length}: {error}");
    }
}
