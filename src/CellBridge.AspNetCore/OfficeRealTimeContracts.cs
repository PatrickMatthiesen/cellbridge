using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace CellBridge.AspNetCore;

internal sealed class OfficeRealTimeBusyException()
    : HubException("Office collaboration is busy. Retry the request.");

internal sealed record OfficeRealTimeTicket(Guid ResourceId, long LifecycleGeneration,
    CellBridgeActor Actor, Guid ClientId, DateTimeOffset ExpiresUtc);
internal sealed record OfficeRealTimeTicketCheck(OfficeRealTimeTicket Ticket,
    DocumentAccess RequiredAccess, bool RequireOwnedClient);

internal sealed class OfficeRealTimeConnection(string connectionId, CancellationToken connectionAborted,
    Func<string, object?[], CancellationToken, Task> send, Action abort)
{
    internal string ConnectionId { get; } = connectionId;
    internal CancellationToken ConnectionAborted { get; } = connectionAborted;
    internal Task SendAsync(string method, object?[] arguments, CancellationToken cancellationToken) =>
        send(method, arguments, cancellationToken);
    internal void Abort() => abort();

    internal static OfficeRealTimeConnection FromSignalR(HubCallerContext context,
        IHubContext<OfficeRealTimeHub> hub) => new(context.ConnectionId, context.ConnectionAborted,
            (method, arguments, cancellationToken) => hub.Clients.Client(context.ConnectionId)
                .SendCoreAsync(method, arguments, cancellationToken), context.Abort);
}

internal interface IOfficeRealTimeSessionStore
{
    Task<bool> ConnectAsync(OfficeRealTimeTicket ticket, OfficeRealTimeConnection connection, CancellationToken cancellationToken);
    Task DisconnectAsync(string connectionId, CancellationToken cancellationToken);
    Task<Dictionary<string, string>> GetCompletePresenceStateAsync(OfficeRealTimeTicket ticket, string connectionId,
        CancellationToken cancellationToken);
    Task<bool> UpdatePresenceAsync(OfficeRealTimeTicket ticket, string connectionId, string payload,
        CancellationToken cancellationToken);
    Task<bool> SendUpdateNotificationAsync(OfficeRealTimeTicket ticket, string connectionId, int channel,
        string payload, CancellationToken cancellationToken);
}

internal interface IOfficeRealTimeAuthorizer
{
    ValueTask<bool> AuthorizeAsync(OfficeRealTimeTicket ticket, DocumentAccess requiredAccess,
        bool requireOwnedClient, CancellationToken cancellationToken);
    ValueTask<bool[]> AuthorizeTogetherAsync(IReadOnlyList<OfficeRealTimeTicketCheck> checks,
        CancellationToken cancellationToken);
}

internal static class OfficeRealTimeRequest
{
    internal const string TicketKey = "CellBridge.OfficeRealTime.Ticket";
    internal const string AuthenticationScheme = "CellBridge.OfficeRealTime";
}
