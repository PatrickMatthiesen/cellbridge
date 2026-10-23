using CellBridge.Lists.SqlServer;
using CellBridge.Lists.SqlServer.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CellBridge.SqlServer.Tests;

[Collection("SQL Server")]
public sealed class SqlServerListsMigrationTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task NullableFoundationUpgradePreservesRowsAndFrozenSecurityTargetModel()
    {
        var connection = await fixture.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<SqlServerListDbContext>().UseSqlServer(connection,
            sql => sql.MigrationsHistoryTable(SqlServerListDbContext.MigrationsHistoryTable, SqlServerListDbContext.Schema)).Options;
        await using var db = new SqlServerListDbContext(options);
        var initial = new InitialListsStorage();
        // Reproduce the reviewed foundation's actual nullable DDL, using the
        // library's original operations rather than a second handwritten schema.
        foreach (var table in initial.UpOperations.OfType<CreateTableOperation>().Where(table => table.Name is "memberships" or "list_grants"))
            table.Columns.Single(column => column.Name == "subject").IsNullable = true;
        var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var migration in new Migration[] { initial, new WorkspaceIsolation() })
            foreach (var command in generator.Generate(migration.UpOperations, db.Model))
                await db.Database.ExecuteSqlRawAsync(command.CommandText);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE cellbridge_lists.__CellBridgeListsMigrationsHistory (
                MigrationId nvarchar(150) NOT NULL PRIMARY KEY, ProductVersion nvarchar(32) NOT NULL);
            INSERT cellbridge_lists.__CellBridgeListsMigrationsHistory VALUES
                ('20261023000001_InitialListsStorage','10.0.13'),('20261023000002_WorkspaceIsolation','10.0.13');
            """);
        var id = Guid.NewGuid();
        await using (var scope = await SqlServerListWorkspaceTransaction.OpenAsync(options, id))
        {
            scope.Context.Workspaces.Add(new() { WorkspaceId = id, Name = "preserve legacy rows" });
            await scope.Context.SaveChangesAsync();
            scope.Context.Memberships.Add(new() { WorkspaceId = id, Subject = "legacy actor " });
            await scope.Context.SaveChangesAsync();
            await scope.CommitAsync();
        }
        await new SqlServerListMigrator(options).MigrateAsync();
        await new SqlServerListMigrator(options).MigrateAsync();
        await using (var scope = await SqlServerListWorkspaceTransaction.OpenAsync(options, id))
        {
            Assert.Equal("preserve legacy rows", (await scope.Context.Workspaces.SingleAsync()).Name);
            Assert.Equal("legacy actor ", (await scope.Context.Memberships.SingleAsync()).Subject);
        }
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS Value FROM sys.columns WHERE object_id IN
                (OBJECT_ID('cellbridge_lists.memberships'),OBJECT_ID('cellbridge_lists.list_grants'))
                AND name='subject' AND is_nullable=1
            """).SingleAsync());
        Assert.Equal(db.Model.GetEntityTypes().Count(), new WorkspaceIsolation().TargetModel.GetEntityTypes().Count());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
