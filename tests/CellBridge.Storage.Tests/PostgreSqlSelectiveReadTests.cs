using System.Collections.Immutable;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class PostgreSqlSelectiveReadTests
{
    [PostgreSqlFact]
    public Task StableHistoryExcludesConcurrentAppends() =>
        BudgetAndQueryTests.WithDatabase(new(), source => CellBridge.Tests.StableHistoryScenario.RunAsync(
            new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source))));

    [PostgreSqlFact]
    public Task UnsignedRevisionReadsUseNormalizedRowsAndIgnoreUnrelatedLegacySnapshots() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var content = new PostgreSqlContentStore(source); var memory = new InMemoryDocumentRepository(content);
            var provider = new StorageProvider(memory, content); var service = new CellBridgeDocumentService(provider);
            var head = (await service.CreateAsync("/unsigned.docx", MinimalDocx.Create("first"), TestActor.Value))!;
            Assert.True((await new SharedDocumentLocks(service).ApplyAsync(head.ResourceId, HostLockOperation.Acquire, "host", TestActor.Value)).Success);
            using (var input = new MemoryStream(MinimalDocx.Create("second"))) await service.SaveWholeFileAsync(head.ResourceId, input, "second", TestActor.Value, "host");
            await new SharedDocumentLocks(service).ApplyAsync(head.ResourceId, HostLockOperation.Release, "host", TestActor.Value);
            var snapshot = (await memory.ExportDocumentAsync(head.ResourceId))!;
            var first = (ulong)long.MaxValue + 7;
            var revisions = snapshot.ApplicationRevisions.Select((revision, index) => revision with
                { RevisionNumber = index == 0 ? first : ulong.MaxValue, Package = revision.Package with { RevisionNumber = index == 0 ? first : ulong.MaxValue } }).ToImmutableArray();
            snapshot = snapshot with { ApplicationRevisions = revisions,
                Current = snapshot.Current with { Head = snapshot.Current.Head with { LatestRevisionNumber = ulong.MaxValue, Package = revisions[^1].Package } },
                Receipts = snapshot.Receipts.Select(receipt => receipt with { RevisionNumber = ulong.MaxValue }).ToImmutableArray() };
            snapshot = snapshot with { Current = snapshot.Current with { Head = snapshot.Current.Head with { Counters = snapshot.Current.Head.Counters with
                { StoredBytes = NormalizedDocumentRecovery.StoredBytes(snapshot) } } } };
            IDocumentRepository store = new PostgreSqlStateStore(source);
            await store.ImportRecoveryAsync(new([snapshot], []), new(Guid.NewGuid(), new string('a', 64), 1, 1));
            await using (var corrupt = source.CreateCommand("INSERT INTO cellbridge_states VALUES ($1,0,'not JSON')"))
            { corrupt.Parameters.AddWithValue(Guid.NewGuid()); await corrupt.ExecuteNonQueryAsync(); }
            Assert.NotNull(await store.FindAsync(new DocumentIdentity.Resource(head.ResourceId)));
            var page = (await store.ReadRevisionsAsync(head.ResourceId, 0, 1))!;
            Assert.Equal(ulong.MaxValue, Assert.Single(page.Revisions).RevisionNumber); Assert.Equal(1, page.NextOffset);
            var next = (await store.ReadRevisionsAsync(head.ResourceId, 1, 1))!;
            Assert.Equal(first, Assert.Single(next.Revisions).RevisionNumber); Assert.Null(next.NextOffset);
            Assert.Equal(ulong.MaxValue, (await store.ReadRevisionAsync(head.ResourceId, ulong.MaxValue))!.Revision!.RevisionNumber);
            Assert.Null((await store.ReadRevisionAsync(head.ResourceId, 1))!.Revision);
        });
    [PostgreSqlFact]
    public Task MalformedOrMismatchedNormalizedMetadataFailsAtItsTargetedRead() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            IDocumentRepository store = new PostgreSqlStateStore(source);
            var head = (await new CellBridgeDocumentService(new(store, new PostgreSqlContentStore(source))).CreateAsync("/corrupt-normalized.docx", MinimalDocx.Create(), TestActor.Value))!;
            await using (var corrupt = source.CreateCommand("UPDATE cellbridge_repository_heads SET \"StateVersion\"=\"StateVersion\"+1")) await corrupt.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.FindAsync(new DocumentIdentity.Resource(head.ResourceId)).AsTask());
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.ListAsync(0, 10).AsTask());
            await using (var repair = source.CreateCommand("UPDATE cellbridge_repository_heads SET \"StateVersion\"=\"StateVersion\"-1")) await repair.ExecuteNonQueryAsync();
            await using (var corrupt = source.CreateCommand("UPDATE cellbridge_repository_revisions SET \"Json\"='{}'")) await corrupt.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.ReadRevisionAsync(head.ResourceId, 1).AsTask());
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.ReadRevisionsAsync(head.ResourceId, 0, 1).AsTask());
            Assert.NotNull(await store.FindAsync(new DocumentIdentity.Resource(head.ResourceId)));
            Assert.Null(await store.FindAsync(new DocumentIdentity.Resource(Guid.NewGuid())));
        });
    [PostgreSqlFact]
    public Task RenameDeleteAndRecreateFenceTheRetiredIdentityAndKeepImmutableHistory() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var content = new PostgreSqlContentStore(source); IDocumentRepository store = new PostgreSqlStateStore(source);
            var head = (await new CellBridgeDocumentService(new(store, content)).CreateAsync("/before.docx", MinimalDocx.Create(), TestActor.Value))!;
            var renamed = (await store.ChangePathAsync(new(head.ResourceId, StorageTestDocuments.Admission(head), "/after.docx", "/AFTER.DOCX", ImmutableDictionary<string, string>.Empty))).Current;
            Assert.Null(await store.FindAsync(new DocumentIdentity.Path(head.PathKey)));
            Assert.Equal(head.Package, (await store.ReadRevisionAsync(head.ResourceId, 1))!.Revision!.Package);
            Assert.True((await store.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(renamed.ResourceId, 1, renamed.StateVersion))).Applied);
            Assert.Null(await store.ReadRevisionsAsync(head.ResourceId, 0, 10)); Assert.NotNull((await store.ReadRevisionAsync(head.ResourceId, 1))!.Revision); Assert.Empty(await store.ListAsync(0, 10));
            var retired = (await store.FindAsync(new DocumentIdentity.Resource(head.ResourceId, true)))!;
            var replacement = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), renamed.Path, MinimalDocx.Create("replacement"), head.Security, content, new(), lifecycleGeneration: 2);
            Assert.True((await store.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(retired.ResourceId, 1, retired.StateVersion, replacement))).Applied);
            Assert.Equal(replacement.Head.ResourceId, (await store.FindAsync(new DocumentIdentity.Path(renamed.PathKey)))!.ResourceId);
            Assert.Single((await store.ReadRevisionsAsync(replacement.Head.ResourceId, 0, 10))!.Revisions);
            Assert.Single((await store.ExportDocumentAsync(retired.ResourceId))!.ApplicationRevisions);
        });
    [PostgreSqlFact]
    public Task IntermediateProtocolRootsSurviveRecoveryInEitherRecordOrderWithoutInventingApplicationRevisions() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var content = new PostgreSqlContentStore(source); IDocumentRepository store = new PostgreSqlStateStore(source); var provider = new StorageProvider(store, content);
            var head = (await new CellBridgeDocumentService(provider).CreateAsync("/intermediate.docx", MinimalDocx.Create(), TestActor.Value))!;
            var plan = await PublicationTestPlans.NoopAsync(provider, head.ResourceId, "intermediate"); Assert.Equal(PublicationStatus.Accepted, (await store.PublishAsync(plan)).Status);
            var snapshot = (await store.ExportDocumentAsync(head.ResourceId))!;
            Assert.Single(snapshot.ApplicationRevisions); Assert.Equal(2, snapshot.MappingRoots.Length);
            Assert.NotEqual(snapshot.Partitions.Single(partition => partition.Head.Kind == 0).Head.StorageIndex,
                (await store.ReadPartitionAsync(head.ResourceId, new(0) { RevisionNumber = 1 }))!.Partition.StorageIndex);
            foreach (var reverse in new[] { false, true })
                await BudgetAndQueryTests.WithDatabase(new(), async target =>
                {
                    var targetContent = new PostgreSqlContentStore(target); await CellBridge.Tests.NormalizedProviderScenarios.CopyReferencesAsync(snapshot, content, targetContent);
                    IDocumentRepository repository = new PostgreSqlStateStore(target); var ordered = reverse ? snapshot with { MappingRoots = snapshot.MappingRoots.Reverse().ToImmutableArray() } : snapshot;
                    await repository.ImportRecoveryAsync(new([ordered], []), new(Guid.NewGuid(), new string('b', 64), 1, 1));
                    Assert.Equal(PortabilityTests.CanonicalSnapshots([snapshot]), PortabilityTests.CanonicalSnapshots([(await repository.ExportDocumentAsync(head.ResourceId))!]));
                });
        });
    [PostgreSqlFact]
    public Task Schema5MigrationPreservesLegacySnapshotsAndBuildsNormalizedReads() =>
        EfPostgreSqlMigrationTests.WithEmptyDatabase(async source =>
        {
            var content = new InMemoryContentStore(); var state = await LegacyStorageFixture.CreateAsync(content, "/schema5.docx", MinimalDocx.Create());
            state = (await LegacyStorageFixture.SaveAsync(state, content, MinimalDocx.Create("second"))).State;
            await LegacyPostgreSqlFixture.SeedAsync(source, content, [state], 5, expandedJson: false);
            await using (var remove = source.CreateCommand("DROP TABLE cellbridge_read_heads,cellbridge_revision_summaries")) await remove.ExecuteNonQueryAsync();
            var before = await SnapshotJson(source); var store = new PostgreSqlStateStore(source); await store.InitializeAsync();
            Assert.Equal(before, await SnapshotJson(source));
            var repository = (IDocumentRepository)store; Assert.Equal(2, (await repository.ReadRevisionsAsync(state.ResourceId, 0, 10))!.Revisions.Length);
            Assert.Equal(state.Content.Sha256, (await repository.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!.Package.Sha256);
        });
    [PostgreSqlFact]
    public Task FailedSchema5MigrationRollsBackVersionTablesAndOriginalJson() =>
        EfPostgreSqlMigrationTests.WithEmptyDatabase(async source =>
        {
            var content = new InMemoryContentStore(); var state = await LegacyStorageFixture.CreateAsync(content, "/schema5-invalid.docx", MinimalDocx.Create());
            await LegacyPostgreSqlFixture.SeedAsync(source, content, [state], 5, expandedJson: false);
            await using (var corrupt = source.CreateCommand("DROP TABLE cellbridge_read_heads,cellbridge_revision_summaries; UPDATE cellbridge_states SET state_json='not JSON'")) await corrupt.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<StorageCorruptionException>(() => new PostgreSqlStateStore(source).InitializeAsync());
            await using var check = source.CreateCommand("SELECT version=5 AND to_regclass('cellbridge_repository_heads') IS NULL AND to_regclass('cellbridge_read_heads') IS NULL FROM cellbridge_schema");
            Assert.Equal(true, await check.ExecuteScalarAsync()); Assert.Equal("not JSON", Assert.Single(await SnapshotJson(source)));
        });
    private static async Task<string[]> SnapshotJson(NpgsqlDataSource source)
    {
        var rows = new List<string>(); await using var command = source.CreateCommand("SELECT state_json FROM cellbridge_states ORDER BY resource_id,state_version");
        await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) rows.Add(reader.GetString(0)); return rows.ToArray();
    }
}
