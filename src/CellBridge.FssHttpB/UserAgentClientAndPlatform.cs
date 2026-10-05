using System.Text;

namespace CellBridge.FssHttpB;

/// <summary>The UTF-8 user-agent identity in MS-FSSHTTPB section 2.2.2.</summary>
public sealed record UserAgentClientAndPlatform(string Client, string Platform)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public void Serialize(BinaryWriterEx writer)
    {
        var payload = new BinaryWriterEx();
        WriteString(payload, Client);
        WriteString(payload, Platform);
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.UserAgentClientandPlatform, payload.Length)
            .Serialize(writer);
        writer.WriteBytes(payload.ToArray());
    }

    internal static UserAgentClientAndPlatform Deserialize(BinaryReaderEx reader, StreamObjectHeaderStart header)
    {
        if (header.HeaderType != StreamObjectHeaderStart.HeaderType32Bit || header.Compound != 0)
            throw new InvalidDataException("User agent client and platform must be a 32-bit noncompound object.");
        var payload = new BinaryReaderEx(reader.ReadMemory(header.Length));
        var identity = new UserAgentClientAndPlatform(ReadString(payload), ReadString(payload));
        if (payload.Remaining != 0)
            throw new InvalidDataException("Unexpected bytes after user agent client and platform.");
        return identity;
    }

    private static string ReadString(BinaryReaderEx reader)
    {
        ulong count = Compact64bitInt.Deserialize(reader).Value;
        if (count > (ulong)reader.Remaining)
            throw new InvalidDataException("User agent string exceeds its object boundary.");
        try { return Utf8.GetString(reader.ReadMemory((int)count).Span); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid UTF-8 user agent string.", error); }
    }

    private static void WriteString(BinaryWriterEx writer, string value)
    {
        byte[] bytes = Utf8.GetBytes(value);
        new Compact64bitInt((ulong)bytes.Length).Serialize(writer);
        writer.WriteBytes(bytes);
    }
}
