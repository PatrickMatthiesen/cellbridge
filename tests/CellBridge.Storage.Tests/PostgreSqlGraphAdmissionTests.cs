using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class PostgreSqlGraphAdmissionTests
{
    [PostgreSqlFact]
    public Task LegacyMembershipBudgetFailureRollsBackAndExactBudgetPreservesHistory() =>
        EfPostgreSqlMigrationTests.WithEmptyDatabase(async source =>
        {
            var oldContent = new InMemoryContentStore();
            var state = await LegacyStorageFixture.CreateAsync(oldContent, "/membership-migration.docx", MinimalDocx.Create("original"));
            for (var index = 0; index < 3; index++) state = (await LegacyStorageFixture.SaveAsync(state, oldContent, MinimalDocx.Create("revision " + index))).State;
            await LegacyPostgreSqlFixture.SeedAsync(source, oldContent, [state], 5, expandedJson: false);
            var before = await AuthoritativeRows(source); var exact = StorageLimits.GetDecodedGraphMembershipBytes(state);
            var rejected = new PostgreSqlStorageMigrator(source, new() { MaxDecodedGraphMembershipBytes = exact - 1 });
            var error = await Assert.ThrowsAsync<StorageQuotaExceededException>(() => rejected.MigrateAsync());
            Assert.Equal("decoded graph membership bytes", error.Budget); Assert.Equal(before, await AuthoritativeRows(source));
            await using (var check = source.CreateCommand("SELECT version=5 AND to_regclass('cellbridge_repository_heads') IS NULL FROM cellbridge_schema")) Assert.Equal(true, await check.ExecuteScalarAsync());
            var limits = new StorageLimits { MaxDecodedGraphMembershipBytes = exact };
            await new PostgreSqlStorageMigrator(source, limits).MigrateAsync();
            IDocumentRepository store = new PostgreSqlStateStore(source, limits); var content = new PostgreSqlContentStore(source);
            var snapshot = (await store.ExportDocumentAsync(state.ResourceId))!; Assert.Equal(state.Revisions.Length, snapshot.ApplicationRevisions.Length);
            var provider = new StorageProvider(store, content, limits);
            foreach (var revision in snapshot.ApplicationRevisions)
                Assert.Equal(await oldContent.ReadVerifiedAsync(state.Revisions.Single(old => old.RevisionNumber == revision.RevisionNumber).Content),
                    await StorageTestDocuments.PackageAsync(provider, revision.Package));
            Assert.Equal(before, await AuthoritativeRows(source));
        });
    [PostgreSqlFact]
    public Task InvalidAndOverQuotaTypedPlansPreserveAllNormalizedPublicationRows() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var content = new PostgreSqlContentStore(source); IDocumentRepository store = new PostgreSqlStateStore(source);
            var provider = new StorageProvider(store, content);
            var head = (await new CellBridgeDocumentService(provider).CreateAsync("/admission.docx", MinimalDocx.Create(), TestActor.Value))!;
            var create = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), "/rejected.docx", MinimalDocx.Create("rejected"), head.Security, content, provider.Limits);
            var part = create.Partitions[0];
            var invalid = create with { Partitions = create.Partitions.SetItem(0, part with { AddedElements = [part.AddedElements[0], part.AddedElements[0]] }) };
            var before = await PublicationRows(source);
            var error = await Record.ExceptionAsync(() => store.CreateAsync(invalid).AsTask());
            Assert.True(error is StorageCorruptionException or InvalidDataException or ArgumentException, error?.ToString());
            Assert.Equal(before, await PublicationRows(source));
            IDocumentRepository restricted = new PostgreSqlStateStore(source, new() { MaxGraphElements = 1 });
            await Assert.ThrowsAsync<StorageQuotaExceededException>(() => restricted.CreateAsync(create).AsTask());
            Assert.Equal(before, await PublicationRows(source)); Assert.Null(await store.FindAsync(new DocumentIdentity.Resource(create.Head.ResourceId)));
            var publication = await PublicationTestPlans.NoopAsync(provider, head.ResourceId, "over-quota");
            await Assert.ThrowsAsync<StorageQuotaExceededException>(() => restricted.PublishAsync(publication).AsTask());
            Assert.Equal(before, await PublicationRows(source));
        });
    private static async Task<string> AuthoritativeRows(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("SELECT jsonb_build_object('documents',(SELECT jsonb_agg(to_jsonb(d) ORDER BY resource_id) FROM cellbridge_documents d),'snapshots',(SELECT jsonb_agg(to_jsonb(s) ORDER BY resource_id,state_version) FROM cellbridge_states s))::text");
        return (string)(await command.ExecuteScalarAsync())!;
    }
    private static async Task<string> PublicationRows(NpgsqlDataSource source)
    {
        var tables = new List<string>();
        await using (var command = source.CreateCommand("SELECT tablename FROM pg_tables WHERE schemaname='public' AND tablename LIKE 'cellbridge_repository_%' ORDER BY tablename"))
        await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        var result = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            var rows = new List<string>(); await using var command = source.CreateCommand($"SELECT to_jsonb(t)::text FROM \"{table}\" t ORDER BY to_jsonb(t)::text COLLATE \"C\"");
            await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) rows.Add(reader.GetString(0)); result.Add(table, rows);
        }
        return JsonSerializer.Serialize(result);
    }
}
