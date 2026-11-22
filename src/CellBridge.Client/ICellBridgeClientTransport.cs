using CellBridge.AspNetCore;
using CellBridge.FssHttp;

namespace CellBridge.Client;

public interface ICellBridgeClientTransport
{
    Task<CellStorageExecution> ExecuteAsync(CellStorageRequest request, CancellationToken cancellationToken = default);
    Task<ICellBridgeRealTimeChannel?> TryConnectRealTimeAsync(RealTimeConnectionTarget target,
        CancellationToken cancellationToken = default);
}

/// <summary>Connection details issued after the server recognizes the client's document session.</summary>
public sealed record RealTimeConnectionTarget(Uri Endpoint, string DocumentAccessToken, DateTimeOffset? ExpiresAt,
    string ClientId)
{
    // The ticket grants access to the document's collaboration channel.
    public override string ToString() => $"{Endpoint.GetLeftPart(UriPartial.Path)} ({ClientId})";
}

public interface ICellBridgeRealTimeChannel : IAsyncDisposable
{
    /// <summary>Reads server notifications. Consumers should use these messages as synchronization hints.</summary>
    IAsyncEnumerable<string> ReadNotificationsAsync(CancellationToken cancellationToken = default);

    Task UpdateClientInfoAsync(RealTimeConnectionTarget target, CancellationToken cancellationToken = default);
}

public interface ICellBridgeRealTimeConnector
{
    Task<ICellBridgeRealTimeChannel?> TryConnectRealTimeAsync(RealTimeConnectionTarget target,
        CancellationToken cancellationToken = default);
}
