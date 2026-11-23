using CellBridge.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.Mcp.AspNetCore;

internal static class McpTransportAuthorization
{
    public static async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation,
        EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        var request = context.Request;
        var expectedOrigin = McpProtectedResource.GetOrigin(context);
        if (!McpProtectedResource.IsAllowedOrigin(context, expectedOrigin))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (expectedOrigin is null) return Results.BadRequest();

        var result = await context.AuthenticateAsync(McpBearerAuthentication.SchemeName);
        if (!result.Succeeded || result.Principal is not { } principal)
            return Results.Challenge(authenticationSchemes: [McpBearerAuthentication.SchemeName]);

        // Use the selected bearer principal even when the application has other authentication schemes.
        context.User = principal;
        if (CellBridgeActor.FromPrincipal(principal) is not { } caller)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var options = context.RequestServices.GetRequiredService<CellBridgeMcpOptions>();
        if (options.AuthorizationPolicy is { } policyName)
        {
            var policy = await context.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>()
                .GetPolicyAsync(policyName)
                ?? throw new InvalidOperationException($"The MCP authorization policy '{policyName}' is not registered.");
            if (policy.AuthenticationSchemes.Any(scheme => scheme != options.BearerScheme))
                throw new InvalidOperationException("The MCP authorization policy must use the selected bearer scheme.");
            var authorization = await context.RequestServices.GetRequiredService<IAuthorizationService>()
                .AuthorizeAsync(principal, context, policy.Requirements);
            if (!authorization.Succeeded) return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var sessions = context.RequestServices.GetRequiredService<McpTransportSessions>();
        McpTransportSessions.PendingSession? reservation = null;
        string? sessionId = null;
        if (request.Headers.TryGetValue(McpTransportSessions.Header, out var ids))
        {
            if (ids.Count != 1 || string.IsNullOrEmpty(ids[0]))
                return Results.BadRequest();

            sessionId = ids[0];
            if (!sessions.IsOwnedBy(sessionId!, caller.Identity.Subject))
                return Results.NotFound();
        }
        else
        {
            if (!HttpMethods.IsPost(request.Method)) return Results.BadRequest();
            reservation = sessions.Reserve(caller.Identity.Subject);
            if (reservation is null) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            context.Features.Set(reservation);
        }

        using (reservation)
        {
            var expiration = McpBearerAuthentication.GetExpiration(result);
            context.Features.Set(new McpRequestAuthentication(caller.Identity.Subject, expiration, sessionId));
            var previousCancellation = context.RequestAborted;
            var timeProvider = context.RequestServices.GetRequiredService<TimeProvider>();
            var remaining = expiration - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                return Results.Challenge(authenticationSchemes: [McpBearerAuthentication.SchemeName]);
            // Timer delays use a 32-bit millisecond interval. Longer-lived credentials
            // are checked at admission on each request.
            if (remaining is null || remaining.Value.TotalMilliseconds > uint.MaxValue - 1)
                return await next(invocation);

            using var expirationTimer = new CancellationTokenSource(remaining.Value, timeProvider);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(previousCancellation, expirationTimer.Token);
            context.RequestAborted = cancellation.Token;
            try
            {
                return await next(invocation);
            }
            catch (OperationCanceledException) when (expirationTimer.IsCancellationRequested && !previousCancellation.IsCancellationRequested)
            {
                if (!context.Response.HasStarted)
                    return Results.Challenge(authenticationSchemes: [McpBearerAuthentication.SchemeName]);
                return Results.Empty;
            }
            finally
            {
                context.RequestAborted = previousCancellation;
            }
        }
    }
}
