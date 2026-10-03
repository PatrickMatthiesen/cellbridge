using CellBridge.Storage.PostgreSql;
using CellBridge.Storage.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args.Where(a => a is not ("--collect-orphans" or "--apply" or "--quiescent")).ToArray());
var connectionString = builder.Configuration.GetConnectionString("cellbridge")
    ?? throw new InvalidOperationException("Set ConnectionStrings:cellbridge for migration.");
await using var source = NpgsqlDataSource.Create(connectionString);
var limits = builder.Configuration.GetSection("Storage").Get<StorageLimits>() ?? new StorageLimits();
if (args.Contains("--collect-orphans", StringComparer.Ordinal))
{
    var report = await new StorageMaintenance(source).CollectOrphansAsync(
        apply: args.Contains("--apply", StringComparer.Ordinal),
        quiescent: args.Contains("--quiescent", StringComparer.Ordinal),
        fileSystemRoot: builder.Configuration["Storage:ContentRoot"]);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
}
else
{
    await new PostgreSqlStateStore(source, limits).InitializeAsync();
    if (builder.Configuration["Storage:ContentProvider"]?.Equals("FileSystem", StringComparison.OrdinalIgnoreCase) == true)
        await new StorageMaintenance(source).RegisterExistingFileSystemAsync(
            builder.Configuration["Storage:ContentRoot"] ?? throw new InvalidOperationException("Filesystem migration requires Storage:ContentRoot."));
    Console.WriteLine("CellBridge storage schema version 2 is ready.");
}
