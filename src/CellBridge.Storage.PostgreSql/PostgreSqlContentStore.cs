using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using CellBridge.Storage.Abstractions;
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
            return new ChunkStream(context, chunks, readCancellation, handle);
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

    private sealed class ChunkStream(CellBridgeDbContext context, IAsyncEnumerator<byte[]> chunks,
        CancellationTokenSource readCancellation, ContentHandle handle) : Stream
    {
        private byte[] _chunk = [];
        private int _offset;
        private long _position;
        private bool _verified;
        private int _disposed;
        private readonly SemaphoreSlim _readGate = new(1, 1);
        private readonly object _lifecycleLock = new();
        private int _activeReads;
        private TaskCompletionSource? _readsDrained;
        private Task? _disposeTask;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public override bool CanRead => Volatile.Read(ref _disposed) == 0;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => handle.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnterRead();
            var enteredGate = false;
            try
            {
                await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                enteredGate = true;
                ThrowIfDisposed();
                if (buffer.Length == 0 || _verified) return 0;
                if (_offset == _chunk.Length)
                {
                    if (!await MoveNextAsync(cancellationToken).ConfigureAwait(false))
                    {
                        _verified = true;
                        if (_position != handle.Length || Convert.ToHexStringLower(_hash.GetHashAndReset()) != handle.Sha256)
                            throw new StorageCorruptionException("Content chunks failed integrity verification.");
                        return 0;
                    }
                    _chunk = chunks.Current;
                    if (_chunk.Length == 0) throw new StorageCorruptionException("An empty content chunk is invalid.");
                    _offset = 0;
                }
                var count = Math.Min(buffer.Length, _chunk.Length - _offset);
                _chunk.AsMemory(_offset, count).CopyTo(buffer);
                _hash.AppendData(_chunk.AsSpan(_offset, count));
                _offset += count;
                _position += count;
                if (_position > handle.Length) throw new StorageCorruptionException("Content exceeds its declared length.");
                if (_position == handle.Length)
                {
                    if (_offset != _chunk.Length || await MoveNextAsync(cancellationToken).ConfigureAwait(false) ||
                        Convert.ToHexStringLower(_hash.GetHashAndReset()) != handle.Sha256)
                        throw new StorageCorruptionException("Content chunks failed integrity verification.");
                    _verified = true;
                }
                return count;
            }
            finally
            {
                if (enteredGate) _readGate.Release();
                ExitRead();
            }
        }

        private async ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(static state =>
                ((CancellationTokenSource)state!).Cancel(), readCancellation);
            return await chunks.MoveNextAsync().ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                base.Dispose(disposing);
                return;
            }
            GetDisposeTask().GetAwaiter().GetResult();
        }
        public override async ValueTask DisposeAsync()
        {
            await GetDisposeTask().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
        private Task GetDisposeTask()
        {
            TaskCompletionSource completion;
            lock (_lifecycleLock)
            {
                if (_disposeTask is not null) return _disposeTask;
                Volatile.Write(ref _disposed, 1);
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = completion.Task;
            }
            _ = CompleteDisposeAsync(completion);
            return completion.Task;
        }
        private async Task CompleteDisposeAsync(TaskCompletionSource completion)
        {
            try
            {
                await DisposeCoreAsync().ConfigureAwait(false);
                completion.SetResult();
            }
            catch (Exception exception) { completion.SetException(exception); }
        }
        private async Task DisposeCoreAsync()
        {
            Exception? failure = null;
            try { readCancellation.Cancel(); }
            catch (Exception exception) { failure = exception; }
            await WaitForActiveReadsAsync().ConfigureAwait(false);
            try { await chunks.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure = Combine(failure, exception); }
            try { await context.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure = Combine(failure, exception); }
            try { readCancellation.Dispose(); }
            catch (Exception exception) { failure = Combine(failure, exception); }
            try { _hash.Dispose(); }
            catch (Exception exception) { failure = Combine(failure, exception); }
            try { _readGate.Dispose(); }
            catch (Exception exception) { failure = Combine(failure, exception); }
            try { base.Dispose(true); }
            catch (Exception exception) { failure = Combine(failure, exception); }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        private void EnterRead()
        {
            lock (_lifecycleLock)
            {
                ThrowIfDisposed();
                _activeReads++;
            }
        }
        private void ExitRead()
        {
            TaskCompletionSource? readsDrained = null;
            lock (_lifecycleLock)
            {
                if (--_activeReads == 0) readsDrained = _readsDrained;
            }
            readsDrained?.TrySetResult();
        }
        private Task WaitForActiveReadsAsync()
        {
            lock (_lifecycleLock)
            {
                if (_activeReads == 0) return Task.CompletedTask;
                _readsDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                return _readsDrained.Task;
            }
        }
        private static Exception Combine(Exception? first, Exception second) =>
            first is null ? second : new AggregateException(first, second);
        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(ChunkStream));
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
