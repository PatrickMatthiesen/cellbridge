namespace OfficeCollabServer.FssHttpB;

/// <summary>
/// A Data Element Package: a reserved byte followed by data elements.
/// [MS-FSSHTTPB] section 2.2.2.1.5.
/// </summary>
public sealed class DataElementPackage
{
    /// <summary>Creates an empty data element package.</summary>
    public DataElementPackage()
    {
        DataElements = new List<DataElement>();
    }

    /// <summary>The data elements in the package.</summary>
    public List<DataElement> DataElements { get; set; }

    /// <summary>Serializes the package to the writer.</summary>
    public void Serialize(BinaryWriterEx writer) => Serialize(writer, FsshttpbSerializationProfile.Current);

    /// <summary>Serializes the package using the selected wire profile.</summary>
    public void Serialize(BinaryWriterEx writer, FsshttpbSerializationProfile profile)
    {
        // Length covers this compound object's immediate preamble only. Its
        // nested DataElements follow the reserved byte and precede the end
        // header; they are not part of the declared length.
        if (profile.UsesSharePointLegacyDataElementFraming())
            new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.DataElementPackage, 1).Serialize(writer);
        else
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.DataElementPackage, 1).Serialize(writer);
        writer.WriteByte(0); // Reserved
        foreach (var dataElement in DataElements)
        {
            dataElement.Serialize(writer, profile);
        }

        if (profile.UsesSharePointLegacyDataElementFraming())
            new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.DataElementPackage).Serialize(writer);
        else
            new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.DataElementPackage).Serialize(writer);
    }

    /// <summary>Deserializes a data element package from the reader.</summary>
    public static DataElementPackage Deserialize(BinaryReaderEx reader)
    {
        var start = StreamObjectHeaderStart.Parse(reader);
        if (start.Type != StreamObjectTypeHeaderStart.DataElementPackage)
        {
            throw new InvalidDataException($"Expected DataElementPackage header, got {start.Type}.");
        }

        var package = new DataElementPackage();
        if (start.Length < 1)
        {
            throw new InvalidDataException("DataElementPackage is missing its reserved byte.");
        }

        reader.ReadByte(); // Reserved
        reader.Skip(start.Length - 1);

        while (!IsHeaderEnd(reader))
        {
            int pos = reader.Position;
            var header = StreamObjectHeaderStart.Parse(reader);
            reader.Position = pos;

            if (header.Type == StreamObjectTypeHeaderStart.DataElement)
            {
                package.DataElements.Add(DataElement.Deserialize(reader));
            }
            else
            {
                throw new InvalidDataException($"Unexpected {header.Type} in DataElementPackage.");
            }
        }

        var end = StreamObjectHeaderEnd.Parse(reader);
        if (end.Type != StreamObjectTypeHeaderEnd.DataElementPackage)
        {
            throw new InvalidDataException($"Expected DataElementPackage end header, got {end.Type}.");
        }

        return package;
    }

    private static bool IsHeaderEnd(BinaryReaderEx reader)
    {
        int position = reader.Position;
        int discriminator = reader.ReadByte() & 0x03;
        reader.Position = position;
        return discriminator is 0x1 or 0x3;
    }
}

/// <summary>
/// A Data Element: an extended GUID, serial number, type, and type-specific data.
/// [MS-FSSHTTPB] section 2.2.2.1.5.1.
/// Wire layout: DataElement header, DataElementExtendedGUID, SerialNumber,
/// DataElementType compact uint, DataElementData, DataElement end header.
/// </summary>
public sealed class DataElement
{
    /// <summary>Creates a data element of the given type.</summary>
    public DataElement(DataElementType type, ExGuid dataElementGuid, SerialNumber serialNumber)
    {
        DataElementType = type;
        DataElementExtendedGuid = dataElementGuid;
        SerialNumber = serialNumber;
    }

    /// <summary>The data element extended GUID.</summary>
    public ExGuid DataElementExtendedGuid { get; set; }

    /// <summary>The data element serial number.</summary>
    public SerialNumber SerialNumber { get; set; }

    /// <summary>The data element type.</summary>
    public DataElementType DataElementType { get; set; }

    /// <summary>The raw type-specific data (parsed lazily by consumers).</summary>
    public byte[]? Data { get; set; }

    /// <summary>Serializes the data element to the writer.</summary>
    public void Serialize(BinaryWriterEx writer) => Serialize(writer, FsshttpbSerializationProfile.Current);

    /// <summary>Serializes the package using the selected wire profile.</summary>
    public void Serialize(BinaryWriterEx writer, FsshttpbSerializationProfile profile)
    {
        var metadataWriter = new BinaryWriterEx();
        DataElementExtendedGuid.Serialize(metadataWriter);
        SerialNumber.Serialize(metadataWriter);
        new Compact64bitInt((ulong)DataElementType).Serialize(metadataWriter);
        byte[] metadata = metadataWriter.ToArray();

        // The DataElement start length covers only the immediate metadata
        // fields. DataElementData follows that region and the compound end
        // header terminates the complete element. This is the framing used by
        // the Microsoft Interop-TestSuites parser.
        if (profile.UsesSharePointLegacyDataElementFraming() && metadata.Length <= StreamObjectHeaderStart.Max16BitLength)
            new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.DataElement, metadata.Length).Serialize(writer);
        else
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.DataElement, metadata.Length).Serialize(writer);
        writer.WriteBytes(metadata);
        if (Data is not null)
        {
            writer.WriteBytes(Data);
        }

        if (profile.UsesSharePointLegacyDataElementFraming())
            new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.DataElement).Serialize(writer);
        else
            new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.DataElement).Serialize(writer);
    }

    /// <summary>Deserializes a data element from the reader (data kept raw).</summary>
    public static DataElement Deserialize(BinaryReaderEx reader)
    {
        var start = StreamObjectHeaderStart.Parse(reader);
        if (start.Type != StreamObjectTypeHeaderStart.DataElement)
        {
            throw new InvalidDataException($"Expected DataElement header, got {start.Type}.");
        }

        int metadataEnd = checked(reader.Position + start.Length);

        var element = new DataElement(
            DataElementType.None,
            ExGuid.Deserialize(reader),
            SerialNumber.Deserialize(reader))
        {
            DataElementType = (DataElementType)Compact64bitInt.Deserialize(reader).Value,
        };

        if (reader.Position != metadataEnd)
        {
            throw new InvalidDataException(
                $"DataElement header length {start.Length} does not match its metadata fields.");
        }

        int dataStart = reader.Position;
        SkipDataElementPayload(reader);
        int dataEnd = reader.Position;
        if (dataEnd > dataStart)
        {
            reader.Position = dataStart;
            element.Data = reader.ReadBytes(dataEnd - dataStart);
            reader.Position = dataEnd;
        }

        var end = StreamObjectHeaderEnd.Parse(reader);
        if (end.Type != StreamObjectTypeHeaderEnd.DataElement)
        {
            throw new InvalidDataException($"Expected DataElement end header, got {end.Type}.");
        }

        return element;
    }

    /// <summary>
    /// Advances over the stream objects that make up DataElementData. Each
    /// data element type defines its own object graph, but all of the graphs
    /// use the same stream-object framing. Walking the declared immediate
    /// lengths and compound end headers preserves the raw payload while
    /// handling both the older embedded-child and current sibling-child forms.
    /// </summary>
    private static void SkipDataElementPayload(BinaryReaderEx reader)
    {
        while (reader.Remaining > 0 && !IsHeaderEnd(reader))
        {
            SkipStreamObject(reader);
        }
    }

    private static bool IsHeaderEnd(BinaryReaderEx reader)
    {
        int position = reader.Position;
        bool result = (reader.ReadByte() & 0x03) is 0x1 or 0x3;
        reader.Position = position;
        return result;
    }

    private static void SkipStreamObject(BinaryReaderEx reader)
    {
        var header = StreamObjectHeaderStart.Parse(reader);
        int immediateEnd = checked(reader.Position + header.Length);
        reader.Position = immediateEnd;

        if (header.Compound != 1)
        {
            return;
        }

        // A compound object's children may be inside its declared immediate
        // region (legacy captures) or after a zero-length immediate region
        // (the Interop-TestSuites representation). In either case, a header
        // end marks the boundary.
        while (reader.Remaining > 0 && !IsHeaderEnd(reader))
        {
            SkipStreamObject(reader);
        }

        var end = StreamObjectHeaderEnd.Parse(reader);
        if ((int)end.Type != (int)header.Type)
        {
            throw new InvalidDataException(
                $"Expected end header for {header.Type}, got {end.Type}.");
        }
    }
}
