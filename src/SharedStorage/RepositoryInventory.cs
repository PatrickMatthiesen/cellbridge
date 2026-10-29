using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

// Explicit quiescent maintenance only. Foreground operations use maintained document counters.
internal static class RepositoryInventory
{
    private static async Task ValidateOwnersAsync<T>(DbContext db, CancellationToken ct) where T : class
    {
        if (await db.Set<T>().AnyAsync(row => !db.Set<RepositoryHeadRow>().Any(head => head.ResourceId == EF.Property<Guid>(row, "ResourceId")), ct))
            throw new CellBridge.Storage.Abstractions.StorageCorruptionException("A normalized repository row has no document owner.");
    }
    internal static async Task ValidateOwnersAsync(DbContext db, CancellationToken ct)
    {
        await ValidateOwnersAsync<RepositoryCoordinationRow>(db, ct);
        await ValidateOwnersAsync<RepositoryEditorRow>(db, ct);
        await ValidateOwnersAsync<RepositoryPartitionRow>(db, ct);
        await ValidateOwnersAsync<RepositoryElementRow>(db, ct);
        await ValidateOwnersAsync<RepositoryVisibilityRow>(db, ct);
        await ValidateOwnersAsync<RepositoryGraphRow>(db, ct);
        await ValidateOwnersAsync<RepositoryBindingRow>(db, ct);
        await ValidateOwnersAsync<RepositoryBindingChangeRow>(db, ct);
        await ValidateOwnersAsync<RepositoryCheckpointRow>(db, ct);
        await ValidateOwnersAsync<RepositoryDeltaRow>(db, ct);
        await ValidateOwnersAsync<RepositoryRevisionRow>(db, ct);
        await ValidateOwnersAsync<RepositoryReceiptRow>(db, ct);
        await ValidateOwnersAsync<RepositoryPathRow>(db, ct);
        await ValidateOwnersAsync<RepositoryDeliveryRow>(db, ct);
        await ValidateOwnersAsync<RepositoryReferenceRow>(db, ct);
        await ValidateOwnersAsync<RepositoryMappingRow>(db, ct);
        await ValidateOwnersAsync<RepositoryMappingIntervalRow>(db, ct);
        await ValidateOwnersAsync<RepositoryMappingClaimRow>(db, ct);
        await ValidateOwnersAsync<RepositoryMappingRootRow>(db, ct);
    }
    private static async Task<long> JsonBytesAsync<T>(DbContext db, CancellationToken ct) where T : class =>
        await db.Set<T>().SumAsync(row => (long?)DocumentSql.ByteLength(EF.Property<string>(row, "Json")), ct) ?? 0;
    internal static async Task<long> JsonBytesAsync(DbContext db, CancellationToken ct)
    {
        long bytes = 0;
        bytes = checked(bytes + await JsonBytesAsync<RepositoryHeadRow>(db, ct));
        bytes = checked(bytes + await JsonBytesAsync<RepositoryCoordinationRow>(db, ct));
        bytes = checked(bytes + await JsonBytesAsync<RepositoryEditorRow>(db, ct));
        bytes = checked(bytes + await JsonBytesAsync<RepositoryPartitionRow>(db, ct));
        bytes = checked(bytes + await JsonBytesAsync<RepositoryElementRow>(db, ct));
        bytes = checked(bytes + await JsonBytesAsync<RepositoryGraphRow>(db, ct));
        bytes = checked(bytes + await JsonBytesAsync<RepositoryBindingRow>(db, ct));
        bytes = checked(bytes + await JsonBytesAsync<RepositoryBindingChangeRow>(db, ct));
        bytes = checked(bytes + await JsonBytesAsync<RepositoryCheckpointRow>(db, ct));
        bytes = checked(bytes + await JsonBytesAsync<RepositoryRevisionRow>(db, ct));
        bytes = checked(bytes + await JsonBytesAsync<RepositoryReceiptRow>(db, ct));
        bytes = checked(bytes + await JsonBytesAsync<RepositoryDeliveryRow>(db, ct));
        bytes = checked(bytes + await JsonBytesAsync<RepositoryMappingRow>(db, ct));
        bytes = checked(bytes + await JsonBytesAsync<RepositoryMappingIntervalRow>(db, ct));
        bytes = checked(bytes + await JsonBytesAsync<RepositoryMappingClaimRow>(db, ct));
        bytes = checked(bytes + await JsonBytesAsync<RepositoryMappingRootRow>(db, ct));
        bytes = checked(bytes + (await db.Set<RepositoryReferenceRow>().SumAsync(row => (long?)row.AdmissionBytes, ct) ?? 0));
        return bytes;
    }
}
