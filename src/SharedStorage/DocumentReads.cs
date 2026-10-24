using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed partial class DocumentOperations<TContext> where TContext : DbContext
{
    private async Task<T> ReadAsync<T>(Func<TContext, Task<T>> action, CancellationToken ct, bool coherent = false)
    {
        try
        {
            await using var db = database.CreateContext();
            return await database.ReadAsync(db, () => action(db), coherent, ct);
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
