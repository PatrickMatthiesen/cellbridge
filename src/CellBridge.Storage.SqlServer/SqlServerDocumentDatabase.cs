using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.SqlClient;
using System.Text;

namespace CellBridge.Storage.SqlServer;

internal sealed partial class SqlServerDocumentDatabase : IDocumentDatabase<SqlServerDocumentDbContext>, IDocumentLocking<SqlServerDocumentDbContext>
{
    internal const string PublicationGate = "CellBridge.Documents.Publication";
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

    public static async Task<UsageRow> LockUsageAsync(SqlServerDocumentDbContext context, CancellationToken ct) =>
        context.Usage.Local.SingleOrDefault() ?? await context.Usage.FromSqlRaw("SELECT * FROM cellbridge_documents.usage WITH (UPDLOCK,HOLDLOCK) WHERE Id=1")
            .SingleOrDefaultAsync(ct) ?? throw new StorageCorruptionException("Missing content usage ledger.");

    public static async Task CheckConfigurationAsync(SqlServerDocumentDbContext db, CancellationToken ct)
    {
        var versions = await db.SchemaVersions.AsNoTracking().Select(row => row.Version).ToListAsync(ct);
        if (versions.Count != 1 || versions[0] != 1)
            throw new StorageUnavailableException("Document schema version 1 is required. Run explicit storage initialization.");
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
    public async Task<T> ReadAsync<T>(SqlServerDocumentDbContext db, Func<Task<T>> action, bool coherent, CancellationToken ct)
    {
        // Lock-based READ COMMITTED does not give a coherent statement snapshot.
        // All SQL Server reads keep serializable protection, including path misses.
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        await ConfigureTransactionAsync(db, ct);
        var result = await action();
        await transaction.CommitAsync(ct);
        return result;
    }
    public Task ConfigureAsync(SqlServerDocumentDbContext db, CancellationToken ct) => ConfigureTransactionAsync(db, ct);
    Task IDocumentDatabase<SqlServerDocumentDbContext>.CheckConfigurationAsync(SqlServerDocumentDbContext db, CancellationToken ct) => CheckConfigurationAsync(db, ct);
    public Task CheckRecoveryAsync(SqlServerDocumentDbContext db, CancellationToken ct) => CheckConfigurationAsync(db, ct);
    public async Task<DateTime> ClockAsync(SqlServerDocumentDbContext db, CancellationToken ct) =>
        DateTime.SpecifyKind(await db.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS Value").SingleAsync(ct), DateTimeKind.Utc);
    bool IDocumentDatabase<SqlServerDocumentDbContext>.IsDatabaseException(Exception ex) => IsDatabaseException(ex);
    public bool IsUniqueViolation(Exception ex) => false;
    public IQueryable<DocumentRow> AtPath(IQueryable<DocumentRow> rows, string key)
    {
        var hash = PathHash(key);
        return rows.Where(row => row.PathHash == hash && row.PathKey == key);
    }
    private static byte[] PathHash(string key) => System.Security.Cryptography.SHA256.HashData(Encoding.BigEndianUnicode.GetBytes(key));
    public void SetNamespaceFields(DocumentRow row, DocumentState state)
    {
        row.PathHash = PathHash(state.PathKey);
        row.RetiredPathCount = state.RetiredPathKeys.Length;
    }
    public void ValidatePointer(DocumentRow row, DocumentState state)
    {
        if (row.RetiredPathCount != state.RetiredPathKeys.Length)
            throw new StorageCorruptionException("Snapshot identity disagrees with its current pointer.");
    }
    public void ValidateNamespaceFields(DocumentRow row, DocumentNamespaceState state)
    {
        if (row.RetiredPathCount != state.RetiredPathKeys.Length || !row.PathHash.AsSpan().SequenceEqual(PathHash(state.PathKey)))
            throw new StorageCorruptionException("The current namespace index disagrees with its snapshot.");
    }
    public async Task VerifyReservationsAsync(SqlServerDocumentDbContext db, IReadOnlyDictionary<Guid, DocumentNamespaceState> states, CancellationToken ct)
    {
        var reservations = await db.Reservations.AsNoTracking().ToListAsync(ct);
        foreach (var row in reservations)
        {
            if (!states.TryGetValue(row.ResourceId, out var state) || row.Ordinal < 0 || row.Ordinal >= state.RetiredPathKeys.Length ||
                row.PathKey != state.RetiredPathKeys[row.Ordinal] || !row.PathHash.AsSpan().SequenceEqual(PathHash(state.RetiredPathKeys[row.Ordinal])))
                throw new StorageCorruptionException("A retired path reservation disagrees with its authoritative snapshot.");
        }
        var counts = reservations.GroupBy(row => row.ResourceId).ToDictionary(group => group.Key, group => group.Count());
        if (states.Values.Any(state => state.RetiredPathKeys.Length != counts.GetValueOrDefault(state.ResourceId)))
            throw new StorageCorruptionException("The retired path reservation index is incomplete.");
    }
    public async Task WriteReservationsAsync(SqlServerDocumentDbContext db, DocumentState state, CancellationToken ct)
    {
        var reservations = await db.Reservations.Where(row => row.ResourceId == state.ResourceId).ToListAsync(ct);
        foreach (var row in reservations)
            if (row.Ordinal >= state.RetiredPathKeys.Length) db.Reservations.Remove(row);
        for (var ordinal = 0; ordinal < state.RetiredPathKeys.Length; ordinal++)
        {
            var key = state.RetiredPathKeys[ordinal];
            var row = reservations.SingleOrDefault(candidate => candidate.Ordinal == ordinal);
            if (row is null) db.Reservations.Add(new() { ResourceId = state.ResourceId, Ordinal = ordinal, PathKey = key, PathHash = PathHash(key) });
            else if (row.PathKey != key) { row.PathKey = key; row.PathHash = PathHash(key); }
        }
    }
    public Task<bool> HasReservationsAsync(SqlServerDocumentDbContext db, CancellationToken ct) => db.Reservations.AnyAsync(ct);
    public Task PublicationAsync(SqlServerDocumentDbContext db, bool exclusive, CancellationToken ct) => LockAsync(db, PublicationGate, ct, exclusive ? "Exclusive" : "Shared");
    public Task NamespaceAsync(SqlServerDocumentDbContext db, CancellationToken ct) => LockAsync(db, "CellBridge.Documents.Namespace", ct);
    public Task DocumentAsync(SqlServerDocumentDbContext db, Guid id, CancellationToken ct) => LockAsync(db, "CellBridge.Document." + id.ToString("N"), ct);
    public Task ContentAsync(SqlServerDocumentDbContext db, string key, CancellationToken ct) => LockAsync(db, "CellBridge.Content." + key, ct);
    public Task<UsageRow> UsageAsync(SqlServerDocumentDbContext db, CancellationToken ct) => LockUsageAsync(db, ct);
    public async Task<bool> InsertObjectAsync(SqlServerDocumentDbContext db, ContentHandle handle, CancellationToken ct)
    {
        if (await db.Objects.AnyAsync(row => row.ObjectKey == handle.Key, ct)) return false;
        db.Objects.Add(new ObjectRow { ObjectKey = handle.Key, Length = handle.Length, Sha256 = handle.Sha256 });
        return true;
    }
}
