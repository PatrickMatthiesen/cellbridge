using System.Security.Cryptography;

namespace OfficeCollabServer.FssHttpB;

/// <summary>A contiguous file chunk and its FSSHTTPD leaf identity.</summary>
public sealed record FsshttpdChunk(
    ulong Offset,
    byte[] Content,
    ExGuid ExtendedGuid,
    byte[] Signature)
{
    /// <summary>Number of file bytes represented by this chunk.</summary>
    public ulong Length => (ulong)Content.Length;
}

/// <summary>
/// The deterministic simple FSSHTTPD chunker.  The editors-table stream is
/// not a ZIP document, so the ZIP-aware chunker used for document content
/// cannot be used for it.  MS-FSSHTTPD permits chunks up to 1 MiB; using that
/// limit also matches the simple chunking behavior in the Microsoft test
/// suite and keeps node sizes bounded.
/// </summary>
public static class FsshttpdChunker
{
    public const int DefaultChunkSize = 1024 * 1024;

    /// <summary>Splits a file into contiguous, SHA-1 signed leaf chunks.</summary>
    public static IReadOnlyList<FsshttpdChunk> Chunk(
        ReadOnlySpan<byte> content,
        Func<int, ExGuid>? guidFactory = null,
        int maxChunkSize = DefaultChunkSize)
    {
        if (maxChunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChunkSize));
        }

        var chunks = new List<FsshttpdChunk>();
        if (content.Length == 0)
        {
            chunks.Add(CreateChunk(0, ReadOnlySpan<byte>.Empty, guidFactory));
            return chunks;
        }

        for (int offset = 0; offset < content.Length; offset += maxChunkSize)
        {
            int length = Math.Min(maxChunkSize, content.Length - offset);
            chunks.Add(CreateChunk((ulong)offset, content.Slice(offset, length), guidFactory));
        }

        return chunks;
    }

    private static FsshttpdChunk CreateChunk(ulong offset, ReadOnlySpan<byte> content, Func<int, ExGuid>? guidFactory)
    {
        byte[] bytes = content.ToArray();
        byte[] signature = SHA1.HashData(bytes);
        ExGuid guid = guidFactory?.Invoke(checked((int)offset)) ?? new ExGuid(
            checked((uint)offset), Guid.NewGuid());
        return new FsshttpdChunk(offset, bytes, guid, signature);
    }
}

/// <summary>Serializes the MS-FSSHTTPD node stream carried by object data.</summary>
public static class FsshttpdNodeSerializer
{
    /// <summary>
    /// Serializes the compact node form emitted by SharePoint's 13/11
    /// implementation.  This form uses 16-bit child headers, a 12-byte
    /// signature BinaryItem, and an eight-byte represented-size value.
    /// </summary>
    public static byte[] SerializeLegacyLeaf(FsshttpdChunk chunk) =>
        SerializeLegacyNode(StreamObjectTypeHeaderStart.LeafNodeObject,
            StreamObjectTypeHeaderEnd.IntermediateNodeEnd,
            LegacySignature(chunk.Content), chunk.Length);

    /// <summary>Serializes a legacy root/intermediate node.</summary>
    public static byte[] SerializeLegacyIntermediate(
        ulong representedSize,
        ReadOnlySpan<byte> content) =>
        SerializeLegacyNode(StreamObjectTypeHeaderStart.IntermediateNodeObject,
            StreamObjectTypeHeaderEnd.RootNodeEnd,
            LegacySignature(content), representedSize);

    /// <summary>Creates a leaf node with SHA-1 signature and represented size.</summary>
    public static byte[] SerializeLeaf(FsshttpdChunk chunk) =>
        SerializeNode(StreamObjectTypeHeaderStart.LeafNodeObject,
            StreamObjectTypeHeaderEnd.IntermediateNodeEnd,
            chunk.Signature, chunk.Length);

    /// <summary>Creates a leaf node from explicit content and signature.</summary>
    public static byte[] SerializeLeaf(ReadOnlySpan<byte> content, ReadOnlySpan<byte> signature) =>
        SerializeNode(StreamObjectTypeHeaderStart.LeafNodeObject,
            StreamObjectTypeHeaderEnd.IntermediateNodeEnd,
            signature.ToArray(), (ulong)content.Length);

    /// <summary>Creates an intermediate/root node for the represented stream.</summary>
    public static byte[] SerializeIntermediate(ulong representedSize, ReadOnlySpan<byte> signature) =>
        SerializeNode(StreamObjectTypeHeaderStart.IntermediateNodeObject,
            StreamObjectTypeHeaderEnd.RootNodeEnd,
            signature.ToArray(), representedSize);

    private static byte[] SerializeNode(
        StreamObjectTypeHeaderStart startType,
        StreamObjectTypeHeaderEnd endType,
        byte[] signature,
        ulong representedSize)
    {
        var body = new BinaryWriterEx();

        var signaturePayload = new BinaryWriterEx();
        new BinaryItem(signature).Serialize(signaturePayload);
        new StreamObjectHeaderStart32Bit(
            StreamObjectTypeHeaderStart.SignatureObject,
            signaturePayload.Length).Serialize(body);
        body.WriteBytes(signaturePayload.ToArray());

        var sizePayload = new BinaryWriterEx();
        sizePayload.WriteUInt64(representedSize);
        new StreamObjectHeaderStart32Bit(
            StreamObjectTypeHeaderStart.DataSizeObject,
            sizePayload.Length).Serialize(body);
        body.WriteBytes(sizePayload.ToArray());

        var output = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(startType, body.Length).Serialize(output);
        output.WriteBytes(body.ToArray());
        new StreamObjectHeaderEnd16Bit(endType).Serialize(output);
        return output.ToArray();
    }

    private static byte[] SerializeLegacyNode(
        StreamObjectTypeHeaderStart startType,
        StreamObjectTypeHeaderEnd endType,
        byte[] signature,
        ulong representedSize)
    {
        var output = new BinaryWriterEx();
        new StreamObjectHeaderStart16Bit(startType, 0).Serialize(output);

        var signaturePayload = new BinaryWriterEx();
        new BinaryItem(signature).Serialize(signaturePayload);
        new StreamObjectHeaderStart16Bit(
            StreamObjectTypeHeaderStart.SignatureObject,
            signaturePayload.Length).Serialize(output);
        output.WriteBytes(signaturePayload.ToArray());

        var sizePayload = new BinaryWriterEx();
        sizePayload.WriteUInt64(representedSize);
        new StreamObjectHeaderStart16Bit(
            StreamObjectTypeHeaderStart.DataSizeObject,
            sizePayload.Length).Serialize(output);
        output.WriteBytes(sizePayload.ToArray());
        new StreamObjectHeaderEnd8Bit(endType).Serialize(output);
        return output.ToArray();
    }

    private static byte[] LegacySignature(ReadOnlySpan<byte> content)
    {
        // SharePoint's legacy node uses a 12-byte opaque signature.  SHA-1
        // truncated to the wire width provides deterministic identities for
        // generated content while preserving the exact node shape.
        return SHA1.HashData(content)[..12];
    }
}
