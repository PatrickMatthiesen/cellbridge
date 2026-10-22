using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

internal static class PostgreSqlDatabase
{
    public static DbContextOptions<CellBridgeDbContext> ValidateOptions(DbContextOptions<CellBridgeDbContext> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var context = new CellBridgeDbContext(options);
        if (context.Database.CreateExecutionStrategy().RetriesOnFailure)
            throw new ArgumentException(
                "CellBridge owns explicit transactions and receipt-based retries. Configure a non-retrying EF execution strategy; do not enable EnableRetryOnFailure.",
                nameof(options));
        return options;
    }

    public static DbContextOptions<CellBridgeDbContext> CreateOptions(NpgsqlDataSource source) =>
        new DbContextOptionsBuilder<CellBridgeDbContext>()
            .UseNpgsql(source, postgres => postgres.MigrationsHistoryTable(CellBridgeDbContext.MigrationsHistoryTable))
            .Options;

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
