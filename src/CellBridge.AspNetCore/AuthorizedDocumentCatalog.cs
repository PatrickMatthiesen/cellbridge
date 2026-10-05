using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public sealed record AuthorizedCatalogPage(IReadOnlyList<DocumentSummary> Documents, int? NextOffset);

public static class AuthorizedDocumentCatalog
{
    /// <summary>Offset counts visible rows, never physical rows or hidden documents.</summary>
    public static async Task<AuthorizedCatalogPage> ReadAsync(CellBridgeDocumentService service,
        CellBridgeActor actor, int offset, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var visible = new List<DocumentSummary>();
        int physicalOffset = 0, skipped = 0;
        while (visible.Count <= limit)
        {
            var candidates = await service.Provider.State.ListAsync(physicalOffset, 256, cancellationToken);
            if (candidates.Count == 0) break;
            foreach (var candidate in candidates)
            {
                var state = await service.Provider.State.FindByResourceIdAsync(candidate.ResourceId, cancellationToken);
                if (state is null || !state.Path.StartsWith("/shared/", StringComparison.OrdinalIgnoreCase) ||
                    !service.Access(actor, state).HasFlag(DocumentAccess.Read)) continue;
                if (skipped++ < offset) continue;
                visible.Add(new(state.ResourceId, state.Path, state.Content.Length, state.ContentVersion,
                    state.ModifiedUtc, state.Editors.Count(e => e.ExpiresUtc > DateTime.UtcNow)) { Security = state.Security });
                if (visible.Count > limit) break;
            }
            physicalOffset += candidates.Count;
            if (candidates.Count < 256) break;
        }
        bool more = visible.Count > limit;
        return new(visible.Take(limit).ToArray(), more ? checked(offset + limit) : null);
    }
}
