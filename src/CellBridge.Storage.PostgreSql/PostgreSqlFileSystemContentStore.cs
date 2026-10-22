using System.Security.Cryptography;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.FileSystem;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

/// <summary>Charges filesystem objects to PostgreSQL before publication.</summary>
public sealed class PostgreSqlFileSystemContentStore : IContentStore, IStorageBudgetParticipant
{
    private readonly DbContextOptions<CellBridgeDbContext> _options;
    private readonly object _budgetScope;
    private readonly FileSystemContentStore _files;
    private readonly long _maxObjectBytes;
    private readonly SemaphoreSlim _writers = new(4);
    public object BudgetScope => _budgetScope;
    public bool Durable => true;
    public bool Shared => _files.Shared;

    public PostgreSqlFileSystemContentStore(NpgsqlDataSource source, string root,
        long maxObjectBytes = 512L * 1024 * 1024, bool shared = false)
        : this(PostgreSqlDatabase.CreateOptions(source), root, maxObjectBytes, shared, source) { }

    public PostgreSqlFileSystemContentStore(DbContextOptions<CellBridgeDbContext> options, string root,
        long maxObjectBytes = 512L * 1024 * 1024, bool shared = false)
        : this(options, root, maxObjectBytes, shared, options) { }

    private PostgreSqlFileSystemContentStore(DbContextOptions<CellBridgeDbContext> options, string root,
        long maxObjectBytes, bool shared, object budgetScope)
    {
        _options = PostgreSqlDatabase.ValidateOptions(options);
        _budgetScope = budgetScope;
        _maxObjectBytes = maxObjectBytes;
        _files = new(root, maxObjectBytes, shared);
    }

    public async ValueTask<ContentHandle> WriteAsync(Stream input, CancellationToken cancellationToken = default)
    {
        await _writers.WaitAsync(cancellationToken);
        try
        {
            var temporary = Path.Combine(Path.GetTempPath(), "cellbridge-reservation-" + Guid.NewGuid().ToString("N"));
            await using var spool = ConfidentialFileSystem.CreateFile(temporary, FileAccess.ReadWrite,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            var buffer = new byte[65536];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
            {
                length = checked(length + read);
                StorageLimits.Check("object bytes", length, _maxObjectBytes);
                hash.AppendData(buffer.AsSpan(0, read));
                await spool.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            var reserved = new ContentHandle(digest, length, digest);
            await using (var context = new CellBridgeDbContext(_options))
            await using (var databaseTransaction = await context.Database.BeginTransactionAsync(cancellationToken))
            {
                await PostgreSqlStateStore.ConfigureTransactionAsync(context, cancellationToken);
                var (connection, transaction) = PostgreSqlDatabase.GetTransaction(context);
                int inserted;
                // The reservation must be conflict-tolerant across hosts.
                await using (var insert = new NpgsqlCommand(
                    "INSERT INTO cellbridge_objects VALUES ($1,$2,$1) ON CONFLICT DO NOTHING", connection, transaction))
                {
                    insert.Parameters.AddWithValue(digest);
                    insert.Parameters.AddWithValue(length);
                    inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
                }
                if (inserted != 0)
                    await PostgreSqlStorageBudget.AdjustAsync(context, length, 0, cancellationToken);
                else
                {
                    var metadata = await context.Objects.AsNoTracking().SingleOrDefaultAsync(
                        row => row.ObjectKey == digest, cancellationToken);
                    if (metadata is null || metadata.Length != length || metadata.Sha256 != digest)
                        throw new StorageCorruptionException("Invalid reserved filesystem object metadata.");
                }
                await context.SaveChangesAsync(cancellationToken);
                await databaseTransaction.CommitAsync(CancellationToken.None);
            }
            spool.Position = 0;
            var published = await _files.WriteAsync(spool, cancellationToken);
            if (published != reserved)
                throw new StorageCorruptionException("Filesystem content disagrees with its reservation.");
            return published;
        }
        catch (Exception ex) when (PostgreSqlDatabase.IsDatabaseException(ex))
        { throw new StorageUnavailableException("Content reservation failed or its outcome is unknown.", ex); }
        finally { _writers.Release(); }
    }

    public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) =>
        _files.OpenReadAsync(handle, cancellationToken);
}
