using CellBridge.Tests;

namespace CellBridge.SqlServer.Tests;

[Collection("SQL Server")]
public sealed class SqlServerPublicationPrefetchTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task BoundedExactKeysPreservePublicationChecksAndAccounting()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        await PublicationPrefetchScenario.RunAsync(database.Provider(), interceptors => database.Provider(interceptors));
    }
}
