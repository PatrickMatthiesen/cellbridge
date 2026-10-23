using System.Security.Cryptography;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.SqlServer.Tests;

internal sealed record SqlServerDocuments(string Admin, string Runtime, StorageLimits Limits)
{
    internal static CellBridgeActor Actor { get; } = new(new("sql:writer", "sql-writer", "SQL writer"), CanCreate: true);
    internal DbContextOptions<SqlServerDocumentDbContext> Options(bool admin = false, params IInterceptor[] interceptors) =>
        new DbContextOptionsBuilder<SqlServerDocumentDbContext>().UseSqlServer(admin ? Admin : Runtime,
            sql => sql.MigrationsHistoryTable(SqlServerDocumentDbContext.MigrationsHistoryTable, SqlServerDocumentDbContext.Schema))
            .AddInterceptors(interceptors).Options;

    internal StorageProvider Provider(params IInterceptor[] interceptors)
    {
        var options = Options(false, interceptors);
        return new(new SqlServerStateStore(options, Limits), new SqlServerContentStore(options, Limits.MaxObjectBytes), Limits);
    }

    internal static async Task<SqlServerDocuments> CreateAsync(SqlServerFixture fixture, StorageLimits? limits = null)
    {
        var admin = await fixture.CreateDatabaseAsync();
        var configuration = limits ?? new();
        var options = new DbContextOptionsBuilder<SqlServerDocumentDbContext>().UseSqlServer(admin).Options;
        await new SqlServerStorageMigrator(options, configuration).MigrateAsync();
        var name = "cb_runtime_" + Guid.NewGuid().ToString("N");
        var password = "Cb!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var connection = new SqlConnection(admin);
        await connection.OpenAsync();
        await using (var command = new SqlCommand($"CREATE LOGIN [{name}] WITH PASSWORD='{password}'; CREATE USER [{name}] FOR LOGIN [{name}];", connection))
            await command.ExecuteNonQueryAsync();
        foreach (var table in new[] { "documents", "snapshots", "metadata", "revisions", "reservations", "recovery_receipts" })
        {
            await using var grant = new SqlCommand($"GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_documents.[{table}] TO [{name}]", connection);
            await grant.ExecuteNonQueryAsync();
        }
        await using (var grant = new SqlCommand($"""
            GRANT SELECT,INSERT ON cellbridge_documents.objects TO [{name}];
            GRANT SELECT,INSERT ON cellbridge_documents.chunks TO [{name}];
            GRANT SELECT ON cellbridge_documents.usage TO [{name}];
            GRANT UPDATE (StoredBytes,DocumentCount) ON cellbridge_documents.usage TO [{name}];
            GRANT SELECT ON cellbridge_documents.schema_version TO [{name}];
            """, connection)) await grant.ExecuteNonQueryAsync();
        var runtime = new SqlConnectionStringBuilder(admin) { UserID = name, Password = password }.ConnectionString;
        return new(admin, runtime, configuration);
    }

    internal async Task AssertAccountingAsync()
    {
        await using var db = new SqlServerDocumentDbContext(Options(admin: true));
        var bytes = await db.Objects.SumAsync(row => (long?)row.Length) ?? 0;
        bytes += await db.Snapshots.SumAsync(row => (long?)SqlServerDocumentDbContext.ByteLength(row.Data)) ?? 0;
        bytes += await db.RecoveryReceipts.SumAsync(row => (long?)SqlServerDocumentDbContext.ByteLength(row.Data)) ?? 0;
        var usage = await db.Usage.SingleAsync();
        Assert.Equal(bytes, usage.StoredBytes);
        Assert.Equal(await db.Documents.LongCountAsync(), usage.DocumentCount);
    }
}
