using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

#if CELLBRIDGE_SQLSERVER
namespace CellBridge.Lists.SqlServer;
#else
namespace CellBridge.Lists.PostgreSql;
#endif

internal sealed partial class EfListEngine
{
    // The operation selects the lock and its order. Providers implement only the
    // database primitive, including their existing workspace protection.
    private interface IListDatabaseLocks
    {
        Task AuthorizationAsync(ListDbContext db, Guid workspaceId, bool exclusive, bool mutation,
            OperationAuthorization? authorization, CancellationToken ct);
        IQueryable<WorkspaceRow> LockedQuota(ListDbContext db, Guid workspaceId);
        Task QuotaAsync(ListDbContext db, Guid workspaceId, CancellationToken ct);
        Task MutationAsync(ListDbContext db, Guid workspaceId, CancellationToken ct);
        Task ListSharedAsync(ListDbContext db, Guid workspaceId, Guid listId, CancellationToken ct);
        Task ListExclusiveAsync(ListDbContext db, Guid workspaceId, Guid listId, CancellationToken ct);
        Task RecordAsync(ListDbContext db, Guid workspaceId, Guid listId, Guid recordId, bool deleteLock, CancellationToken ct);
        Task CreateStateAsync(ListDbContext db, Guid workspaceId, Guid listId, CancellationToken ct);
    }

    private static async Task SaveRequiredAsync(ListDbContext db, CancellationToken ct)
    {
        db.ChangeTracker.DetectChanges();
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.DetectChanges();
        if (db.ChangeTracker.HasChanges())
            throw new ListException(ListFailure.Unavailable, "Required Lists publication was suppressed or left pending changes.");
    }
}
