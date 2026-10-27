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
    private string database = null!;

    private DbContextOptions<SqlServerDocumentDbContext> Options(string? connection = null) =>
        new DbContextOptionsBuilder<SqlServerDocumentDbContext>().UseSqlServer(connection ?? database,
            sql => sql.MigrationsHistoryTable(SqlServerDocumentDbContext.MigrationsHistoryTable, SqlServerDocumentDbContext.Schema)).Options;

    [Fact]
    public async Task ContentWritesRequireExplicitMigrationAndUnknownMigrationHistoryIsRejected()
    {
        database = await fixture.CreateDatabaseAsync();
        var options = Options();
        var store = new SqlServerContentStore(options);
        await using var db = new SqlServerDocumentDbContext(options);
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(0, await ScalarAsync<int>(database,
            "SELECT COUNT(*) FROM sys.schemas WHERE name='cellbridge_documents'"));
        await Assert.ThrowsAsync<StorageUnavailableException>(async () =>
            await store.WriteAsync(new MemoryStream([1, 2, 3])));
        Assert.Equal(0, await ScalarAsync<int>(database,
            "SELECT COUNT(*) FROM sys.schemas WHERE name='cellbridge_documents'"));

        var migrator = new SqlServerStorageMigrator(options);
        await Task.WhenAll(migrator.MigrateAsync(), new SqlServerStorageMigrator(options).MigrateAsync());
        await migrator.MigrateAsync();
        await store.WriteAsync(new MemoryStream([1, 2, 3]));
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO cellbridge_documents.__CellBridgeDocumentMigrationsHistory VALUES ('20990101000000_Unknown','10.0.13')
            """);
        await Assert.ThrowsAsync<StorageUnavailableException>(() => migrator.MigrateAsync());
    }

    [Fact]
    public async Task ConcurrentIdenticalWritesDeduplicateAcrossRestrictedHostsAndReadersReleaseConnections()
    {
        var runtime = await CreateRuntimeAsync();
        var first = new SqlServerContentStore(Options(runtime));
        var second = new SqlServerContentStore(Options(runtime));
        var bytes = RandomNumberGenerator.GetBytes(3 * 65536 + 19);
        var writes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
            await (index % 2 == 0 ? first : second).WriteAsync(new MemoryStream(bytes))));
        Assert.All(writes, handle => Assert.Equal(writes[0], handle));
        Assert.Equal(1, await ScalarAsync<int>(database, "SELECT COUNT(*) FROM cellbridge_documents.objects"));
        Assert.Equal(bytes.LongLength, await ScalarAsync<long>(database, "SELECT StoredBytes FROM cellbridge_documents.usage"));
        Assert.Equal(bytes, await second.ReadVerifiedAsync(writes[0]));
        var singleConnection = new SqlServerContentStore(Options(new SqlConnectionStringBuilder(runtime) { MaxPoolSize = 1 }.ConnectionString));
        await using (var stream = await singleConnection.OpenReadAsync(writes[0]))
        {
            Assert.False(stream.CanSeek);
            Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        }
        Assert.Equal(bytes, await singleConnection.ReadVerifiedAsync(writes[0]));
        var empty = await first.WriteAsync(new MemoryStream());
        Assert.Empty(await second.ReadVerifiedAsync(empty));
    }

    [Fact]
    public async Task MismatchedObjectKeyAndLengthAreRejected()
    {
        var store = new SqlServerContentStore(Options(await CreateRuntimeAsync()));
        var handle = await store.WriteAsync(new MemoryStream([1, 2, 3]));
        await Assert.ThrowsAsync<StorageCorruptionException>(async () =>
            await store.OpenReadAsync(handle with { Key = handle.Key + " " }));
        await Assert.ThrowsAsync<StorageCorruptionException>(async () =>
            await store.OpenReadAsync(handle with { Length = handle.Length + 1 }));
    }

    [Fact]
    public async Task PrecancelledAndOversizedWritesDoNotPublishObjects()
    {
        var options = Options(await CreateRuntimeAsync());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new SqlServerContentStore(options).WriteAsync(new MemoryStream([9]), cancelled.Token));
        await Assert.ThrowsAsync<StorageQuotaExceededException>(async () =>
            await new SqlServerContentStore(options, 3).WriteAsync(new MemoryStream([1, 2, 3, 4])));
        Assert.Equal(0, await ScalarAsync<int>(database, "SELECT COUNT(*) FROM cellbridge_documents.objects"));
        Assert.Equal(0, await ScalarAsync<long>(database, "SELECT StoredBytes FROM cellbridge_documents.usage"));
    }

    [Fact]
    public async Task CompetingHostsSharePersistedByteQuotaAndDeduplicationDoesNotChargeAgain()
    {
        var runtime = await CreateRuntimeAsync();
        var first = new SqlServerContentStore(Options(runtime));
        var second = new SqlServerContentStore(Options(runtime));
        byte[] bytes = [90, 91, 92, 93];
        var handle = await first.WriteAsync(new MemoryStream(bytes));
        await new SqlServerStorageMigrator(Options(), new StorageLimits { MaxStoredBytes = bytes.LongLength + 24 }).MigrateAsync();
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
        Assert.Equal(bytes.LongLength + 20, await ScalarAsync<long>(database,
            "SELECT StoredBytes FROM cellbridge_documents.usage"));
        Assert.Equal(handle, await second.WriteAsync(new MemoryStream(bytes)));
        Assert.Equal(bytes.LongLength + 20, await ScalarAsync<long>(database,
            "SELECT StoredBytes FROM cellbridge_documents.usage"));
    }

    [Fact]
    public async Task CancellationDuringChunkInsertionRollsBackObjectAndByteLedger()
    {
        var runtime = await CreateRuntimeAsync();
        using var cancellation = new CancellationTokenSource();
        var interceptor = new CancelChunkInsert(cancellation);
        var interrupted = new SqlServerContentStore(new DbContextOptionsBuilder<SqlServerDocumentDbContext>(Options(runtime))
            .AddInterceptors(interceptor).Options);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await interrupted.WriteAsync(
            new MemoryStream(RandomNumberGenerator.GetBytes(17 * 65536)), cancellation.Token));
        Assert.True(interceptor.Triggered);
        Assert.Equal(0, await ScalarAsync<int>(database, "SELECT COUNT(*) FROM cellbridge_documents.objects"));
        Assert.Equal(0, await ScalarAsync<int>(database, "SELECT COUNT(*) FROM cellbridge_documents.chunks"));
        Assert.Equal(0, await ScalarAsync<long>(database, "SELECT StoredBytes FROM cellbridge_documents.usage"));
    }

    [Fact]
    public async Task RestrictedRuntimeCanReadContentButCannotChangeSchemaOrMigrate()
    {
        var options = Options(await CreateRuntimeAsync());
        var store = new SqlServerContentStore(options);
        byte[] bytes = [1, 2, 3];
        var handle = await store.WriteAsync(new MemoryStream(bytes));
        await using var db = new SqlServerDocumentDbContext(options);
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE cellbridge_documents.forbidden(id int)"));
        await Assert.ThrowsAsync<SqlException>(() => new SqlServerStorageMigrator(options).MigrateAsync());
        Assert.Equal(bytes, await store.ReadVerifiedAsync(handle));
    }

    [Fact]
    public async Task DeduplicationAndVerifiedReadsRejectCorruptStoredChunks()
    {
        var store = new SqlServerContentStore(Options(await CreateRuntimeAsync()));
        var bytes = RandomNumberGenerator.GetBytes(65536 + 19);
        var handle = await store.WriteAsync(new MemoryStream(bytes));
        await using var admin = new SqlServerDocumentDbContext(Options());
        await admin.Database.ExecuteSqlAsync($"""
            UPDATE cellbridge_documents.chunks SET Bytes=0x00 WHERE [Key]={handle.Key} AND Ordinal=0
            """);
        await Assert.ThrowsAsync<StorageCorruptionException>(async () => await store.WriteAsync(new MemoryStream(bytes)));
        await Assert.ThrowsAsync<StorageCorruptionException>(async () => await store.ReadVerifiedAsync(handle));
    }

    [Fact]
    public async Task BatchWritesReadMetadataOnceAndStillVerifyExistingChunksAndMetadata()
    {
        var metadata = new CountBatchMetadataReads();
        var options = new DbContextOptionsBuilder<SqlServerDocumentDbContext>(Options(await CreateRuntimeAsync()))
            .AddInterceptors(metadata).Options;
        var store = new SqlServerContentStore(options);
        ReadOnlyMemory<byte>[] inputs = [new byte[] { 1, 2, 3 }, new byte[] { 4, 5 }, new byte[] { 1, 2, 3 }, ReadOnlyMemory<byte>.Empty];
        var handles = await store.WriteManyAsync(inputs);
        Assert.Equal(1, metadata.Reads);
        Assert.Equal(handles[0], handles[2]);
        for (var index = 0; index < handles.Count; index++)
            Assert.Equal(inputs[index].ToArray(), await store.ReadVerifiedAsync(handles[index]));

        metadata.Reads = 0;
        Assert.Equal(handles, await store.WriteManyAsync(inputs));
        Assert.Equal(1, metadata.Reads);
        await using var admin = new SqlServerDocumentDbContext(Options());
        await admin.Database.ExecuteSqlAsync($"UPDATE cellbridge_documents.chunks SET Bytes=0x030201 WHERE [Key]={handles[0].Key}");
        await Assert.ThrowsAsync<StorageCorruptionException>(() => store.WriteManyAsync(inputs).AsTask());
        await admin.Database.ExecuteSqlAsync($"UPDATE cellbridge_documents.objects SET Length=1 WHERE [Key]={handles[3].Key}");
        await Assert.ThrowsAsync<StorageCorruptionException>(() => store.WriteManyAsync([ReadOnlyMemory<byte>.Empty]).AsTask());
    }

    private sealed class CountBatchMetadataReads : DbCommandInterceptor
    {
        public int Reads;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.Ordinal) &&
                command.CommandText.Contains("[cellbridge_documents].[objects]", StringComparison.Ordinal) &&
                command.CommandText.Contains("[Length]", StringComparison.Ordinal) &&
                command.CommandText.Contains("[Sha256]", StringComparison.Ordinal)) Reads++;
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task ReversedOverlappingBatchesDeduplicateWithoutDeadlocksOrExtraQuotaCharges()
    {
        var runtime = await CreateRuntimeAsync();
        var first = new SqlServerContentStore(Options(runtime));
        var second = new SqlServerContentStore(Options(runtime));
        var a = RandomNumberGenerator.GetBytes(65537);
        var b = RandomNumberGenerator.GetBytes(65538);
        var outcomes = await Task.WhenAll(
            first.WriteManyAsync(new ReadOnlyMemory<byte>[] { a, b, ReadOnlyMemory<byte>.Empty }).AsTask(),
            second.WriteManyAsync(new ReadOnlyMemory<byte>[] { b, ReadOnlyMemory<byte>.Empty, a }).AsTask())
            .WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(outcomes[0][0], outcomes[1][2]);
        Assert.Equal(outcomes[0][1], outcomes[1][0]);
        Assert.Equal(outcomes[0][2], outcomes[1][1]);
        Assert.Equal(a, await first.ReadVerifiedAsync(outcomes[0][0]));
        Assert.Equal(b, await second.ReadVerifiedAsync(outcomes[0][1]));
        Assert.Empty(await second.ReadVerifiedAsync(outcomes[0][2]));
        Assert.Equal(3, await ScalarAsync<int>(database, "SELECT COUNT(*) FROM cellbridge_documents.objects"));
        Assert.Equal(4, await ScalarAsync<int>(database, "SELECT COUNT(*) FROM cellbridge_documents.chunks"));
        Assert.Equal(a.LongLength + b.LongLength, await ScalarAsync<long>(database, "SELECT StoredBytes FROM cellbridge_documents.usage"));
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
        database = await fixture.CreateDatabaseAsync();
        await new SqlServerStorageMigrator(Options()).MigrateAsync();
        var name = "cb_content_" + Guid.NewGuid().ToString("N");
        var password = "Cb!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var connection = new SqlConnection(database);
        await connection.OpenAsync();
        // The generated identifiers and password contain no user input.
        await using var command = new SqlCommand($"""
            CREATE LOGIN [{name}] WITH PASSWORD='{password}';
            CREATE USER [{name}] FOR LOGIN [{name}];
            GRANT SELECT,INSERT ON cellbridge_documents.objects TO [{name}];
            GRANT SELECT,INSERT ON cellbridge_documents.chunks TO [{name}];
            GRANT SELECT ON cellbridge_documents.usage TO [{name}];
            GRANT SELECT ON cellbridge_documents.schema_version TO [{name}];
            GRANT UPDATE (StoredBytes) ON cellbridge_documents.usage TO [{name}];
            """, connection);
        await command.ExecuteNonQueryAsync();
        var result = new SqlConnectionStringBuilder(database)
        { UserID = name, Password = password, Pooling = true };
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
