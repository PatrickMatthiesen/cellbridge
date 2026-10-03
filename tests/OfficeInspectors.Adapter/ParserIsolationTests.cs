using CellBridge.OfficeInspectors;
using CellBridge.OfficeInspectors.Parsers;
using Server = CellBridge.FssHttpB;

namespace OfficeInspectors.Adapter;

public sealed class ParserIsolationTests
{
    [Theory]
    [InlineData("editors-table-token-8.fsshttpb.b64")]
    [InlineData("metadata-token-7.fsshttpb.b64")]
    [InlineData("file-contents-token-6.fsshttpb.b64")]
    [InlineData("query-access-token-13.fsshttpb.b64")]
    public void EveryTruncatedFixtureReportsFailureWithAnOffset(string name)
    {
        var bytes = Fixture(name);
        Assert.True(OfficeInspector.ParseResponse(bytes).Parsed);
        for (int length = 0; length < bytes.Length; length++)
        {
            var result = OfficeInspector.ParseResponse(bytes[..length]);
            Assert.False(result.Parsed, $"Accepted truncated {name} at length {length}.");
            Assert.NotNull(result.Error);
            Assert.InRange(result.Consumed, 0, length);
        }
    }

    [Fact]
    public void TrailingBytesSignatureAndWrongEndHeadersAreRejected()
    {
        var bytes = Fixture("metadata-token-7.fsshttpb.b64");
        Assert.False(OfficeInspector.ParseResponse([.. bytes, 0]).Parsed);
        var badSignature = bytes.ToArray();
        badSignature[4] ^= 1;
        Assert.False(OfficeInspector.ParseResponse(badSignature).Parsed);
        var badDiscriminator = bytes.ToArray();
        badDiscriminator[12] ^= 1;
        Assert.False(OfficeInspector.ParseResponse(badDiscriminator).Parsed);
        var badEnd = bytes.ToArray();
        badEnd[^2] = 0x07; // DataElement end in place of Response end.
        badEnd[^1] = 0;
        Assert.False(OfficeInspector.ParseResponse(badEnd).Parsed);
    }

    [Fact]
    public async Task ConcurrentEditorsTablesKeepTheirOwnStateAndDecodeBeyondTheLegacyBuffer()
    {
        var tasks = Enumerable.Range(0, 64).Select(index => Task.Run(() =>
        {
            string cacheId = "client-" + index;
            string name = cacheId + new string('x', 10_000);
            var response = Server.EditorsTablePartitionBuilder.BuildQueryChangesResponse(
                1, [new Server.EditorsTableEditor(cacheId, 123456789, name)]);
            var parsed = OfficeInspector.ParseResponse(response.ToByteArray());
            Assert.True(parsed.Parsed, parsed.Error ?? parsed.Summary);
            var table = Assert.Single(parsed.Response.DataElementPackage.DataElements
                .OfType<ObjectGroupDataElements>()
                .SelectMany(group => group.ObjectDataOrObjectDataBLOBReference)
                .OfType<ObjectData>()
                .Select(item => item.Data)
                .OfType<EditorsTable>());
            var editor = Assert.Single(table.Editors);
            Assert.Equal(cacheId, editor.CacheID);
            Assert.Equal(name, editor.FriendlyName);
            Assert.Equal(123456789, editor.Timeout);
        }));
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task ConcurrentReferenceFixturesRemainIndependent()
    {
        var tasks = Enumerable.Range(0, 64).Select(index => Task.Run(() =>
        {
            string name = index % 2 == 0 ? "editors-table-token-8.fsshttpb.b64" : "query-access-token-13.fsshttpb.b64";
            var result = OfficeInspector.ParseResponse(Fixture(name));
            Assert.True(result.Parsed, result.Error ?? result.Summary);
            var sub = Assert.Single(result.Response.SubResponses);
            Assert.Equal(index % 2 == 0 ? 2UL : 1UL, sub.RequestType.GetUint(sub.RequestType));
        }));
        await Task.WhenAll(tasks);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(127UL)]
    [InlineData(128UL)]
    [InlineData(ulong.MaxValue)]
    public void CompactIntegersPreserveTheirUnsignedValue(ulong value)
    {
        var writer = new Server.BinaryWriterEx();
        new Server.Compact64bitInt(value).Serialize(writer);
        using var stream = new MemoryStream(writer.ToArray());
        var parsed = new CompactUnsigned64bitInteger().TryParse(stream);
        Assert.Equal(value, parsed.GetUint(parsed));
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public void MultipleCellReferencesRetainDistinctIdentities()
    {
        var first = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var writer = new Server.BinaryWriterEx();
        new Server.Compact64bitInt(2).Serialize(writer);
        new Server.CellId(new Server.ExGuid(1, first), new Server.ExGuid(2, first)).Serialize(writer);
        new Server.CellId(new Server.ExGuid(3, second), new Server.ExGuid(4, second)).Serialize(writer);
        using var stream = new MemoryStream(writer.ToArray());
        var parsed = new CellIDArray();
        parsed.Parse(stream);
        Assert.Equal(first, parsed.Content[0].EXGUID1.GetGUID(parsed.Content[0].EXGUID1));
        Assert.Equal(second, parsed.Content[1].EXGUID1.GetGUID(parsed.Content[1].EXGUID1));
        Assert.NotSame(parsed.Content[0], parsed.Content[1]);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Theory]
    [InlineData(0x1_0000_0000UL)]
    [InlineData(0x1_0000_0001UL)]
    public void OpaqueExtensionsCannotAcceptOverflowingDeclaredLengths(ulong length)
    {
        var writer = new Server.BinaryWriterEx();
        writer.WriteBytes([0x72, 0x02, 0xfe, 0xff]); // 32-bit 0x04E header with extended length.
        new Server.Compact64bitInt(length).Serialize(writer);
        using var stream = new MemoryStream(writer.ToArray());
        Assert.Throws<OverflowException>(() => new OpaqueStreamObject().Parse(stream));
    }

    private static byte[] Fixture(string name) => Convert.FromBase64String(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)).Trim());
}
