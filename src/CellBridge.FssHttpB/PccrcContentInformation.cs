using System.Security.Cryptography;

namespace CellBridge.FssHttpB;

/// <summary>Full-content schema-1 encoding, MS-PCCRC 2.3, 2.3.1.1 and 2.3.1.2.</summary>
public static class PccrcContentInformation
{
    public const int BlockSize = 65536;
    public const int SegmentSize = 32 * 1024 * 1024;

    /// <summary>Hashes ordered content without allocating its concatenation. Empty content is not a content range.</summary>
    public static byte[] Create(IReadOnlyList<ReadOnlyMemory<byte>> orderedContent, ReadOnlySpan<byte> serverSecret)
    {
        if (serverSecret.Length != 32) throw new ArgumentException("Expected a SHA-256 server secret.", nameof(serverSecret));
        long length = 0;
        foreach (var item in orderedContent) length = checked(length + item.Length);
        if (length == 0) throw new ArgumentException("Content range must be nonempty.", nameof(orderedContent));
        var descriptions = new BinaryWriterEx();
        var blocks = new BinaryWriterEx();
        var block = new byte[BlockSize];
        int itemIndex = 0, itemOffset = 0;
        uint segments = 0;
        for (long segmentOffset = 0; segmentOffset < length; segmentOffset += SegmentSize)
        {
            int segmentLength = (int)Math.Min(SegmentSize, length - segmentOffset);
            var hashes = new BinaryWriterEx();
            for (int offset = 0; offset < segmentLength; offset += BlockSize)
            {
                int blockLength = Math.Min(BlockSize, segmentLength - offset), copied = 0;
                while (copied < blockLength)
                {
                    var source = orderedContent[itemIndex].Span[itemOffset..];
                    int count = Math.Min(source.Length, blockLength - copied);
                    source[..count].CopyTo(block.AsSpan(copied));
                    copied += count;
                    itemOffset += count;
                    if (itemOffset == orderedContent[itemIndex].Length) { itemIndex++; itemOffset = 0; }
                }
                hashes.WriteBytes(SHA256.HashData(block.AsSpan(0, blockLength)));
            }
            var blockHashes = hashes.ToArray();
            var hod = SHA256.HashData(blockHashes);
            var secretInput = new byte[64];
            hod.CopyTo(secretInput, 0);
            serverSecret.CopyTo(secretInput.AsSpan(32));
            descriptions.WriteUInt64((ulong)segmentOffset);
            descriptions.WriteUInt32((uint)segmentLength);
            descriptions.WriteUInt32(BlockSize);
            descriptions.WriteBytes(hod);
            descriptions.WriteBytes(SHA256.HashData(secretInput));
            blocks.WriteUInt32((uint)(blockHashes.Length / 32));
            blocks.WriteBytes(blockHashes);
            segments++;
        }
        var writer = new BinaryWriterEx();
        writer.WriteUInt16(0x0100);
        writer.WriteUInt32(0x800c); // SHA-256.
        writer.WriteUInt32(0); // Full range, starts at the first segment.
        writer.WriteUInt32(0); // Zero denotes the entire final segment (2.3, example 3.2).
        writer.WriteUInt32(segments);
        writer.WriteBytes(descriptions.ToArray());
        writer.WriteBytes(blocks.ToArray());
        return writer.ToArray();
    }
}
