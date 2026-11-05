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
    internal static void AddOfficeRealTime(this IServiceCollection services, bool multipleInstances)
    {
        services.AddDataProtection();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<OfficeRealTimeAccess>();
        services.AddSingleton<IOfficeRealTimePropertiesProvider>(provider => provider.GetRequiredService<OfficeRealTimeAccess>());
        services.AddSingleton<IOfficeRealTimeAuthorizer>(provider => provider.GetRequiredService<OfficeRealTimeAccess>());
        if (multipleInstances)
        {
            services.AddSingleton<IOfficeRealTimeBackplane, RedisOfficeRealTimeBackplane>();
            services.AddSingleton<DistributedOfficeRealTimeSessionStore>();
            services.AddSingleton<IOfficeRealTimeSessionStore>(provider => provider.GetRequiredService<DistributedOfficeRealTimeSessionStore>());
            services.AddHostedService(provider => provider.GetRequiredService<DistributedOfficeRealTimeSessionStore>());
        }
        else
        {
            services.AddSingleton<OfficeRealTimeSessionStore>();
            services.AddSingleton<IOfficeRealTimeSessionStore>(provider => provider.GetRequiredService<OfficeRealTimeSessionStore>());
        }
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, OfficeRealTimeAuthenticationHandler>(
            OfficeRealTimeRequest.AuthenticationScheme, _ => { });
        services.AddSingleton<OfficeClassicSignalR>();
        services.PostConfigure<AuthenticationOptions>(options =>
        {
            // RTC has an explicitly selected scheme. Do not let that internal
            // scheme prevent ASP.NET Core's sole host scheme from being the default.
            if (options.DefaultScheme is not null ||
                (AppContext.TryGetSwitch("Microsoft.AspNetCore.Authentication.SuppressAutoDefaultScheme", out var suppressed) && suppressed))
                return;
            var hostSchemes = options.Schemes.Where(scheme => scheme.Name != OfficeRealTimeRequest.AuthenticationScheme).Take(2).ToArray();
            if (hostSchemes.Length == 1) options.DefaultScheme = hostSchemes[0].Name;
        });
        services.AddSignalR().AddHubOptions<OfficeRealTimeHub>(options =>
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
            throw new NotSupportedException("Office RTC requires stored grants or an immutable versioned authorization policy without a legacy access evaluator. Set EnableOfficeRealTime to false to use other authorization providers.");
        var authorization = new AuthorizeAttribute { AuthenticationSchemes = OfficeRealTimeRequest.AuthenticationScheme };
        var routes = app.MapGroup("/_cellbridge/rtc").RequireAuthorization(authorization);
        routes.MapGet("/FindSession", FindSession);
        var classic = app.ServiceProvider.GetRequiredService<OfficeClassicSignalR>();
        RequestDelegate connect = WebSocketEndpoint(app, classic.ConnectAsync);
        RequestDelegate reconnect = WebSocketEndpoint(app, classic.ReconnectAsync);
        routes.MapGet("/signalr/negotiate", (Delegate)classic.NegotiateAsync);
        routes.MapGet("/signalr/connect", connect);
        routes.MapGet("/signalr/reconnect", reconnect);
        routes.MapGet("/signalr/start", classic.Start);
        routes.MapGet("/signalr/abort", (Delegate)classic.AbortAsync);
        routes.MapMethods("/signalr/abort", ["POST"], (Delegate)classic.AbortAsync);
        routes.MapMethods("/signalr/disconnect", ["POST"], (Delegate)classic.AbortAsync);
        // Classic Office appends the SignalR subpath to the advertised hub URL.
        routes.MapGet("/signalr/signalr/negotiate", (Delegate)classic.NegotiateAsync);
        routes.MapGet("/signalr/signalr/connect", connect);
        routes.MapGet("/signalr/signalr/reconnect", reconnect);
        routes.MapGet("/signalr/signalr/start", classic.Start);
        routes.MapGet("/signalr/signalr/abort", (Delegate)classic.AbortAsync);
        routes.MapMethods("/signalr/signalr/abort", ["POST"], (Delegate)classic.AbortAsync);
        routes.MapMethods("/signalr/signalr/disconnect", ["POST"], (Delegate)classic.AbortAsync);
        // The observed discovery response names signalr; current desktop Word
        // negotiates rtchub. Both routes use the same authenticated hub.
        routes.MapHub<OfficeRealTimeHub>("/signalr");
        routes.MapHub<OfficeRealTimeHub>("/rtchub");
    }

    private static RequestDelegate WebSocketEndpoint(IEndpointRouteBuilder routes,
        Func<HttpContext, Task<IResult>> handler)
    {
        var branch = routes.CreateApplicationBuilder();
        branch.UseWebSockets();
        branch.Run(async context => await (await handler(context).ConfigureAwait(false))
            .ExecuteAsync(context).ConfigureAwait(false));
        return branch.Build();
    }

    private static async Task<IResult> FindSession(HttpContext context, IOfficeRealTimeAuthorizer authorizer)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        if (context.Items[OfficeRealTimeRequest.TicketKey] is not OfficeRealTimeTicket ticket ||
            OfficeRealTimeAccess.PublicBase(context) is not { } publicBase) return Results.NotFound();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (!await authorizer.AuthorizeAsync(ticket, DocumentAccess.Read, true, deadline.Token))
                return Results.NotFound();
        }
        catch (OfficeRealTimeBusyException)
        {
            context.Response.Headers.RetryAfter = "1";
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        {
            context.Response.Headers.RetryAfter = "1";
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        var query = QueryString.Create(context.Request.Query.Where(pair =>
            !pair.Key.Equals("clientProtocol", StringComparison.OrdinalIgnoreCase))).Value?.TrimStart('?') ?? "";
        return Results.Json(new
        {
            connectionString = "environment=CellBridge",
            enabled = true,
            errorMessage = "",
            // Preserve the observed netcore discovery flag. This host also exposes
            // the captured SignalR 1.4 routes for Office clients that choose them.
            netcore = true,
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
