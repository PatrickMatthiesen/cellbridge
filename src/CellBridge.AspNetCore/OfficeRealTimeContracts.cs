using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace CellBridge.AspNetCore;

internal sealed class OfficeRealTimeBusyException()
    : HubException("Office collaboration is busy. Retry the request.");

internal sealed record OfficeRealTimeTicket(Guid ResourceId, long LifecycleGeneration,
    CellBridgeActor Actor, Guid ClientId, DateTimeOffset ExpiresUtc);
internal sealed record OfficeRealTimeTicketCheck(OfficeRealTimeTicket Ticket,
    DocumentAccess RequiredAccess, bool RequireOwnedClient);

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
