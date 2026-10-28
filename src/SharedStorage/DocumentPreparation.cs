using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed partial class DocumentOperations<TContext> where TContext : DbContext
{
    public async ValueTask<DocumentPreparationRead?> ReadPreparationAsync(Guid resourceId,
        CancellationToken cancellationToken = default) => await ReadAsync(async db =>
    {
        // PostgreSQL gives this statement one snapshot. SQL Server's ReadAsync
        // retains serializable protection around the complete statement.
        var row = await (from pointer in db.Set<DocumentRow>().AsNoTracking()
            where pointer.ResourceId == resourceId && !pointer.IsDeleted
            join snapshot in db.Set<StateRow>().AsNoTracking()
                on new { pointer.ResourceId, pointer.StateVersion }
                equals new { snapshot.ResourceId, snapshot.StateVersion } into snapshots
            from snapshot in snapshots.DefaultIfEmpty()
            select new
            {
                Pointer = pointer,
                Data = snapshot == null ? null : snapshot.StateJson,
                UtcNow = DocumentSql.ClockUtc(),
            }).SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        var state = Decode(row.Data ?? throw new StorageCorruptionException("The current snapshot is missing."), row.Pointer);
        return new DocumentPreparationRead(state, DateTime.SpecifyKind(row.UtcNow, DateTimeKind.Utc));
    }, cancellationToken);
}
