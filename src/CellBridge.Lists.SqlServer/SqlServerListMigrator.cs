using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.Lists.SqlServer;

/// <summary>Explicit library-owned Lists migration. The consumer owns database users and grants.</summary>
public sealed class SqlServerListMigrator
{
    private readonly DbContextOptions<SqlServerListDbContext> options;

    public SqlServerListMigrator(DbContextOptions<SqlServerListDbContext> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var check = new SqlServerListDbContext(options);
        if (!check.Database.IsSqlServer()) throw new ArgumentException("Configure the SQL Server EF provider.", nameof(options));
        if (check.Database.CreateExecutionStrategy().RetriesOnFailure)
            throw new ArgumentException("CellBridge owns explicit transactions. Do not enable EnableRetryOnFailure.", nameof(options));
        this.options = new DbContextOptionsBuilder<SqlServerListDbContext>(options)
            .UseSqlServer(sql => sql.MigrationsHistoryTable(SqlServerListDbContext.MigrationsHistoryTable, SqlServerListDbContext.Schema))
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.MigrationsUserTransactionWarning)).Options;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new SqlServerListDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            SET XACT_ABORT ON; SET LOCK_TIMEOUT 10000;
            DECLARE @result int;
            EXEC @result=sys.sp_getapplock @Resource='CellBridge.Lists.Migrations',
                @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000;
            IF @result < 0 THROW 51000, 'CellBridge Lists migration lock timed out.', 1;
            """, cancellationToken);
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        if ((await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Any(id => !known.Contains(id)))
            throw new InvalidOperationException("Unsupported Lists migration history; persisted data was not changed.");
        await db.Database.MigrateAsync(cancellationToken);
        if (await db.SchemaVersions.AsNoTracking().Where(row => row.Singleton).Select(row => row.Version)
                .SingleAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Unsupported Lists schema version.");
        await transaction.CommitAsync(cancellationToken);
    }
}
