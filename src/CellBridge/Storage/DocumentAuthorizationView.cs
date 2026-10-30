namespace CellBridge.Storage.Abstractions;

/// <summary>
/// Current identity and authority facts available to a host policy. Authorization
/// never requires payloads, protocol catalogs, receipts or revision history.
/// </summary>
public sealed record DocumentAuthorizationView(Guid ResourceId, string Path, string PathKey,
    long LifecycleGeneration, long StateVersion, bool IsDeleted, DocumentSecurity Security,
    uint ContentVersion, long ContentLength);
