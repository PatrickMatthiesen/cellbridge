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
            var candidates = await service.Provider.Documents.ListAsync(physicalOffset, 256, cancellationToken);
            if (candidates.Count == 0) break;
            foreach (var candidate in candidates)
            {
                if (!candidate.Path.StartsWith("/shared/", StringComparison.OrdinalIgnoreCase)) continue;
                DocumentHead state;
                try { state = await service.GetDocumentMetadataAsync(candidate.ResourceId, actor, cancellationToken); }
                catch (UnauthorizedAccessException) { continue; }
                catch (KeyNotFoundException) { continue; }
                if (!state.Path.StartsWith("/shared/", StringComparison.OrdinalIgnoreCase)) continue;
                if (skipped++ < offset) continue;
                visible.Add(new DocumentSummary(state.ResourceId, state.Path, state.Package.Length, state.ContentVersion, state.ModifiedUtc, state.ActiveEditors) { Security = state.Security, PendingDeliveryCount = state.Counters.PendingDeliveries, DeliveryBlock = state.Delivery?.BlockedReason });
                if (visible.Count > limit) break;
            }
            physicalOffset += candidates.Count;
            if (candidates.Count < 256) break;
        }
        bool more = visible.Count > limit;
        return new(visible.Take(limit).ToArray(), more ? checked(offset + limit) : null);
    }
}
