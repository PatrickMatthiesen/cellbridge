using CellBridge.Storage.PostgreSql;
using CellBridge.Authentication;
using CellBridge.Storage.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

var flags = new HashSet<string>(["--collect-orphans", "--apply", "--quiescent"], StringComparer.Ordinal);
if (args.Any(argument => !flags.Contains(argument)))
    throw new ArgumentException("Supported options: --collect-orphans [--apply --quiescent]. Initialization migrates storage schema 3 to 4; older development schemas require recreation.");
if (!args.Contains("--collect-orphans", StringComparer.Ordinal) && args.Length != 0)
    throw new ArgumentException("--apply and --quiescent require --collect-orphans.");
var builder = Host.CreateApplicationBuilder();
var connectionString = builder.Configuration.GetConnectionString("cellbridge")
    ?? throw new InvalidOperationException("Set ConnectionStrings:cellbridge for storage setup.");
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
    await AuthenticationDatabase.InitializeAsync(connectionString);
    Console.WriteLine("CellBridge storage schema version 4 and authentication schema version 1 are ready.");
}
