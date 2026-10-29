using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.SqlClient;
using System.Text;

namespace CellBridge.Storage.SqlServer;

internal sealed partial class SqlServerDocumentDatabase : IDocumentDatabase<SqlServerDocumentDbContext>, IDocumentLocking<SqlServerDocumentDbContext>
{
    internal const string MigrationGate = "CellBridge.Documents.Migrations";
    public static DbContextOptions<SqlServerDocumentDbContext> Normalize(
        DbContextOptions<SqlServerDocumentDbContext> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var check = new SqlServerDocumentDbContext(options);
        if (!check.Database.IsSqlServer()) throw new ArgumentException("Configure the SQL Server EF provider.", nameof(options));
        if (check.Database.CreateExecutionStrategy().RetriesOnFailure)
            throw new ArgumentException("CellBridge owns explicit transactions. Do not enable EnableRetryOnFailure.", nameof(options));
        return new DbContextOptionsBuilder<SqlServerDocumentDbContext>(options)
            .UseSqlServer(sql => sql.MigrationsHistoryTable(SqlServerDocumentDbContext.MigrationsHistoryTable,
                SqlServerDocumentDbContext.Schema))
            .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.MigrationsUserTransactionWarning)).Options;
    }

    public static Task ConfigureTransactionAsync(SqlServerDocumentDbContext context, CancellationToken ct)
    {
        context.Database.AutoSavepointsEnabled = false;
        return context.Database.ExecuteSqlRawAsync("SET XACT_ABORT ON; SET LOCK_TIMEOUT 10000;", ct);
    }

    public static Task LockAsync(SqlServerDocumentDbContext context, string resource, CancellationToken ct, string mode = "Exclusive") =>
        context.Database.ExecuteSqlAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode={mode},
                @LockOwner='Transaction', @LockTimeout=10000;
            IF @result < 0 THROW 51000, 'CellBridge storage coordination timed out.', 1;
            """, ct);



    public static async Task CheckConfigurationAsync(SqlServerDocumentDbContext db, CancellationToken ct)
    {
        var versions = await db.SchemaVersions.AsNoTracking().Select(row => row.Version).ToListAsync(ct);
        if (versions.Count != 1 || versions[0] != 4)
            throw new StorageUnavailableException("Document schema version 4 is required. Run explicit storage initialization.");
        await CheckDurabilityAsync(db, ct);
    }

    public static async Task CheckDurabilityAsync(SqlServerDocumentDbContext db, CancellationToken ct)
    {
        var delayed = await db.Database.SqlQueryRaw<string>(
            "SELECT delayed_durability_desc AS Value FROM sys.databases WHERE database_id=DB_ID()").SingleAsync(ct);
        if (delayed != "DISABLED")
            throw new StorageUnavailableException("CellBridge requires database delayed durability to be DISABLED.");
    }

    public static bool IsDatabaseException(Exception ex) => ex is SqlException or DbUpdateConcurrencyException ||
        ex.InnerException is not null && IsDatabaseException(ex.InnerException);

    private readonly DbContextOptions<SqlServerDocumentDbContext> options;
    internal SqlServerDocumentDatabase(DbContextOptions<SqlServerDocumentDbContext> options) => this.options = options;
    public SqlServerDocumentDbContext CreateContext() => new(options);
    public IDocumentLocking<SqlServerDocumentDbContext> Locks => this;
    public Task ConfigureAsync(SqlServerDocumentDbContext db, CancellationToken ct) => ConfigureTransactionAsync(db, ct);
    Task IDocumentDatabase<SqlServerDocumentDbContext>.CheckConfigurationAsync(SqlServerDocumentDbContext db, CancellationToken ct) => CheckConfigurationAsync(db, ct);
    bool IDocumentDatabase<SqlServerDocumentDbContext>.IsDatabaseException(Exception ex) => IsDatabaseException(ex);
    private static byte[] PathHash(string key) => System.Security.Cryptography.SHA256.HashData(Encoding.BigEndianUnicode.GetBytes(key));
    public void ValidateNamespaceFields(DocumentRow row, DocumentNamespaceState state)
    {
        if (row.RetiredPathCount != state.RetiredPathKeys.Length || !row.PathHash.AsSpan().SequenceEqual(PathHash(state.PathKey)))
            throw new StorageCorruptionException("The current namespace index disagrees with its snapshot.");
    }
    public Task ContentAsync(SqlServerDocumentDbContext db, string key, CancellationToken ct) => LockAsync(db, "CellBridge.Content." + key, ct);
    public async Task<bool> InsertObjectAsync(SqlServerDocumentDbContext db, ContentHandle handle, CancellationToken ct)
    {
        if (await db.Objects.AnyAsync(row => row.ObjectKey == handle.Key, ct)) return false;
        db.Objects.Add(new ObjectRow { ObjectKey = handle.Key, Length = handle.Length, Sha256 = handle.Sha256 });
        return true;
    }
}
