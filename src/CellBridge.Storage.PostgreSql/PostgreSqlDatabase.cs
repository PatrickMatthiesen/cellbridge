using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

internal static class PostgreSqlDatabase
{
    public static DbContextOptions<CellBridgeDbContext> NormalizeOptions(
        DbContextOptions<CellBridgeDbContext> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var normalized = new DbContextOptionsBuilder<CellBridgeDbContext>(options)
            .UseNpgsql(postgres => postgres.MigrationsHistoryTable(CellBridgeDbContext.MigrationsHistoryTable))
            // Library-owned mutations and quota ledger updates require tracked rows.
            .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll)
            // Explicit initialization owns one transaction so legacy transforms, EF
            // migrations, quota validation and the history record cannot diverge.
            .ConfigureWarnings(warnings =>
                warnings.Ignore(RelationalEventId.MigrationsUserTransactionWarning))
            .Options;
        using var context = new CellBridgeDbContext(normalized);
        if (context.Database.CreateExecutionStrategy().RetriesOnFailure)
            throw new ArgumentException(
                "CellBridge owns explicit transactions and receipt-based retries. Configure a non-retrying EF execution strategy; do not enable EnableRetryOnFailure.",
                nameof(options));
        return normalized;
    }

    public static DbContextOptions<CellBridgeDbContext> CreateOptions(NpgsqlDataSource source) =>
        new DbContextOptionsBuilder<CellBridgeDbContext>()
            .UseNpgsql(source)
            .Options;

    public static PooledDbContextFactory<CellBridgeDbContext>? CreateRuntimeFactory(
        DbContextOptions<CellBridgeDbContext> options) =>
        options.Extensions.OfType<RelationalOptionsExtension>().Any(extension => extension.Connection is not null)
            ? null
            : new PooledDbContextFactory<CellBridgeDbContext>(options);

    public static (NpgsqlConnection Connection, NpgsqlTransaction Transaction) GetTransaction(CellBridgeDbContext context)
    {
        var transaction = context.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction
            ?? throw new InvalidOperationException("The CellBridge database transaction is missing.");
        return ((NpgsqlConnection)context.Database.GetDbConnection(), transaction);
    }

    public static bool IsDatabaseException(Exception exception) =>
        exception is NpgsqlException || exception.InnerException is not null && IsDatabaseException(exception.InnerException);

    public static PostgresException? FindPostgresException(Exception exception) => exception switch
    {
        PostgresException postgres => postgres,
        { InnerException: not null } => FindPostgresException(exception.InnerException),
        _ => null,
    };
}
