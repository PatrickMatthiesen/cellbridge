using CellBridge.Lists;
using Microsoft.AspNetCore.Http;

namespace CellBridge.Lists.AspNetCore;

public sealed class CellBridgeListsOptions
{
    /// <summary>The host must map authenticated identities to stable issuer-qualified subjects.</summary>
    public Func<HttpContext, ListActor?>? ActorResolver { get; set; }
    /// <summary>An explicitly configured non-cookie integration scheme. Its handler must validate bearer credentials.</summary>
    public string? IntegrationAuthenticationScheme { get; set; }
    public int MaxRequestBodyBytes { get; set; } = 128 * 1024;
    /// <summary>Absolute adapter deadline, including authentication checks, body reads, engine calls, and response writes. Expiry aborts the request.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public ListLimits Limits { get; set; } = new();
    public bool EnableOData { get; set; } = true;
}

public sealed record ListQueryRequest(ListQuery Query, string? Cursor = null);
public sealed record ListQueryResponse(IReadOnlyList<ListRecord> Records, long SchemaVersion, string? NextCursor);
public sealed record ListGrantRequest(string Subject, ListAccess Access);
