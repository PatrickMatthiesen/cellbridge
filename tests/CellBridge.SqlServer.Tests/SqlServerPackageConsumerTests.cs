using System.Diagnostics;
using System.Text.Json;
using CellBridge.TestInfrastructure;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace CellBridge.SqlServer.Tests;

// Only the fixture owner references source projects. The consumer test assemblies
// run in separate processes with their own package-only dependency graphs.
public sealed class SqlServerPackageConsumerTests(ITestOutputHelper output)
{
    internal const string OutputVariable = "CELLBRIDGE_SQLSERVER_PACKAGE_OUTPUT";

    [PackageVerificationFact]
    public async Task SeparatePackagedConsumersPassAgainstOwnedSqlServer()
    {
        var directory = Environment.GetEnvironmentVariable(OutputVariable)!;
        var setupPath = Path.Combine(directory, "sqlserver-setup.json");
        SqlServerEnvironment environment;
        try
        {
            environment = await SqlServerEnvironment.StartAsync();
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(setupPath, JsonSerializer.Serialize(new
                { status = "setup-failed", errorType = error.GetType().FullName }));
            throw new InvalidOperationException("SQL Server fixture setup failed before package consumer tests started.", error);
        }
        await using (environment)
        {
            await using var connection = new SqlConnection(environment.ConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand("SELECT @@VERSION", connection);
            var version = (string)(await command.ExecuteScalarAsync())!;
            output.WriteLine(version);
            await File.WriteAllTextAsync(setupPath, JsonSerializer.Serialize(new
                { status = "ready", engineVersion = version, image = "mcr.microsoft.com/mssql/server:2022-latest" }));
            using var identities = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "consumers.json")));
            foreach (var (name, database) in new[]
            {
                ("CellBridge.Storage.SqlServer.Packages.Tests", environment.ConnectionString),
                ("CellBridge.Lists.SqlServer.Packages.Tests", environment.ListsConnectionString)
            })
            {
                var identity = identities.RootElement.EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
                var results = Path.Combine(directory, "sqlserver-tests", name);
                Directory.CreateDirectory(results);
                var start = new ProcessStartInfo("dotnet")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
                };
                foreach (var argument in new[] { "vstest", identity.GetProperty("assembly").GetString()!,
                             "--logger:trx;LogFileName=results.trx", "--ResultsDirectory:" + results })
                    start.ArgumentList.Add(argument);
                start.Environment["CELLBRIDGE_PACKAGE_SQLSERVER_CONNECTION"] = database;
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    throw;
                }
                var password = new SqlConnectionStringBuilder(database).Password;
                var log = ((await stdout) + (await stderr)).Replace(database, "[owned fixture connection]", StringComparison.Ordinal)
                    .Replace(password, "[redacted]", StringComparison.Ordinal);
                await File.WriteAllTextAsync(Path.Combine(results, "console.log"), log);
                output.WriteLine(log);
                Assert.True(process.ExitCode == 0, $"{name} failed with exit code {process.ExitCode}. See {results}.");
            }
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "sqlserver-disposal.json"), "{\"status\":\"disposed\"}\n");
    }
}

public sealed class PackageVerificationFactAttribute : FactAttribute
{
    public PackageVerificationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerPackageConsumerTests.OutputVariable)))
            Skip = "Run tools/verify_packages.py --sqlserver after packing the independent consumers.";
    }
}
