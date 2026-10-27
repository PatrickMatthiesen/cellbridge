using System.Security.Cryptography;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class ContentBatchReaderTests
{
    [Fact]
    public async Task BatchesAreBoundedAndAliasesReadOnce()
    {
        var payloads = Enumerable.Range(0, 130).Select(BitConverter.GetBytes)
            .ToDictionary(bytes => Handle(bytes).Key, StringComparer.Ordinal);
        var handles = payloads.Values.Select(Handle).ToArray();
        var store = new BatchContent(payloads);
        var result = await store.ReadManyVerifiedAsync(handles.Concat(handles));
        Assert.Equal([64, 64, 2], store.BatchSizes);
        Assert.Equal(130, result.Count);
        foreach (var handle in handles) Assert.Equal(payloads[handle.Key], result[handle.Key]);
    }

    [Fact]
    public async Task ConflictingAliasesLimitsAndCancellationFailBeforeAnyBatch()
    {
        var store = new BatchContent(new Dictionary<string, byte[]>());
        var handle = Handle([1]);
        await Assert.ThrowsAsync<StorageCorruptionException>(() =>
            store.ReadManyVerifiedAsync([handle, handle with { Length = 2 }]).AsTask());
        await Assert.ThrowsAsync<StorageCorruptionException>(() =>
            store.ReadManyVerifiedAsync([handle, Handle([2]) with { Length = long.MaxValue }]).AsTask());
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() =>
            store.ReadManyVerifiedAsync([handle, Handle([2, 3])], 1).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.ReadManyVerifiedAsync([handle], cancellationToken: new(true)).AsTask());
        Assert.Empty(store.BatchSizes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task BatchOutputIsIndependentlyVerified(int mode)
    {
        var handle = Handle([1, 2, 3]);
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [handle.Key] = mode switch { 0 => [1, 2], 1 => [1, 2, 3, 4], 2 => [3, 2, 1], _ => [1, 2, 3] },
        };
        if (mode == 3) payloads.Remove(handle.Key);
        if (mode == 4) payloads.Add("unexpected", []);
        await Assert.ThrowsAsync<StorageCorruptionException>(() =>
            new BatchContent(payloads, returnAll: true).ReadManyVerifiedAsync([handle]).AsTask());
    }

    [Fact]
    public async Task EmptyPayloadStillRequiresItsCorrectHash()
    {
        var handle = Handle([]);
        var store = new BatchContent(new Dictionary<string, byte[]> { [handle.Key] = [] });
        Assert.Empty((await store.ReadManyVerifiedAsync([handle]))[handle.Key]);
        await Assert.ThrowsAsync<StorageCorruptionException>(() =>
            store.ReadManyVerifiedAsync([handle with { Sha256 = new string('0', 64) }]).AsTask());
    }

    [Fact]
    public async Task ProvidersWithoutBatchSupportUseVerifiedSequentialReads()
    {
        byte[][] payloads = [[], [1, 2], [3, 4, 5]];
        var store = new SequentialContent(payloads.ToDictionary(bytes => Handle(bytes).Key));
        var handles = payloads.Select(Handle).ToArray();
        var result = await store.ReadManyVerifiedAsync(handles.Concat(handles));
        Assert.Equal(3, store.Reads);
        foreach (var payload in payloads) Assert.Equal(payload, result[Handle(payload).Key]);
        store.Payloads[handles[1].Key] = [2, 1];
        await Assert.ThrowsAsync<StorageCorruptionException>(() => store.ReadManyVerifiedAsync(handles).AsTask());
    }

    [Fact]
    public async Task DocumentAndFileGraphRestoreUseBatchReads()
    {
        var bytes = MinimalDocx.Create();
        var content = new InMemoryContentStore();
        var state = await new DocumentStore().Put("/batch.docx", bytes).CaptureAsync(content);
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var handle in ContentStoreReader.UniqueHandles(StorageReferences.Handles(state)))
            payloads.Add(handle.Key, await content.ReadVerifiedAsync(handle));
        var store = new BatchContent(payloads);
        var restored = await StoredDocument.RestoreAsync(state, store);
        Assert.Equal(bytes, restored.Content);
        Assert.Single(store.BatchSizes);
        store.BatchSizes.Clear();
        var graph = await DocumentPartition.RestoreFileGraphAsync(state, store, new());
        Assert.Equal(bytes, graph.Materialize());
        Assert.Single(store.BatchSizes);
    }

    private static ContentHandle Handle(byte[] bytes)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new(hash, bytes.LongLength, hash);
    }

    private sealed class BatchContent(IReadOnlyDictionary<string, byte[]> payloads, bool returnAll = false)
        : IContentStore, IContentBatchReader
    {
        public List<int> BatchSizes { get; } = [];
        public bool Durable => false;
        public bool Shared => false;
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A batch read must not open individual streams.");
        public ValueTask<IReadOnlyDictionary<string, byte[]>> ReadManyAsync(IReadOnlyCollection<ContentHandle> handles,
            CancellationToken cancellationToken = default)
        {
            BatchSizes.Add(handles.Count);
            return ValueTask.FromResult(returnAll ? payloads : (IReadOnlyDictionary<string, byte[]>)handles
                .ToDictionary(handle => handle.Key, handle => payloads[handle.Key], StringComparer.Ordinal));
        }
    }

    private sealed class SequentialContent(Dictionary<string, byte[]> payloads) : IContentStore
    {
        public Dictionary<string, byte[]> Payloads { get; } = payloads;
        public int Reads { get; private set; }
        public bool Durable => false;
        public bool Shared => false;
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
        {
            Reads++;
            return ValueTask.FromResult<Stream>(new MemoryStream(Payloads[handle.Key], writable: false));
        }
    }
}
