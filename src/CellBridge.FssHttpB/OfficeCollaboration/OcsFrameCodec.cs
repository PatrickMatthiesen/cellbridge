using System.Buffers.Binary;
using System.Text;

namespace CellBridge.FssHttpB.OfficeCollaboration;

internal enum OcsFrameType : uint
{
    Terminator = 1,
    Blob = 2,
    Json = 3,
}

/// <summary>A borrowed payload view from one decoded OCS message.</summary>
/// <remarks>
/// The payload references the input buffer passed to <see cref="OcsFrameCodec.Read"/>.
/// Keep that buffer alive and unchanged for as long as this frame is used.
/// </remarks>
internal readonly record struct OcsFrame(
    OcsFrameType Type,
    string Name,
    ReadOnlyMemory<byte> Payload);

internal sealed record OcsFrameLimits
{
    public static OcsFrameLimits Default { get; } = new(
        maxMessageBytes: 128 * 1024 * 1024,
        maxFrameCount: 4096,
        maxNameBytes: 256,
        maxPayloadBytes: 128 * 1024 * 1024);

    public OcsFrameLimits(
        int maxMessageBytes,
        int maxFrameCount,
        int maxNameBytes,
        int maxPayloadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrameCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxNameBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadBytes);

        MaxMessageBytes = maxMessageBytes;
        MaxFrameCount = maxFrameCount;
        MaxNameBytes = maxNameBytes;
        MaxPayloadBytes = maxPayloadBytes;
    }

    public int MaxMessageBytes { get; }
    public int MaxFrameCount { get; }
    public int MaxNameBytes { get; }
    public int MaxPayloadBytes { get; }
}

/// <summary>Reads and writes the observed 16-byte OCS frame envelope.</summary>
/// <remarks>
/// JSON and blob payloads remain opaque. This codec does not implement WPM
/// revisions, OCS merging, or publication to a document host.
/// </remarks>
internal static class OcsFrameCodec
{
    private const int HeaderLength = 16;
    private const uint TerminatorType = (uint)OcsFrameType.Terminator;
    private const uint BlobType = (uint)OcsFrameType.Blob;
    private const uint JsonType = (uint)OcsFrameType.Json;

    // Type-2 frame names wrap an unpadded standard-Base64 blob ID in this JSON shape.
    private const string BlobNamePrefix = "{\"BlobId\":\"";
    private const string BlobNameSuffix = "==\"}";
    private const int BlobIdByteLength = 16;
    private const int BlobIdUnpaddedBase64Length = 22;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static IReadOnlyList<OcsFrame> Read(
        ReadOnlyMemory<byte> message,
        OcsFrameLimits? limits = null)
    {
        limits ??= OcsFrameLimits.Default;
        if (message.Length > limits.MaxMessageBytes)
        {
            throw new InvalidDataException("OCS message exceeds the configured byte limit.");
        }

        var frames = new List<OcsFrame>();
        ReadOnlySpan<byte> bytes = message.Span;
        int offset = 0;
        int wireFrameCount = 0;
        long totalBytes = 0;
        bool terminated = false;

        while (offset < bytes.Length)
        {
            if (++wireFrameCount > limits.MaxFrameCount)
            {
                throw new InvalidDataException("OCS message exceeds the configured frame limit.");
            }

            if (bytes.Length - offset < HeaderLength)
            {
                throw new InvalidDataException("OCS message contains a truncated frame header.");
            }

            uint type = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            uint nameLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 4, 4));
            uint reserved = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8, 4));
            uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 12, 4));
            offset += HeaderLength;

            if (reserved != 0)
            {
                throw new InvalidDataException("OCS frame has a nonzero reserved field.");
            }

            if (type == TerminatorType)
            {
                if (nameLength != 0 || payloadLength != 0)
                {
                    throw new InvalidDataException("OCS terminator must not contain a name or payload.");
                }

                if (offset != bytes.Length)
                {
                    throw new InvalidDataException("OCS message contains bytes after its terminator.");
                }

                terminated = true;
                break;
            }

            if (type is not (BlobType or JsonType))
            {
                throw new InvalidDataException($"OCS frame has unsupported type {type}.");
            }

            if (nameLength > limits.MaxNameBytes)
            {
                throw new InvalidDataException("OCS frame name exceeds the configured byte limit.");
            }

            if (payloadLength > limits.MaxPayloadBytes)
            {
                throw new InvalidDataException("OCS frame payload exceeds the configured byte limit.");
            }

            long frameEnd = (long)offset + nameLength + payloadLength;
            if (frameEnd > bytes.Length)
            {
                throw new InvalidDataException("OCS message contains a truncated frame body.");
            }

            string name;
            try
            {
                name = StrictUtf8.GetString(bytes.Slice(offset, (int)nameLength));
            }
            catch (DecoderFallbackException error)
            {
                throw new InvalidDataException("OCS frame name is not valid UTF-8.", error);
            }

            offset += (int)nameLength;
            int payloadOffset = offset;
            offset += (int)payloadLength;
            totalBytes += HeaderLength + nameLength + payloadLength;
            if (totalBytes > limits.MaxMessageBytes)
            {
                throw new InvalidDataException("OCS message exceeds the configured byte limit.");
            }

            frames.Add(new OcsFrame((OcsFrameType)type, name,
                message.Slice(payloadOffset, (int)payloadLength)));
        }

        if (!terminated)
        {
            throw new InvalidDataException("OCS message is missing its terminator.");
        }

        return frames;
    }

    /// <summary>Writes frames and appends the required type-1 terminator.</summary>
    /// <remarks>
    /// Frames are validated as they are written. A later invalid frame or stream
    /// failure can leave earlier frames in the destination.
    /// </remarks>
    public static void Write(
        Stream destination,
        IEnumerable<OcsFrame> frames,
        OcsFrameLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(frames);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("Destination stream must be writable.", nameof(destination));
        }

        limits ??= OcsFrameLimits.Default;
        long totalBytes = 0;
        int wireFrameCount = 0;
        Span<byte> header = stackalloc byte[HeaderLength];

        foreach (OcsFrame frame in frames)
        {
            if (++wireFrameCount >= limits.MaxFrameCount)
            {
                throw new InvalidDataException("OCS message exceeds the configured frame limit.");
            }

            if (frame.Type is not (OcsFrameType.Blob or OcsFrameType.Json))
            {
                throw new InvalidDataException("Only blob and JSON frames can be written as message content.");
            }

            if (frame.Name is null)
            {
                throw new InvalidDataException("OCS frame name cannot be null.");
            }

            int nameByteCount;
            try
            {
                nameByteCount = StrictUtf8.GetByteCount(frame.Name);
            }
            catch (EncoderFallbackException error)
            {
                throw new InvalidDataException("OCS frame name is not valid UTF-8.", error);
            }

            if (nameByteCount > limits.MaxNameBytes)
            {
                throw new InvalidDataException("OCS frame name exceeds the configured byte limit.");
            }

            if (frame.Payload.Length > limits.MaxPayloadBytes)
            {
                throw new InvalidDataException("OCS frame payload exceeds the configured byte limit.");
            }

            long nextTotalBytes = totalBytes + HeaderLength + (long)nameByteCount + frame.Payload.Length;
            if (nextTotalBytes + HeaderLength > limits.MaxMessageBytes)
            {
                throw new InvalidDataException("OCS message exceeds the configured byte limit.");
            }

            byte[] nameBytes = StrictUtf8.GetBytes(frame.Name);
            totalBytes = nextTotalBytes;

            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)frame.Type);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)nameBytes.Length);
            BinaryPrimitives.WriteUInt32BigEndian(header[8..], 0);
            BinaryPrimitives.WriteUInt32BigEndian(header[12..], (uint)frame.Payload.Length);
            destination.Write(header);
            destination.Write(nameBytes);
            destination.Write(frame.Payload.Span);
        }

        if (++wireFrameCount > limits.MaxFrameCount)
        {
            throw new InvalidDataException("OCS message exceeds the configured frame limit.");
        }

        totalBytes += HeaderLength;
        if (totalBytes > limits.MaxMessageBytes)
        {
            throw new InvalidDataException("OCS message exceeds the configured byte limit.");
        }

        BinaryPrimitives.WriteUInt32BigEndian(header, TerminatorType);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(header[12..], 0);
        destination.Write(header);
    }

    public static byte[] Write(IEnumerable<OcsFrame> frames, OcsFrameLimits? limits = null)
    {
        using var destination = new MemoryStream();
        Write(destination, frames, limits);
        return destination.ToArray();
    }

    /// <summary>Builds the observed type-2 name for one 16-byte blob ID.</summary>
    public static string BlobReferenceToFrameName(string blobReference)
    {
        ArgumentNullException.ThrowIfNull(blobReference);
        byte[] blobId;
        try
        {
            blobId = Convert.FromBase64String(blobReference);
        }
        catch (FormatException error)
        {
            throw new FormatException("Blob reference must be canonical Base64 for a 16-byte ID.", error);
        }

        if (blobId.Length != BlobIdByteLength || Convert.ToBase64String(blobId) != blobReference)
        {
            throw new FormatException("Blob reference must be canonical Base64 for a 16-byte ID.");
        }

        string unpaddedBase64Id = Convert.ToBase64String(blobId).TrimEnd('=');
        return string.Concat(BlobNamePrefix, unpaddedBase64Id, BlobNameSuffix);
    }

    /// <summary>Returns the standard Base64 blob reference from an observed type-2 name.</summary>
    public static string FrameNameToBlobReference(string frameName)
    {
        ArgumentNullException.ThrowIfNull(frameName);
        if (!frameName.StartsWith(BlobNamePrefix, StringComparison.Ordinal) ||
            !frameName.EndsWith(BlobNameSuffix, StringComparison.Ordinal) ||
            frameName.Length != BlobNamePrefix.Length + BlobIdUnpaddedBase64Length + BlobNameSuffix.Length)
        {
            throw new FormatException("Blob frame name does not match the observed BlobId wrapper.");
        }

        string unpaddedBase64Id = frameName.Substring(BlobNamePrefix.Length, BlobIdUnpaddedBase64Length);
        foreach (char character in unpaddedBase64Id)
        {
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/'))
            {
                throw new FormatException("Blob frame name contains an invalid Base64 blob ID.");
            }
        }

        string standardBase64 = unpaddedBase64Id + "==";
        byte[] blobId;
        try
        {
            blobId = Convert.FromBase64String(standardBase64);
        }
        catch (FormatException error)
        {
            throw new FormatException("Blob frame name does not contain a valid Base64 blob ID.", error);
        }

        if (blobId.Length != BlobIdByteLength || BlobReferenceToFrameName(Convert.ToBase64String(blobId)) != frameName)
        {
            throw new FormatException("Blob frame name does not contain a canonical 16-byte blob ID.");
        }

        return Convert.ToBase64String(blobId);
    }
}
