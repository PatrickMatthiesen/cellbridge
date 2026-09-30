using Microsoft.Protocols.TestSuites.SharedAdapter;

namespace CellBridge.Interop.Tests;

public sealed class QueryAccessTests
{
    [Fact]
    public void QueryAccessRequest_UsesS11ProtocolDefaults()
    {
        var request = InteropRequestFactory.QueryAccess(7);
        var bytes = request.SerializeToByteList();

        Assert.Equal((ushort)12, request.ProtocolVersion);
        Assert.Equal((ushort)11, request.MinimumVersion);
        Assert.Equal(0x9B069439F329CF9CUL, request.Signature);
        Assert.True(bytes.Count > 40);
    }

    [Fact]
    public void QueryAccessResponse_RoundTripsThroughMicrosoftParser()
    {
        var response = new CellBridge.FssHttpB.FsshttpbResponse
        {
            SubResponses = new()
            {
                new CellBridge.FssHttpB.FsshttpbSubResponse
                {
                    RequestId = 7,
                    RequestType = CellBridge.FssHttpB.RequestTypes.QueryAccess,
                    Data = new CellBridge.FssHttpB.QueryAccessSubResponseData(),
                },
            },
        };

        var parsed = FsshttpbResponse.DeserializeResponseFromByteArray(response.ToByteArray(), 0);
        Assert.False(parsed.Status);
        Assert.Single(parsed.CellSubResponses);
        Assert.Equal((ulong)RequestTypes.QueryAccess, parsed.CellSubResponses[0].RequestType.DecodedValue);
    }
}

internal static class InteropRequestFactory
{
    public static FsshttpbCellRequest QueryAccess(ulong id)
    {
        var request = new FsshttpbCellRequest
        {
            ProtocolVersion = 12,
            MinimumVersion = 11,
            Signature = 0x9B069439F329CF9C,
            Version = 0xFA12994,
            GUID = FsshttpbCellRequest.UserAgentGuid,
        };
        request.AddSubRequest(new QueryAccessCellSubRequest(id), null);
        return request;
    }

    public static FsshttpbCellRequest QueryChanges(ulong id)
    {
        var request = new FsshttpbCellRequest
        {
            ProtocolVersion = 12,
            MinimumVersion = 11,
            Signature = 0x9B069439F329CF9C,
            Version = 0xFA12994,
            GUID = FsshttpbCellRequest.UserAgentGuid,
        };
        var changes = new QueryChangesCellSubRequest(id)
        {
            IncludeStorageManifest = 1,
            IncludeCellChanges = 1,
        };
        request.AddSubRequest(changes, null);
        return request;
    }
}
