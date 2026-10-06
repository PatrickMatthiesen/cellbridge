using CellBridge.Storage.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("cellbridge")
    ?? throw new InvalidOperationException("ConnectionStrings:cellbridge is required.");
await using var source = NpgsqlDataSource.Create(connectionString);
await new PostgreSqlStateStore(source).InitializeAsync();
Console.WriteLine("CellBridge PostgreSQL storage schema is ready.");
