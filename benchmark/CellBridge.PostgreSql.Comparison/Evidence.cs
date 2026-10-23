using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestInfrastructure;
using Npgsql;

internal sealed class Evidence
{
    private readonly string path;
    public Evidence(string benchmark, string variant, object conditions)
    {
        Settings.Validate();
        var appHostProjectPath = Projects.CellBridge_AppHost.ProjectPath;
        Settings.Require(Path.GetFullPath(appHostProjectPath) == Path.Combine(Settings.Repo, "src/CellBridge.AppHost"),
            "AppHost ProjectMetadata points outside this worktree. Build current TestInfrastructure and AppHost before collection.");
        path = Path.Combine(Settings.Output, $"{benchmark}-{variant}-{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl");
        var assemblies = new[] { typeof(Evidence).Assembly, typeof(IListEngine).Assembly,
            typeof(StorageProvider).Assembly, typeof(StoredDocument).Assembly, typeof(CellBridgeDocumentService).Assembly,
            typeof(PostgreSqlEnvironment).Assembly, typeof(Projects.CellBridge_AppHost).Assembly, typeof(NpgsqlDataSource).Assembly,
            typeof(PostgreSqlListEngine).Assembly, typeof(PostgreSqlStateStore).Assembly,
        }.Concat(Providers.SqlAssemblies);
        Write("setup-started", new { method = Settings.Method, benchmark, variant, conditions, appHostProjectPath,
            currentHead = Git("rev-parse", "HEAD"), status = Git("status", "--porcelain"),
            diffSha256 = Settings.Hash(System.Text.Encoding.UTF8.GetBytes(Git("diff", "HEAD", "--", "src", "tests/CellBridge.TestInfrastructure") ?? "")),
            sqlHead = Settings.SqlHead,
            runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), processors = Environment.ProcessorCount,
            serverGc = System.Runtime.GCSettings.IsServerGC,
            assemblies = assemblies.Distinct().Select(a => new { identity = a.FullName, path = a.Location,
                loadContext = AssemblyLoadContext.GetLoadContext(a)?.Name, mvid = a.ManifestModule.ModuleVersionId,
                version = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                sha256 = Settings.Hash(File.ReadAllBytes(a.Location)) }).ToArray(),
            sourceHashes = Directory.GetFiles(Settings.Root, "*.cs").Append(Path.Combine(Settings.Root, "CellBridge.PostgreSql.Comparison.csproj"))
                .Append(Path.Combine(Settings.Root, "expected-inputs.json"))
                .Append(Path.Combine(Settings.Root, "compare.mjs"))
                .Concat(new[] { "benchmark/CellBridge.Storage.Benchmark/SyntheticWorkload.cs", "tests/CellBridge.TestFixtures/MinimalDocx.cs",
                    "tests/CellBridge.TestInfrastructure/PostgreSqlEnvironment.cs", "src/CellBridge.AppHost/Program.cs" }.Select(p => Path.Combine(Settings.Repo, p)))
                .Where(File.Exists).ToDictionary(p => Path.GetRelativePath(Settings.Repo, p), p => Settings.Hash(File.ReadAllBytes(p))) });
    }
    public void Write(string stage, object value) => File.AppendAllText(path,
        JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, stage, value }) + Environment.NewLine);
    private static string? Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Settings.Repo,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd(); process.WaitForExit();
        return process.ExitCode == 0 ? output.TrimEnd() : null;
    }
}
