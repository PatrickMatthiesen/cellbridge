using System.Data.Common;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace CellBridge.Lists.SqlServer;

internal sealed partial class EfListEngine
{
    private static DbContextOptions<SqlServerListDbContext> NormalizeOptions(DbContextOptions<SqlServerListDbContext> options) => SqlServerListDatabase.Normalize(options);
    private static PooledDbContextFactory<SqlServerListDbContext>? CreateContextFactory(DbContextOptions<SqlServerListDbContext> options) => null;
    private SqlServerListDbContext CreateContext() => new(_dbOptions);
    private Task<SqlServerListWorkspaceTransaction> OpenSessionAsync(Guid workspaceId, CancellationToken ct) =>
        SqlServerListWorkspaceTransaction.OpenAsync(_dbOptions, workspaceId, ct, _limits.StatementTimeout);
    private static Task<T> MapStoreFailuresAsync<T>(Func<Task<T>> operation, CancellationToken ct) => SqlServerListDatabase.MapFailuresAsync(operation, ct);

    private async Task EnsureHealthyAsync(CancellationToken ct)
    {
        await using var session = await OpenOperationSessionAsync(Guid.NewGuid(), ct);
        await session.CommitAsync(ct);
    }

    private async Task<SqlServerListWorkspaceTransaction> OpenOperationSessionAsync(Guid workspaceId, CancellationToken ct)
    {
        var session = await OpenSessionAsync(workspaceId, ct);
        try
        {
            await CheckConfigurationAsync(session.Context, ct);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private async Task LockAuthorizationAsync(SqlServerListDbContext db, Guid workspaceId, bool exclusive, bool mutation,
        OperationAuthorization? authorization, CancellationToken ct)
    {
        await SqlServerListDatabase.LockAsync(db, SqlServerListDatabase.WorkspaceLock(workspaceId), exclusive || mutation, _limits, ct);
        await RequireAnchorAsync(db, workspaceId, authorization, authorization is not null, ct);
    }

    // SQL Server's workspace lock protects these rows for the whole operation.
    // Keep its existing coarse protection while PostgreSQL retains row locks.
    private static Task LockQuotaAsync(DbConnection connection, DbTransaction transaction, Guid workspaceId, CancellationToken ct) => Task.CompletedTask;
    private static Task LockWorkspaceMutationAsync(DbConnection connection, DbTransaction transaction, Guid workspaceId, CancellationToken ct) => Task.CompletedTask;
    private static Task LockListSchemaSharedAsync(DbConnection connection, DbTransaction transaction, Guid workspaceId, Guid listId, CancellationToken ct) => Task.CompletedTask;
    private static Task LockListExclusiveAsync(DbConnection connection, DbTransaction transaction, Guid workspaceId, Guid listId, CancellationToken ct) => Task.CompletedTask;
    private static Task LockCreateStateAsync(DbConnection connection, DbTransaction transaction, Guid workspaceId, Guid listId, CancellationToken ct) => Task.CompletedTask;
    private Task LockRecordAsync(SqlServerListDbContext db, Guid workspaceId, Guid listId, Guid recordId, bool deleteLock, CancellationToken ct) => Task.CompletedTask;

    private static Task ConfigureWorkspaceAsync(DbConnection connection, DbTransaction transaction, Guid workspaceId, CancellationToken ct) => Task.CompletedTask;
    private static Task<long> CanonicalJsonBytesAsync(SqlServerListDbContext db, string json, CancellationToken ct) =>
        Task.FromResult((long)Encoding.UTF8.GetByteCount(JsonNode.Parse(json)!.ToJsonString()));
    private static Task<DateTimeOffset> ClockAsync(SqlServerListDbContext db, CancellationToken ct) => SqlServerListDatabase.ClockAsync(db, ct);

    private async Task<(long Bytes, bool Claimed)> ClaimIdentityAsync(SqlServerListDbContext db,
        Guid workspaceId, Guid listId, Guid recordId, string valuesJson, CancellationToken ct)
    {
        var bytes = await CanonicalJsonBytesAsync(db, valuesJson, ct);
        if (bytes > _limits.MaxRecordBytes) return (bytes, false);
        if (await db.RecordTombstones.AnyAsync(row => row.WorkspaceId == workspaceId && row.ListId == listId && row.RecordId == recordId, ct))
            return (bytes, false);
        db.RecordTombstones.Add(new RecordTombstoneRow { WorkspaceId = workspaceId, ListId = listId, RecordId = recordId });
        return (bytes, true);
    }

    private static async Task<HashSet<Guid>> LockReferenceTargetsAsync(SqlServerListDbContext db,
        Guid workspaceId, ReferenceAssignment[] ordered, Guid[] lockableFieldIds, CancellationToken ct)
    {
        var found = new HashSet<Guid>();
        foreach (var reference in ordered.Where(reference => lockableFieldIds.Contains(reference.FieldId)))
            if (await db.Records.AsNoTracking().AnyAsync(row => row.WorkspaceId == workspaceId &&
                row.ListId == reference.TargetListId && row.RecordId == reference.TargetRecordId, ct)) found.Add(reference.FieldId);
        return found;
    }

    private static bool IsUniqueViolation(Exception ex) => ex is SqlException { Number: 2601 or 2627 } || ex.InnerException is not null && IsUniqueViolation(ex.InnerException);
    private static bool IsReferenceViolation(Exception ex) => ex is SqlException { Number: 547 } || ex.InnerException is not null && IsReferenceViolation(ex.InnerException);
    private static ListException InvalidStored() => new(ListFailure.Unavailable, "Persisted list state is invalid or changed during publication.");
    private Task ConfigureCandidateReadAsync(ListDbContext db, Guid workspaceId, OperationAuthorization authorization, CancellationToken ct) => SqlServerListDatabase.LockAsync(db, SqlServerListDatabase.WorkspaceLock(workspaceId), false, _limits, ct);

    private Task LockRegistrationAsync(SqlServerListDbContext db, Guid workspaceId, bool template, CancellationToken ct) =>
        SqlServerListDatabase.LockAsync(db, template ? "CellBridge.Lists.Templates" : SqlServerListDatabase.WorkspaceLock(workspaceId), true, _limits, ct);
    private static Task<bool> TemplateDefinitionsEqualAsync(SqlServerListDbContext db, string left, string right, CancellationToken ct) =>
        Task.FromResult(JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right)));

}
