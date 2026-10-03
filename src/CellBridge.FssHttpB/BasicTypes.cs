namespace CellBridge.FssHttpB;

/// <summary>
/// A Binary Item: a compact length followed by raw bytes.
/// [MS-FSSHTTPB] section 2.2.1.3.
/// </summary>
public sealed class BinaryItem
{
    /// <summary>Creates a binary item with the given content.</summary>
    public BinaryItem(byte[] content)
    {
        Content = content ?? Array.Empty<byte>();
    }

    /// <summary>Creates an empty binary item.</summary>
    public BinaryItem()
        : this(Array.Empty<byte>())
    {
    }

    /// <summary>The item content. <see cref="Length"/> is derived from this.</summary>
    public byte[] Content { get; set; }

    /// <summary>The byte length of the content.</summary>
    public ulong Length => (ulong)Content.Length;

    /// <summary>Serializes the binary item to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        new Compact64bitInt(Length).Serialize(writer);
        writer.WriteBytes(Content);
    }

    /// <summary>Deserializes a binary item from the reader.</summary>
    public static BinaryItem Deserialize(BinaryReaderEx reader)
    {
        ulong length = Compact64bitInt.Deserialize(reader).Value;
        if (length > int.MaxValue)
        {
            throw new InvalidDataException($"Binary item length {length} exceeds int.MaxValue.");
        }

        return new BinaryItem(reader.ReadBytes((int)length));
    }

    /// <inheritdoc />
    public override string ToString() => $"{Content.Length} bytes";
}

/// <summary>
/// A String Item: a compact UTF-16 character count followed by little-endian UTF-16 bytes.
/// [MS-FSSHTTPB] section 2.2.1.4.
/// </summary>
public sealed class StringItem
{
    /// <summary>Creates a string item with the given value.</summary>
    public StringItem(string value)
    {
        Value = value ?? string.Empty;
    }

    /// <summary>Creates an empty string item.</summary>
    public StringItem()
        : this(string.Empty)
    {
    }

    /// <summary>The string value.</summary>
    public string Value { get; set; }

    /// <summary>Serializes the string item to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        byte[] bytes = System.Text.Encoding.Unicode.GetBytes(Value);
        new Compact64bitInt((ulong)Value.Length).Serialize(writer);
        writer.WriteBytes(bytes);
    }

    /// <summary>Deserializes a string item from the reader.</summary>
    public static StringItem Deserialize(BinaryReaderEx reader)
    {
        ulong length = Compact64bitInt.Deserialize(reader).Value;
        if (length > int.MaxValue / 2)
        {
            throw new InvalidDataException($"String item length {length} exceeds int.MaxValue.");
        }

        byte[] bytes = reader.ReadBytes(checked((int)length * 2));
        return new StringItem(System.Text.Encoding.Unicode.GetString(bytes));
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// A Cell ID: a pair of extended GUIDs.
/// [MS-FSSHTTPB] section 2.2.1.10.
/// </summary>
public sealed class CellId
{
    /// <summary>Creates a cell ID from two extended GUIDs.</summary>
    public CellId(ExGuid longId, ExGuid shortId)
    {
        LongId = longId;
        ShortId = shortId;
    }

    /// <summary>The first (long) extended GUID.</summary>
    public ExGuid LongId { get; set; }

    /// <summary>The second (short) extended GUID.</summary>
    public ExGuid ShortId { get; set; }

    /// <summary>Serializes the cell ID to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        LongId.Serialize(writer);
        ShortId.Serialize(writer);
    }

    /// <summary>Deserializes a cell ID from the reader.</summary>
    public static CellId Deserialize(BinaryReaderEx reader) =>
        new(ExGuid.Deserialize(reader), ExGuid.Deserialize(reader));

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is CellId other && other.LongId.Equals(LongId) && other.ShortId.Equals(ShortId);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(LongId, ShortId);

    /// <inheritdoc />
    public override string ToString() => $"{LongId}/{ShortId}";
}

/// <summary>
/// A File Chunk: a compact length followed by raw chunk bytes.
/// [MS-FSSHTTPB] section 2.2.1.9.
/// </summary>
public sealed class FileChunk
{
    /// <summary>Creates a file chunk with the given content.</summary>
    public FileChunk(byte[] content)
    {
        Content = content ?? Array.Empty<byte>();
    }

    /// <summary>The chunk content.</summary>
    public byte[] Content { get; set; }

    /// <summary>The byte length of the chunk.</summary>
    public ulong Length => (ulong)Content.Length;

    /// <summary>Serializes the file chunk to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        new Compact64bitInt(Length).Serialize(writer);
        writer.WriteBytes(Content);
    }

    /// <summary>Deserializes a file chunk from the reader.</summary>
    public static FileChunk Deserialize(BinaryReaderEx reader)
    {
        ulong length = Compact64bitInt.Deserialize(reader).Value;
        if (length > int.MaxValue)
        {
            throw new InvalidDataException($"File chunk length {length} exceeds int.MaxValue.");
        }

        return new FileChunk(reader.ReadBytes((int)length));
    }
}
