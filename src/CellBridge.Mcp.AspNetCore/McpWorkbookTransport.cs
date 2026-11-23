using System.Net;
using CellBridge.AspNetCore;
using CellBridge.Client;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Mcp.AspNetCore;

// The registry serializes calls and clears the invocation before releasing the session gate.
// The client disables background protocol renewal and uses the transport only inside that gate.
internal sealed class McpWorkbookTransport(CellBridgeInProcessHost host, Guid resourceId, long generation, string subject,
    int leaseTimeoutSeconds, TimeProvider clock)
    : ICellBridgeClientTransport
{
    public McpWorkbookInvocation? Invocation { private get; set; }
    private Guid _clientId;
    private long? _lastLeaseRequest;

    // The server applies its default to shorter leases. Measure from the end of the last possible renewal,
    // including a failed response, so time spent executing the request does not shorten the bound.
    public bool LeaseExpired => _lastLeaseRequest is { } last &&
        clock.GetElapsedTime(last) >= TimeSpan.FromSeconds(Math.Max(leaseTimeoutSeconds, CoauthSession.DefaultTimeoutSeconds));

    public async Task<CellStorageExecution> ExecuteAsync(CellStorageRequest request, CancellationToken cancellationToken = default)
    {
        var invocation = Invocation ?? throw new CellBridgeProtocolException("FileUnauthorizedAccess");
        invocation.ThrowIfExpired();
        var file = request.Requests.Single();
        // Bind even initial discovery to the resource authorized by open_workbook.
        file.UseResourceId = true;
        file.ResourceId = resourceId.ToString("D");
        if (_clientId == Guid.Empty && file.SubRequests is [{ Type: SubRequestType.EditorsTable } editor] &&
            editor.SubRequestDataAttributes.GetValueOrDefault("EditorsTableRequestType") == "JoinEditingSession")
            _clientId = Guid.Parse(editor.SubRequestDataAttributes["ClientID"]);

        bool renewsLease = file.SubRequests is [var operation] && (
            operation.Type == SubRequestType.EditorsTable &&
            operation.SubRequestDataAttributes.GetValueOrDefault("EditorsTableRequestType") is "JoinEditingSession" or "RefreshEditingSession" ||
            operation.Type == SubRequestType.Coauth &&
            operation.SubRequestDataAttributes.GetValueOrDefault("CoauthRequestType") is "JoinCoauthoring" or "RefreshCoauthoring");
        try
        {
            return await host.ExecuteAsync(request, invocation.Actor, invocation.PublicOrigin, invocation.PathBase, cancellationToken);
        }
        finally
        {
            if (renewsLease)
                _lastLeaseRequest = clock.GetTimestamp();
        }
    }

    public Task<ICellBridgeRealTimeChannel?> TryConnectRealTimeAsync(RealTimeConnectionTarget target,
        CancellationToken cancellationToken = default)
    {
        var invocation = Invocation ?? throw new CellBridgeProtocolException("FileUnauthorizedAccess");
        invocation.ThrowIfExpired();
        return host.CreateRealTimeConnector(invocation.Actor).TryConnectRealTimeAsync(target, cancellationToken);
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        if (_clientId == Guid.Empty)
            return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            try
            {
                await host.ReleaseParticipationAsync(resourceId, generation, subject, _clientId, timeout.Token);
                return;
            }
            catch (HttpRequestException error) when (!timeout.IsCancellationRequested &&
                error.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
            }
        }
    }
}

internal sealed record McpWorkbookInvocation(
    CellBridgeActor Actor, string SessionId, DateTimeOffset? ExpiresAt, string PublicOrigin, string PathBase,
    TimeProvider Clock)
{
    public bool IsExpired => ExpiresAt is { } expiry && expiry <= Clock.GetUtcNow();

    public McpWorkbookInvocation Bind(McpWorkbookEntry entry)
    {
        var actor = Actor with
        {
            AccessLimit = new DocumentAccessLimit(entry.Key.ResourceId, DocumentAccess.Read | DocumentAccess.Write)
            {
                LifecycleGeneration = entry.LifecycleGeneration,
            },
        };
        return this with { Actor = actor };
    }

    public McpWorkbookInvocation WithWorkbookExpiry(DateTimeOffset? entryExpiry) =>
        entryExpiry is { } expiry && (ExpiresAt is null || expiry < ExpiresAt)
            ? this with { ExpiresAt = expiry }
            : this;

    public void ThrowIfExpired()
    {
        // Match the client's cleanup-denied protocol code when expiry interrupts disposal.
        if (IsExpired)
            throw new CellBridgeProtocolException("FileUnauthorizedAccess");
    }

    public CancellationTokenSource? CreateDeadline()
    {
        if (ExpiresAt is { } expiry)
        {
            var remaining = expiry - Clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                throw McpWorkbookRegistry.Unavailable();
            if (remaining.TotalMilliseconds <= uint.MaxValue - 1)
                return new CancellationTokenSource(remaining, Clock);
        }
        return null;
    }
}
