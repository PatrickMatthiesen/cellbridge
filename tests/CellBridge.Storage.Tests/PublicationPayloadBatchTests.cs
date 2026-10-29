using System.Collections.Immutable;
using System.Security.Cryptography;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;
using CellBridge.Tests;

namespace CellBridge.Storage.Tests;

public sealed class PublicationPayloadBatchTests
{
    [Fact]
    public async Task ValidationUsesAtMostSixtyFourPayloadsAndOneMiBPerBatch()
    {
        var (partition, content) = Blobs(Enumerable.Repeat(8, 130));
        await DocumentPublicationRules.ValidatePartitionPayloadsAsync(partition, content, new());
        Assert.Equal(new[] { 64, 64, 2 }, content.Batches.Select(batch => batch.Count));
        Assert.All(content.Batches, batch => Assert.InRange(batch.Bytes, 0, 1024 * 1024));
        (partition, content) = Blobs([300_000, 300_000, 300_000, 300_000, 1024 * 1024 + 17, 8]);
        await DocumentPublicationRules.ValidatePartitionPayloadsAsync(partition, content, new());
        Assert.Equal(new[] { 3, 1, 1, 1 }, content.Batches.Select(batch => batch.Count));
        Assert.All(content.Batches, batch => Assert.True(batch.Bytes <= 1024 * 1024 || batch.Count == 1));
    }

    [Fact]
    public async Task CrossBatchAliasesObjectBoundsAndCancellationFailBeforeAnyRead()
    {
        var (partition, content) = Blobs(Enumerable.Repeat(8, 65));
        var first = partition.AddedElements[0];
        var last = partition.AddedElements[^1];
        var aliased = partition with { AddedElements = partition.AddedElements.SetItem(64, last with { Payload = first.Payload with { Length = first.Payload.Length + 1 } }) };
        await Assert.ThrowsAsync<StorageCorruptionException>(() => DocumentPublicationRules.ValidatePartitionPayloadsAsync(aliased, content, new()).AsTask());
        var excessive = partition with { AddedElements = partition.AddedElements.SetItem(64, last with { Payload = last.Payload with { Length = 1_000_000 } }) };
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => DocumentPublicationRules.ValidatePartitionPayloadsAsync(excessive, content, new() { MaxObjectBytes = 100 }).AsTask());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Equal(cancelled.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DocumentPublicationRules.ValidatePartitionPayloadsAsync(partition, content, new(), cancelled.Token).AsTask())).CancellationToken);
        Assert.Empty(content.Batches);
    }

    [Fact]
    public async Task BatchBytesMappingSerialsAndDerivedIndexesAreStillIndependentlyVerified()
    {
        var (partition, content) = Blobs([8]);
        var element = partition.AddedElements[0];
        content.Payloads[element.Payload.Key][^1] ^= 1;
        await Assert.ThrowsAsync<StorageCorruptionException>(() => DocumentPublicationRules.ValidatePartitionPayloadsAsync(partition, content, new()).AsTask());
        (partition, content) = Blobs([8]);
        var mismatch = partition with { AddedElements = [partition.AddedElements[0] with { MappingSerials = [new(Guid.NewGuid(), 1)] }] };
        await Assert.ThrowsAsync<InvalidDataException>(() => DocumentPublicationRules.ValidatePartitionPayloadsAsync(mismatch, content, new()).AsTask());

        var scope = partition.EffectiveChanges.Scope;
        var manifest = new GraphFixture([1]).Elements.Single(e => e.DataElementType == DataElementType.StorageManifestDataElementData);
        var metadata = IndexedDocumentGraph.ReadMetadata(scope, manifest);
        var handle = Handle(manifest.Data!);
        content = new(new() { [handle.Key] = manifest.Data! });
        mismatch = partition with
        {
            AddedElements = [new(IndexedDocumentGraph.Capture(manifest.DataElementExtendedGuid), (uint)manifest.DataElementType,
                new(manifest.SerialNumber.Guid, manifest.SerialNumber.Value), handle) { MappingSerials = [] }],
            StorageManifests = [metadata.StorageManifest! with { Schema = Guid.NewGuid() }],
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => DocumentPublicationRules.ValidatePartitionPayloadsAsync(mismatch, content, new()).AsTask());
    }

    private static (PreparedPartitionPublication Partition, BatchOnlyContent Content) Blobs(IEnumerable<int> lengths)
    {
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var elements = ImmutableArray.CreateBuilder<GraphElementState>();
        foreach (var length in lengths)
        {
            var bytes = new byte[length]; BitConverter.GetBytes(elements.Count).AsSpan(0, Math.Min(length, 4)).CopyTo(bytes);
            var blob = StorageManifestBuilder.BuildObjectDataBlobDataElement(new(1, Guid.NewGuid()), new(Guid.NewGuid(), 1), bytes);
            var handle = Handle(blob.Data!); payloads[handle.Key] = blob.Data!;
            elements.Add(new(IndexedDocumentGraph.Capture(blob.DataElementExtendedGuid), (uint)blob.DataElementType,
                new(blob.SerialNumber.Guid, blob.SerialNumber.Value), handle) { MappingSerials = [] });
        }
        var empty = new ExtendedId(0, Guid.Empty);
        var identity = new PartitionIdentity(empty, empty, empty, empty, empty, empty, empty, empty, empty, Guid.Empty);
        var before = new PartitionHead(0, identity, 0, null, 0, []);
        var scope = new IndexedGraphScope(Guid.NewGuid(), 1, 0);
        return (new(before, before with { PublicationSequence = 1 }, elements.ToImmutable(), [], [], [], [], new(scope, 0, 1, [], 2)), new(payloads));
    }
    private static ContentHandle Handle(byte[] bytes)
    { var sha = Convert.ToHexStringLower(SHA256.HashData(bytes)); return new(sha, bytes.LongLength, sha); }
    private sealed class BatchOnlyContent(Dictionary<string, byte[]> payloads) : IContentStore, IContentBatchReader
    {
        public Dictionary<string, byte[]> Payloads { get; } = payloads;
        public List<(int Count, long Bytes)> Batches { get; } = [];
        public bool Durable => false;
        public bool Shared => false;
        public ValueTask<ContentHandle> WriteAsync(Stream input, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken ct = default) => throw new InvalidOperationException("Validation must use the bounded batch path.");
        public ValueTask<IReadOnlyDictionary<string, byte[]>> ReadManyAsync(IReadOnlyCollection<ContentHandle> handles, CancellationToken ct = default)
        { Batches.Add((handles.Count, handles.Sum(handle => handle.Length))); return ValueTask.FromResult<IReadOnlyDictionary<string, byte[]>>(handles.ToDictionary(handle => handle.Key, handle => Payloads[handle.Key], StringComparer.Ordinal)); }
    }
}
