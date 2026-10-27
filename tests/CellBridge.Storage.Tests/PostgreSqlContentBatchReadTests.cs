using System.Data.Common;
using System.Security.Cryptography;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class PostgreSqlContentBatchReadTests
{
    [PostgreSqlFact]
    public Task SixtyFiveObjectsUseTwoQueriesPerBoundedBatchAndReleaseTheConnection() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var writer = new PostgreSqlContentStore(source);
            var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var handles = new List<ContentHandle>();
            foreach (var index in Enumerable.Range(0, 65))
            {
                var bytes = index == 0 ? [] : index == 1 ? RandomNumberGenerator.GetBytes(65536 + 19) : BitConverter.GetBytes(index);
                var handle = await writer.WriteAsync(new MemoryStream(bytes));
                handles.Add(handle);
                expected.Add(handle.Key, bytes);
            }
            var commands = new ReadCommands();
            var store = new PostgreSqlContentStore(new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source).AddInterceptors(commands).Options);
            // Call the provider directly to verify it also bounds requests that bypass the extension.
            var result = await store.ReadManyAsync(handles.Concat(handles).ToArray());
            Assert.Equal(4, commands.Sql.Count);
            Assert.All(commands.Sql, sql => Assert.StartsWith("SELECT", sql));
            Assert.Equal(expected.Count, result.Count);
            foreach (var handle in handles) Assert.Equal(expected[handle.Key], result[handle.Key]);
            commands.Sql.Clear();
            Assert.Empty(await store.ReadManyAsync([]));
            Assert.Empty(commands.Sql);
            Assert.Empty(await store.ReadVerifiedAsync(handles[0]));
        });

    [PostgreSqlFact]
    public Task EveryReadRechecksMetadataAndChunksIncludingEmptyMissingAndCorruptObjects() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var store = new PostgreSqlContentStore(source);
            var handles = new List<ContentHandle>();
            var mutations = new[]
            {
                "UPDATE cellbridge_objects SET length=length+1 WHERE object_key=$1",
                "UPDATE cellbridge_objects SET sha256=repeat('0',64) WHERE object_key=$1",
                "DELETE FROM cellbridge_chunks WHERE object_key=$1",
                "UPDATE cellbridge_chunks SET ordinal=1 WHERE object_key=$1",
                "UPDATE cellbridge_chunks SET bytes=decode('','hex') WHERE object_key=$1",
                "UPDATE cellbridge_chunks SET bytes=decode('010203','hex') WHERE object_key=$1",
                "UPDATE cellbridge_chunks SET bytes=decode(repeat('00',65537),'hex') WHERE object_key=$1",
                "UPDATE cellbridge_chunks SET bytes=decode('01','hex') WHERE object_key=$1",
            };
            for (var index = 0; index < mutations.Length; index++)
            {
                var handle = await store.WriteAsync(new MemoryStream([(byte)(index + 10), 2, 3]));
                handles.Add(handle);
                Assert.Single(await store.ReadManyAsync([handle]));
                await using var mutate = source.CreateCommand(mutations[index]);
                mutate.Parameters.AddWithValue(handle.Key);
                await mutate.ExecuteNonQueryAsync();
                await Assert.ThrowsAsync<StorageCorruptionException>(() => store.ReadManyAsync([handle]).AsTask());
            }
            var empty = await store.WriteAsync(new MemoryStream());
            Assert.Empty((await store.ReadManyAsync([empty]))[empty.Key]);
            await using (var corrupt = source.CreateCommand("INSERT INTO cellbridge_chunks(object_key,ordinal,bytes) VALUES($1,0,decode('01','hex'))"))
            {
                corrupt.Parameters.AddWithValue(empty.Key);
                await corrupt.ExecuteNonQueryAsync();
            }
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.ReadManyAsync([empty]).AsTask());
            var missing = new ContentHandle(new string('0', 64), 0, new string('0', 64));
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.ReadManyAsync([missing]).AsTask());
        });

    [PostgreSqlFact]
    public Task InvalidRequestsAreRejectedBeforeQueriesAndDatabaseFailuresAreTranslated() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var commands = new ReadCommands();
            var store = new PostgreSqlContentStore(new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source).AddInterceptors(commands).Options, maxObjectBytes: 1);
            var key = Convert.ToHexStringLower(SHA256.HashData([1]));
            var handle = new ContentHandle(key, 1, key);
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.ReadManyAsync([handle with { Key = "bad" }]).AsTask());
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.ReadManyAsync([handle, handle with { Length = 2 }]).AsTask());
            await Assert.ThrowsAsync<StorageQuotaExceededException>(() => store.ReadManyAsync([handle with { Length = 2 }]).AsTask());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadManyAsync([handle], new(true)).AsTask());
            Assert.Empty(commands.Sql);
            await using (var remove = source.CreateCommand("DROP TABLE cellbridge_chunks,cellbridge_objects"))
                await remove.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<StorageUnavailableException>(() => store.ReadManyAsync([handle]).AsTask());
        });

    private sealed class ReadCommands : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Sql.Add(command.CommandText); return ValueTask.FromResult(result); }
    }
}
