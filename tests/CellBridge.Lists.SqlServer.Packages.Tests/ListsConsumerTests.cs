using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.SqlServer;
using CellBridge.PackageChecks;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace CellBridge.Lists.SqlServer.Packages.Tests;

public sealed class ListsConsumerTests(ITestOutputHelper output)
{
    private static DbContextOptions<SqlServerListDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<SqlServerListDbContext>().UseSqlServer(connection).Options;

    [Fact]
    public void PackagedEngineNeedsNoDatabaseOrDocumentProvider()
    {
        var options = Options("Server=unused.invalid;Database=unused;User ID=unused;Password=unused");
        Assert.IsAssignableFrom<IListEngine>(new SqlServerListEngine(options));
        Assert.IsAssignableFrom<IPagedListEngine>(new SqlServerListEngine(options));
        Assert.NotNull(new SqlServerListMigrator(options));
    }

    [OwnedSqlServerFact]
    public async Task ExplicitMigrationAndNativeListsCreateReadQueryAndRetry()
    {
        var database = new SqlServerPackageDatabase();
        await database.AssertEmptyAsync(output);
        var adminOptions = Options(database.Admin);
        var uninitialized = new SqlServerListEngine(adminOptions);
        var failure = await Assert.ThrowsAsync<ListException>(() => uninitialized.CheckHealthAsync());
        Assert.Equal(ListFailure.Unavailable, failure.Failure);
        var beforeMigration = await Assert.ThrowsAsync<ListException>(() => uninitialized.CreateListAsync(
            new("package:writer"), Guid.NewGuid(), new(Guid.NewGuid(), "Before migration", [])));
        Assert.Equal(ListFailure.Unavailable, beforeMigration.Failure);
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0"));

        var migrator = new SqlServerListMigrator(adminOptions);
        await migrator.MigrateAsync();
        await migrator.MigrateAsync();
        var actor = new ListActor("package:writer");
        var workspace = Guid.NewGuid();
        await SqlServerListSetup.RegisterWorkspaceAsync(adminOptions, workspace, "Package consumer", actor.Subject);
        var runtime = await database.RuntimeAsync("""
            GRANT VIEW DEFINITION ON SCHEMA::cellbridge_lists TO [runtime];
            GRANT SELECT ON cellbridge_lists.schema_version TO [runtime];
            GRANT SELECT ON cellbridge_lists.templates TO [runtime];
            GRANT SELECT ON cellbridge_lists.__CellBridgeListsMigrationsHistory TO [runtime];
            GRANT SELECT,UPDATE ON cellbridge_lists.workspaces TO [runtime];
            GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_lists.memberships TO [runtime];
            GRANT SELECT,INSERT,UPDATE ON cellbridge_lists.lists TO [runtime];
            GRANT SELECT,INSERT ON cellbridge_lists.list_schemas TO [runtime];
            GRANT SELECT,INSERT,UPDATE ON cellbridge_lists.list_counters TO [runtime];
            GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_lists.list_grants TO [runtime];
            GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_lists.records TO [runtime];
            GRANT SELECT,INSERT ON cellbridge_lists.record_tombstones TO [runtime];
            GRANT SELECT,INSERT,DELETE ON cellbridge_lists.create_receipts TO [runtime];
            GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_lists.record_references TO [runtime];
            GRANT SELECT,INSERT,UPDATE ON cellbridge_lists.saved_views TO [runtime];
            GRANT SELECT,INSERT ON cellbridge_lists.audit_events TO [runtime];
            GRANT EXECUTE ON cellbridge_lists.scalar_key TO [runtime];
            GRANT EXECUTE ON cellbridge_lists.uuid_key TO [runtime];
            GRANT EXECUTE ON cellbridge_lists.key_compare TO [runtime];
            """);
        var options = Options(runtime);
        var engine = new SqlServerListEngine(options);
        await engine.CheckHealthAsync();
        var field = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        var list = await engine.CreateListAsync(actor, workspace, new(Guid.NewGuid(), "Tasks", [field]));
        var request = new CreateRecordRequest(Guid.NewGuid(), list.SchemaVersion,
            new JsonObject { [field.Id.ToString("D")] = "Package-only record " });
        var created = await engine.CreateRecordAsync(actor, workspace, list.Id, request);
        Assert.False(created.Replayed);
        var tables = await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0");

        var fresh = new SqlServerListEngine(Options(runtime));
        var record = await fresh.GetRecordAsync(actor, workspace, list.Id, request.Id);
        Assert.True(JsonNode.DeepEquals(request.Values, record.Values));
        var retry = await fresh.CreateRecordAsync(actor, workspace, list.Id, request);
        Assert.Equal(created with { Replayed = true }, retry);
        var page = await fresh.QueryAsync(actor, workspace, list.Id,
            new([new(field.Id, FilterOperator.Equal, JsonValue.Create("Package-only record "))]));
        Assert.Equal(record.Id, Assert.Single(page.Records).Id);
        Assert.Single(await fresh.GetListsAsync(actor, workspace));
        await migrator.MigrateAsync();
        await new SqlServerListEngine(Options(runtime)).CheckHealthAsync();
        await using var restricted = new SqlServerListDbContext(options);
        await Assert.ThrowsAsync<SqlException>(() => restricted.Database.ExecuteSqlRawAsync("CREATE TABLE cellbridge_lists.forbidden(id int)"));
        Assert.Equal(tables, await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0"));
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.schemas WHERE name='cellbridge_documents'"));
        output.WriteLine("Explicit empty initialization, restricted runtime native Lists create/read/exact query/retry passed.");
    }
}
