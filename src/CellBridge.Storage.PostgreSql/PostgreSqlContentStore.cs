using System.Security.Cryptography;
using CellBridge.Storage.Abstractions;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

/// <summary>Immutable, deduplicated content in bounded database chunks; reclamation is quiescent.</summary>
public sealed class PostgreSqlContentStore(NpgsqlDataSource dataSource, long maxObjectBytes = 512L * 1024 * 1024) : IContentStore, IStorageBudgetParticipant
{
    public object BudgetScope => dataSource;
    private readonly SemaphoreSlim _writers = new(4);
    public bool Durable => true;
    public bool Shared => true;
    public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxObjectBytes);
        await _writers.WaitAsync(cancellationToken);
        try { return await WriteCoreAsync(source, cancellationToken); }
        finally { _writers.Release(); }
    }

    private async ValueTask<ContentHandle> WriteCoreAsync(Stream source, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "cellbridge-" + Guid.NewGuid().ToString("N"));
        await using var spool = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        var buffer = new byte[65536];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            length = checked(length + read);
            StorageLimits.Check("object bytes", length, maxObjectBytes);
            hash.AppendData(buffer.AsSpan(0, read));
            await spool.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        var handle = new ContentHandle(digest, length, digest);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PostgreSqlStateStore.ConfigureTransactionAsync(connection, transaction, cancellationToken);
        await using var insert = new NpgsqlCommand("INSERT INTO cellbridge_objects VALUES ($1,$2,$1) ON CONFLICT DO NOTHING", connection, transaction);
        insert.Parameters.Add(new NpgsqlParameter { Value = digest });
        insert.Parameters.Add(new NpgsqlParameter { Value = length });
        if (await insert.ExecuteNonQueryAsync(cancellationToken) != 0)
        {
            await PostgreSqlStorageBudget.AdjustAsync(connection, transaction, length, 0, cancellationToken);
            spool.Position = 0;
            int ordinal = 0;
            while ((read = await spool.ReadAsync(buffer, cancellationToken)) != 0)
            {
                await using var chunk = new NpgsqlCommand("INSERT INTO cellbridge_chunks VALUES ($1,$2,$3)", connection, transaction);
                chunk.Parameters.Add(new NpgsqlParameter { Value = digest });
                chunk.Parameters.Add(new NpgsqlParameter { Value = ordinal++ });
                chunk.Parameters.Add(new NpgsqlParameter { Value = buffer.AsSpan(0, read).ToArray() });
                await chunk.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        else
        {
            // Reuse is publication too: a deduplicated handle must refer to
            // complete, verified bytes rather than just an existing key.
            await using (var metadata = new NpgsqlCommand(
                "SELECT length,sha256 FROM cellbridge_objects WHERE object_key=$1", connection, transaction))
            {
                metadata.Parameters.Add(new NpgsqlParameter { Value = digest });
                await using var reader = await metadata.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken) || reader.GetInt64(0) != length || reader.GetString(1) != digest)
                    throw new StorageCorruptionException("Existing content has invalid metadata.");
            }
            await using var chunks = new NpgsqlCommand(
                "SELECT ordinal,bytes FROM cellbridge_chunks WHERE object_key=$1 ORDER BY ordinal", connection, transaction);
            chunks.Parameters.Add(new NpgsqlParameter { Value = digest });
            await using var stored = await chunks.ExecuteReaderAsync(cancellationToken);
            using var verification = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long storedLength = 0;
            int ordinal = 0;
            while (await stored.ReadAsync(cancellationToken))
            {
                var bytes = stored.GetFieldValue<byte[]>(1);
                storedLength = checked(storedLength + bytes.Length);
                if (stored.GetInt32(0) != ordinal++ || bytes.Length == 0 || storedLength > length)
                    throw new StorageCorruptionException("Existing content has invalid chunks.");
                verification.AppendData(bytes);
            }
            if (storedLength != length || Convert.ToHexStringLower(verification.GetHashAndReset()) != digest)
                throw new StorageCorruptionException("Existing content chunks failed integrity verification.");
        }
        await transaction.CommitAsync(CancellationToken.None);
        return handle;
    }
    public async ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using (var check = new NpgsqlCommand("SELECT length,sha256 FROM cellbridge_objects WHERE object_key=$1", connection))
            {
                check.Parameters.Add(new NpgsqlParameter { Value = handle.Key });
                await using var metadata = await check.ExecuteReaderAsync(cancellationToken);
                if (!await metadata.ReadAsync(cancellationToken) || metadata.GetInt64(0) != handle.Length || metadata.GetString(1) != handle.Sha256)
                    throw new StorageCorruptionException("Referenced content is missing or has invalid metadata.");
            }
            var command = new NpgsqlCommand("SELECT bytes FROM cellbridge_chunks WHERE object_key=$1 ORDER BY ordinal", connection);
            command.Parameters.Add(new NpgsqlParameter { Value = handle.Key });
            var reader = await command.ExecuteReaderAsync(cancellationToken);
            return new ChunkStream(connection, command, reader, handle);
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    private sealed class ChunkStream(NpgsqlConnection connection, NpgsqlCommand command, NpgsqlDataReader reader, ContentHandle handle) : Stream
    {
        private byte[] _chunk = [];
        private int _offset;
        private long _position;
        private bool _verified;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => handle.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0 || _verified) return 0;
            if (_offset == _chunk.Length)
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    if (!_verified)
                    {
                        _verified = true;
                        if (_position != handle.Length || Convert.ToHexStringLower(_hash.GetHashAndReset()) != handle.Sha256)
                            throw new StorageCorruptionException("Content chunks failed integrity verification.");
                    }
                    return 0;
                }
                _chunk = reader.GetFieldValue<byte[]>(0);
                if (_chunk.Length == 0) throw new StorageCorruptionException("An empty content chunk is invalid.");
                _offset = 0;
            }
            int count = Math.Min(buffer.Length, _chunk.Length - _offset);
            _chunk.AsMemory(_offset, count).CopyTo(buffer);
            _hash.AppendData(_chunk.AsSpan(_offset, count));
            _offset += count;
            _position += count;
            if (_position > handle.Length) throw new StorageCorruptionException("Content exceeds its declared length.");
            if (_position == handle.Length)
            {
                // Validate before returning the final bytes to HTTP streaming.
                // An error after writing the full Content-Length could otherwise
                // look like a completed download to its caller.
                if (_offset != _chunk.Length || await reader.ReadAsync(cancellationToken) ||
                    Convert.ToHexStringLower(_hash.GetHashAndReset()) != handle.Sha256)
                    throw new StorageCorruptionException("Content chunks failed integrity verification.");
                _verified = true;
            }
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { reader.Dispose(); command.Dispose(); connection.Dispose(); _hash.Dispose(); }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await reader.DisposeAsync(); await command.DisposeAsync(); await connection.DisposeAsync(); _hash.Dispose();
            GC.SuppressFinalize(this);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
