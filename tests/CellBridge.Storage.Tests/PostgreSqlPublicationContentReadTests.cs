using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.Tests;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class PostgreSqlPublicationContentReadTests
{
    [PostgreSqlFact]
    public Task ActualPairedDatabaseReaderUsesOnePoolSlotForCreatePutAndCancellation() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!)
                { Database = new NpgsqlConnectionStringBuilder(source.ConnectionString).Database, MaxPoolSize = 1, MinPoolSize = 0 };
            await using var single = NpgsqlDataSource.Create(settings.ConnectionString);
            await PublicationContentReadScenario.SingleSlotAsync(interceptors =>
            {
                var options = new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(single).AddInterceptors(interceptors).Options;
                var content = new PostgreSqlContentStore(options);
                return new(new PostgreSqlStateStore(options, validationContent: content), content);
            });
        });

    [PostgreSqlFact]
    public Task DatabasePublicationValidationUsesBoundedVerifiedPhysicalQueriesAndPreservesCancellation() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var content = new PostgreSqlContentStore(source);
            await PublicationContentReadScenario.RunAsync(content, (interceptors, external) => new PostgreSqlStateStore(
                new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source, pg => pg.CommandTimeout(45)).AddInterceptors(interceptors).Options,
                validationContent: external), async handle =>
            {
                await using var command = source.CreateCommand("DELETE FROM cellbridge_chunks WHERE object_key=$1");
                command.Parameters.AddWithValue(handle.Key); await command.ExecuteNonQueryAsync();
            });
        });

    [PostgreSqlFact]
    public Task FilesystemValidationChecksBatchedDatabaseHeadersBeforeOpeningActualFiles() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cellbridge-batch-files-" + Guid.NewGuid().ToString("N"));
            try
            {
                var content = new PostgreSqlFileSystemContentStore(source, root);
                var plan = await PublicationContentReadScenario.PrepareAsync(content, Enumerable.Repeat(16, 65));
                var physical = plan.Partitions[0].AddedElements[0].Payload;
                await using (var corrupt = source.CreateCommand("UPDATE cellbridge_objects SET length=length+1 WHERE object_key=$1"))
                { corrupt.Parameters.AddWithValue(physical.Key); await corrupt.ExecuteNonQueryAsync(); }
                var files = new Files(content); var commands = new PublicationContentReadScenario.Reads();
                IDocumentRepository repository = new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>()
                    .UseNpgsql(source, pg => pg.CommandTimeout(45)).AddInterceptors(commands).Options, validationContent: files);
                await Assert.ThrowsAsync<StorageCorruptionException>(() => repository.CreateAsync(plan).AsTask());
                Assert.Equal(0, files.Reads); Assert.Null(await repository.FindAsync(new DocumentIdentity.Resource(plan.Head.ResourceId)));
                await using (var repair = source.CreateCommand("UPDATE cellbridge_objects SET length=length-1 WHERE object_key=$1"))
                { repair.Parameters.AddWithValue(physical.Key); await repair.ExecuteNonQueryAsync(); }
                commands.Metadata.Clear();
                Assert.True((await repository.CreateAsync(plan)).Created);
                Assert.Equal(plan.Partitions[0].AddedElements.Length, files.Reads);
                Assert.Equal(0, commands.Chunks); Assert.Equal(2, commands.Metadata.Count);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        });

    private sealed class Files(IContentStore inner) : IContentStore
    {
        public int Reads { get; private set; }
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken ct = default)
        { Reads++; return inner.OpenReadAsync(handle, ct); }
    }
}
