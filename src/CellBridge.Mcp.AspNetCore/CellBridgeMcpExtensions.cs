using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Routing.Template;
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
        services.TryAddSingleton(_ => new RootMetadataMapping());
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddScoped<McpRequestIdentity>();
        services.AddSingleton<McpTransportSessions>();
        services.AddSingleton<McpWorkbookRegistry>();
        services.AddHostedService(sp => sp.GetRequiredService<McpWorkbookRegistry>());
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, McpBearerAuthentication>(
            McpBearerAuthentication.SchemeName, _ => { });
        services.PostConfigure<AuthenticationOptions>(authentication =>
        {
            if (authentication.DefaultScheme is not null ||
                (AppContext.TryGetSwitch("Microsoft.AspNetCore.Authentication.SuppressAutoDefaultScheme", out var suppressed) && suppressed))
                return;

            // Neither explicitly selected CellBridge scheme should change the sole host scheme's default.
            var hostSchemes = authentication.Schemes.Where(scheme =>
                scheme.Name is not (McpBearerAuthentication.SchemeName or "CellBridge.OfficeRealTime")).Take(2).ToArray();
            if (hostSchemes.Length == 1) authentication.DefaultScheme = hostSchemes[0].Name;
        });
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
        if (options.AuthorizationServer is { } authorizationServer)
        {
            if (options.MapRootProtectedResourceMetadata)
                endpoints.ServiceProvider.GetRequiredService<RootMetadataMapping>().Claim(pattern);

            var rootPattern = RoutePatternFactory.Parse(McpProtectedResource.RootMetadataPath);
            var binderFactory = endpoints.ServiceProvider.GetRequiredService<TemplateBinderFactory>();
            var scopes = options.ScopesSupported;
            IResult GetMetadata(HttpContext context)
            {
                var origin = McpProtectedResource.GetOrigin(context);
                var endpointPath = McpProtectedResource.ResolveEndpointPath(context);
                if (origin is null || endpointPath is null) return Results.BadRequest();
                return Results.Json(new ProtectedResourceMetadata(
                    origin + context.Request.PathBase.ToUriComponent() + endpointPath,
                    [authorizationServer.OriginalString], ["header"], scopes.Count > 0 ? scopes : null));
            }

            endpoints.MapGet(pattern, GetMetadata).AllowAnonymous().Finally(builder =>
            {
                var endpoint = (RouteEndpointBuilder)builder;
                endpoint.Metadata.Add(new McpProtectedResource(endpoint.RoutePattern, binderFactory));
                // Discovery inserts the well-known path before the full resource path.
                endpoint.RoutePattern = RoutePatternFactory.Combine(rootPattern, endpoint.RoutePattern);
            });
            if (options.MapRootProtectedResourceMetadata)
            {
                endpoints.MapGet(pattern, GetMetadata).AllowAnonymous().Finally(builder =>
                {
                    var endpoint = (RouteEndpointBuilder)builder;
                    if (endpoint.RoutePattern.Parameters.Count > 0)
                        throw new InvalidOperationException("The root MCP protected resource metadata alias requires a literal route group prefix.");
                    var resource = new McpProtectedResource(endpoint.RoutePattern, binderFactory);
                    var literalPath = resource.BindPath(new RouteValueDictionary());
                    if (string.Equals(literalPath, McpProtectedResource.RootMetadataPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The MCP endpoint and its root protected resource metadata alias require different paths.");
                    endpoint.Metadata.Add(resource);
                    endpoint.RoutePattern = rootPattern;
                });
            }
        }

        var group = endpoints.MapGroup(string.Empty);
        group.RequireAuthorization(new AuthorizationPolicyBuilder(McpBearerAuthentication.SchemeName)
            .RequireAuthenticatedUser()
            .Build());
        group.AddEndpointFilter(McpTransportAuthorization.InvokeAsync);
        var transport = group.MapMcp(pattern);
        if (options.AuthorizationServer is not null)
        {
            var binderFactory = endpoints.ServiceProvider.GetRequiredService<TemplateBinderFactory>();
            transport.Finally(builder =>
            {
                var endpoint = (RouteEndpointBuilder)builder;
                endpoint.Metadata.Add(new McpProtectedResource(endpoint.RoutePattern, binderFactory));
            });
        }
        return transport;
    }

    private sealed record ProtectedResourceMetadata(
        [property: JsonPropertyName("resource")] string Resource,
        [property: JsonPropertyName("authorization_servers")] string[] AuthorizationServers,
        [property: JsonPropertyName("bearer_methods_supported")] string[] BearerMethodsSupported,
        [property: JsonPropertyName("scopes_supported"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? ScopesSupported);

    private sealed class RootMetadataMapping
    {
        private string? _owner;

        public void Claim(string pattern)
        {
            if (Interlocked.CompareExchange(ref _owner, pattern, null) is { } existing)
                throw new InvalidOperationException(
                    $"The root MCP protected resource metadata alias is already mapped by the MCP endpoint '{existing}'.");
        }
    }
}
