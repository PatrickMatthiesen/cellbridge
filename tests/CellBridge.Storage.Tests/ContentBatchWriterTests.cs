using System.Security.Cryptography;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;
using CellBridge.Tests;

namespace CellBridge.Storage.Tests;

public sealed class ContentBatchWriterTests
{
    [Fact]
    public async Task BoundedBatchesPreserveOrderAndDuplicateInputs()
    {
        IContentStore store = new RecordingWriter();
        var inputs = Enumerable.Range(0, 130).Select(i => (ReadOnlyMemory<byte>)BitConverter.GetBytes(i % 65)).ToArray();
        var handles = await store.WriteManyAsync(inputs);
        Assert.Equal([64, 64, 2], ((RecordingWriter)store).BatchSizes);
        Assert.Equal(inputs.Length, handles.Count);
        for (var i = 0; i < inputs.Length; i++) Assert.Equal(Handle(inputs[i]), handles[i]);
    }

    [Fact]
    public async Task SequentialProvidersStillStoreAndReadEveryPayload()
    {
        IContentStore store = new InMemoryContentStore();
        ReadOnlyMemory<byte>[] inputs = [ReadOnlyMemory<byte>.Empty, new byte[] { 1, 2 }, new byte[] { 1, 2 }];
        var handles = await store.WriteManyAsync(inputs);
        for (var i = 0; i < inputs.Length; i++)
            Assert.Equal(inputs[i].ToArray(), await store.ReadVerifiedAsync(handles[i]));
    }

    [Fact]
    public async Task IncorrectBatchResultsAreRejected()
    {
        IContentStore store = new RecordingWriter { CorruptResult = true };
        await Assert.ThrowsAsync<StorageCorruptionException>(() => store.WriteManyAsync([new byte[] { 1 }]).AsTask());
    }

    [Fact]
    public async Task EmptyBatchesStillHonorCancellation()
    {
        IContentStore store = new RecordingWriter();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteManyAsync([], new(true)).AsTask());
    }

    [Fact]
    public async Task LegacyGraphCaptureStreamsLargePayloadInsteadOfAnOversizedBatch()
    {
        var content = new InMemoryContentStore();
        var initial = await LegacyStorageFixture.CreateAsync(content, "/bounded.docx", MinimalDocx.Create());
        var bytes = RandomNumberGenerator.GetBytes(9 * 1024 * 1024);
        var graph = new GraphFixture(bytes, blob: true).File();
        var recording = new StreamingCaptureStore();

        var captured = await DocumentPartition.CaptureFileGraphAsync(initial.Partitions.Single(partition => partition.Kind == 0),
            graph, 1, Handle(bytes), recording);

        Assert.Equal(1, recording.LargeStreamWrites);
        Assert.NotEmpty(recording.BatchBytes);
        Assert.All(recording.BatchBytes, size => Assert.InRange(size, 1, 8 * 1024 * 1024));
        var payloads = await recording.ReadManyVerifiedAsync(captured.Elements.Select(element => element.Payload));
        Assert.All(captured.Elements, element => Assert.True(payloads.ContainsKey(element.Payload.Key)));
    }

    private static ContentHandle Handle(ReadOnlyMemory<byte> bytes)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
        return new(hash, bytes.Length, hash);
    }

    private sealed class RecordingWriter : IContentStore, IContentBatchWriter
    {
        public bool Durable => false;
        public bool Shared => false;
        public bool CorruptResult { get; init; }
        public List<int> BatchSizes { get; } = [];
        public ValueTask<IReadOnlyList<ContentHandle>> WriteManyAsync(IReadOnlyList<ReadOnlyMemory<byte>> contents,
            CancellationToken cancellationToken = default)
        {
            BatchSizes.Add(contents.Count);
            return ValueTask.FromResult<IReadOnlyList<ContentHandle>>(contents.Select(bytes =>
                CorruptResult ? Handle(bytes) with { Sha256 = new string('0', 64) } : Handle(bytes)).ToArray());
        }
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The batch path must be used.");
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StreamingCaptureStore : IContentStore, IContentBatchWriter
    {
        private readonly InMemoryContentStore inner = new();
        public bool Durable => false;
        public bool Shared => false;
        public int LargeStreamWrites;
        public List<long> BatchBytes { get; } = [];
        public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
        {
            if (source.Length > 8 * 1024 * 1024) LargeStreamWrites++;
            return await inner.WriteAsync(source, cancellationToken);
        }
        public async ValueTask<IReadOnlyList<ContentHandle>> WriteManyAsync(IReadOnlyList<ReadOnlyMemory<byte>> contents,
            CancellationToken cancellationToken = default)
        {
            BatchBytes.Add(contents.Sum(bytes => (long)bytes.Length));
            return await ((IContentStore)inner).WriteManyAsync(contents, cancellationToken);
        }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(handle, cancellationToken);
    }
}
