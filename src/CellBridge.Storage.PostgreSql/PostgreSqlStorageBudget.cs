using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.PostgreSql;

internal static class PostgreSqlStorageBudget
{
    public static async Task AdjustAsync(CellBridgeDbContext context, long bytes, long documents,
        CancellationToken cancellationToken)
    {
        // The ledger row serializes quota admission across every host and content backend.
        var usage = DocumentBudget.FindTrackedUsage(context)
            ?? await context.Usage.FromSqlRaw("SELECT * FROM cellbridge_usage WHERE singleton=true FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            ?? throw new StorageCorruptionException("Missing storage usage ledger.");
        DocumentBudget.Adjust(usage, bytes, documents);
    }
}
