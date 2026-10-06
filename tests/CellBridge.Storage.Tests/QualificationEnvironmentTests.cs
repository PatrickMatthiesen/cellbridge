using System.Runtime.InteropServices;
using System.Text.Json;
using Npgsql;
using CellBridge.Storage.PostgreSql;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.Tests;

public sealed class QualificationEnvironmentTests
{
    [PostgreSqlFact]
    public Task UnsupportedFileSystemReclamationPreservesObjectsAndCharges() =>
        BudgetAndQueryTests.WithDatabase(new(), async db =>
        {
            if (OperatingSystem.IsLinux()) return; // Linux behavior has separate collection tests.
            using var files = new PortabilityTests.TemporaryFiles();
            var root = files.Path("content");
            var content = new PostgreSqlFileSystemContentStore(db, root);
            var handle = await content.WriteAsync(new MemoryStream([1, 2, 3]));
            var maintenance = new StorageMaintenance(db);
            var before = await maintenance.CollectOrphansAsync(fileSystemRoot: root);
            await Assert.ThrowsAsync<PlatformNotSupportedException>(() => maintenance.CollectOrphansAsync(
                apply: true, quiescent: true, fileSystemRoot: root));
            Assert.Equal(new byte[] { 1, 2, 3 }, await content.ReadVerifiedAsync(handle));
            Assert.Equal(before, await maintenance.CollectOrphansAsync(fileSystemRoot: root));
        });

    [QualificationFact]
    public async Task RecordDatabaseDurabilityAndLocalFileSystemEnvironment()
    {
        var directory = Environment.GetEnvironmentVariable("CELLBRIDGE_QUALIFICATION_DIRECTORY");
        Assert.False(string.IsNullOrEmpty(directory));
        Directory.CreateDirectory(directory!);
        await using var db = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await using var command = db.CreateCommand("""
            SELECT version(), current_setting('fsync'), current_setting('synchronous_commit'),
                   current_setting('full_page_writes')
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("on", reader.GetString(1));
        Assert.Equal("on", reader.GetString(2));
        Assert.Equal("on", reader.GetString(3));
        var roots = new[] { Path.GetTempPath(), directory }.Select(Path.GetFullPath).Distinct().Select(path =>
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            return new { path, drive.DriveFormat, DriveType = drive.DriveType.ToString() };
        }).ToArray();
        var report = new
        {
            schemaVersion = 1, capturedUtc = DateTime.UtcNow,
            operatingSystem = RuntimeInformation.OSDescription,
            runtime = RuntimeInformation.FrameworkDescription,
            postgres = reader.GetString(0), fsync = reader.GetString(1),
            synchronousCommit = reader.GetString(2), fullPageWrites = reader.GetString(3),
            localContentRoots = roots,
            scope = "Environment metadata only. Passing recovery test results are required separately; no power-loss or failover qualification."
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "environment.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class PostgreSqlLinuxFactAttribute : FactAttribute
{
    public PostgreSqlLinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Durable filesystem reclamation is qualified on Linux only.";
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")))
            Skip = "Set the migrated disposable PostgreSQL test database.";
    }
}
