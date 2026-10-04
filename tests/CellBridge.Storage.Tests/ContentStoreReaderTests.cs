using System.Collections.Immutable;
using System.Security.Cryptography;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Web;

namespace CellBridge.Storage.Tests;

public sealed class ContentStoreReaderTests
{
    [Fact]
    public async Task HealthProbeRejectsOverlongProviderOutputAfterOneExtraByteAndDisposesSource()
    {
        var store = new TestContent([67, 66, 99, 100], writeHandle: Handle([67, 66]));
        var provider = new StorageProvider(new InMemoryStateStore(), store);
        await Assert.ThrowsAsync<StorageCorruptionException>(() => provider.CheckHealthAsync().AsTask());
        Assert.True(store.LastStream!.Disposed);
        Assert.Equal(3, store.LastStream.ReadCalls);
    }

    [Fact]
    public async Task HealthProbeRejectsOversizedDeclaredHandleBeforeOpeningSource()
    {
        var store = new TestContent([], writeHandle: Handle([67, 66]) with { Length = 3 });
        var provider = new StorageProvider(new InMemoryStateStore(), store);
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => provider.CheckHealthAsync().AsTask());
        Assert.Null(store.LastStream);
    }

    [Fact]
    public async Task VerifiedReaderHandlesShortReadsWithoutConsultingStreamLengthAndDisposesSource()
    {
        byte[] bytes = [1, 2, 3, 4, 5];
        var store = new TestContent(bytes);
        Assert.Equal(bytes, await store.ReadVerifiedAsync(Handle(bytes)));
        Assert.True(store.LastStream!.Disposed);
        Assert.Equal(6, store.LastStream.ReadCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task VerifiedReaderRejectsTruncatedOverlongAndWrongHashContent(int mode)
    {
        byte[] expected = [1, 2, 3];
        byte[] actual = mode switch { 0 => [1, 2], 1 => [1, 2, 3, 4, 5], _ => [3, 2, 1] };
        var store = new TestContent(actual);
        await Assert.ThrowsAsync<StorageCorruptionException>(() => store.ReadVerifiedAsync(Handle(expected)).AsTask());
        Assert.True(store.LastStream!.Disposed);
        Assert.True(store.LastStream.ReadCalls <= 4);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public async Task InvalidLengthsFailBeforeOpeningOrAllocatingContent(long length)
    {
        var store = new TestContent([]);
        await Assert.ThrowsAsync<StorageCorruptionException>(() => store.ReadVerifiedAsync(Handle([]) with { Length = length }).AsTask());
        Assert.Null(store.LastStream);
    }

    [Fact]
    public async Task AdmissionLimitAndPreCancellationFailBeforeOpeningContent()
    {
        var store = new TestContent([]);
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => store.ReadVerifiedAsync(Handle([1, 2]), 1).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.ReadVerifiedAsync(Handle([]), cancellationToken: new(true)).AsTask());
        Assert.Null(store.LastStream);
    }

    [Fact]
    public async Task CancellationDuringShortReadsDisposesSource()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new TestContent([1, 2, 3], cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.ReadVerifiedAsync(Handle([1, 2, 3]), cancellationToken: cancellation.Token).AsTask());
        Assert.True(store.LastStream!.Disposed);
    }

    [Fact]
    public async Task EmptyContentStillVerifiesHashAndEof()
    {
        Assert.Empty(await new TestContent([]).ReadVerifiedAsync(Handle([])));
        await Assert.ThrowsAsync<StorageCorruptionException>(() => new TestContent([1]).ReadVerifiedAsync(Handle([])).AsTask());
    }

    [Fact]
    public async Task RestoreRejectsConflictingAliasesBeforeOpeningContent()
    {
        var content = new InMemoryContentStore();
        var state = await new DocumentStore().Put("/aliases.docx", MinimalDocx.Create()).CaptureAsync(content);
        var file = state.Partitions.Single(p => p.Kind == 0);
        var conflicting = file.Content with { Length = file.Content.Length + 1 };
        state = state with { Partitions = state.Partitions.Select(p => p.Kind == 0 ? p with { Content = conflicting } : p).ToImmutableArray() };
        var store = new TestContent([]);
        await Assert.ThrowsAsync<StorageCorruptionException>(() => StoredDocument.RestoreAsync(state, store).AsTask());
        Assert.Null(store.LastStream);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryRejectsHashedMalformedOrTrailingResponseBytesWithoutPublishing(bool malformed)
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/receipt.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = StorageTests.Fixture("save-first");
        ((PutChangesSubRequestData)request.SubRequests.Single().Data!).ExpectedStorageIndex =
            StorageIds.Restore(state.Partitions.Single(p => p.Kind == 0).StorageIndex!);
        var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value);
        var receipt = Assert.Single(saved.State.Receipts);
        var response = await provider.Content.ReadVerifiedAsync(receipt.Response!);
        using var appended = new MemoryStream(malformed ? [1, 2, 3] : [..response, 0xff]);
        var corrupted = await provider.Content.WriteAsync(appended);
        await provider.State.TransitionAsync(state.ResourceId, (current, _) => new StateTransition<bool>(current with
            { Receipts = [receipt with { Response = corrupted }] }, true));
        var before = await provider.State.FindByResourceIdAsync(state.ResourceId);
        var error = await Assert.ThrowsAsync<StorageCorruptionException>(() => service.ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value).AsTask());
        if (malformed) Assert.NotNull(error.InnerException);
        Assert.Equal(before, await provider.State.FindByResourceIdAsync(state.ResourceId));
    }

    private static ContentHandle Handle(byte[] bytes) => new("key", bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));

    private sealed class TestContent(byte[] bytes, CancellationTokenSource? cancellation = null,
        ContentHandle? writeHandle = null) : IContentStore
    {
        public bool Durable => false;
        public bool Shared => false;
        public ShortReadStream? LastStream { get; private set; }
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
            => writeHandle is null ? throw new NotSupportedException() : ValueTask.FromResult(writeHandle);
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<Stream>(LastStream = new(bytes, cancellation));
    }

    private sealed class ShortReadStream(byte[] bytes, CancellationTokenSource? cancellation) : Stream
    {
        private int _position;
        public bool Disposed { get; private set; }
        public int ReadCalls { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new InvalidOperationException("Length must not be consulted.");
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            int count = Math.Min(1, Math.Min(buffer.Length, bytes.Length - _position));
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            cancellation?.Cancel();
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
