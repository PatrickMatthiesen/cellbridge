using CellBridge.Tests;
using Xunit.Abstractions;

namespace CellBridge.SqlServer.Tests;

[Collection("SQL Server")]
public sealed class SqlServerCurrentMetadataReadTests(SqlServerFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task PreparationAndPublicationFenceReuseHeadersAndBatchOnlyAffectedCurrentMappings()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        await CurrentMetadataReadScenario.RunAsync(database.Provider(), interceptors => database.Provider(interceptors).Documents, output.WriteLine);
    }
}
