using CellBridge.Storage.Abstractions;
using CellBridge.Storage.SqlServer;
using CellBridge.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CellBridge.SqlServer.Tests;

[Collection("SQL Server")]
public sealed class NormalizedSqlServerProviderTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task ReadPartitionKeepsCatalogAndMappingsOnOneCoherentSnapshot()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        await CoherentPartitionMappingScenario.RunAsync(database.Provider(), interceptors => database.Provider(interceptors).Documents);
    }

    [Fact]
    public async Task ResourceLookupReadsOneCoherentHeadWithoutAnExplicitTransaction()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        await ImmutableIndexedReadScenario.RunHeadReadAsync(database.Provider(), interceptors =>
            new SqlServerStateStore(new DbContextOptionsBuilder<SqlServerDocumentDbContext>(database.Options(false, interceptors))
                .UseSqlServer(sql => sql.CommandTimeout(42)).Options));
    }

    [Fact]
    public async Task ExactImmutablePointReadsUseNoExplicitTransactionAndPreserveScopeTimeoutAndCancellation()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        await ImmutableIndexedReadScenario.RunAsync(database.Provider(), interceptors =>
            new SqlServerStateStore(new DbContextOptionsBuilder<SqlServerDocumentDbContext>(database.Options(false, interceptors))
                .UseSqlServer(sql => sql.CommandTimeout(42)).Options));
    }

    [Fact]
    public async Task FreshSchemaMatchesModelAndTypedSaveHistoryOutboxRecoveryRoundTrip()
    {
        var options = new DbContextOptionsBuilder<SqlServerDocumentDbContext>().UseSqlServer(await fixture.CreateDatabaseAsync()).Options;
        await new SqlServerStorageMigrator(options).MigrateAsync();
        var content = new SqlServerContentStore(options);
        var state = new SqlServerStateStore(options, validationContent: content);
        await ((IDocumentRepository)state).CheckHealthAsync();
        await using (var context = new SqlServerDocumentDbContext(options))
            await NormalizedRelationalSchemaAssertions.VerifyAsync(context, sqlServer: true);
        var snapshot = await NormalizedProviderScenarios.CreateSaveMetadataRestoreAsync(new(state, content));
        await NormalizedProviderScenarios.OutboxBlocksLifecycleAsync(new(state, content));
        await NormalizedProviderScenarios.AcceptedPublicationExpiresControlsAtomicallyAsync(new(state, content),
            limits => new(new SqlServerStateStore(options, limits, content), content, limits));
        await NormalizedProviderScenarios.LimitsCancellationAndIndependentDocumentsAsync(new(state, content),
            limits => new(new SqlServerStateStore(options, limits, content), content, limits),
            interceptors => new(new SqlServerStateStore(new DbContextOptionsBuilder<SqlServerDocumentDbContext>(options)
                .AddInterceptors(interceptors).Options, validationContent: content), content));
        await NormalizedProviderScenarios.PhysicalCreationAsync(interceptors => new SqlServerContentStore(
            new DbContextOptionsBuilder<SqlServerDocumentDbContext>(options).AddInterceptors(interceptors).Options), async () =>
        {
            await using var context = new SqlServerDocumentDbContext(options);
            return string.Join(",", await context.Database.SqlQueryRaw<long>("SELECT COUNT_BIG(*) AS Value FROM cellbridge_documents.objects UNION ALL SELECT COUNT_BIG(*) FROM cellbridge_documents.chunks UNION ALL SELECT COALESCE(SUM(Length),0) FROM cellbridge_documents.objects").ToArrayAsync());
        });
        var targetOptions = new DbContextOptionsBuilder<SqlServerDocumentDbContext>().UseSqlServer(await fixture.CreateDatabaseAsync()).Options;
        await new SqlServerStorageMigrator(targetOptions).MigrateAsync();
        var targetContent = new SqlServerContentStore(targetOptions);
        await NormalizedProviderScenarios.CopyReferencesAsync(snapshot, content, targetContent);
        await NormalizedProviderScenarios.RecoverAsync(snapshot, new(new SqlServerStateStore(targetOptions, validationContent: targetContent), targetContent));
    }
    [Fact]
    public Task Schema3UpgradePreservesLegacySnapshotsHistoryReceiptsLeasesAndLedgerAndRollsBackRejectedMigration() => UpgradeLegacyAsync(false);
    [Fact]
    public Task Schema3UpgradePreservesRenameBackClaimsAndRecoveryAccounting() => UpgradeLegacyAsync(true);
    [Fact]
    public Task Schema3UpgradeReplaysCurrentMetadataReceiptAndRejectsSupersededReceipt() => UpgradeLegacyAsync(false, true);
    private async Task UpgradeLegacyAsync(bool renameBack, bool metadataReceipts = false)
    {
        var options = new DbContextOptionsBuilder<SqlServerDocumentDbContext>().UseSqlServer(await fixture.CreateDatabaseAsync(),
            sql => sql.MigrationsHistoryTable(SqlServerDocumentDbContext.MigrationsHistoryTable, SqlServerDocumentDbContext.Schema)).Options;
        await using (var context = new SqlServerDocumentDbContext(options))
            await context.GetService<IMigrator>().MigrateAsync("20261028130000_RemoveSharedQuotas");
        var oldContent = new NormalizedLegacyDatabase.Content();
        var legacy = await NormalizedLegacyFixture.CreateAsync(oldContent, renameBack, metadataReceipts);
        string original;
        await using (var context = new SqlServerDocumentDbContext(options))
        {
            await NormalizedLegacyDatabase.SeedAsync(context, true, legacy, oldContent);
            original = await NormalizedLegacyDatabase.LegacyRowsAsync(context, true);
        }
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => new SqlServerStorageMigrator(options, new() { MaxDocumentBytes = 1 }).MigrateAsync());
        await using (var context = new SqlServerDocumentDbContext(options))
            Assert.Equal(1, await context.Database.SqlQueryRaw<int>("SELECT CASE WHEN Version=3 AND OBJECT_ID('cellbridge_documents.repository_heads') IS NULL THEN 1 ELSE 0 END AS Value FROM cellbridge_documents.schema_version").SingleAsync());
        await new SqlServerStorageMigrator(options).MigrateAsync();
        var content = new SqlServerContentStore(options);
        IDocumentRepository repository = new SqlServerStateStore(options, validationContent: content);
        await NormalizedLegacyFixture.VerifyAsync(new(repository, content), oldContent, legacy);
        Assert.Equal(NormalizedLegacyDatabase.OldReceipt, await repository.FindRecoveryReceiptAsync(NormalizedLegacyDatabase.OldReceipt.OperationId));
        await using (var context = new SqlServerDocumentDbContext(options))
            Assert.Equal(original, await NormalizedLegacyDatabase.LegacyRowsAsync(context, true));
        await new SqlServerStorageMigrator(options).MigrateAsync();
    }
}
