using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.PostgreSql;

internal static class PostgreSqlStorageBudget
{
    public static async Task AdjustAsync(CellBridgeDbContext context, long bytes, long documents,
        CancellationToken cancellationToken)
    {
        // The ledger row serializes quota admission across every host and content backend.
        var usage = context.Usage.Local.SingleOrDefault(row => row.Singleton)
            ?? await context.Usage.FromSqlRaw("SELECT * FROM cellbridge_usage WHERE singleton=true FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            ?? throw new StorageCorruptionException("Missing storage usage ledger.");
        var storedBytes = checked(usage.StoredBytes + bytes);
        var documentCount = checked(usage.DocumentCount + documents);
        StorageLimits.Check("total stored bytes", storedBytes, bytes > 0 ? usage.MaxStoredBytes : long.MaxValue);
        StorageLimits.Check("document count", documentCount, documents > 0 ? usage.MaxDocuments : long.MaxValue);
        usage.StoredBytes = storedBytes;
        usage.DocumentCount = documentCount;
    }
}
