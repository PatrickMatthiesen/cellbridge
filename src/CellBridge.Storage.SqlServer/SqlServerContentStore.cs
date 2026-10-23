using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.Shared;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.SqlServer;

/// <summary>Immutable SHA-256 content stored in bounded SQL Server chunks.</summary>
public sealed class SqlServerContentStore : IContentStore, IStorageBudgetParticipant
{
    private const int ChunkSize = 65536;
    private readonly DbContextOptions<SqlServerDocumentDbContext> options;
    private readonly long maxObjectBytes;
    private readonly SemaphoreSlim writers = new(4, 4);
    public bool Durable => true;
    public bool Shared => true;
    public object BudgetScope { get; }

    public SqlServerContentStore(DbContextOptions<SqlServerDocumentDbContext> options,
        long maxObjectBytes = 512L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxObjectBytes);
        this.options = SqlServerDocumentDatabase.Normalize(options);
        this.maxObjectBytes = maxObjectBytes;
        BudgetScope = options;
    }

    public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await writers.WaitAsync(cancellationToken);
        try { return await WriteCoreAsync(source, cancellationToken); }
        catch (Exception ex) when (IsSqlException(ex))
        { throw new StorageUnavailableException("Content write failed or its commit outcome is unknown.", ex); }
        finally { writers.Release(); }
    }

    private async Task<ContentHandle> WriteCoreAsync(Stream source, CancellationToken ct)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "cellbridge-" + Guid.NewGuid().ToString("N"));
        await using var spool = ConfidentialFileSystem.CreateFile(temporary, FileAccess.ReadWrite,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkSize];
        long length = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) != 0)
        {
            length = checked(length + read);
            StorageLimits.Check("object bytes", length, maxObjectBytes);
            hash.AppendData(buffer.AsSpan(0, read));
            await spool.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        var handle = new ContentHandle(digest, length, digest);
        await using var db = new SqlServerDocumentDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SqlServerDocumentDatabase.ConfigureTransactionAsync(db, ct);
        await SqlServerDocumentDatabase.CheckConfigurationAsync(db, ct);
        await SqlServerDocumentDatabase.LockAsync(db, SqlServerDocumentDatabase.PublicationGate, ct, "Shared");
        // The transaction-owned lock serializes duplicate admission across hosts without
        // catching a unique violation in an XACT_ABORT transaction or replaying callbacks.
        await SqlServerDocumentDatabase.LockAsync(db, "CellBridge.Content." + digest, ct);
        var existing = await db.Objects.AsNoTracking().SingleOrDefaultAsync(row => row.Key == digest, ct);
        if (existing is not null)
        {
            if (existing.Length != length || existing.Sha256 != digest)
                throw new StorageCorruptionException("Existing content has invalid metadata.");
            using var verification = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long storedLength = 0;
            await foreach (var chunk in ReadChunksAsync(db, digest, ct))
            {
                storedLength = checked(storedLength + chunk.Length);
                if (storedLength > length) throw new StorageCorruptionException("Existing content exceeds its declared length.");
                verification.AppendData(chunk);
            }
            if (storedLength != length || Convert.ToHexStringLower(verification.GetHashAndReset()) != digest)
                throw new StorageCorruptionException("Existing content chunks failed integrity verification.");
        }
        else
        {
            var usage = await SqlServerDocumentDatabase.LockUsageAsync(db, ct);
            var total = checked(usage.StoredBytes + length);
            StorageLimits.Check("total stored bytes", total, usage.MaxStoredBytes);
            usage.StoredBytes = total;
            db.Objects.Add(new() { Key = digest, Length = length, Sha256 = digest });
            // Metadata, quota and chunks commit together. Batches bound tracked content memory.
            await db.SaveChangesAsync(ct);
            spool.Position = 0;
            var ordinal = 0;
            var batch = new List<ContentChunk>(16);
            while ((read = await spool.ReadAsync(buffer, ct)) != 0)
            {
                batch.Add(new() { Key = digest, Ordinal = ordinal++, Bytes = buffer.AsSpan(0, read).ToArray() });
                if (batch.Count == 16) await SaveBatchAsync(db, batch, ct);
            }
            if (batch.Count != 0) await SaveBatchAsync(db, batch, ct);
        }
        // Once committing an immutable object begins, caller cancellation cannot turn
        // a completed write into an invented cancellation outcome.
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        return handle;
    }

    private static async Task SaveBatchAsync(SqlServerDocumentDbContext db, List<ContentChunk> batch, CancellationToken ct)
    {
        db.Chunks.AddRange(batch);
        await db.SaveChangesAsync(ct);
        foreach (var chunk in batch) db.Entry(chunk).State = EntityState.Detached;
        batch.Clear();
    }

    public async ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.Key.Length != 64 || handle.Key.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            handle.Key != handle.Sha256 || handle.Length < 0)
            throw new StorageCorruptionException("Invalid content handle.");
        var db = new SqlServerDocumentDbContext(options);
        try
        {
            var metadata = await db.Objects.AsNoTracking().SingleOrDefaultAsync(row => row.Key == handle.Key, cancellationToken);
            if (metadata is null || metadata.Length != handle.Length || metadata.Sha256 != handle.Sha256)
                throw new StorageCorruptionException("Referenced content is missing or has invalid metadata.");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return new VerifiedChunkStream(db, ReadChunksAsync(db, handle.Key).GetAsyncEnumerator(cancellation.Token),
                cancellation, handle);
        }
        catch { await db.DisposeAsync(); throw; }
    }

    private static async IAsyncEnumerable<byte[]> ReadChunksAsync(SqlServerDocumentDbContext db, string key,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var ordinal = 0;
        await foreach (var row in db.Chunks.AsNoTracking().Where(row => row.Key == key)
                           .OrderBy(row => row.Ordinal).Select(row => new { row.Ordinal, row.Bytes })
                           .AsAsyncEnumerable().WithCancellation(ct))
        {
            if (row.Ordinal != ordinal++ || row.Bytes.Length is 0 or > ChunkSize)
                throw new StorageCorruptionException("Invalid content chunk sequence.");
            yield return row.Bytes;
        }
    }

    private static bool IsSqlException(Exception ex) =>
        ex is SqlException || ex.InnerException is not null && IsSqlException(ex.InnerException);
}
