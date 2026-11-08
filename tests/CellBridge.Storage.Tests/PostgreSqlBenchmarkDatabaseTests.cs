using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class PostgreSqlBenchmarkDatabaseTests
{
    [PostgreSqlFact]
    public Task RepeatedSetupResetsDocumentIdentityHistoryAndContent() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge"))
            { Database = new NpgsqlConnectionStringBuilder(source.ConnectionString).Database };
            var initial = MinimalDocx.Create("initial");
            var saved = MinimalDocx.Create("saved");
            Guid? previousId = null;
            for (var iteration = 0; iteration < 2; iteration++)
            {
                await using var database = await BenchmarkDatabase.OpenAsync(connection.ConnectionString, new());
                var provider = new StorageProvider(new PostgreSqlStateStore(database.Source), new PostgreSqlContentStore(database.Source));
                await provider.Documents.CheckHealthAsync();
                if (previousId is { } id)
                    Assert.Null(await provider.Documents.FindAsync(new DocumentIdentity.Resource(id, IncludeDeleted: true)));
                var service = new CellBridgeDocumentService(provider);
                var head = await service.CreateAsync("/benchmark-0.docx", initial, TestActor.Value);
                Assert.NotNull(head);
                Assert.Equal(1, head.Counters.Revisions);
                Assert.Equal(0, head.Counters.SaveReceipts);
                Assert.Equal(initial, await StorageTestDocuments.PackageAsync(provider, head.Package));

                var request = await StorageTestDocuments.SaveRequestAsync(service, head, saved);
                var result = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents,
                    request, new Dictionary<string, string>(), TestActor.Value);
                Assert.False(result.IsCancelled);
                Assert.False(result.Response.Status);
                Assert.False(Assert.Single(result.Response.SubResponses).Status);
                Assert.False(Assert.Single(result.AcceptedSaves).IsReplay);
                Assert.Equal(2, result.State.Counters.Revisions);
                Assert.Equal(1, result.State.Counters.SaveReceipts);
                Assert.Equal(saved, await StorageTestDocuments.PackageAsync(provider, result.State.Package));
                Assert.Equal(saved, (await StorageTestDocuments.GraphAsync(service, result.State)).Materialize());
                previousId = head.ResourceId;
            }
        });
}
