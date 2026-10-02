using System.Text.Json;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Web;

namespace CellBridge.AspNetCore;

internal static class WireCaptureSummary
{
    // Written only when raw capture is enabled. A Word smoke test must see an
    // accepted file-partition PutChanges, not just an HTTP 200 or a local save.
    public static string Create(CellStorageRequest request, CellStorageResponse response) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            completedUtc = DateTime.UtcNow,
            files = response.Responses.Select((file, index) => new
            {
                url = file.Url,
                resourceId = file.ResourceId,
                errorCode = file.ErrorCode,
                subResponses = file.SubResponses.Select((sub, subIndex) => new
                {
                    type = sub.Type.ToString(),
                    errorCode = sub.ErrorCode,
                    partition = CellPartitionSelector.TryResolve(request.Requests[index].SubRequests[subIndex].SubRequestDataAttributes,
                        out var kind) ? kind.ToString() : "Unknown",
                    binary = Decode(sub.SubResponseDataBase64),
                }),
            }),
        }, new JsonSerializerOptions { WriteIndented = true });

    private static object? Decode(byte[]? payload)
    {
        if (payload is null) return null;
        try
        {
            var response = FsshttpbResponse.Deserialize(new BinaryReaderEx(payload));
            return new { failed = response.Status, operations = response.SubResponses.Select(sub => new
                { type = sub.RequestType.ToString(), failed = sub.Status }) };
        }
        catch (Exception error) when (error is InvalidDataException or FormatException or EndOfStreamException or ArgumentException)
        {
            return new { failed = true, decodeError = error.Message };
        }
    }
}
