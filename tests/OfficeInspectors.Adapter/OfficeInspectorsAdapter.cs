using OfficeCollabServer.FssHttpB;

namespace OfficeInspectors.Adapter;

public sealed record InspectorParseResult(
    bool Available,
    bool Parsed,
    string Summary,
    string? Error);

public static class OfficeInspectorsAdapter
{
#if OFFICE_INSPECTORS
    private static readonly object ParserGate = new();

    internal static (FSSHTTPandWOPIInspector.Parsers.FsshttpbResponse Response, long Consumed)
        ParseIsolated(byte[] bytes)
    {
        // The Fiddler parser keeps per-message state in static host fields.
        // Reset and serialize access so unrelated fixtures cannot affect one another.
        lock (ParserGate)
        {
            FSSHTTPandWOPIInspector.FSSHTTPandWOPIInspector.isNextEditorTable = false;
            FSSHTTPandWOPIInspector.FSSHTTPandWOPIInspector.IsOneStore = false;
            FSSHTTPandWOPIInspector.FSSHTTPandWOPIInspector.encryptedObjectGroupIDList.Clear();
            FSSHTTPandWOPIInspector.Parsers.BaseStructure.editTableQueue.Clear();
            var parsed = new FSSHTTPandWOPIInspector.Parsers.FsshttpbResponse();
            using var stream = new MemoryStream(bytes, writable: false);
            parsed.Parse(stream);
            return (parsed, stream.Position);
        }
    }

    public static InspectorParseResult ParseResponse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            var (parsed, consumed) = ParseIsolated(bytes);
            return new InspectorParseResult(
                Available: true,
                Parsed: consumed == bytes.Length,
                Summary: Describe(parsed, consumed, bytes.Length),
                Error: null);
        }
        catch (Exception ex)
        {
            return new InspectorParseResult(true, false, "", ex.ToString());
        }
    }

    private static string Describe(
        FSSHTTPandWOPIInspector.Parsers.FsshttpbResponse response,
        long consumed,
        int length)
    {
        var elements = response.DataElementPackage?.DataElements ?? Array.Empty<object>();
        var subresponses = response.SubResponses ?? Array.Empty<FSSHTTPandWOPIInspector.Parsers.FsshttpbSubResponse>();
        return $"version={response.ProtocolVersion}/{response.MinimumVersion} " +
            $"status={response.Status} bytes={length} consumed={consumed} " +
            $"data-elements={string.Join(',', elements.Select(x => x.GetType().Name))} " +
            $"subresponses={subresponses.Length}";
    }
#else
    public static InspectorParseResult ParseResponse(byte[] bytes) =>
        new(false, false, "", "Office Inspectors source was not found. Set OfficeInspectorsRoot to its checkout.");
#endif
}
