using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class DocumentStateJsonTests
{
    [Fact]
    public Task ChangedSaveHistoryHasLinearNormalizedGrowthAndRestoresFromRoundTrip() =>
        CheckWholeFileHistoryAsync(StorageTestDocuments.Memory());

    [PostgreSqlFact]
    public Task DurableChangedSaveHistoryHasLinearGrowthAndRestoresEveryRevision() =>
        EfPostgreSqlMigrationTests.WithInitializedDatabaseAsync(new(), async source =>
        {
            var provider = new StorageProvider(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));
            await CheckWholeFileHistoryAsync(provider);
            await using var command = source.CreateCommand("SELECT count(*) FROM cellbridge_states");
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        });

    private static async Task CheckWholeFileHistoryAsync(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var submitted = new List<byte[]> { MinimalDocx.Create("revision 0") };
        var state = (await service.CreateAsync("/normalized-history.docx", submitted[0], TestActor.Value))!;
        var originalFileCell = state.Package.Root.Cell;
        var locks = new SharedDocumentLocks(service);
        Assert.True((await locks.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value)).Success);
        var sizes = new Dictionary<int, long>();
        for (var saveNumber = 1; saveNumber <= 100; saveNumber++)
        {
            var bytes = MinimalDocx.Create("revision " + saveNumber);
            submitted.Add(bytes);
            using var package = new MemoryStream(bytes);
            var result = await service.SaveWholeFileAsync(state.ResourceId, package, "save-" + saveNumber, TestActor.Value, "lock");
            Assert.False(result.IsReplay);
            state = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
            if (saveNumber is 10 or 20 or 40 or 100)
                sizes[saveNumber] = JsonSerializer.SerializeToUtf8Bytes(await provider.Documents.ExportDocumentAsync(state.ResourceId), RecoveryJson.Options).LongLength;
        }
        Assert.True((double)sizes[40] / sizes[20] < 2.8, $"Snapshot grew {sizes[40] / (double)sizes[20]:F2}x from 20 to 40 saves.");
        Assert.True((double)sizes[100] / sizes[40] < 2.8, $"Snapshot grew {sizes[100] / (double)sizes[40]:F2}x from 40 to 100 saves.");
        Assert.True((await locks.ApplyAsync(state.ResourceId, HostLockOperation.Release, "lock", TestActor.Value)).Success);
        state = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.Equal(101, state.Counters.Revisions);
        Assert.Equal(100, state.Counters.WholeFileSaveReceipts);
        var snapshot = (await provider.Documents.ExportDocumentAsync(state.ResourceId))!;
        var json = JsonSerializer.Serialize(snapshot, RecoveryJson.Options);
        var decoded = JsonSerializer.Deserialize<NormalizedDocumentSnapshot>(json, RecoveryJson.Options)!;
        NormalizedDocumentRecovery.Validate(decoded, provider.Limits);
        Assert.Equal(json, JsonSerializer.Serialize(decoded, RecoveryJson.Options));
        Assert.Equal(NormalizedDocumentRecovery.StoredBytes(decoded), state.Counters.StoredBytes);
        var reader = new NormalizedSnapshotGraphSource(decoded, provider.Content, provider.Limits).PackageReader();
        for (var index = 0; index < decoded.ApplicationRevisions.Length; index++)
        {
            var revision = decoded.ApplicationRevisions[index];
            Assert.Equal(originalFileCell, revision.Package.Root.Cell);
            await using var package = await reader.OpenReadAsync(revision.Package);
            using var copy = new MemoryStream();
            await package.CopyToAsync(copy);
            Assert.Equal(submitted[index], copy.ToArray());
        }
        var selected = decoded.ApplicationRevisions[9];
        var restoreProvider = provider.Documents.Durable ? provider : StorageTestDocuments.Memory(provider.Content);
        if (!provider.Documents.Durable)
            await restoreProvider.Documents.ImportRecoveryAsync(new([decoded], []), new(Guid.NewGuid(), new string('a', 64), 1, 1));
        var restoreService = new CellBridgeDocumentService(restoreProvider);
        var restored = await restoreService.RestoreRevisionAsync(state.ResourceId, selected.RevisionNumber,
            state.LatestRevisionNumber, "round-trip-restore", TestActor.Value);
        Assert.False(restored.IsReplay);
        var restoredState = (await restoreProvider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.Equal(submitted[9], await StorageTestDocuments.PackageAsync(restoreProvider, restoredState.Package));
        Assert.Equal(originalFileCell, restoredState.Package.Root.Cell);
        var replay = await restoreService.RestoreRevisionAsync(state.ResourceId, selected.RevisionNumber,
            state.LatestRevisionNumber, "round-trip-restore", TestActor.Value);
        Assert.True(replay.IsReplay);
        Assert.Equal(restored.Revision.RevisionNumber, replay.Revision.RevisionNumber);
        Assert.Equal(100, restoredState.Counters.WholeFileSaveReceipts);
        using var replayBytes = new MemoryStream(submitted[1]);
        var wholeFileReplay = await restoreService.SaveWholeFileAsync(state.ResourceId, replayBytes, "save-1", TestActor.Value, "lock");
        Assert.True(wholeFileReplay.IsReplay);
        Assert.Equal(decoded.ApplicationRevisions[1].RevisionNumber, wholeFileReplay.Revision.RevisionNumber);
    }

    [Fact]
    public async Task ExpandedLegacyJsonRemainsReadableAndGraphRangesPreserveOrderAndDescriptorIdentity()
    {
        var content = new InMemoryContentStore();
        var state = await LegacyStorageFixture.CreateAsync(content, "/expanded-history.docx", MinimalDocx.Create("legacy"));
        state = (await LegacyStorageFixture.SaveAsync(state, content, MinimalDocx.Create("changed"))).State;

        var partition = state.Partitions.Single(value => value.Kind == 0);
        var elements = partition.Elements;
        Assert.True(elements.Length >= 3);
        var nullSerial = elements[0] with { Serial = new(Guid.Empty, 0) };
        var changedMappings = elements[0] with
        {
            Id = elements[0].Id with { Guid = Guid.NewGuid() },
            MappingSerials = elements[0].MappingSerials.Add(new(Guid.NewGuid(), 999)),
        };
        var variants = ImmutableArray.Create(elements[2], elements[0], elements[1], changedMappings);
        var nonPrefix = ImmutableArray.Create(nullSerial, elements[2]);
        var stateWithRanges = state with
        {
            Partitions = state.Partitions.Select(value => value.Kind == 0 ? value with { Elements = variants } : value).ToImmutableArray(),
            Revisions = state.Revisions.Select((revision, index) => revision with
            {
                Partitions = revision.Partitions.Select(value => value.Kind == 0
                    ? value with { Elements = index == 0 ? nonPrefix : variants.RemoveAt(3) }
                    : value).ToImmutableArray(),
            }).ToImmutableArray(),
        };

        var compact = DocumentStateJson.Serialize(stateWithRanges);
        var decodedCompact = DocumentStateJson.Deserialize(compact, strict: true);
        Assert.Equal(JsonSerializer.Serialize(stateWithRanges), JsonSerializer.Serialize(decodedCompact));
        var decodedElements = decodedCompact.Partitions.Single(value => value.Kind == 0).Elements;
        Assert.Equal(variants.Length, decodedElements.Length);
        for (var index = 0; index < variants.Length; index++)
        {
            Assert.Equal(variants[index].Id, decodedElements[index].Id);
            Assert.Equal(variants[index].Type, decodedElements[index].Type);
            Assert.Equal(variants[index].Serial, decodedElements[index].Serial);
            Assert.Equal(variants[index].Payload, decodedElements[index].Payload);
            Assert.True(variants[index].MappingSerials.AsSpan().SequenceEqual(decodedElements[index].MappingSerials.AsSpan()));
        }
        var decodedOlderElements = decodedCompact.Revisions[0].Partitions.Single(value => value.Kind == 0).Elements;
        Assert.NotSame(decodedElements[1], decodedOlderElements[0]);
        Assert.Equal(Guid.Empty, decodedOlderElements[0].Serial.Guid);

        var expanded = JsonSerializer.Serialize(stateWithRanges);
        var decodedExpanded = DocumentStateJson.Deserialize(expanded, strict: true);
        Assert.Equal(expanded, JsonSerializer.Serialize(decodedExpanded));

        var sameIdDifferentKind = elements[0] with { Type = elements[0].Type + 1 };
        var crossKindState = state with
        {
            Partitions = state.Partitions.Select(value => value.Kind == 1
                ? value with { Elements = [sameIdDifferentKind] }
                : value).ToImmutableArray(),
            Revisions = state.Revisions.Select(revision => revision with
            {
                Partitions = revision.Partitions.Select(value => value.Kind == 1
                    ? value with { Elements = [sameIdDifferentKind] }
                    : value).ToImmutableArray(),
            }).ToImmutableArray(),
        };
        var decodedCrossKind = DocumentStateJson.Deserialize(DocumentStateJson.Serialize(crossKindState), strict: true);
        Assert.Equal(JsonSerializer.Serialize(crossKindState), JsonSerializer.Serialize(decodedCrossKind));

        var aliasDescriptor = elements[0] with { Payload = elements[0].Payload with { Key = elements[0].Payload.Key + ".alias" } };
        var aliasState = state with
        {
            Revisions = state.Revisions.Select((revision, index) => index == 0 ? revision with
            {
                Partitions = revision.Partitions.Select(value => value.Kind == 0
                    ? value with { Elements = [aliasDescriptor] }
                    : value).ToImmutableArray(),
            } : revision).ToImmutableArray(),
        };
        var decodedAlias = DocumentStateJson.Deserialize(DocumentStateJson.Serialize(aliasState), strict: true);
        Assert.Equal(JsonSerializer.Serialize(aliasState), JsonSerializer.Serialize(decodedAlias));
    }

    [Fact]
    public async Task DecoderRejectsConflictingDescriptorsForTheSameIdentity()
    {
        var state = await LegacyStorageFixture.CreateAsync(new InMemoryContentStore(), "/descriptor-conflicts.docx", MinimalDocx.Create("conflicts"));
        var original = state.Partitions.Single(partition => partition.Kind == 0).Elements.First(element => element.Serial.Guid != Guid.Empty);
        var validCompact = DocumentStateJson.Serialize(state);
        var historyFilePartitionIndex = Array.FindIndex(state.Revisions[0].Partitions.ToArray(), partition => partition.Kind == 0);
        var conflicts = new[]
        {
            original with { Type = original.Type + 1 },
            original with { Payload = original.Payload with { Length = original.Payload.Length + 1 } },
            original with { Serial = new(original.Serial.Guid, original.Serial.Value + 1) },
            original with { MappingSerials = original.MappingSerials.Add(new(Guid.NewGuid(), 999)) },
        };

        foreach (var conflict in conflicts)
        {
            var conflictingState = state with
            {
                Partitions = state.Partitions.Select(partition => partition.Kind == 0
                    ? partition with { Elements = [original, conflict] }
                    : partition).ToImmutableArray(),
            };
            Assert.Throws<StorageCorruptionException>(() => DocumentStateJson.Serialize(conflictingState));

            var root = JsonNode.Parse(validCompact)!;
            var table = root["GraphMetadata"]!["Elements"]!.AsArray();
            var conflictIndex = table.Count;
            table.Add(JsonSerializer.SerializeToNode(conflict));
            root["Revisions"]![0]!["Partitions"]![historyFilePartitionIndex]!["Elements"] =
                new JsonArray(conflictIndex, 1);
            Assert.Throws<StorageCorruptionException>(() => DocumentStateJson.Deserialize(root.ToJsonString()));
        }
    }

    [Fact]
    public async Task DecoderRejectsMalformedRangesVersionsAndDuplicateProperties()
    {
        var content = new InMemoryContentStore();
        var state = await LegacyStorageFixture.CreateAsync(content, "/bad-ranges.docx", MinimalDocx.Create("ranges"));
        state = (await LegacyStorageFixture.SaveAsync(state, content, MinimalDocx.Create("second range version"))).State;
        var json = DocumentStateJson.Serialize(state);
        var filePartitionIndex = Array.FindIndex(state.Partitions.ToArray(), partition => partition.Kind == 0);

        var badStart = JsonNode.Parse(json)!;
        badStart["Partitions"]![filePartitionIndex]!["Elements"]![0] = 1000000;
        Assert.Throws<StorageCorruptionException>(() => DocumentStateJson.Deserialize(badStart.ToJsonString()));

        var badCount = JsonNode.Parse(json)!;
        badCount["Partitions"]![filePartitionIndex]!["Elements"]![1] = 0;
        Assert.Throws<StorageCorruptionException>(() => DocumentStateJson.Deserialize(badCount.ToJsonString()));

        var badVersion = JsonNode.Parse(json)!;
        badVersion["GraphMetadata"]!["Version"] = 99;
        Assert.Throws<StorageCorruptionException>(() => DocumentStateJson.Deserialize(badVersion.ToJsonString()));

        var duplicateMember = JsonNode.Parse(json)!;
        duplicateMember["Partitions"]![filePartitionIndex]!["Elements"] = new JsonArray(0, 1, 0, 1);
        Assert.Throws<StorageCorruptionException>(() => DocumentStateJson.Deserialize(duplicateMember.ToJsonString()));

        var unusedTableEntry = JsonNode.Parse(json)!;
        var table = unusedTableEntry["GraphMetadata"]!["Elements"]!.AsArray();
        table.Add(JsonNode.Parse(table[0]!.ToJsonString()));
        Assert.Throws<StorageCorruptionException>(() => DocumentStateJson.Deserialize(unusedTableEntry.ToJsonString()));

        Assert.Throws<StorageQuotaExceededException>(() => DocumentStateJson.Deserialize(json,
            limits: new StorageLimits { MaxHistoryRevisions = 1 }));
        Assert.Throws<StorageQuotaExceededException>(() => DocumentStateJson.Deserialize(json,
            limits: new StorageLimits { MaxGraphElements = 1 }));

        var repeatedHistory = state with
        {
            Revisions = Enumerable.Range(0, 1001)
                .Select(index => state.Revisions[0] with { RevisionNumber = (ulong)index + 1 })
                .ToImmutableArray(),
        };
        var repeatedJson = DocumentStateJson.Serialize(repeatedHistory);
        Assert.Throws<StorageQuotaExceededException>(() => DocumentStateJson.Deserialize(repeatedJson));
        var allowedHistory = DocumentStateJson.Deserialize(repeatedJson,
            limits: new StorageLimits { MaxHistoryRevisions = 1002 });
        Assert.Equal(1001, allowedHistory.Revisions.Length);

        var duplicate = json.Replace("\"FormatVersion\":2", "\"FormatVersion\":2,\"FormatVersion\":2", StringComparison.Ordinal);
        Assert.NotEqual(json, duplicate);
        Assert.Throws<StorageCorruptionException>(() => DocumentStateJson.Deserialize(duplicate, strict: true));
    }
}
