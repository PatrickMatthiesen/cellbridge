using System.Buffers.Binary;
using System.Text;
using CellBridge.FssHttpB.OfficeCollaboration;

namespace CellBridge.FssHttpB.Tests;

public sealed class OcsFrameCodecTests
{
    [Fact]
    public void ReadsAndWritesJsonAndOpaqueBlobFramesWithTerminator()
    {
        byte[] blobId = Enumerable.Repeat((byte)0xfb, 16).ToArray();
        string blobReference = Convert.ToBase64String(blobId);
        string blobName = OcsFrameCodec.BlobReferenceToFrameName(blobReference);
        byte[] json = Encoding.UTF8.GetBytes("{\"supported\":true}");
        byte[] blob = [0, 255, 1, 128, 0x42];
        OcsFrame[] frames =
        [
            new(OcsFrameType.Json, string.Empty, json),
            new(OcsFrameType.Blob, blobName, blob),
        ];

        byte[] encoded = OcsFrameCodec.Write(frames);
        IReadOnlyList<OcsFrame> decoded = OcsFrameCodec.Read(encoded);

        Assert.Equal(2, decoded.Count);
        Assert.Equal(OcsFrameType.Json, decoded[0].Type);
        Assert.Equal(string.Empty, decoded[0].Name);
        Assert.Equal(json, decoded[0].Payload.ToArray());
        Assert.Equal(OcsFrameType.Blob, decoded[1].Type);
        Assert.Equal(blobName, decoded[1].Name);
        Assert.Equal(blob, decoded[1].Payload.ToArray());
        Assert.Equal(blobReference, OcsFrameCodec.FrameNameToBlobReference(decoded[1].Name));

        Assert.Equal((uint)OcsFrameType.Terminator,
            BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(encoded.Length - 16, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(encoded.Length - 12, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(encoded.Length - 8, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(encoded.Length - 4, 4)));
    }

    [Fact]
    public void BlobReferenceAndObservedFrameNameRoundTripA16ByteId()
    {
        byte[] blobId = Enumerable.Repeat((byte)0xfb, 16).ToArray();
        const string blobReference = "+/v7+/v7+/v7+/v7+/v7+w==";
        const string frameName = "{\"BlobId\":\"+/v7+/v7+/v7+/v7+/v7+w==\"}";

        Assert.Equal(blobReference, Convert.ToBase64String(blobId));
        Assert.Equal(frameName, OcsFrameCodec.BlobReferenceToFrameName(blobReference));
        Assert.Equal(blobReference, OcsFrameCodec.FrameNameToBlobReference(frameName));
    }

    [Fact]
    public void ZeroLengthBlobAndArbitraryJsonBytesArePreserved()
    {
        string frameName = OcsFrameCodec.BlobReferenceToFrameName(Convert.ToBase64String(new byte[16]));
        OcsFrame[] frames =
        [
            new(OcsFrameType.Json, string.Empty, new byte[] { 0xff, 0x00, 0x7b }),
            new(OcsFrameType.Blob, frameName, ReadOnlyMemory<byte>.Empty),
        ];

        IReadOnlyList<OcsFrame> decoded = OcsFrameCodec.Read(OcsFrameCodec.Write(frames));

        Assert.Equal(new byte[] { 0xff, 0x00, 0x7b }, decoded[0].Payload.ToArray());
        Assert.Empty(decoded[1].Payload.ToArray());
    }

    [Fact]
    public void ReadPayloadsBorrowTheSuppliedMessageBuffer()
    {
        byte[] payload = [1, 2, 3];
        byte[] encoded = OcsFrameCodec.Write(
            [new(OcsFrameType.Json, string.Empty, payload)]);
        IReadOnlyList<OcsFrame> decoded = OcsFrameCodec.Read(encoded);
        int payloadOffset = 16;

        encoded[payloadOffset] = 42;

        Assert.Equal(new byte[] { 42, 2, 3 }, decoded[0].Payload.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void RejectsTruncatedFrameHeader(int length)
    {
        Assert.Throws<InvalidDataException>(() => OcsFrameCodec.Read(new byte[length]));
    }

    [Fact]
    public void RejectsTruncatedFramePayload()
    {
        byte[] message = [..Header((uint)OcsFrameType.Json, 0, 0, 1)];

        Assert.Throws<InvalidDataException>(() => OcsFrameCodec.Read(message));
    }

    [Fact]
    public void RejectsUnknownFrameTypeAndNonzeroReservedField()
    {
        byte[] unknownType = [..Header(4, 0, 0, 0), ..Terminator()];
        byte[] nonzeroReserved = [..Header((uint)OcsFrameType.Json, 0, 1, 0), ..Terminator()];

        Assert.Throws<InvalidDataException>(() => OcsFrameCodec.Read(unknownType));
        Assert.Throws<InvalidDataException>(() => OcsFrameCodec.Read(nonzeroReserved));
    }

    [Fact]
    public void RejectsMalformedUtf8InFrameName()
    {
        byte[] message =
        [
            ..Header((uint)OcsFrameType.Blob, 1, 0, 0),
            0xff,
            ..Terminator(),
        ];

        Assert.Throws<InvalidDataException>(() => OcsFrameCodec.Read(message));
        Assert.Throws<InvalidDataException>(() => OcsFrameCodec.Write(
            [new(OcsFrameType.Json, "\ud800", ReadOnlyMemory<byte>.Empty)]));
    }

    [Fact]
    public void RequiresOneFinalTerminator()
    {
        byte[] frame = Header((uint)OcsFrameType.Json, 0, 0, 0);
        byte[] trailingBytes = [..Terminator(), 0];
        byte[] twoTerminators = [..Terminator(), ..Terminator()];

        Assert.Throws<InvalidDataException>(() => OcsFrameCodec.Read(frame));
        Assert.Throws<InvalidDataException>(() => OcsFrameCodec.Read(trailingBytes));
        Assert.Throws<InvalidDataException>(() => OcsFrameCodec.Read(twoTerminators));
    }

    [Fact]
    public void RejectsNoncanonicalBlobReferencesAndFrameNames()
    {
        Assert.Throws<FormatException>(() => OcsFrameCodec.BlobReferenceToFrameName("AQID"));
        Assert.Throws<FormatException>(() => OcsFrameCodec.BlobReferenceToFrameName(
            " AAECAwQFBgcICQoLDA0ODw=="));
        Assert.Throws<FormatException>(() => OcsFrameCodec.FrameNameToBlobReference(
            "{\"BlobId\":\"-_v7-_v7-_v7-_v7-_v7-w==\"}"));
        Assert.Throws<FormatException>(() => OcsFrameCodec.FrameNameToBlobReference(
            "{\"BlobId\":\"AAECAwQFBgcICQoLDA0ODw==\"}x"));
    }

    [Fact]
    public void EnforcesMessageFrameNamePayloadAndFrameCountLimits()
    {
        var limits = new OcsFrameLimits(
            maxMessageBytes: 128,
            maxFrameCount: 2,
            maxNameBytes: 4,
            maxPayloadBytes: 8);
        byte[] oversizedName =
        [..Header((uint)OcsFrameType.Json, 5, 0, 0), ..Encoding.UTF8.GetBytes("12345"), ..Terminator()];
        byte[] oversizedPayload =
        [..Header((uint)OcsFrameType.Json, 0, 0, 9), ..new byte[9], ..Terminator()];
        byte[] tooManyFrames =
        [..Header((uint)OcsFrameType.Json, 0, 0, 0), ..Header((uint)OcsFrameType.Json, 0, 0, 0), ..Terminator()];

        Assert.Throws<InvalidDataException>(() => OcsFrameCodec.Read(new byte[129], limits));
        Assert.Throws<InvalidDataException>(() => OcsFrameCodec.Read(oversizedName, limits));
        Assert.Throws<InvalidDataException>(() => OcsFrameCodec.Read(oversizedPayload, limits));
        Assert.Throws<InvalidDataException>(() => OcsFrameCodec.Read(tooManyFrames, limits));
        Assert.Throws<InvalidDataException>(() => OcsFrameCodec.Write(
            [new(OcsFrameType.Json, "12345", ReadOnlyMemory<byte>.Empty)], limits));
        Assert.Throws<InvalidDataException>(() => OcsFrameCodec.Write(
            [new(OcsFrameType.Json, string.Empty, new byte[9])], limits));

        var bodyLimits = new OcsFrameLimits(
            maxMessageBytes: 32,
            maxFrameCount: 2,
            maxNameBytes: 4,
            maxPayloadBytes: 64);
        Assert.Throws<InvalidDataException>(() => OcsFrameCodec.Write(
            [new(OcsFrameType.Json, string.Empty, new byte[17])], bodyLimits));
    }

    private static byte[] Header(uint type, uint nameLength, uint reserved, uint payloadLength)
    {
        byte[] header = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(header, type);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), nameLength);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), reserved);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), payloadLength);
        return header;
    }

    private static byte[] Terminator() => Header((uint)OcsFrameType.Terminator, 0, 0, 0);
}
