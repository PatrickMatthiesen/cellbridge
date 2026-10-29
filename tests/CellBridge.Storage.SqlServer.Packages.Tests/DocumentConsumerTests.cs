using CellBridge.AspNetCore;
using CellBridge.PackageChecks;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace CellBridge.Storage.SqlServer.Packages.Tests;

public sealed class DocumentConsumerTests(ITestOutputHelper output)
{
    private static DbContextOptions<SqlServerDocumentDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<SqlServerDocumentDbContext>().UseSqlServer(connection).Options;

    private static StorageProvider Provider(DbContextOptions<SqlServerDocumentDbContext> options)
    {
        var content = new SqlServerContentStore(options);
        return new(new SqlServerStateStore(options, validationContent: content), content);
    }

    [Fact]
    public void PackagedRegistrationNeedsNoDatabaseOrListsProvider()
    {
        var provider = Provider(Options("Server=unused.invalid;Database=unused;User ID=unused;Password=unused"));
        var services = new ServiceCollection();
        services.AddCellBridge(provider, multipleInstances: true);
        using var host = services.BuildServiceProvider();
        Assert.NotNull(host.GetRequiredService<CellBridgeDocumentService>());
        provider.Require(durable: true, multipleInstances: true);
    }

    [OwnedSqlServerFact]
    public async Task ExplicitMigrationAndDocumentContinuationAcrossIndependentProviders()
    {
        var database = new SqlServerPackageDatabase();
        await database.AssertEmptyAsync(output);
        var actor = new CellBridgeActor(new("package:writer", "writer", "Package writer"), CanCreate: true);
        var adminOptions = Options(database.Admin);
        var services = new ServiceCollection();
        services.AddCellBridge(Provider(adminOptions), multipleInstances: true);
        using (var host = services.BuildServiceProvider())
        {
            Assert.NotNull(host.GetRequiredService<CellBridgeDocumentService>());
            await Assert.ThrowsAsync<StorageUnavailableException>(async () =>
                await Provider(adminOptions).CheckHealthAsync());
            await Assert.ThrowsAsync<StorageUnavailableException>(async () =>
                await host.GetRequiredService<CellBridgeDocumentService>().CreateAsync("/before-migration.bin", [1], actor));
        }
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0"));

        var migrator = new SqlServerStorageMigrator(adminOptions);
        await migrator.MigrateAsync();
        await migrator.MigrateAsync();
        var normalizedGrants = string.Join('\n',
            new[] { "heads", "coordination", "editors", "partitions", "bindings", "paths", "deliveries", "references", "mappings" }
                .Select(table => $"GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_documents.[repository_{table}] TO [runtime];")
                .Concat(new[] { "elements", "graph", "binding_changes", "checkpoints", "revisions", "receipts", "deltas", "mapping_roots", "mapping_claims" }
                    .Select(table => $"GRANT SELECT,INSERT ON cellbridge_documents.[repository_{table}] TO [runtime];"))) + """

            GRANT SELECT,INSERT ON cellbridge_documents.repository_visibility TO [runtime];
            GRANT UPDATE (EndSequence) ON cellbridge_documents.repository_visibility TO [runtime];
            GRANT SELECT,INSERT ON cellbridge_documents.repository_mapping_intervals TO [runtime];
            GRANT UPDATE (EndSequence) ON cellbridge_documents.repository_mapping_intervals TO [runtime];
            """;
        var runtime = await database.RuntimeAsync("""
            GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_documents.documents TO [runtime];
            GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_documents.snapshots TO [runtime];
            GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_documents.metadata TO [runtime];
            GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_documents.revisions TO [runtime];
            GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_documents.reservations TO [runtime];
            GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_documents.recovery_receipts TO [runtime];
            GRANT SELECT,INSERT ON cellbridge_documents.objects TO [runtime];
            GRANT SELECT,INSERT ON cellbridge_documents.chunks TO [runtime];
            GRANT SELECT ON cellbridge_documents.schema_version TO [runtime];
            """ + normalizedGrants);
        var options = Options(runtime);
        var provider = Provider(options);
        await provider.CheckHealthAsync();
        var service = new CellBridgeDocumentService(provider);
        byte[] bytes = [1, 2, 3, 4, 255];
        var created = (await service.CreateAsync(Guid.NewGuid(), "/shared/package.bin", bytes, actor))!;
        Assert.NotNull(created);
        var tables = await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0");

        var fresh = Provider(Options(runtime));
        var renamed = await new CellBridgeDocumentService(fresh).RenameDocumentAsync(created.ResourceId, "continued.bin", actor);
        Assert.True(renamed.StateVersion > created.StateVersion);
        await migrator.MigrateAsync();
        var restarted = Provider(Options(runtime));
        var reloaded = (await restarted.Documents.FindAsync(new DocumentIdentity.Resource(created.ResourceId)))!;
        Assert.Equal("/shared/continued.bin", reloaded.Path);
        Assert.Equal(renamed.StateVersion, reloaded.StateVersion);
        await using (var package = await restarted.Packages.OpenReadAsync(reloaded.Package))
        {
            using var downloaded = new MemoryStream();
            await package.CopyToAsync(downloaded);
            Assert.Equal(bytes, downloaded.ToArray());
        }
        Assert.Equal(created.Security.Owner, reloaded.Security.Owner);
        Assert.Single(await restarted.Documents.ListAsync(0, 10));
        await using var restricted = new SqlServerDocumentDbContext(options);
        await Assert.ThrowsAsync<SqlException>(() => restricted.Database.ExecuteSqlRawAsync("CREATE TABLE cellbridge_documents.forbidden(id int)"));
        Assert.Equal(tables, await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0"));
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.schemas WHERE name='cellbridge_lists'"));
        output.WriteLine("Explicit empty initialization, restricted runtime create, rename and fresh-provider content/state continuation passed.");
    }
}
