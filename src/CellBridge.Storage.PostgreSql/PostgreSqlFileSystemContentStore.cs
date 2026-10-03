using System.Security.Cryptography;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.FileSystem;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

/// <summary>
/// Charges durable filesystem objects to the shared PostgreSQL ledger before
/// publication. Failed writes remain charged until quiescent maintenance verifies them.
/// </summary>
public sealed class PostgreSqlFileSystemContentStore : IContentStore, IStorageBudgetParticipant
{
    public object BudgetScope => _source;
    private readonly NpgsqlDataSource _source;
    private readonly FileSystemContentStore _files;
    private readonly long _maxObjectBytes;
    private readonly SemaphoreSlim _writers = new(4);
    public bool Durable => true;
    public bool Shared => _files.Shared;

    public PostgreSqlFileSystemContentStore(NpgsqlDataSource source, string root,
        long maxObjectBytes = 512L * 1024 * 1024, bool shared = false)
    {
        _source = source;
        _maxObjectBytes = maxObjectBytes;
        _files = new(root, maxObjectBytes, shared);
    }

    public async ValueTask<ContentHandle> WriteAsync(Stream input, CancellationToken cancellationToken = default)
    {
        await _writers.WaitAsync(cancellationToken);
        try
        {
            var temporary = Path.Combine(Path.GetTempPath(), "cellbridge-reservation-" + Guid.NewGuid().ToString("N"));
            await using var spool = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
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
            await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                await PostgreSqlStateStore.ConfigureTransactionAsync(connection, transaction, cancellationToken);
                await using var insert = new NpgsqlCommand("INSERT INTO cellbridge_objects VALUES ($1,$2,$1) ON CONFLICT DO NOTHING", connection, transaction);
                insert.Parameters.Add(new NpgsqlParameter { Value = digest });
                insert.Parameters.Add(new NpgsqlParameter { Value = length });
                if (await insert.ExecuteNonQueryAsync(cancellationToken) != 0)
                    await PostgreSqlStorageBudget.AdjustAsync(connection, transaction, length, 0, cancellationToken);
                else
                {
                    await using var verify = new NpgsqlCommand("SELECT length,sha256 FROM cellbridge_objects WHERE object_key=$1", connection, transaction);
                    verify.Parameters.Add(new NpgsqlParameter { Value = digest });
                    await using var metadata = await verify.ExecuteReaderAsync(cancellationToken);
                    if (!await metadata.ReadAsync(cancellationToken) || metadata.GetInt64(0) != length || metadata.GetString(1) != digest)
                        throw new StorageCorruptionException("Invalid reserved filesystem object metadata.");
                }
                await transaction.CommitAsync(CancellationToken.None);
            }
            spool.Position = 0;
            var published = await _files.WriteAsync(spool, cancellationToken);
            if (published != reserved) throw new StorageCorruptionException("Filesystem content disagrees with its reservation.");
            return published;
        }
        catch (NpgsqlException ex) { throw new StorageUnavailableException("Content reservation failed or its outcome is unknown.", ex); }
        finally { _writers.Release(); }
    }

    public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) =>
        _files.OpenReadAsync(handle, cancellationToken);
}
