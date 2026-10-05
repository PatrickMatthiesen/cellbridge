using System.Diagnostics;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed class PortabilityToolTests
{
    [PostgreSqlFact]
    public Task PublicCommandsExportValidateImportRetryAndPublishRestoredVersion() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        await BudgetAndQueryTests.WithDatabase(new(), async destination =>
        {
            using var files = new PortabilityTests.TemporaryFiles();
            var provider = new StorageProvider(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));
            var document = (await new CellBridgeDocumentService(provider).CreateAsync("/operator.docx", MinimalDocx.Create(), TestActor.Value))!;
            await File.WriteAllTextAsync(files.Path("context.json"), JsonSerializer.Serialize(PortabilityTests.Context));
            var context = new[] { "--archive", files.Path("operator.zip"), "--context", files.Path("context.json") };
            await Command("export", source.ConnectionString, context);
            await Command("validate", null, context);
            var imported = await Command("import", destination.ConnectionString, context);
            await Command("restore-version", destination.ConnectionString,
                ["--resource-id", document.ResourceId.ToString(), "--revision", "1", "--expected-revision", "1",
                 "--operation-key", "operator-recovery", "--subject", TestActor.Value.Identity.Subject]);
            var restored = (await new PostgreSqlStateStore(destination).FindByResourceIdAsync(document.ResourceId))!;
            Assert.Equal(2UL, RevisionHistory.Latest(restored));
            Assert.Equal(imported, await Command("import", destination.ConnectionString, context));
        }));

    private static async Task<string> Command(string command, string? connectionString, string[] arguments)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var executable = Path.Combine(root, "tools/CellBridge.Storage.Portability/bin", configuration, "net10.0/CellBridge.Storage.Portability.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(executable); start.ArgumentList.Add(command);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("Storage__", StringComparison.OrdinalIgnoreCase) ||
            k.StartsWith("Recovery__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        if (connectionString is not null)
        {
            // NpgsqlDataSource.ConnectionString omits the password. Keep the runner's credentials private,
            // and retain only this test's disposable database name for the child process.
            var privateConnection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!)
                { Database = new NpgsqlConnectionStringBuilder(connectionString).Database };
            start.Environment["ConnectionStrings__cellbridge"] = privateConnection.ConnectionString;
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, await errors);
        return (await output).Trim();
    }
}
