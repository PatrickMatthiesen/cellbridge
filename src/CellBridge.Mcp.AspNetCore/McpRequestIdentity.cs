using Microsoft.AspNetCore.Http;

namespace CellBridge.Mcp.AspNetCore;

internal sealed class McpRequestIdentity(IHttpContextAccessor accessor)
{
    public HttpContext Context => accessor.HttpContext
        ?? throw new InvalidOperationException("MCP request identity requires an active HTTP request.");

    private McpRequestAuthentication Authentication => Context.Features.Get<McpRequestAuthentication>()
        ?? throw new InvalidOperationException("MCP request identity requires an authenticated MCP endpoint.");

    public string SessionId => Authentication.SessionId
        ?? throw new InvalidOperationException("MCP workbook tools require an initialized session.");

    public string TransportSubject => Authentication.Subject;
    public DateTimeOffset? ExpiresAt => Authentication.ExpiresAt;
}

internal sealed record McpRequestAuthentication(string Subject, DateTimeOffset? ExpiresAt, string? SessionId);
