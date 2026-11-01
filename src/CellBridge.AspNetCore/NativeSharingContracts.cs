namespace CellBridge.AspNetCore;

/// <summary>A trusted host identity for an already stored document grant.</summary>
public sealed record CellBridgeSharingRecipient(string Subject, string Login, string DisplayName,
    string? EmailAddress, int? PrincipalId = null, bool? IsActive = null, bool? IsExternal = null);

/// <summary>
/// Resolves subjects already present in a document's stored grants. Implementations must use a trusted
/// host directory and must not infer identity from a value supplied by an Office client.
/// </summary>
public interface ICellBridgeSharingRecipientResolver
{
    ValueTask<CellBridgeSharingRecipient?> ResolveAsync(string subject, CancellationToken cancellationToken = default);
}
