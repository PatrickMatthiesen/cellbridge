using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

namespace CellBridge.Storage.Abstractions;

internal sealed class VerifiedChunkStream(IAsyncDisposable context, IAsyncEnumerator<byte[]> chunks,
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
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(VerifiedChunkStream));
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
