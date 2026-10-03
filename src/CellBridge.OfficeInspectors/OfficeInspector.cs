using CellBridge.OfficeInspectors.Parsers;

namespace CellBridge.OfficeInspectors;

public sealed record ResponseInspection(
    FsshttpbResponse Response,
    long Consumed,
    int ByteLength,
    string? Error)
{
    public bool Parsed => Error is null && Consumed == ByteLength;

    public string Summary =>
        $"version={Response.ProtocolVersion}/{Response.MinimumVersion} " +
        $"status={Response.Status} bytes={ByteLength} consumed={Consumed} " +
        $"data-elements={string.Join(',', (Response.DataElementPackage?.DataElements ?? []).Select(x => x.GetType().Name))} " +
        $"subresponses={Response.SubResponses?.Length ?? 0}";
}

/// <summary>Inspects binary Cell responses independently of CellBridge's serializer.</summary>
public static class OfficeInspector
{
    public static ResponseInspection ParseResponse(byte[] bytes, bool isOneStore = false)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var stream = new MemoryStream(bytes, writable: false);
        InspectionContext.For(stream).IsOneStore = isOneStore;
        var response = new FsshttpbResponse();
        string? error = null;
        try
        {
            response.Parse(stream);
            if (response.Signature != 0x9B069439F329CF9D)
                throw new InvalidDataException("Invalid FSSHTTPB response signature.");
            if (response.ResponseStart.Type != StreamObjectTypeHeaderStart.FsshttpbResponse ||
                response.ResponseEnd.Type != StreamObjectTypeHeaderEnd.Response)
                throw new InvalidDataException("Invalid response start or end header.");
            if (stream.Position != stream.Length)
                throw new InvalidDataException($"Unconsumed response bytes at offset {stream.Position}.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The inherited grammar also throws plain Exception for unsupported
            // structures. Preserve the partial model and offset for diagnostics.
            error = ex.ToString();
        }
        return new ResponseInspection(response, stream.Position, bytes.Length, error);
    }
}
