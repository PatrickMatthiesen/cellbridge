namespace OfficeCollabServer.FssHttpB;

/// <summary>
/// A Serial Number: a GUID plus a 64-bit sequence value, or a null encoding.
/// [MS-FSSHTTPB] section 2.2.1.6.
/// Encoded as: 0x00 (null) or 0x80 followed by a 16-byte GUID and a 64-bit value.
/// </summary>
public sealed class SerialNumber
{
    /// <summary>Creates a serial number with the given GUID and value.</summary>
    public SerialNumber(Guid guid, ulong value)
    {
        Guid = guid;
        Value = value;
    }

    /// <summary>The GUID identifying the serial number sequence.</summary>
    public Guid Guid { get; set; }

    /// <summary>The sequence value.</summary>
    public ulong Value { get; set; }

    /// <summary>Creates a null serial number (Guid.Empty).</summary>
    public static SerialNumber Null => new(System.Guid.Empty, 0);

    /// <summary>Whether this serial number represents the null encoding.</summary>
    public bool IsNull => Guid == System.Guid.Empty;

    /// <summary>Serializes the serial number to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        if (IsNull)
        {
            writer.WriteByte(0x00);
            return;
        }

        writer.WriteByte(0x80);
        ExGuid.WriteGuid(writer, Guid);
        writer.WriteUInt64(Value);
    }

    /// <summary>Deserializes a serial number from the reader.</summary>
    public static SerialNumber Deserialize(BinaryReaderEx reader)
    {
        byte type = reader.ReadByte();
        if (type == 0x00)
        {
            return Null;
        }

        if (type != 0x80)
        {
            throw new InvalidDataException($"Invalid serial number type byte 0x{type:X2}.");
        }

        var guid = ExGuid.ReadGuid(reader);
        ulong value = reader.ReadUInt64();
        return new SerialNumber(guid, value);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is SerialNumber other && other.Guid == Guid && other.Value == Value;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Guid, Value);

    /// <inheritdoc />
    public override string ToString() => IsNull ? "null" : $"{Value}:{Guid}";
}
