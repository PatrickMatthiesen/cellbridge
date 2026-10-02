using CellBridge.Storage.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("cellbridge")
    ?? throw new InvalidOperationException("Set ConnectionStrings:cellbridge for migration.");
await using var source = NpgsqlDataSource.Create(connectionString);
await new PostgreSqlStateStore(source).InitializeAsync();
Console.WriteLine("CellBridge storage schema version 1 is ready.");
