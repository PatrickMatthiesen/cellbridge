using System.Security.Cryptography;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.Tests;

public sealed class PendingSaveContentTests
{
    [Fact]
    public async Task CanonicalHandlesDeduplicateContentAndRemainVerifiableUntilDisposal()
    {
        using var pending = new PendingSaveContent(1024);
        byte[] bytes = [1, 2, 3];
        var handle = await pending.WriteAsync(new MemoryStream(bytes));
        var duplicate = await pending.WriteAsync(new MemoryStream(bytes));
        var empty = await pending.WriteAsync(new MemoryStream());
        Assert.Equal(handle, duplicate);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), handle.Key);
        Assert.Equal(handle.Key, handle.Sha256);
        Assert.Equal(bytes.Length, handle.Length);
        Assert.Equal(2, pending.Contents.Count);
        Assert.Equal(bytes, await pending.ReadVerifiedAsync(handle));
        Assert.Empty(await pending.ReadVerifiedAsync(empty));
        await Assert.ThrowsAsync<StorageCorruptionException>(() => pending.OpenReadAsync(handle with { Length = 4 }).AsTask());
        await Assert.ThrowsAsync<StorageCorruptionException>(() => pending.OpenReadAsync(handle with { Sha256 = empty.Sha256 }).AsTask());
        pending.Dispose();
        Assert.Empty(pending.Contents);
        await Assert.ThrowsAsync<StorageCorruptionException>(() => pending.OpenReadAsync(handle).AsTask());
    }

    [Fact]
    public async Task UnseekableSourcesEnforceTheObjectLimitWithoutReadingTheirRemainingBytes()
    {
        using var pending = new PendingSaveContent(3);
        var retained = await pending.WriteAsync(new MemoryStream([9]));
        using var source = new ByteAtATimeStream(100);
        var failure = await Assert.ThrowsAsync<StorageQuotaExceededException>(() => pending.WriteAsync(source).AsTask());
        Assert.Equal("object bytes", failure.Budget);
        Assert.Equal(4, source.BytesRead);
        Assert.Single(pending.Contents);
        Assert.Equal([9], await pending.ReadVerifiedAsync(retained));
    }

    [Fact]
    public async Task ARejectedAggregateWriteDoesNotChangeTheAdmittedBatch()
    {
        using var pending = new PendingSaveContent(PendingSaveContent.MaxBytes);
        var retained = await pending.WriteAsync(new MemoryStream(new byte[5 * 1024 * 1024]));
        var failure = await Assert.ThrowsAsync<StorageQuotaExceededException>(() =>
            pending.WriteAsync(new MemoryStream(new byte[4 * 1024 * 1024])).AsTask());
        Assert.Equal("pending save bytes", failure.Budget);
        Assert.Single(pending.Contents);
        Assert.Equal(retained.Length, (await pending.ReadVerifiedAsync(retained)).LongLength);
    }

    [Fact]
    public async Task RejectedObjectCountAndCancellationPreservePreviouslyAdmittedObjects()
    {
        using var pending = new PendingSaveContent(1024);
        var handles = new List<ContentHandle>();
        for (var index = 0; index < IContentBatchWriter.MaxBatchObjects; index++)
            handles.Add(await pending.WriteAsync(new MemoryStream(BitConverter.GetBytes(index))));
        Assert.Equal(handles[0], await pending.WriteAsync(new MemoryStream(BitConverter.GetBytes(0))));
        var failure = await Assert.ThrowsAsync<StorageQuotaExceededException>(() =>
            pending.WriteAsync(new MemoryStream(BitConverter.GetBytes(64))).AsTask());
        Assert.Equal("pending save objects", failure.Budget);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pending.WriteAsync(new MemoryStream([1]), new(true)).AsTask());
        Assert.Equal(IContentBatchWriter.MaxBatchObjects, pending.Contents.Count);
        foreach (var handle in handles) Assert.Equal(handle.Length, (await pending.ReadVerifiedAsync(handle)).LongLength);
    }

    private sealed class ByteAtATimeStream(int length) : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new InvalidOperationException("The source length is not trusted.");
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BytesRead == length || buffer.Length == 0) return ValueTask.FromResult(0);
            buffer.Span[0] = (byte)BytesRead++;
            return ValueTask.FromResult(1);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
