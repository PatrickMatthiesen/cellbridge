using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CellBridge.Storage.PostgreSql;

internal sealed class CellBridgeDesignTimeDbContextFactory : IDesignTimeDbContextFactory<CellBridgeDbContext>
{
    public CellBridgeDbContext CreateDbContext(string[] args) => new(
        PostgreSqlDatabase.NormalizeOptions(new DbContextOptionsBuilder<CellBridgeDbContext>()
            .UseNpgsql("Host=localhost;Database=cellbridge_document_design")
            .Options));
}
