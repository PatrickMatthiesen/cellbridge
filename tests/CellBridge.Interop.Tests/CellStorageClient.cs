#nullable enable
using Microsoft.Protocols.TestSuites.SharedAdapter;
using CellBridge.TestFixtures;

namespace CellBridge.Interop.Tests;

/// <summary>Uses shared HTTP framing and the independent Microsoft binary codec.</summary>
public sealed class CellStorageClient(HttpClient http, Uri endpoint)
{
    public const string ProtocolNamespace = CellStorageTransport.ProtocolNamespace;
    public const string SoapAction = CellStorageTransport.SoapAction;
    private readonly CellStorageTransport transport = new(http, endpoint);

    public Task<CellStorageCallResult> SendCellAsync(string fileUrl, FsshttpbCellRequest cellRequest,
        Guid partitionId, CancellationToken cancellationToken = default) =>
        SendCellAsync(fileUrl, cellRequest, (Guid?)partitionId, false, cancellationToken);

    public async Task<CellStorageCallResult> SendCellAsync(string fileUrl, FsshttpbCellRequest cellRequest,
        Guid? partitionId, bool getFileProps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cellRequest);
        var response = await transport.SendCellAsync(fileUrl, cellRequest.SerializeToByteList().ToArray(),
            partitionId, getFileProps, FsshttpbCellRequest.UserAgentGuid, cancellationToken);
        return new(response.StatusCode, response.Soap, response.Binary);
    }
}

public sealed class CellStorageCallResult
{
    internal CellStorageCallResult(System.Net.HttpStatusCode statusCode, string soap, byte[]? binary)
    {
        StatusCode = statusCode;
        Soap = soap;
        Binary = binary;
    }

    public System.Net.HttpStatusCode StatusCode { get; }
    public string Soap { get; }
    public byte[]? Binary { get; }
    public FsshttpbResponse ParseBinaryResponse() =>
        Binary is null ? throw new InvalidDataException("Cell response did not contain an MTOM binary part.") :
        FsshttpbResponse.DeserializeResponseFromByteArray(Binary, 0);
}
