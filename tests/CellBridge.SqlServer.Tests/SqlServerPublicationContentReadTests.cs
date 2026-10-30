using CellBridge.Storage.SqlServer;
using CellBridge.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;

namespace CellBridge.SqlServer.Tests;

[Collection("SQL Server")]
public sealed class SqlServerPublicationContentReadTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task ActualPairedDatabaseReaderUsesOnePoolSlotForCreatePutAndCancellation()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        var single = new SqlConnectionStringBuilder(database.Runtime) { MaxPoolSize = 1, MinPoolSize = 0 }.ConnectionString;
        await PublicationContentReadScenario.SingleSlotAsync(interceptors =>
        {
            var options = new DbContextOptionsBuilder<SqlServerDocumentDbContext>().UseSqlServer(single).AddInterceptors(interceptors).Options;
            var content = new SqlServerContentStore(options);
            return new(new SqlServerStateStore(options, validationContent: content), content);
        });
    }

    [Fact]
    public async Task PublicationValidationUsesBoundedVerifiedPhysicalQueriesAndPreservesCancellation()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        await PublicationContentReadScenario.RunAsync(database.Provider().Content, (interceptors, external) =>
            new SqlServerStateStore(new DbContextOptionsBuilder<SqlServerDocumentDbContext>(database.Options(false, interceptors))
                .UseSqlServer(sql => sql.CommandTimeout(45)).Options, validationContent: external), async handle =>
        {
            await using var admin = new SqlServerDocumentDbContext(database.Options(admin: true));
            await admin.Database.ExecuteSqlAsync($"DELETE FROM cellbridge_documents.chunks WHERE [Key]={handle.Key}");
        });
    }
}
