using System.Text;

namespace CellBridge.FssHttpB.Tests;

public sealed class UserAgentTests
{
    [Theory]
    [InlineData("OneNote", "Windows")]
    [InlineData("", "")]
    [InlineData("文字", "桌面📝")]
    public void ClientAndPlatformReplacesGuidAndKeepsFollowingRequestAligned(string client, string platform)
    {
        var request = new FsshttpbCellRequest { UserAgentClientAndPlatform = new(client, platform), UserAgentVersionValue = 17 };
        request.SubRequests.Add(new(RequestTypes.QueryAccess) { RequestId = 9 });
        var reader = new BinaryReaderEx(request.ToByteArray());
        var parsed = FsshttpbCellRequest.Deserialize(reader);
        Assert.Equal(new(client, platform), parsed.UserAgentClientAndPlatform);
        Assert.Equal(17U, parsed.UserAgentVersionValue);
        Assert.Equal(9UL, Assert.Single(parsed.SubRequests).RequestId);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void LongMultibyteIdentityUsesTheLargeLengthHeader()
    {
        string client = new('文', 20000);
        var request = new FsshttpbCellRequest { UserAgentClientAndPlatform = new(client, "Windows") };
        request.SubRequests.Add(new(RequestTypes.QueryAccess));
        var parsed = FsshttpbCellRequest.Deserialize(new(request.ToByteArray()));
        Assert.Equal(client, parsed.UserAgentClientAndPlatform!.Client);
    }

    [Fact]
    public void BothIdentitiesArePermittedOnRead()
    {
        var parsed = FsshttpbCellRequest.Deserialize(new(Build(guid: true)));
        Assert.Equal(Guid.Parse("12345678-1234-1234-1234-123456789012"), parsed.UserAgentGuid);
        Assert.Equal(new("A", "B"), parsed.UserAgentClientAndPlatform);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("version")]
    [InlineData("guid")]
    public void DuplicateKnownFieldsAreRejected(string duplicate) =>
        Assert.Throws<InvalidDataException>(() => FsshttpbCellRequest.Deserialize(new(Build(guid: duplicate == "guid", duplicate: duplicate))));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void IdentityAndVersionAreRequiredWithinUserAgent(bool identity, bool version) =>
        Assert.Throws<InvalidDataException>(() => FsshttpbCellRequest.Deserialize(new(Build(identity: identity, version: version))));

    [Theory]
    [InlineData(new byte[] { 3, 0xFF, 3, 66 })] // one-byte strings; invalid UTF-8 client
    [InlineData(new byte[] { 7, 65, 3, 66 })] // declared three-byte client consumes platform
    [InlineData(new byte[] { 3, 65, 3, 66, 0 })] // trailing byte
    public void MalformedIdentityIsRejected(byte[] payload)
    {
        var error = Record.Exception(() => FsshttpbCellRequest.Deserialize(new(Build(payload: payload))));
        Assert.True(error is InvalidDataException or EndOfStreamException, error?.ToString() ?? "Expected rejection.");
    }

    [Fact]
    public void SurrogateWithoutPairCannotBeSerialized()
    {
        var identity = new UserAgentClientAndPlatform("\uD800", "Windows");
        Assert.Throws<EncoderFallbackException>(() => identity.Serialize(new()));
    }

    [Fact]
    public void CompoundIdentityIsRejected() =>
        Assert.Throws<InvalidDataException>(() => FsshttpbCellRequest.Deserialize(new(Build(compound: true))));

    // Compact values 1 encode as 0x03. This fixed body is independent of the identity writer.
    private static byte[] Build(bool guid = false, string? duplicate = null, bool identity = true,
        bool version = true, byte[]? payload = null, bool compound = false)
    {
        var writer = new BinaryWriterEx();
        writer.WriteUInt16(13); writer.WriteUInt16(11); writer.WriteUInt64(FsshttpbCellRequest.RequestSignature);
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.Request, 0).Serialize(writer);
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.UserAgent, 0).Serialize(writer);
        if (guid) WriteGuid();
        if (identity) WriteIdentity();
        if (version) WriteVersion();
        if (duplicate == "identity") WriteIdentity();
        if (duplicate == "version") WriteVersion();
        if (duplicate == "guid") WriteGuid();
        new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.UserAgent).Serialize(writer);
        new FsshttpbCellSubRequest(RequestTypes.QueryAccess) { RequestId = 1 }.Serialize(writer);
        new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.Request).Serialize(writer);
        return writer.ToArray();
        void WriteIdentity()
        {
            byte[] body = payload ?? [3, 65, 3, 66];
            var header = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.UserAgentClientandPlatform, body.Length);
            if (compound) header.Compound = 1;
            header.Serialize(writer); writer.WriteBytes(body);
        }
        void WriteVersion()
        {
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.UserAgentVersion, 4).Serialize(writer);
            writer.WriteUInt32(17);
        }
        void WriteGuid()
        {
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.UserAgentGUID, 16).Serialize(writer);
            writer.WriteBytes(Guid.Parse("12345678-1234-1234-1234-123456789012").ToByteArray());
        }
    }
}
