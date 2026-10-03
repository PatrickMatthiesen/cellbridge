using CellBridge.OfficeInspectors;
using CellBridge.OfficeInspectors.Parsers;

namespace OfficeInspectors.Adapter;

public static class OfficeInspectorsAdapter
{
    internal static (FsshttpbResponse Response, long Consumed) ParseIsolated(byte[] bytes)
    {
        var result = OfficeInspector.ParseResponse(bytes);
        if (!result.Parsed)
            throw new InvalidDataException(result.Error ?? result.Summary);
        return (result.Response, result.Consumed);
    }

    public static ResponseInspection ParseResponse(byte[] bytes) => OfficeInspector.ParseResponse(bytes);
}
