using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.Storage.FileSystem;
using CellBridge.Tests;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class NormalizedPostgreSqlProviderTests
{
    [PostgreSqlFact]
    public async Task FreshSchemaMatchesModelAndTypedSaveHistoryOutboxRecoveryRoundTrip()
    {
        var admin = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!;
        await using var source = NpgsqlDataSource.Create(await CreateDatabaseAsync(admin));
        var content = new PostgreSqlContentStore(source);
        var state = new PostgreSqlStateStore(source, validationContent: content);
        await state.InitializeAsync();
        await ((IDocumentRepository)state).CheckHealthAsync();
        await using (var context = new CellBridgeDbContext(new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source).Options))
            await NormalizedRelationalSchemaAssertions.VerifyAsync(context, sqlServer: false);
        var snapshot = await NormalizedProviderScenarios.CreateSaveMetadataRestoreAsync(new(state, content));
        await NormalizedProviderScenarios.OutboxBlocksLifecycleAsync(new(state, content));
        await NormalizedProviderScenarios.AcceptedPublicationExpiresControlsAtomicallyAsync(new(state, content),
            limits => new(new PostgreSqlStateStore(source, limits, content), content, limits));
        await NormalizedProviderScenarios.LimitsCancellationAndIndependentDocumentsAsync(new(state, content),
            limits => new(new PostgreSqlStateStore(source, limits, content), content, limits),
            interceptors => new(new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source)
                .AddInterceptors(interceptors).Options, validationContent: content), content));
        await NormalizedProviderScenarios.PhysicalCreationAsync(interceptors => new PostgreSqlContentStore(
            new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source).AddInterceptors(interceptors).Options), async () =>
        {
            await using var command = source.CreateCommand("SELECT ROW((SELECT count(*) FROM cellbridge_objects),(SELECT count(*) FROM cellbridge_chunks),(SELECT COALESCE(sum(length),0) FROM cellbridge_objects))::text");
            return (string)(await command.ExecuteScalarAsync())!;
        });
        await using var targetSource = NpgsqlDataSource.Create(await CreateDatabaseAsync(admin));
        var targetContent = new PostgreSqlContentStore(targetSource);
        var targetState = new PostgreSqlStateStore(targetSource, validationContent: targetContent);
        await targetState.InitializeAsync();
        await NormalizedProviderScenarios.CopyReferencesAsync(snapshot, content, targetContent);
        await NormalizedProviderScenarios.RecoverAsync(snapshot, new(targetState, targetContent));
    }
    [PostgreSqlFact]
    public Task Schema8UpgradePreservesLegacySnapshotsHistoryReceiptsLeasesAndLedgerAndRollsBackRejectedMigration() => UpgradeLegacyAsync(false);
    [PostgreSqlFact]
    public Task Schema8UpgradePreservesRenameBackClaimsAndRecoveryAccounting() => UpgradeLegacyAsync(true);
    private async Task UpgradeLegacyAsync(bool renameBack)
    {
        var admin = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!;
        await using var source = NpgsqlDataSource.Create(await CreateDatabaseAsync(admin));
        var options = new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source,
            pg => pg.MigrationsHistoryTable("__CellBridgeDocumentMigrationsHistory")).Options;
        await using (var context = new CellBridgeDbContext(options))
            await context.GetService<IMigrator>().MigrateAsync("20261028000200_RemoveSharedQuotas");
        var oldContent = new NormalizedLegacyDatabase.Content();
        var legacy = await NormalizedLegacyFixture.CreateAsync(oldContent, renameBack);
        string original;
        await using (var context = new CellBridgeDbContext(options))
        {
            await NormalizedLegacyDatabase.SeedAsync(context, false, legacy, oldContent);
            original = await NormalizedLegacyDatabase.LegacyRowsAsync(context, false);
        }
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => new PostgreSqlStorageMigrator(options, new() { MaxDocumentBytes = 1 }).MigrateAsync());
        await using (var command = source.CreateCommand("SELECT version=8 AND to_regclass('cellbridge_repository_heads') IS NULL FROM cellbridge_schema"))
            Assert.Equal(true, await command.ExecuteScalarAsync());
        await new PostgreSqlStorageMigrator(options).MigrateAsync();
        var content = new PostgreSqlContentStore(options);
        IDocumentRepository repository = new PostgreSqlStateStore(options, validationContent: content);
        await NormalizedLegacyFixture.VerifyAsync(new(repository, content), oldContent, legacy);
        Assert.Equal(NormalizedLegacyDatabase.OldReceipt, await repository.FindRecoveryReceiptAsync(NormalizedLegacyDatabase.OldReceipt.OperationId));
        await using (var context = new CellBridgeDbContext(options))
            Assert.Equal(original, await NormalizedLegacyDatabase.LegacyRowsAsync(context, false));
        await new PostgreSqlStorageMigrator(options).MigrateAsync();
    }
    private static async Task<string> CreateDatabaseAsync(string connectionString)
    {
        var name = "cb_normalized_" + Guid.NewGuid().ToString("N");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var command = source.CreateCommand("CREATE DATABASE " + name);
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(connectionString) { Database = name }.ConnectionString;
    }
    [PostgreSqlFact]
    public async Task FilesystemUpgradeRequiresItsExplicitReaderAndRollsBackBeforeRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "cellbridge-normalized-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            var admin = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!;
            await using var source = NpgsqlDataSource.Create(await CreateDatabaseAsync(admin));
            var options = new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source,
                pg => pg.MigrationsHistoryTable("__CellBridgeDocumentMigrationsHistory")).Options;
            await using (var context = new CellBridgeDbContext(options))
                await context.GetService<IMigrator>().MigrateAsync("20261028000200_RemoveSharedQuotas");
            var files = new FileSystemContentStore(root);
            var legacy = await NormalizedLegacyFixture.CreateAsync(files);
            await using (var context = new CellBridgeDbContext(options))
                await NormalizedLegacyDatabase.SeedAsync(context, false, legacy, files, storeChunks: false);
            var error = await Assert.ThrowsAsync<StorageUnavailableException>(() => new PostgreSqlStorageMigrator(options).MigrateAsync());
            Assert.Contains("filesystem content reader", error.Message);
            await using (var command = source.CreateCommand("SELECT version=8 AND to_regclass('cellbridge_repository_heads') IS NULL FROM cellbridge_schema"))
                Assert.Equal(true, await command.ExecuteScalarAsync());
            await new PostgreSqlStorageMigrator(options, migrationContent: files).MigrateAsync();
            var content = new PostgreSqlFileSystemContentStore(options, root);
            IDocumentRepository repository = new PostgreSqlStateStore(options, validationContent: content);
            await NormalizedLegacyFixture.VerifyAsync(new(repository, content), files, legacy);
            await NormalizedProviderScenarios.CreateSaveMetadataRestoreAsync(new(repository, content));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
    [PostgreSqlFact]
    public async Task TerminatingAProcessBeforeDuringAndAfterRealPublicationPreservesAtomicFileAndMetadataResults()
    {
        var admin = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!;
        var connection = await CreateDatabaseAsync(admin);
        await using (var source = NpgsqlDataSource.Create(connection)) await new PostgreSqlStorageMigrator(source).MigrateAsync();
        var root = Path.Combine(Path.GetTempPath(), "cellbridge-normalized-probes-" + Guid.NewGuid().ToString("N"));
        try
        {
            await NormalizedProcessProbeScenario.RunAsync(connection,
                Path.Combine(AppContext.BaseDirectory, "Probe", "CellBridge.Storage.ProcessProbe.dll"),
                Path.Combine(AppContext.BaseDirectory, "Fixtures"), root);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
    [PostgreSqlFact]
    public async Task QuiescentCollectorRejectsMissingCanonicalReferenceRowsBeforeDeletingObjects()
    {
        var admin = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!;
        await using var source = NpgsqlDataSource.Create(await CreateDatabaseAsync(admin));
        await new PostgreSqlStorageMigrator(source).MigrateAsync();
        var content = new PostgreSqlContentStore(source); IDocumentRepository repository = new PostgreSqlStateStore(source);
        var provider = new StorageProvider(repository, content); var bytes = MinimalDocx.Create("collector roots");
        var head = (await new CellBridge.AspNetCore.CellBridgeDocumentService(provider).CreateAsync("/collector.docx", bytes, TestActor.Value))!;
        var snapshot = (await repository.ExportDocumentAsync(head.ResourceId))!; var reference = snapshot.ContentReferences[0];
        using var input = new MemoryStream([91, 92, 93]); var orphan = await content.WriteAsync(input);
        await using (var remove = source.CreateCommand("DELETE FROM cellbridge_repository_references WHERE \"ResourceId\"=$1 AND \"ObjectKey\"=$2"))
        { remove.Parameters.AddWithValue(head.ResourceId); remove.Parameters.AddWithValue(reference.Handle.Key); await remove.ExecuteNonQueryAsync(); }
        var maintenance = new StorageMaintenance(source);
        await Assert.ThrowsAsync<StorageCorruptionException>(() => maintenance.CollectOrphansAsync());
        await Assert.ThrowsAsync<StorageCorruptionException>(() => maintenance.CollectOrphansAsync(apply: true, quiescent: true));
        Assert.Equal(new byte[] { 91, 92, 93 }, await content.ReadVerifiedAsync(orphan));
        Assert.Equal(reference.Handle.Length, (await content.ReadVerifiedAsync(reference.Handle)).LongLength);
        await using (var restore = source.CreateCommand("INSERT INTO cellbridge_repository_references (\"ResourceId\",\"ObjectKey\",\"Length\",\"Sha256\",\"ReferenceCount\") VALUES ($1,$2,$3,$4,$5)"))
        {
            restore.Parameters.AddWithValue(head.ResourceId); restore.Parameters.AddWithValue(reference.Handle.Key); restore.Parameters.AddWithValue(reference.Handle.Length);
            restore.Parameters.AddWithValue(reference.Handle.Sha256); restore.Parameters.AddWithValue(reference.ReferenceCount); await restore.ExecuteNonQueryAsync();
        }
        Assert.Equal(1, (await maintenance.CollectOrphansAsync(apply: true, quiescent: true)).OrphanObjects);
        Assert.Equal(bytes, await StorageTestDocuments.PackageAsync(provider, head.Package));
    }
}
