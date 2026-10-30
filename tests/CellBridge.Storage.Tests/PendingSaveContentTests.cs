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

    [Fact]
    public async Task KnownLengthSourcesReadTheirRemainingBytesIntoOwnedContent()
    {
        using var pending = new PendingSaveContent(1024);
        byte[] input = [1, 2, 3, 4];
        using var source = new MemoryStream(input, writable: false) { Position = 1 };
        var handle = await pending.WriteAsync(source);
        input[1] = 99;
        Assert.Equal([2, 3, 4], await pending.ReadVerifiedAsync(handle));
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(-1)]
    public async Task FalseKnownLengthsNeverPublishContent(long declaredLength)
    {
        using var pending = new PendingSaveContent(1024);
        var retained = await pending.WriteAsync(new MemoryStream([9]));
        using var source = new DeclaredLengthStream([1, 2, 3], declaredLength);
        await Assert.ThrowsAsync<StorageCorruptionException>(() => pending.WriteAsync(source).AsTask());
        Assert.Single(pending.Contents);
        Assert.Equal([9], await pending.ReadVerifiedAsync(retained));
    }

    [Fact]
    public async Task OversizedKnownLengthsFailBeforeReadingOrAllocatingTheirAdvertisedSize()
    {
        using var pending = new PendingSaveContent(1024);
        using var source = new DeclaredLengthStream([1], long.MaxValue);
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => pending.WriteAsync(source).AsTask());
        Assert.Equal(0, source.Position);
        Assert.Empty(pending.Contents);
    }

    [Fact]
    public async Task BatchesOwnTheirBuffersAndDeduplicateNewAndExistingInputs()
    {
        using var pending = new PendingSaveContent(1024);
        var retained = await pending.WriteAsync(new MemoryStream([9]));
        byte[] first = [1, 2];
        byte[] second = [3, 4];
        var handles = await pending.WriteManyAsync([first, second, first, new byte[] { 9 }, ReadOnlyMemory<byte>.Empty]);
        first[0] = 99;
        second[0] = 98;
        Assert.Equal(handles[0], handles[2]);
        Assert.Equal(retained, handles[3]);
        Assert.Equal(4, pending.Contents.Count);
        Assert.Equal([1, 2], await pending.ReadVerifiedAsync(handles[0]));
        Assert.Equal([3, 4], await pending.ReadVerifiedAsync(handles[1]));
        Assert.Empty(await pending.ReadVerifiedAsync(handles[4]));
    }

    [Fact]
    public async Task LaterBatchObjectFailuresDoNotPublishEarlierItems()
    {
        using var pending = new PendingSaveContent(4);
        var retained = await pending.WriteAsync(new MemoryStream([9]));
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() =>
            pending.WriteManyAsync([new byte[] { 1 }, new byte[5]]).AsTask());
        Assert.Single(pending.Contents);
        Assert.Equal([9], await pending.ReadVerifiedAsync(retained));
    }

    [Fact]
    public async Task BatchObjectCountAdmissionIsAtomic()
    {
        using var pending = new PendingSaveContent(1024);
        await pending.WriteManyAsync(Enumerable.Range(0, 63).Select(index => (ReadOnlyMemory<byte>)BitConverter.GetBytes(index)).ToArray());
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() =>
            pending.WriteManyAsync([BitConverter.GetBytes(63), BitConverter.GetBytes(64)]).AsTask());
        Assert.Equal(63, pending.Contents.Count);
        await pending.WriteAsync(new MemoryStream(BitConverter.GetBytes(64)));
        Assert.Equal(64, pending.Contents.Count);
    }

    [Fact]
    public async Task BatchByteAdmissionIsAtomicAndDoesNotChargeRejectedItems()
    {
        using var pending = new PendingSaveContent(PendingSaveContent.MaxBytes);
        await pending.WriteAsync(new MemoryStream(new byte[6 * 1024 * 1024]));
        var first = Enumerable.Repeat((byte)1, 1536 * 1024).ToArray();
        var second = Enumerable.Repeat((byte)2, 1536 * 1024).ToArray();
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => pending.WriteManyAsync([first, second]).AsTask());
        Assert.Single(pending.Contents);
        await pending.WriteAsync(new MemoryStream(new byte[2 * 1024 * 1024]));
        Assert.Equal(2, pending.Contents.Count);
    }

    [Fact]
    public async Task CancellationDuringBatchPreparationPublishesNothing()
    {
        using var pending = new PendingSaveContent(1024);
        using var cancellation = new CancellationTokenSource();
        var retained = await pending.WriteAsync(new MemoryStream([9]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pending.WriteManyAsync(new CancellingInputs(cancellation), cancellation.Token).AsTask());
        Assert.Single(pending.Contents);
        Assert.Equal([9], await pending.ReadVerifiedAsync(retained));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WriteManyAsync([], new(true)).AsTask());
    }

    [Fact]
    public async Task AFullStoreCanReuseAnAlreadyOwnedObjectWithoutAnotherByteCharge()
    {
        using var pending = new PendingSaveContent(PendingSaveContent.MaxBytes);
        var bytes = new byte[(int)PendingSaveContent.MaxBytes];
        var handle = await pending.WriteAsync(new MemoryStream(bytes));
        Assert.Equal(handle, await pending.WriteAsync(new MemoryStream(bytes)));
        Assert.Equal(handle, Assert.Single(await pending.WriteManyAsync([bytes])));
        Assert.Single(pending.Contents);
    }

    private sealed class DeclaredLengthStream(byte[] bytes, long declaredLength) : MemoryStream(bytes, writable: false)
    {
        public override long Length => declaredLength;
    }

    private sealed class CancellingInputs(CancellationTokenSource cancellation) : IReadOnlyList<ReadOnlyMemory<byte>>
    {
        private readonly ReadOnlyMemory<byte>[] contents = [new byte[] { 1 }, new byte[] { 2 }];
        public int Count => contents.Length;
        public ReadOnlyMemory<byte> this[int index]
        {
            get
            {
                if (index == 1) cancellation.Cancel();
                return contents[index];
            }
        }
        public IEnumerator<ReadOnlyMemory<byte>> GetEnumerator() => ((IEnumerable<ReadOnlyMemory<byte>>)contents).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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
