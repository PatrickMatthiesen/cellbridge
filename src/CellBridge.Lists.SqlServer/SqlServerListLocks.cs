using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CellBridge.Lists.SqlServer;

internal sealed partial class EfListEngine
{
    private sealed class DatabaseLocks(EfListEngine engine) : IListDatabaseLocks
    {
        public bool BatchesRecordLocks(OperationAuthorization? authorization) => false;
        public IQueryable<WorkspaceRow> LockedQuota(ListDbContext db, Guid workspaceId) =>
            db.Workspaces.Where(row => row.WorkspaceId == workspaceId);
        public Task AuthorizationAsync(ListDbContext db, Guid workspaceId, bool exclusive, bool mutation,
            OperationAuthorization? authorization, CancellationToken ct, RecordLocks? recordLocks = null) =>
            engine.LockAuthorizationAsync(db, workspaceId, exclusive, mutation, authorization, ct);
        public Task QuotaAsync(ListDbContext db, Guid workspaceId, CancellationToken ct) =>
            LockQuotaAsync(db.Database.GetDbConnection(), db.Database.CurrentTransaction!.GetDbTransaction(), workspaceId, ct);
        public Task MutationAsync(ListDbContext db, Guid workspaceId, CancellationToken ct) =>
            LockWorkspaceMutationAsync(db.Database.GetDbConnection(), db.Database.CurrentTransaction!.GetDbTransaction(), workspaceId, ct);
        public Task ListSharedAsync(ListDbContext db, Guid workspaceId, Guid listId, CancellationToken ct) =>
            LockListSchemaSharedAsync(db.Database.GetDbConnection(), db.Database.CurrentTransaction!.GetDbTransaction(), workspaceId, listId, ct);
        public Task ListExclusiveAsync(ListDbContext db, Guid workspaceId, Guid listId, CancellationToken ct) =>
            LockListExclusiveAsync(db.Database.GetDbConnection(), db.Database.CurrentTransaction!.GetDbTransaction(), workspaceId, listId, ct);
        public Task RecordAsync(ListDbContext db, Guid workspaceId, Guid listId, Guid recordId, bool deleteLock, CancellationToken ct) =>
            engine.LockRecordAsync(db, workspaceId, listId, recordId, deleteLock, ct);
        public Task CreateStateAsync(ListDbContext db, Guid workspaceId, Guid listId, CancellationToken ct) =>
            LockCreateStateAsync(db.Database.GetDbConnection(), db.Database.CurrentTransaction!.GetDbTransaction(), workspaceId, listId, ct);
    }

}
