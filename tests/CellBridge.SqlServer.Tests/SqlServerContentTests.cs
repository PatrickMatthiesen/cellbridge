using System.Security.Cryptography;
using System.Data.Common;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.SqlServer.Tests;

[Collection("SQL Server")]
public sealed class SqlServerContentTests(SqlServerFixture fixture)
{
    private DbContextOptions<SqlServerDocumentDbContext> Options(string? connection = null) =>
        new DbContextOptionsBuilder<SqlServerDocumentDbContext>().UseSqlServer(connection ?? fixture.Documents,
            sql => sql.MigrationsHistoryTable(SqlServerDocumentDbContext.MigrationsHistoryTable, SqlServerDocumentDbContext.Schema)).Options;

    [Fact]
    public async Task ExplicitMigrationRestrictedRuntimeAndContentContracts()
    {
        var options = Options();
        var store = new SqlServerContentStore(options);
        await using (var db = new SqlServerDocumentDbContext(options))
        {
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal(0, await ScalarAsync<int>(fixture.Documents,
                "SELECT COUNT(*) FROM sys.schemas WHERE name='cellbridge_documents'"));
        }
        await Assert.ThrowsAsync<StorageUnavailableException>(async () =>
            await store.WriteAsync(new MemoryStream([1, 2, 3])));
        Assert.Equal(0, await ScalarAsync<int>(fixture.Documents,
            "SELECT COUNT(*) FROM sys.schemas WHERE name='cellbridge_documents'"));

        var migrator = new SqlServerStorageMigrator(options);
        await Task.WhenAll(migrator.MigrateAsync(), new SqlServerStorageMigrator(options).MigrateAsync());
        await migrator.MigrateAsync();
        await using (var db = new SqlServerDocumentDbContext(options))
        {
            Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync()).Count());
            Assert.False(db.Database.HasPendingModelChanges());
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO cellbridge_documents.__CellBridgeDocumentMigrationsHistory VALUES ('20990101000000_Unknown','10.0.13')
                """);
            await Assert.ThrowsAsync<StorageUnavailableException>(() => migrator.MigrateAsync());
            await db.Database.ExecuteSqlRawAsync("""
                DELETE FROM cellbridge_documents.__CellBridgeDocumentMigrationsHistory WHERE MigrationId='20990101000000_Unknown'
                """);
        }

        var runtime = await CreateRuntimeAsync();
        var first = new SqlServerContentStore(Options(runtime));
        var second = new SqlServerContentStore(Options(runtime));
        var bytes = RandomNumberGenerator.GetBytes(3 * 65536 + 19);
        var writes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
            await (index % 2 == 0 ? first : second).WriteAsync(new MemoryStream(bytes))));
        Assert.All(writes, handle => Assert.Equal(writes[0], handle));
        Assert.Equal(1, await ScalarAsync<int>(fixture.Documents, "SELECT COUNT(*) FROM cellbridge_documents.objects"));
        Assert.Equal(bytes.LongLength, await ScalarAsync<long>(fixture.Documents, "SELECT StoredBytes FROM cellbridge_documents.usage"));
        Assert.Equal(bytes, await second.ReadVerifiedAsync(writes[0]));

        await using (var stream = await first.OpenReadAsync(writes[0]))
        {
            Assert.False(stream.CanSeek);
            Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        }
        // Early disposal releases the reader and connection; another read still succeeds.
        Assert.Equal(bytes, await second.ReadVerifiedAsync(writes[0]));
        var empty = await first.WriteAsync(new MemoryStream());
        Assert.Empty(await second.ReadVerifiedAsync(empty));
        await Assert.ThrowsAsync<StorageCorruptionException>(async () =>
            await first.OpenReadAsync(writes[0] with { Key = writes[0].Key + " " }));
        await Assert.ThrowsAsync<StorageCorruptionException>(async () =>
            await first.OpenReadAsync(writes[0] with { Length = bytes.Length + 1 }));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await first.WriteAsync(new MemoryStream([9]), cancelled.Token));
        var limited = new SqlServerContentStore(Options(runtime), 3);
        await Assert.ThrowsAsync<StorageQuotaExceededException>(async () =>
            await limited.WriteAsync(new MemoryStream([1, 2, 3, 4])));
        Assert.Equal(2, await ScalarAsync<int>(fixture.Documents, "SELECT COUNT(*) FROM cellbridge_documents.objects"));

        // Different hosts contend for the same persisted quota, including under
        // deduplication. Exactly two of these distinct ten-byte objects can fit.
        await new SqlServerStorageMigrator(options, new StorageLimits { MaxStoredBytes = bytes.LongLength + 24 }).MigrateAsync();
        var admitted = await Task.WhenAll(Enumerable.Range(1, 8).Select(async value =>
        {
            try
            {
                await (value % 2 == 0 ? first : second).WriteAsync(new MemoryStream(Enumerable.Repeat((byte)value, 10).ToArray()));
                return true;
            }
            catch (StorageQuotaExceededException) { return false; }
        }));
        Assert.Equal(2, admitted.Count(value => value));
        Assert.Equal(bytes.LongLength + 20, await ScalarAsync<long>(fixture.Documents,
            "SELECT StoredBytes FROM cellbridge_documents.usage"));
        Assert.Equal(writes[0], await second.WriteAsync(new MemoryStream(bytes)));
        await migrator.MigrateAsync();

        // Cancel after object and quota INSERT/UPDATE, while the first chunk batch
        // is about to execute. Disposal must roll the complete transaction back.
        using (var cancellation = new CancellationTokenSource())
        {
            var interceptor = new CancelChunkInsert(cancellation);
            var interrupted = new SqlServerContentStore(new DbContextOptionsBuilder<SqlServerDocumentDbContext>(Options(runtime))
                .AddInterceptors(interceptor).Options);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await interrupted.WriteAsync(
                new MemoryStream(RandomNumberGenerator.GetBytes(17 * 65536)), cancellation.Token));
            Assert.True(interceptor.Triggered);
        }
        Assert.Equal(4, await ScalarAsync<int>(fixture.Documents, "SELECT COUNT(*) FROM cellbridge_documents.objects"));
        Assert.Equal(bytes.LongLength + 20, await ScalarAsync<long>(fixture.Documents,
            "SELECT StoredBytes FROM cellbridge_documents.usage"));

        await using var runtimeDb = new SqlServerDocumentDbContext(Options(runtime));
        await Assert.ThrowsAsync<SqlException>(() => runtimeDb.Database.ExecuteSqlRawAsync(
            "CREATE TABLE cellbridge_documents.forbidden(id int)"));
        await Assert.ThrowsAsync<SqlException>(() => new SqlServerStorageMigrator(Options(runtime)).MigrateAsync());
        Assert.Equal(bytes, await second.ReadVerifiedAsync(writes[0]));

        // Deduplication verifies existing bytes, rather than trusting a matching hash key.
        await using var adminDb = new SqlServerDocumentDbContext(options);
        await adminDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE cellbridge_documents.chunks SET Bytes=0x00 WHERE [Key]={writes[0].Key} AND Ordinal=0
            """);
        await Assert.ThrowsAsync<StorageCorruptionException>(async () =>
            await second.WriteAsync(new MemoryStream(bytes)));
        await Assert.ThrowsAsync<StorageCorruptionException>(async () => await first.ReadVerifiedAsync(writes[0]));
    }

    [Fact]
    public void RetryingExecutionStrategiesAreRejectedWithoutConnecting()
    {
        var options = new DbContextOptionsBuilder<SqlServerDocumentDbContext>()
            .UseSqlServer("Server=unreachable;Database=test;Integrated Security=true", sql => sql.EnableRetryOnFailure()).Options;
        Assert.Throws<ArgumentException>(() => new SqlServerContentStore(options));
        Assert.Throws<ArgumentException>(() => new SqlServerStorageMigrator(options));
        Assert.Throws<ArgumentException>(() => new SqlServerStateStore(options));
        Assert.Throws<ArgumentException>(() => new StorageMaintenance(options));
    }

    private async Task<string> CreateRuntimeAsync()
    {
        var password = "Cb!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var connection = new SqlConnection(fixture.Documents);
        await connection.OpenAsync();
        // Names are test-owned constants; the generated password is hex, never user input.
        await using var command = new SqlCommand($"""
            CREATE LOGIN cb_content_runtime WITH PASSWORD='{password}';
            CREATE USER cb_content_runtime FOR LOGIN cb_content_runtime;
            GRANT SELECT,INSERT ON cellbridge_documents.objects TO cb_content_runtime;
            GRANT SELECT,INSERT ON cellbridge_documents.chunks TO cb_content_runtime;
            GRANT SELECT ON cellbridge_documents.usage TO cb_content_runtime;
            GRANT SELECT ON cellbridge_documents.schema_version TO cb_content_runtime;
            GRANT UPDATE (StoredBytes) ON cellbridge_documents.usage TO cb_content_runtime;
            """, connection);
        await command.ExecuteNonQueryAsync();
        var result = new SqlConnectionStringBuilder(fixture.Documents)
        { UserID = "cb_content_runtime", Password = password, Pooling = true };
        Assert.Equal(0, await ScalarAsync<int>(result.ConnectionString, "SELECT HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CONTROL')"));
        Assert.Equal(0, await ScalarAsync<int>(result.ConnectionString, "SELECT IS_SRVROLEMEMBER('sysadmin')"));
        return result.ConnectionString;
    }

    internal static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private sealed class CancelChunkInsert(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[cellbridge_documents].[chunks]", StringComparison.Ordinal) &&
                (command.CommandText.Contains("INSERT", StringComparison.Ordinal) || command.CommandText.Contains("MERGE", StringComparison.Ordinal)))
            {
                Triggered = true;
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
