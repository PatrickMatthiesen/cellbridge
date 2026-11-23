using CellBridge.AspNetCore;
using CellBridge.FssHttp;

namespace CellBridge.Client;

/// <summary>Executes requests with the supplied actor and the document service's authorization checks.</summary>
public sealed class InProcessCellBridgeTransport(CellBridgeRequestProcessor processor, string publicOrigin,
    CellBridgeActor actor, string applicationPathBase = "", ICellBridgeRealTimeConnector? connector = null)
    : ICellBridgeClientTransport
{
    public Task<CellStorageExecution> ExecuteAsync(CellStorageRequest request, CancellationToken cancellationToken = default) =>
        processor.ExecuteAsync(request, publicOrigin, actor, cancellationToken, applicationPathBase);

    public Task<ICellBridgeRealTimeChannel?> TryConnectRealTimeAsync(RealTimeConnectionTarget target,
        CancellationToken cancellationToken = default) =>
        connector?.TryConnectRealTimeAsync(target, cancellationToken) ?? Task.FromResult<ICellBridgeRealTimeChannel?>(null);
}
