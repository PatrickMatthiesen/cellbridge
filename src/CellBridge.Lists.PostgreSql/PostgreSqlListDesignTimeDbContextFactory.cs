using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CellBridge.Lists.PostgreSql;

internal sealed class PostgreSqlListDesignTimeDbContextFactory : IDesignTimeDbContextFactory<PostgreSqlListDbContext>
{
    public PostgreSqlListDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlListDbContext>()
            .UseNpgsql("Host=localhost;Database=cellbridge_lists_design",
                provider => provider.MigrationsHistoryTable(PostgreSqlListSetup.MigrationsHistoryTable,
                    PostgreSqlListSetup.SchemaName))
            .Options;
        return new PostgreSqlListDbContext(options);
    }
}
