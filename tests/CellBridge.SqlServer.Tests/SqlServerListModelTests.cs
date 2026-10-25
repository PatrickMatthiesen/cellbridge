using CellBridge.Lists.SqlServer;
using CellBridge.Lists.SqlServer.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CellBridge.SqlServer.Tests;

public sealed class SqlServerListModelTests
{
    [Fact]
    public void InitialMigrationMatchesTheModelAndRequiresIdentitySubjects()
    {
        using var db = new SqlServerListDbContext(new DbContextOptionsBuilder<SqlServerListDbContext>()
            .UseSqlServer("Server=localhost;Database=model_inspection;Integrated Security=true").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var model = db.GetService<IDesignTimeModel>().Model;
        var migrationModel = new InitialListsStorage().TargetModel;
        Assert.Equal(model.GetEntityTypes().Select(entity => entity.Name).Order(),
            migrationModel.GetEntityTypes().Select(entity => entity.Name).Order());
        foreach (var table in new[] { "memberships", "list_grants" })
        {
            var entity = migrationModel.GetEntityTypes().Single(entity => entity.GetTableName() == table);
            Assert.False(entity.GetProperties().Single(property => property.GetColumnName(
                StoreObjectIdentifier.Table(table, SqlServerListDbContext.Schema)) == "subject").IsNullable);
        }
    }
}
