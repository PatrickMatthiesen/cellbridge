using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Lists.PostgreSql;

internal sealed partial class EfListEngine
{
    private readonly bool _useWriteReservations;

    // Only the public data-source constructors build these known EF options.
    // Consumer options keep tracked workspace/counter materialization and saves.
    internal EfListEngine(DbContextOptions<PostgreSqlListDbContext> options, ListLimits? limits,
        byte[]? continuationKey, IListAuthorizationProvider? authorizationProvider, bool useWriteReservations)
        : this(options, limits, continuationKey, authorizationProvider)
    {
        _useWriteReservations = useWriteReservations;
    }

    private async Task<(bool Claimed, long ValueBytes, long Number, DateTimeOffset Now, WorkspaceRow Workspace)> ReserveCreateAsync(
        DbConnection connection, DbTransaction transaction, Guid workspaceId, Guid listId,
        Guid recordId, string valuesJson, long receiptBytes, long auditBytes, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT * FROM cellbridge_lists.reserve_create($1,$2,$3,$4,$5::bigint,$6::bigint,
              $7::bigint,$8::bigint,$9::bigint,$10::bigint)
            """, workspaceId, listId, recordId, valuesJson, _limits.MaxRecordBytes,
            receiptBytes, auditBytes, _limits.MaxRecordsPerList, _limits.MaxWorkspaceBytes, _limits.MaxAuditEvents);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("The create reservation returned no result.");
        var status = reader.GetInt32(0);
        if (status == 2) return (false, reader.GetInt64(1), 0, default, null!);
        RequireReservation(status);
        return (true, reader.GetInt64(1), reader.GetInt64(2), reader.GetFieldValue<DateTimeOffset>(3),
            ReservationWorkspace(reader, workspaceId, 4));
    }

    private async Task<(long ValueBytes, DateTimeOffset Now, WorkspaceRow Workspace)> ReservePatchAsync(
        DbConnection connection, DbTransaction transaction, Guid workspaceId, string valuesJson, long previousBytes,
        long auditBytes, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT * FROM cellbridge_lists.reserve_patch($1,$2,$3::bigint,$4::bigint,
              $5::bigint,$6::bigint,$7::bigint)
            """, workspaceId, valuesJson, _limits.MaxRecordBytes, previousBytes, auditBytes,
            _limits.MaxWorkspaceBytes, _limits.MaxAuditEvents);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("The patch reservation returned no result.");
        RequireReservation(reader.GetInt32(0));
        return (reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2), ReservationWorkspace(reader, workspaceId, 3));
    }

    // Publication validation needs the accepted counters, not another tracked read.
    private static WorkspaceRow ReservationWorkspace(DbDataReader reader, Guid workspaceId, int ordinal) => new()
    {
        WorkspaceId = workspaceId,
        StoredBytes = reader.GetInt64(ordinal),
        AuditCount = reader.GetInt64(ordinal + 1),
        SecurityAuditCount = reader.GetInt32(ordinal + 2)
    };

    private static void RequireReservation(int status)
    {
        switch (status)
        {
            case 0: return;
            case 1: throw Limit("The normalized record exceeds the configured byte limit.");
            case 3: throw Limit("The record identity limit has been reached.");
            case 4: throw Limit("The workspace storage or audit limit has been reached.");
            case 5: throw NotFound();
            case 6: throw new OverflowException("Lists accounting exceeded the signed 64-bit range.");
            default: throw new InvalidOperationException("The reservation returned an unknown status.");
        }
    }
}
