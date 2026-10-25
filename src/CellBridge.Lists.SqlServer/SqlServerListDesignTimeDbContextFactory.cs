using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CellBridge.Lists.SqlServer;

public sealed class SqlServerListDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SqlServerListDbContext>
{
    public SqlServerListDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<SqlServerListDbContext>()
            .UseSqlServer("Server=localhost;Database=cellbridge_design;Integrated Security=true;TrustServerCertificate=true",
                sql => sql.MigrationsHistoryTable(SqlServerListDbContext.MigrationsHistoryTable, SqlServerListDbContext.Schema))
            .Options);
}
