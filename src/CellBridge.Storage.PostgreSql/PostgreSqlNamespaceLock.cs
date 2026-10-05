using Npgsql;

namespace CellBridge.Storage.PostgreSql;

/// <summary>Acquired before path or document locks by every namespace writer.</summary>
public static class PostgreSqlNamespaceLock
{
    public static async Task AcquireAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(748219352)", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
