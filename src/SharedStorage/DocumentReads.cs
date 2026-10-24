using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed partial class DocumentOperations<TContext> where TContext : DbContext
{
    private async Task<T> ReadAsync<T>(Func<TContext, Task<T>> action, CancellationToken ct)
    {
        try
        {
            await using var db = database.CreateContext();
            // SQL Server uses serializable reads; PostgreSQL uses statement snapshots.
            await using var transaction = await db.Database.BeginTransactionAsync(database.ReadIsolation, ct);
            await database.ConfigureAsync(db, ct);
            var result = await action(db);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (Exception ex) when (database.IsDatabaseException(ex))
        { throw new StorageUnavailableException("The document view could not be read.", ex); }
    }

    public async ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return await ReadAsync<IReadOnlyList<DocumentSummary>>(async db =>
        {
            var rows = await CurrentReadRows(db, null, null, false).OrderBy(row => row.PathKey)
                .Skip(offset).Take(limit).ToListAsync(cancellationToken);
            return rows.Select(row => DecodeReadMetadata(row).ToSummary(DateTime.SpecifyKind(row.DatabaseUtc, DateTimeKind.Utc))).ToArray();
        }, cancellationToken);
    }
}
