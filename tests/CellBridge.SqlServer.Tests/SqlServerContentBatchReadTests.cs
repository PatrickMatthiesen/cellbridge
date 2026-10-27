using System.Data.Common;
using System.Security.Cryptography;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.SqlServer.Tests;

[Collection("SQL Server")]
public sealed class SqlServerContentBatchReadTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task BoundedBatchesReadEmptyAndMultichunkObjectsWithTwoQueriesPerBatch()
    {
        var database = await fixture.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<SqlServerDocumentDbContext>().UseSqlServer(database).Options;
        await new SqlServerStorageMigrator(options).MigrateAsync();
        var writer = new SqlServerContentStore(options);
        var handles = new List<ContentHandle>();
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var index in Enumerable.Range(0, 65))
        {
            var bytes = index == 0 ? [] : index == 1 ? RandomNumberGenerator.GetBytes(65536 + 19) : BitConverter.GetBytes(index);
            var handle = await writer.WriteAsync(new MemoryStream(bytes));
            handles.Add(handle);
            expected.Add(handle.Key, bytes);
        }
        var commands = new ReadCommands();
        var reader = new SqlServerContentStore(new DbContextOptionsBuilder<SqlServerDocumentDbContext>(options)
            .AddInterceptors(commands).Options);
        var result = await reader.ReadManyAsync(handles.Concat(handles).ToArray());
        Assert.Equal(4, commands.Sql.Count);
        Assert.All(commands.Sql, sql => Assert.StartsWith("SELECT", sql));
        Assert.Equal(expected.Count, result.Count);
        foreach (var handle in handles) Assert.Equal(expected[handle.Key], result[handle.Key]);
        commands.Sql.Clear();
        Assert.Empty(await reader.ReadManyAsync([]));
        Assert.Empty(commands.Sql);

        await using var admin = new SqlServerDocumentDbContext(options);
        await admin.Database.ExecuteSqlAsync($"UPDATE cellbridge_documents.chunks SET Bytes=0x01020304 WHERE [Key]={handles[2].Key}");
        await Assert.ThrowsAsync<StorageCorruptionException>(() => reader.ReadManyAsync([handles[2]]).AsTask());
        await admin.Database.ExecuteSqlAsync($"UPDATE cellbridge_documents.objects SET Length=1 WHERE [Key]={handles[0].Key}");
        await Assert.ThrowsAsync<StorageCorruptionException>(() => reader.ReadManyAsync([handles[0]]).AsTask());
        var missing = new ContentHandle(new string('0', 64), 0, new string('0', 64));
        await Assert.ThrowsAsync<StorageCorruptionException>(() => reader.ReadManyAsync([missing]).AsTask());
    }

    private sealed class ReadCommands : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Sql.Add(command.CommandText); return ValueTask.FromResult(result); }
    }
}
