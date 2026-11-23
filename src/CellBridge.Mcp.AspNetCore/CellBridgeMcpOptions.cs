namespace CellBridge.Mcp.AspNetCore;

public sealed class CellBridgeMcpOptions
{
    public string BearerScheme { get; set; } = "Bearer";
    public string? AuthorizationPolicy { get; set; }
    public TimeSpan IdleSessionTimeout { get; set; } = TimeSpan.FromMinutes(15);
    public int MaxConcurrentSessionsPerActor { get; set; } = 8;
    public int MaxTotalSessions { get; set; } = 256;
    public int MaxReadCells { get; set; } = 1000;
    public long MaxWorkbookBytes { get; set; } = 64 * 1024 * 1024;
    public Uri? AuthorizationServer { get; set; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(BearerScheme) || BearerScheme == McpBearerAuthentication.SchemeName ||
            AuthorizationPolicy is not null && string.IsNullOrWhiteSpace(AuthorizationPolicy))
            throw new ArgumentException("Configure a bearer authentication scheme and, optionally, a named authorization policy.");
        if (IdleSessionTimeout <= TimeSpan.Zero || IdleSessionTimeout.Ticks > long.MaxValue / McpTransportTimeProvider.Frequency ||
            MaxConcurrentSessionsPerActor <= 0 || MaxTotalSessions <= 0 || MaxReadCells <= 0 || MaxWorkbookBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(CellBridgeMcpOptions), "MCP limits must be positive and the idle timeout must fit the transport clock.");
        if (AuthorizationServer is { } issuer &&
            (!issuer.IsAbsoluteUri || issuer.Scheme != "https" || issuer.Host.Length == 0 || issuer.UserInfo.Length != 0 ||
                issuer.Query.Length != 0 || issuer.Fragment.Length != 0))
            throw new ArgumentException("The authorization server must be an HTTPS issuer URI.", nameof(AuthorizationServer));
    }
}
