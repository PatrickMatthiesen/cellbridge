using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.AspNetCore;

namespace CellBridge.Mcp.AspNetCore;

public static class CellBridgeMcpExtensions
{
    public static IServiceCollection AddCellBridgeMcp(this IServiceCollection services,
        Action<CellBridgeMcpOptions>? configure = null)
    {
        var options = new CellBridgeMcpOptions();
        configure?.Invoke(options);
        options.Validate();

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddScoped<McpRequestIdentity>();
        services.AddSingleton<McpTransportSessions>();
        services.AddSingleton<McpWorkbookRegistry>();
        services.AddHostedService(sp => sp.GetRequiredService<McpWorkbookRegistry>());
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, McpBearerAuthentication>(
            McpBearerAuthentication.SchemeName, _ => { });
        services.AddAuthorization();

        services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<McpWorkbookTools>();
        services.AddOptions<HttpServerTransportOptions>().Configure<McpTransportSessions, TimeProvider>(
            (transport, sessions, timeProvider) =>
            {
                transport.Stateless = false;
                transport.PerSessionExecutionContext = false;
                transport.IdleTimeout = options.IdleSessionTimeout;
                transport.MaxIdleSessionCount = options.MaxTotalSessions;
                transport.TimeProvider = new McpTransportTimeProvider(timeProvider);
#pragma warning disable MCPEXP002 // The SDK session runner exposes the session lifetime, including idle cleanup.
                transport.RunSessionHandler = sessions.RunSession;
#pragma warning restore MCPEXP002
            });
        return services;
    }

    public static IEndpointConventionBuilder MapCellBridgeMcp(this IEndpointRouteBuilder endpoints,
        string pattern = "/mcp")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        pattern = pattern.TrimEnd('/');
        if (pattern.Length == 0 || pattern[0] != '/' ||
            pattern[1..].Split('/').Any(segment => segment.Length == 0 || segment is "." or ".." ||
                segment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or '~'))))
            throw new ArgumentException("The MCP endpoint requires a literal absolute path.", nameof(pattern));

        var options = endpoints.ServiceProvider.GetRequiredService<CellBridgeMcpOptions>();
        var resource = new McpProtectedResource(pattern);
        if (options.AuthorizationServer is { } authorizationServer)
        {
            endpoints.MapGet(resource.MetadataPath, (HttpContext context) =>
            {
                var origin = McpProtectedResource.GetOrigin(context);
                if (origin is null) return Results.BadRequest();
                return Results.Json(new
                {
                    resource = origin + context.Request.PathBase.ToUriComponent() + resource.EndpointPath,
                    authorization_servers = new[] { authorizationServer.OriginalString },
                    bearer_methods_supported = new[] { "header" },
                });
            }).AllowAnonymous();
        }

        var group = endpoints.MapGroup(string.Empty);
        group.WithMetadata(resource);
        group.RequireAuthorization(new AuthorizationPolicyBuilder(McpBearerAuthentication.SchemeName)
            .RequireAuthenticatedUser()
            .Build());
        group.AddEndpointFilter(McpTransportAuthorization.InvokeAsync);
        return group.MapMcp(pattern);
    }
}
