using CellBridge.Tests;

namespace CellBridge.SqlServer.Tests;

[Collection("SQL Server")]
public sealed class SqlServerControlReadBatchTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task CoherentBatchPreservesControlRowsReusesPartitionsAndGuardedClock()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        await ControlReadBatchScenario.RunAsync(database.Provider(), interceptors => database.Provider(interceptors).Documents);
    }
}
