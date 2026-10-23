using System.Security.Cryptography;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

/// <summary>Immutable, deduplicated content in bounded database chunks; reclamation is quiescent.</summary>
public sealed class PostgreSqlContentStore : IContentStore, IStorageBudgetParticipant
{
    private const int ChunkSize = 65536;
    private const int ChunksPerBatch = 16;
    private readonly DbContextOptions<CellBridgeDbContext> _options;
    private readonly PooledDbContextFactory<CellBridgeDbContext>? _contextFactory;
    private readonly object _budgetScope;
    private readonly long _maxObjectBytes;
    private readonly SemaphoreSlim _writers = new(4);
    // Delegates belong to this store's options, not a process-wide default model.
    private IModel? _queryModel;
    private readonly Func<CellBridgeDbContext, string, CancellationToken, Task<ContentMetadata?>> _contentMetadata =
        EF.CompileAsyncQuery((CellBridgeDbContext context, string key, CancellationToken token) =>
            context.Objects.AsNoTracking().Where(row => row.ObjectKey == key)
                .Select(row => new ContentMetadata { Length = row.Length, Sha256 = row.Sha256 }).SingleOrDefault());
    private readonly Func<CellBridgeDbContext, string, IAsyncEnumerable<byte[]>> _contentChunks =
        EF.CompileAsyncQuery((CellBridgeDbContext context, string key) =>
            context.Chunks.AsNoTracking().Where(row => row.ObjectKey == key)
                .OrderBy(row => row.Ordinal).Select(row => row.Bytes));

    public PostgreSqlContentStore(NpgsqlDataSource dataSource, long maxObjectBytes = 512L * 1024 * 1024)
        : this(PostgreSqlDatabase.CreateOptions(dataSource), maxObjectBytes, dataSource) { }

    public PostgreSqlContentStore(DbContextOptions<CellBridgeDbContext> options,
        long maxObjectBytes = 512L * 1024 * 1024) : this(options, maxObjectBytes, options) { }

    private PostgreSqlContentStore(DbContextOptions<CellBridgeDbContext> options, long maxObjectBytes, object budgetScope)
    {
        _options = PostgreSqlDatabase.NormalizeOptions(options);
        _contextFactory = PostgreSqlDatabase.CreateRuntimeFactory(_options);
        _maxObjectBytes = maxObjectBytes;
        _budgetScope = budgetScope;
    }

    public object BudgetScope => _budgetScope;
    public bool Durable => true;
    public bool Shared => true;
    private CellBridgeDbContext CreateContext() =>
        _contextFactory?.CreateDbContext() ?? new CellBridgeDbContext(_options);

    public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_maxObjectBytes);
        await _writers.WaitAsync(cancellationToken);
        try { return await WriteCoreAsync(source, cancellationToken); }
        finally { _writers.Release(); }
    }

    private async ValueTask<ContentHandle> WriteCoreAsync(Stream source, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "cellbridge-" + Guid.NewGuid().ToString("N"));
        await using var spool = ConfidentialFileSystem.CreateFile(temporary, FileAccess.ReadWrite,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        var buffer = new byte[ChunkSize];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            length = checked(length + read);
            StorageLimits.Check("object bytes", length, _maxObjectBytes);
            hash.AppendData(buffer.AsSpan(0, read));
            await spool.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        var handle = new ContentHandle(digest, length, digest);
        await using var context = CreateContext();
        await using var databaseTransaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await PostgreSqlStateStore.ConfigureTransactionAsync(context, cancellationToken);
        var (connection, transaction) = PostgreSqlDatabase.GetTransaction(context);
        int inserted;
        // Deduplication needs one atomic insert across hosts; EF has no ON CONFLICT LINQ operation.
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO cellbridge_objects VALUES ($1,$2,$1) ON CONFLICT DO NOTHING", connection, transaction))
        {
            insert.Parameters.AddWithValue(digest);
            insert.Parameters.AddWithValue(length);
            inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        if (inserted != 0)
        {
            await PostgreSqlStorageBudget.AdjustAsync(context, length, 0, cancellationToken);
            spool.Position = 0;
            var ordinal = 0;
            var pending = new List<ChunkRow>(ChunksPerBatch);
            while ((read = await spool.ReadAsync(buffer, cancellationToken)) != 0)
            {
                pending.Add(new ChunkRow
                {
                    ObjectKey = digest,
                    Ordinal = ordinal++,
                    Bytes = buffer.AsSpan(0, read).ToArray(),
                });
                if (pending.Count == ChunksPerBatch)
                    await SaveChunkBatchAsync(context, pending, cancellationToken);
            }
            if (pending.Count != 0) await SaveChunkBatchAsync(context, pending, cancellationToken);
            else if (ordinal == 0) await context.SaveChangesAsync(cancellationToken);
        }
        else
        {
            var metadata = await ReadContentMetadataAsync(context, digest, cancellationToken);
            if (metadata is null || metadata.Length != length || metadata.Sha256 != digest)
                throw new StorageCorruptionException("Existing content has invalid metadata.");
            using var verification = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long storedLength = 0;
            var ordinal = 0;
            await foreach (var chunk in context.Chunks.AsNoTracking().Where(row => row.ObjectKey == digest)
                               .OrderBy(row => row.Ordinal).Select(row => new { row.Ordinal, row.Bytes })
                               .AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                storedLength = checked(storedLength + chunk.Bytes.Length);
                if (chunk.Ordinal != ordinal++ || chunk.Bytes.Length == 0 || storedLength > length)
                    throw new StorageCorruptionException("Existing content has invalid chunks.");
                verification.AppendData(chunk.Bytes);
            }
            if (storedLength != length || Convert.ToHexStringLower(verification.GetHashAndReset()) != digest)
                throw new StorageCorruptionException("Existing content chunks failed integrity verification.");
        }
        await databaseTransaction.CommitAsync(CancellationToken.None);
        return handle;
    }

    private static async Task SaveChunkBatchAsync(CellBridgeDbContext context, List<ChunkRow> pending,
        CancellationToken cancellationToken)
    {
        context.Chunks.AddRange(pending);
        await context.SaveChangesAsync(cancellationToken);
        foreach (var chunk in pending) context.Entry(chunk).State = EntityState.Detached;
        pending.Clear();
    }

    public async ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
    {
        var context = CreateContext();
        try
        {
            await context.Database.OpenConnectionAsync(cancellationToken);
            var metadata = await ReadContentMetadataAsync(context, handle.Key, cancellationToken);
            if (metadata is null || metadata.Length != handle.Length || metadata.Sha256 != handle.Sha256)
                throw new StorageCorruptionException("Referenced content is missing or has invalid metadata.");
            var rows = ReadContentChunks(context, handle.Key);
            var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var chunks = rows.GetAsyncEnumerator(readCancellation.Token);
            return new VerifiedChunkStream(context, chunks, readCancellation, handle);
        }
        catch { await context.DisposeAsync(); throw; }
    }

    private bool UsesCompiledModel(CellBridgeDbContext context)
    {
        var model = context.Model;
        return ReferenceEquals(Interlocked.CompareExchange(ref _queryModel, model, null) ?? model, model);
    }

    private Task<ContentMetadata?> ReadContentMetadataAsync(CellBridgeDbContext context, string key,
        CancellationToken cancellationToken) => UsesCompiledModel(context)
            ? _contentMetadata(context, key, cancellationToken)
            : context.Objects.AsNoTracking().Where(row => row.ObjectKey == key)
                .Select(row => new ContentMetadata { Length = row.Length, Sha256 = row.Sha256 })
                .SingleOrDefaultAsync(cancellationToken);

    private IAsyncEnumerable<byte[]> ReadContentChunks(CellBridgeDbContext context, string key) =>
        UsesCompiledModel(context)
            ? _contentChunks(context, key)
            : context.Chunks.AsNoTracking().Where(row => row.ObjectKey == key)
                .OrderBy(row => row.Ordinal).Select(row => row.Bytes).AsAsyncEnumerable();

    private sealed class ContentMetadata
    {
        public long Length { get; init; }
        public required string Sha256 { get; init; }
    }

}
