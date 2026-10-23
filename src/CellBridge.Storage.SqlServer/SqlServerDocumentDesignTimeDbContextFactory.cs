using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CellBridge.Storage.SqlServer;

public sealed class SqlServerDocumentDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SqlServerDocumentDbContext>
{
    public SqlServerDocumentDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<SqlServerDocumentDbContext>()
            .UseSqlServer("Server=localhost;Database=cellbridge_design;Integrated Security=true;TrustServerCertificate=true",
                sql => sql.MigrationsHistoryTable(SqlServerDocumentDbContext.MigrationsHistoryTable, SqlServerDocumentDbContext.Schema))
            .Options);
}
