using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Web;

namespace CellBridge.Storage.Tests;

public sealed class DocumentStateJsonAdmissionTests
{
    [Fact]
    public async Task DecodedMembershipBudgetHasTheSameExactBoundaryForCompactExpandedAndWrites()
    {
        var state = await HistoryStateAsync();
        var expected = state.Partitions.Sum(partition => (long)partition.Elements.Length) +
            state.Revisions.Sum(revision => revision.Partitions.Sum(partition => (long)partition.Elements.Length));
        expected *= IntPtr.Size;
        Assert.Equal(expected, StorageLimits.GetDecodedGraphMembershipBytes(state));
        var admitted = new StorageLimits { MaxDecodedGraphMembershipBytes = expected };
        var rejected = admitted with { MaxDecodedGraphMembershipBytes = expected - 1 };
        var compact = DocumentStateJson.Serialize(state);
        var expanded = JsonSerializer.Serialize(state);

        foreach (var json in new[] { compact, expanded })
        {
            foreach (var strict in new[] { false, true })
            {
                var decoded = DocumentStateJson.Deserialize(json, strict: strict, limits: admitted);
                Assert.Equal(JsonSerializer.Serialize(state), JsonSerializer.Serialize(decoded));
                Assert.Throws<StorageQuotaExceededException>(() =>
                    DocumentStateJson.Deserialize(json, strict: strict, limits: rejected));
            }
        }
        admitted.CheckDocument(state);
        Assert.Throws<StorageQuotaExceededException>(() => rejected.CheckDocument(state));
        Assert.True(await new InMemoryStateStore(new StorageBudget(admitted)).TryCreateAsync(state));
        var refusedStore = new InMemoryStateStore(new StorageBudget(rejected));
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => refusedStore.TryCreateAsync(state).AsTask());
        Assert.Null(await refusedStore.FindByResourceIdAsync(state.ResourceId));
    }

    [Fact]
    public void RepeatedRangesAreRejectedBeforeMalformedDescriptorsAreMaterialized()
    {
        const int descriptorCount = 100_000;
        const int revisionCount = 1_000;
        // Null descriptors would fail decoding. The membership quota must be checked
        // first, without creating one hundred million graph references.
        var descriptors = string.Join(',', Enumerable.Repeat("null", descriptorCount));
        var partition = "{\"Elements\":[0," + descriptorCount + "]}";
        var revisions = string.Join(',', Enumerable.Repeat("{\"Partitions\":[" + partition + "]}", revisionCount));
        var json = "{\"GraphMetadata\":{\"Version\":1,\"Elements\":[" + descriptors +
            "]},\"Partitions\":[" + partition + "],\"Revisions\":[" + revisions + "]}";
        var limits = new StorageLimits();
        Assert.Throws<StorageQuotaExceededException>(() => DocumentStateJson.Deserialize(json, limits: limits));
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<StorageQuotaExceededException>(() => DocumentStateJson.Deserialize(json, limits: limits));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        // Allow parser metadata and runtime variation, while ruling out expanded
        // reference arrays and per-revision identity sets for this input.
        Assert.True(allocated < json.Length * 32L + 8L * 1024 * 1024,
            $"A {json.Length}-character input allocated {allocated} bytes before admission failed.");
    }

    [Fact]
    public void SharedDescriptorCountIsCheckedBeforeMalformedDescriptorDecoding()
    {
        const string json = "{\"GraphMetadata\":{\"Version\":1,\"Elements\":[null,null,null]},\"Partitions\":[],\"Revisions\":[]}";
        Assert.Throws<StorageQuotaExceededException>(() => DocumentStateJson.Deserialize(json,
            limits: new StorageLimits { MaxGraphElements = 1, MaxHistoryRevisions = 1 }));
    }

    [Fact]
    public async Task MalformedMembershipsCannotBypassThePreflight()
    {
        var state = await HistoryStateAsync();
        var compact = DocumentStateJson.Serialize(state);
        var root = JsonNode.Parse(compact)!;
        var partitionIndex = Array.FindIndex(state.Partitions.ToArray(), partition => partition.Elements.Length > 0);
        var malformedRanges = new JsonNode[]
        {
            new JsonArray(int.MaxValue, int.MaxValue),
            new JsonArray(0, long.MaxValue),
            new JsonArray(0, -1),
            new JsonArray(0),
            new JsonArray("invalid", 1),
            new JsonObject(),
        };
        foreach (var ranges in malformedRanges)
        {
            var changed = root.DeepClone();
            changed["Partitions"]![partitionIndex]!["Elements"] = ranges.DeepClone();
            Assert.Throws<StorageCorruptionException>(() => DocumentStateJson.Deserialize(changed.ToJsonString()));
        }
        foreach (var property in new[] { "Partitions", "Revisions" })
        {
            var changed = root.DeepClone();
            changed[property] = new JsonObject();
            Assert.Throws<StorageCorruptionException>(() => DocumentStateJson.Deserialize(changed.ToJsonString()));
        }
        var badMetadata = root.DeepClone();
        badMetadata["GraphMetadata"]!["Elements"] = new JsonObject();
        Assert.Throws<StorageCorruptionException>(() => DocumentStateJson.Deserialize(badMetadata.ToJsonString()));
        foreach (var version in new JsonNode?[] { JsonValue.Create("1"), new JsonObject(), null })
        {
            var changed = root.DeepClone();
            changed["GraphMetadata"]!["Version"] = version?.DeepClone();
            Assert.Throws<StorageCorruptionException>(() => DocumentStateJson.Deserialize(changed.ToJsonString()));
        }
    }

    [Fact]
    public async Task StructuralDuplicatesAreRejectedEvenWhenStrictModeIsDisabled()
    {
        var state = await HistoryStateAsync();
        foreach (var original in new[] { DocumentStateJson.Serialize(state), JsonSerializer.Serialize(state) })
        {
            var root = JsonNode.Parse(original)!;
            foreach (var property in new[] { "Partitions", "Revisions" })
            {
                var duplicate = original.Insert(1, "\"" + property + "\":[],");
                Assert.Throws<StorageCorruptionException>(() => DocumentStateJson.Deserialize(duplicate));
            }
            var partitions = root["Partitions"]!.AsArray();
            var populated = partitions.First(partition => partition!["Elements"]!.AsArray().Count > 0)!;
            var originalPartition = populated.ToJsonString();
            var duplicatedPartition = originalPartition.Insert(1, "\"Elements\":[],");
            var duplicateElements = original.Replace(originalPartition, duplicatedPartition, StringComparison.Ordinal);
            Assert.NotEqual(original, duplicateElements);
            Assert.Throws<StorageCorruptionException>(() => DocumentStateJson.Deserialize(duplicateElements));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DecodedMembershipBudgetMustBePositive(long limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StorageLimits { MaxDecodedGraphMembershipBytes = limit }.Validate());

    [Fact]
    public async Task DuplicateGraphMembersCannotBeSerializedOrAdmitted()
    {
        var state = await HistoryStateAsync();
        var partitionIndex = Array.FindIndex(state.Partitions.ToArray(), partition => partition.Elements.Length > 0);
        var partition = state.Partitions[partitionIndex];
        var duplicate = partition with { Elements = [partition.Elements[0], partition.Elements[0]] };
        var invalid = state with { Partitions = state.Partitions.SetItem(partitionIndex, duplicate) };
        Assert.Throws<StorageCorruptionException>(() => DocumentStateJson.Serialize(invalid));
        Assert.Throws<StorageCorruptionException>(() => DocumentStateJson.GetSerializedSize(invalid));
        Assert.Throws<StorageCorruptionException>(() => new StorageLimits().CheckDocument(invalid));
        var store = new InMemoryStateStore();
        await Assert.ThrowsAsync<StorageCorruptionException>(() => store.TryCreateAsync(invalid).AsTask());
        Assert.Null(await store.FindByResourceIdAsync(invalid.ResourceId));

        var historicalDuplicate = state with
        {
            Revisions = state.Revisions.SetItem(0, state.Revisions[0] with
            {
                Partitions = state.Revisions[0].Partitions.SetItem(partitionIndex, duplicate),
            }),
        };
        Assert.Throws<StorageCorruptionException>(() => DocumentStateJson.Serialize(historicalDuplicate));
    }

    [Fact]
    public async Task HistoricalGraphElementLimitIsEnforcedBeforeWriteAdmission()
    {
        var state = await HistoryStateAsync();
        var currentElementCount = state.Partitions.Sum(partition => partition.Elements.Length);
        var partitionIndex = Array.FindIndex(state.Partitions.ToArray(), partition => partition.Elements.Length > 0);
        var original = state.Partitions[partitionIndex];
        var oversized = original with
        {
            Elements = Enumerable.Range(0, currentElementCount + 1)
                .Select(index => original.Elements[0] with { Id = new((uint)index, Guid.NewGuid()) })
                .ToImmutableArray(),
        };
        var invalid = state with
        {
            Revisions = state.Revisions.SetItem(0, state.Revisions[0] with
            {
                Partitions = state.Revisions[0].Partitions.SetItem(partitionIndex, oversized),
            }),
        };
        var limits = new StorageLimits { MaxGraphElements = currentElementCount };
        limits.CheckDocument(state);
        Assert.Throws<StorageQuotaExceededException>(() => limits.CheckDocument(invalid));
        foreach (var json in new[] { DocumentStateJson.Serialize(invalid), JsonSerializer.Serialize(invalid) })
            Assert.Throws<StorageQuotaExceededException>(() => DocumentStateJson.Deserialize(json, limits: limits));
        var store = new InMemoryStateStore(new StorageBudget(limits));
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => store.TryCreateAsync(invalid).AsTask());
        Assert.Null(await store.FindByResourceIdAsync(invalid.ResourceId));
    }

    [Fact]
    public async Task CrossRevisionIdentityReuseAndNullSerialUpgradeRemainReadable()
    {
        var state = await HistoryStateAsync();
        var partitionIndex = Array.FindIndex(state.Partitions.ToArray(), partition => partition.Elements.Any(element => element.Serial.Guid != Guid.Empty));
        var partition = state.Partitions[partitionIndex];
        var elementIndex = Array.FindIndex(partition.Elements.ToArray(), element => element.Serial.Guid != Guid.Empty);
        var oldElements = partition.Elements.SetItem(elementIndex,
            partition.Elements[elementIndex] with { Serial = new(Guid.Empty, 0) });
        var oldRevision = state.Revisions[0] with
        {
            Partitions = state.Revisions[0].Partitions.SetItem(partitionIndex, partition with { Elements = oldElements }),
        };
        state = state with { Revisions = state.Revisions.SetItem(0, oldRevision) };
        var decoded = DocumentStateJson.Deserialize(DocumentStateJson.Serialize(state), strict: true);
        Assert.Equal(JsonSerializer.Serialize(state), JsonSerializer.Serialize(decoded));
        Assert.Same(decoded.Partitions[partitionIndex].Elements[elementIndex],
            decoded.Revisions[1].Partitions[partitionIndex].Elements[elementIndex]);
        Assert.NotSame(decoded.Partitions[partitionIndex].Elements[elementIndex],
            decoded.Revisions[0].Partitions[partitionIndex].Elements[elementIndex]);
    }

    private static async Task<DocumentState> HistoryStateAsync()
    {
        var content = new InMemoryContentStore();
        var state = await new DocumentStore().Put("/membership-admission.docx", MinimalDocx.Create("membership"))
            .CaptureAsync(content);
        var author = new SubjectIdentity("admission", "admission", "admission");
        return state with
        {
            Revisions = Enumerable.Range(1, 3).Select(number => new DocumentRevision(state.ResourceId,
                state.LifecycleGeneration, (ulong)number, state.ContentVersion, state.ModifiedUtc,
                author, state.Content, state.Partitions)).ToImmutableArray(),
        };
    }
}
