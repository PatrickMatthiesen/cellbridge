using CellBridge.Storage.PostgreSql;
using CellBridge.Tests;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class PostgreSqlPublicationPrefetchTests
{
    [PostgreSqlFact]
    public Task BoundedExactKeysPreservePublicationChecksAndAccounting() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var content = new PostgreSqlContentStore(source);
            await PublicationPrefetchScenario.RunAsync(new(new PostgreSqlStateStore(source, validationContent: content), content), interceptors =>
                new(new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source)
                    .AddInterceptors(interceptors).Options, validationContent: content), content));
        });
}
