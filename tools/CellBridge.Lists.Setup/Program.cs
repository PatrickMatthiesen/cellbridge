using CellBridge.Lists.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("lists-admin")
    ?? throw new InvalidOperationException("ConnectionStrings:lists-admin is required for operator setup.");
var runtimeRole = builder.Configuration["Lists:RuntimeRole"]
    ?? throw new InvalidOperationException("Lists:RuntimeRole is required. Create this non-owner role before setup.");
await using var source = NpgsqlDataSource.Create(connectionString);
await PostgreSqlListSetup.InitializeAsync(source, runtimeRole);
Console.WriteLine("Independent list schema is ready. Runtime applications must use separate least-privilege credentials.");
