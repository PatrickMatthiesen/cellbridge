using System.Text;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed partial class RelationalDocumentRepository<TContext> where TContext : DbContext
{
    // Recovery only. SaveChanges retains the caller's transaction and EF hooks.
    // Text counts UTF-8 JSON and string column values, excluding scalar/index overhead.
    // A row exceeding the byte budget is saved alone; a domain row is indivisible.
    private sealed class RecoveryImportBatch(TContext db)
    {
        private const int MaximumRows = 1024;
        private const long MaximumTextBytes = 1024 * 1024;
        private int rows;
        private long textBytes;

        public async Task AddAsync(object row, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = RowTextBytes(row);
            if (rows != 0 && (rows == MaximumRows || bytes > MaximumTextBytes - textBytes))
                await FlushAsync(ct);
            ct.ThrowIfCancellationRequested();
            db.Add(row);
            rows++;
            textBytes = checked(textBytes + bytes);
            if (rows == MaximumRows || textBytes >= MaximumTextBytes) await FlushAsync(ct);
        }

        public async Task FlushAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (rows == 0) return;
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            rows = 0;
            textBytes = 0;
            ct.ThrowIfCancellationRequested();
        }

        private static long RowTextBytes(object row) => row switch
        {
            RepositoryHeadRow value => Text(value.Json, value.PathKey, value.PathHash),
            RepositoryCoordinationRow value => Text(value.Json),
            RepositoryEditorRow value => Text(value.Json),
            RepositoryPartitionRow value => Text(value.Json),
            RepositoryElementRow value => Text(value.Json, value.ElementKey),
            RepositoryVisibilityRow value => Text(value.ElementKey),
            RepositoryGraphRow value => Text(value.Json, value.Kind, value.OwnerKey, value.RecordKey),
            RepositoryBindingRow value => Text(value.Json, value.BindingKey),
            RepositoryBindingChangeRow value => Text(value.Json, value.BindingKey),
            RepositoryCheckpointRow value => Text(value.Json, value.BindingKey),
            RepositoryRevisionRow value => Text(value.Json),
            RepositoryReceiptRow value => Text(value.Json, value.OperationHash),
            RepositoryPathRow value => Text(value.PathKey, value.PathHash),
            RepositoryDeliveryRow value => Text(value.Json),
            RepositoryReferenceRow value => Text(value.ObjectKey, value.Sha256),
            RepositoryDeltaRow => 0,
            RepositoryMappingRow value => Text(value.Json, value.MappingKey),
            RepositoryMappingIntervalRow value => Text(value.Json, value.MappingKey),
            RepositoryMappingRootRow value => Text(value.Json, value.IndexKey),
            RepositoryMappingClaimRow value => Text(value.Json),
            RecoveryReceiptRow value => Text(value.ReceiptJson),
            _ => throw new InvalidOperationException("Unknown recovery import row type."),
        };

        private static long Text(params string[] values)
        {
            long bytes = 0;
            foreach (var value in values) bytes = checked(bytes + Encoding.UTF8.GetByteCount(value));
            return bytes;
        }
    }
}
