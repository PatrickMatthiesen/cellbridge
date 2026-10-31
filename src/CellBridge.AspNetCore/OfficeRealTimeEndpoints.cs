using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CellBridge.AspNetCore;

internal static class OfficeRealTimeEndpoints
{
    internal static void AddOfficeRealTime(this IServiceCollection services)
    {
        services.AddDataProtection();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<OfficeRealTimeAccess>();
        services.AddSingleton<IOfficeRealTimePropertiesProvider>(provider => provider.GetRequiredService<OfficeRealTimeAccess>());
        services.AddSingleton<IOfficeRealTimeAuthorizer>(provider => provider.GetRequiredService<OfficeRealTimeAccess>());
        services.AddSingleton<OfficeRealTimeSessionStore>();
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, OfficeRealTimeAuthenticationHandler>(
            OfficeRealTimeRequest.AuthenticationScheme, _ => { });
        services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 64 * 1024;
            options.MaximumParallelInvocationsPerClient = 1;
            options.EnableDetailedErrors = false;
        });
    }

    internal static void MapOfficeRealTime(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.GetRequiredService<CellBridgeOptions>().EnableOfficeRealTime) return;
        if (!app.ServiceProvider.GetRequiredService<CellBridgeDocumentService>().SupportsOfficeRealTimeSnapshots)
            throw new NotSupportedException("Office RTC requires stored grants or an immutable versioned authorization policy without a legacy access evaluator.");
        var authorization = new AuthorizeAttribute { AuthenticationSchemes = OfficeRealTimeRequest.AuthenticationScheme };
        var routes = app.MapGroup("/_cellbridge/rtc").RequireAuthorization(authorization);
        routes.MapGet("/FindSession", FindSession);
        // The observed discovery response names signalr; current desktop Word
        // negotiates rtchub. Both routes use the same authenticated hub.
        routes.MapHub<OfficeRealTimeHub>("/signalr");
        routes.MapHub<OfficeRealTimeHub>("/rtchub");
    }

    private static async Task<IResult> FindSession(HttpContext context, IOfficeRealTimeAuthorizer authorizer)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        if (context.Items[OfficeRealTimeRequest.TicketKey] is not OfficeRealTimeTicket ticket ||
            OfficeRealTimeAccess.PublicBase(context) is not { } publicBase) return Results.NotFound();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        if (!await authorizer.AuthorizeAsync(ticket, DocumentAccess.Read, true, deadline.Token))
            return Results.NotFound();
        var query = QueryString.Create(context.Request.Query.Where(pair =>
            !pair.Key.Equals("clientProtocol", StringComparison.OrdinalIgnoreCase))).Value?.TrimStart('?') ?? "";
        return Results.Json(new
        {
            connectionString = "environment=CellBridge",
            enabled = true,
            errorMessage = "",
            protocolVersion = "1.0",
            querySignature = context.Request.Query["qs"].ToString(),
            queryString = query,
            replayToken = "",
            throttling_globalMaxNumberOfOutstandingMessages = 64,
            throttling_maxNumberOfOutstandingMessages = 16,
            throttling_messageDelay = 0,
            throttling_timeToWaitForResponse = 10000,
            url = publicBase + "/_cellbridge/rtc/signalr",
        });
    }
}
