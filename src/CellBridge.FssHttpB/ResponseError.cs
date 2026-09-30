namespace CellBridge.FssHttpB;

/// <summary>
/// A Response Error: an error code (Win32, HRESULT, or protocol) plus optional
/// supplemental error string. [MS-FSSHTTPB] section 2.2.2.2.5.
/// Wire layout: ResponseError header with a 16-byte error-type GUID, followed
/// by an error type child with a 32-bit error value, and optional supplemental
/// error string information.
/// </summary>
public sealed class ResponseError
{
    private static readonly Guid CellErrorGuid = new("5A66A756-87CE-4290-A38B-C61C5BA05A67");
    private static readonly Guid ProtocolErrorGuid = new("7AFEAEBF-033D-4828-9C31-3977AFE58249");
    private static readonly Guid Win32ErrorGuid = new("32C39011-6E39-46C4-AB78-DB41929D679E");
    private static readonly Guid HResultErrorGuid = new("8454C8F2-E401-405A-A198-A10B6991B56E");

    /// <summary>The error type.</summary>
    public ErrorType Type { get; set; }

    /// <summary>The error code value.</summary>
    public ulong ErrorCode { get; set; }

    /// <summary>Optional supplemental error string.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Creates a response error of the given type and code.</summary>
    public ResponseError(ErrorType type, ulong errorCode, string? errorMessage = null)
    {
        Type = type;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    /// <summary>Serializes the response error to the writer.</summary>
    public void Serialize(BinaryWriterEx writer)
    {
        // Serialize the payload first to learn its length.
        var payloadWriter = new BinaryWriterEx();

        (Guid errorTypeGuid, StreamObjectTypeHeaderStart typeHeader) = Type switch
        {
            ErrorType.Cell => (CellErrorGuid, StreamObjectTypeHeaderStart.CellError),
            ErrorType.Win32 => (Win32ErrorGuid, StreamObjectTypeHeaderStart.Win32Error),
            ErrorType.HResult => (HResultErrorGuid, StreamObjectTypeHeaderStart.HRESULTError),
            ErrorType.Protocol => (ProtocolErrorGuid, StreamObjectTypeHeaderStart.ProtocolError),
            _ => throw new InvalidOperationException($"Unknown error type {Type}."),
        };

        if (ErrorCode > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(ErrorCode), "Error codes must fit in a 32-bit wire value.");

        // ResponseError's declared length covers only its fixed GUID. The
        // typed error and optional supplemental info follow as child objects.
        var header = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.ResponseError, 16);
        header.Serialize(payloadWriter);
        payloadWriter.WriteBytes(errorTypeGuid.ToByteArray());

        var errorHeader = new StreamObjectHeaderStart32Bit(typeHeader, 4);
        errorHeader.Serialize(payloadWriter);
        payloadWriter.WriteUInt32((uint)ErrorCode);

        if (ErrorMessage is not null)
        {
            var stringWriter = new BinaryWriterEx();
            new StringItem(ErrorMessage).Serialize(stringWriter);
            byte[] stringBytes = stringWriter.ToArray();

            var stringHeader = new StreamObjectHeaderStart32Bit(
                StreamObjectTypeHeaderStart.ErrorStringSupplementalInfo, stringBytes.Length);
            stringHeader.Serialize(payloadWriter);
            payloadWriter.WriteBytes(stringBytes);
        }

        writer.WriteBytes(payloadWriter.ToArray());

        var end = new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.Error);
        end.Serialize(writer);
    }

    /// <summary>Deserializes a response error from the reader.</summary>
    public static ResponseError Deserialize(BinaryReaderEx reader)
    {
        var start = StreamObjectHeaderStart.Parse(reader);
        if (start.Type != StreamObjectTypeHeaderStart.ResponseError)
        {
            throw new InvalidDataException($"Expected ResponseError header, got {start.Type}.");
        }

        if (start.Length != 16)
            throw new InvalidDataException($"ResponseError must have a 16-byte fixed payload, got {start.Length}.");

        Guid errorTypeGuid = new Guid(reader.ReadBytes(16));
        ErrorType type = errorTypeGuid switch
        {
            var guid when guid == CellErrorGuid => ErrorType.Cell,
            var guid when guid == Win32ErrorGuid => ErrorType.Win32,
            var guid when guid == HResultErrorGuid => ErrorType.HResult,
            var guid when guid == ProtocolErrorGuid => ErrorType.Protocol,
            _ => throw new InvalidDataException($"Unexpected ResponseError type GUID {errorTypeGuid}.")
        };

        var typeHeader = StreamObjectHeaderStart.Parse(reader);
        var expectedType = type switch
        {
            ErrorType.Cell => StreamObjectTypeHeaderStart.CellError,
            ErrorType.Win32 => StreamObjectTypeHeaderStart.Win32Error,
            ErrorType.HResult => StreamObjectTypeHeaderStart.HRESULTError,
            ErrorType.Protocol => StreamObjectTypeHeaderStart.ProtocolError,
            _ => throw new InvalidDataException($"Unexpected response error type {type}.")
        };
        if (typeHeader.Type != expectedType || typeHeader.Length != 4)
            throw new InvalidDataException($"Invalid {expectedType} error child header.");

        ulong errorCode = reader.ReadUInt32();

        string? errorMessage = null;
        if (reader.Remaining > 0 && !IsHeaderEnd(reader))
        {
            var stringHeader = StreamObjectHeaderStart.Parse(reader);
            if (stringHeader.Type == StreamObjectTypeHeaderStart.ErrorStringSupplementalInfo)
            {
                int stringStart = reader.Position;
                errorMessage = StringItem.Deserialize(reader).Value;
                if (reader.Position - stringStart != stringHeader.Length)
                    throw new InvalidDataException("ErrorStringSupplementalInfo length does not match its StringItem.");
            }
        }

        var end = StreamObjectHeaderEnd.Parse(reader);
        if (end.Type != StreamObjectTypeHeaderEnd.Error)
        {
            throw new InvalidDataException($"Expected Error end header, got {end.Type}.");
        }

        return new ResponseError(type, errorCode, errorMessage);
    }

    private static bool IsHeaderEnd(BinaryReaderEx reader)
    {
        int position = reader.Position;
        int discriminator = reader.ReadByte() & 0x03;
        reader.Position = position;
        return discriminator is 1 or 3;
    }
}

/// <summary>
/// The response error type. [MS-FSSHTTPB] section 2.2.2.2.5.
/// </summary>
public enum ErrorType
{
    /// <summary>An MS-FSSHTTPB cell storage error.</summary>
    Cell,
    /// <summary>A Win32 error code.</summary>
    Win32,

    /// <summary>An HRESULT error code.</summary>
    HResult,

    /// <summary>A protocol error code.</summary>
    Protocol,
}
