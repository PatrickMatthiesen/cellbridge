using System.Security.Cryptography;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CellBridge.Storage.Shared;

/// <summary>Stores immutable content in bounded database chunks and reuses matching objects.</summary>
internal sealed class DocumentContent<TContext> : IContentStore, IStorageBudgetParticipant where TContext : DbContext
{
    private const int ChunkSize = 65536;
    private const int ChunksPerBatch = 16;
    private readonly IDocumentDatabase<TContext> database;
    private readonly object _budgetScope;
    private readonly long _maxObjectBytes;
    private readonly SemaphoreSlim _writers = new(4);
    // Delegates belong to this store's options, not a process-wide default model.
    private IModel? _queryModel;
    private readonly Func<TContext, string, CancellationToken, Task<ContentMetadata?>> _contentMetadata =
        EF.CompileAsyncQuery((TContext context, string key, CancellationToken token) =>
            context.Set<ObjectRow>().AsNoTracking().Where(row => row.ObjectKey == key)
                .Select(row => new ContentMetadata { Length = row.Length, Sha256 = row.Sha256 }).SingleOrDefault());
    private readonly Func<TContext, string, IAsyncEnumerable<ChunkProjection>> _contentChunks =
        EF.CompileAsyncQuery((TContext context, string key) =>
            context.Set<ChunkRow>().AsNoTracking().Where(row => row.ObjectKey == key)
                .OrderBy(row => row.Ordinal).Select(row => new ChunkProjection { Ordinal = row.Ordinal, Bytes = row.Bytes }));

    public DocumentContent(IDocumentDatabase<TContext> database, long maxObjectBytes, object budgetScope)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxObjectBytes);
        this.database = database;
        _maxObjectBytes = maxObjectBytes;
        _budgetScope = budgetScope;
    }

    public object BudgetScope => _budgetScope;
    public bool Durable => true;
    public bool Shared => true;
    private TContext CreateContext() =>
        database.CreateContext();

    public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await _writers.WaitAsync(cancellationToken);
        try { return await WriteCoreAsync(source, cancellationToken); }
        catch (Exception ex) when (database.IsDatabaseException(ex))
        { throw new StorageUnavailableException("Content write failed or its commit outcome is unknown.", ex); }
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
        await database.ConfigureAsync(context, cancellationToken);
        await database.CheckConfigurationAsync(context, cancellationToken);
        await database.Locks.PublicationAsync(context, false, cancellationToken);
        await database.Locks.ContentAsync(context, digest, cancellationToken);
        var inserted = await database.InsertObjectAsync(context, handle, cancellationToken);
        if (inserted)
        {
            await DocumentBudget.AdjustAsync(database, context, length, 0, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
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
        }
        else
        {
            var metadata = await ReadContentMetadataAsync(context, digest, cancellationToken);
            if (metadata is null || metadata.Length != length || metadata.Sha256 != digest)
                throw new StorageCorruptionException("Existing content has invalid metadata.");
            using var verification = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long storedLength = 0;
            await foreach (var chunk in ReadContentChunks(context, digest, cancellationToken))
            {
                storedLength = checked(storedLength + chunk.Length);
                if (storedLength > length)
                    throw new StorageCorruptionException("Existing content exceeds its declared length.");
                verification.AppendData(chunk);
            }
            if (storedLength != length || Convert.ToHexStringLower(verification.GetHashAndReset()) != digest)
                throw new StorageCorruptionException("Existing content chunks failed integrity verification.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        await databaseTransaction.CommitAsync(CancellationToken.None);
        return handle;
    }

    private static async Task SaveChunkBatchAsync(TContext context, List<ChunkRow> pending,
        CancellationToken cancellationToken)
    {
        context.Set<ChunkRow>().AddRange(pending);
        await context.SaveChangesAsync(cancellationToken);
        foreach (var chunk in pending) context.Entry(chunk).State = EntityState.Detached;
        pending.Clear();
    }

    public async ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.Key.Length != 64 || handle.Key.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            handle.Key != handle.Sha256 || handle.Length < 0)
            throw new StorageCorruptionException("Invalid content handle.");
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

    private bool UsesCompiledModel(TContext context)
    {
        var model = context.Model;
        return ReferenceEquals(Interlocked.CompareExchange(ref _queryModel, model, null) ?? model, model);
    }

    private Task<ContentMetadata?> ReadContentMetadataAsync(TContext context, string key,
        CancellationToken cancellationToken) => UsesCompiledModel(context)
            ? _contentMetadata(context, key, cancellationToken)
            : context.Set<ObjectRow>().AsNoTracking().Where(row => row.ObjectKey == key)
                .Select(row => new ContentMetadata { Length = row.Length, Sha256 = row.Sha256 })
                .SingleOrDefaultAsync(cancellationToken);

    private async IAsyncEnumerable<byte[]> ReadContentChunks(TContext context, string key,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var rows = UsesCompiledModel(context)
            ? _contentChunks(context, key)
            : context.Set<ChunkRow>().AsNoTracking().Where(row => row.ObjectKey == key)
                .OrderBy(row => row.Ordinal).Select(row => new ChunkProjection { Ordinal = row.Ordinal, Bytes = row.Bytes }).AsAsyncEnumerable();
        var ordinal = 0;
        await foreach (var row in rows.WithCancellation(ct))
        {
            if (row.Ordinal != ordinal++ || row.Bytes.Length is 0 or > ChunkSize)
                throw new StorageCorruptionException("Invalid content chunk sequence.");
            yield return row.Bytes;
        }
    }

    private sealed class ChunkProjection
    {
        public int Ordinal { get; init; }
        public required byte[] Bytes { get; init; }
    }

    private sealed class ContentMetadata
    {
        public long Length { get; init; }
        public required string Sha256 { get; init; }
    }

}
