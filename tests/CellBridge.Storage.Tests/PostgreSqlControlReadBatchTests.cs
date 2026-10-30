using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.Tests;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class PostgreSqlControlReadBatchTests
{
    [PostgreSqlFact]
    public Task CoherentBatchPreservesControlRowsReusesPartitionsAndGuardedClock() => BudgetAndQueryTests.WithDatabase(new(), async source =>
    {
        var content = new PostgreSqlContentStore(source);
        await ControlReadBatchScenario.RunAsync(new(new PostgreSqlStateStore(source), content), interceptors =>
            new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source).AddInterceptors(interceptors).Options));
    });
}
