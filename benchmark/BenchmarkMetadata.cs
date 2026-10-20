using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

internal static class BenchmarkMetadata
{
    public static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CellBridge.slnx"))) return directory.FullName;
        return Directory.GetCurrentDirectory();
    }

    public static string[] Arguments(string[] args, string environmentName)
    {
        if (args.Length != 0) return args;
        var configured = Environment.GetEnvironmentVariable(environmentName);
        return string.IsNullOrWhiteSpace(configured) ? [] : JsonSerializer.Deserialize<string[]>(configured)
            ?? throw new ArgumentException($"{environmentName} must be a JSON array of arguments.");
    }

    public static object Capture(params Assembly[] assemblies) => new
    {
        capturedUtc = DateTimeOffset.UtcNow,
        commit = Git("rev-parse", "HEAD"),
        workingTreeStatus = Git("status", "--porcelain", "--untracked-files=normal"),
        trackedDiffSha256 = Hash(System.Text.Encoding.UTF8.GetBytes(Git("diff", "HEAD", "--", "src", "benchmark", "tests/CellBridge.TestFixtures") ?? "")),
        runtime = RuntimeInformation.FrameworkDescription,
        os = RuntimeInformation.OSDescription,
        architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        processors = Environment.ProcessorCount,
        serverGc = System.Runtime.GCSettings.IsServerGC,
        buildConfigurations = assemblies.Distinct().ToDictionary(a => a.GetName().Name!, a => a.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration),
        assemblySha256 = assemblies.Distinct().ToDictionary(a => a.GetName().Name!, a => Hash(File.ReadAllBytes(a.Location)))
    };

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string? Git(params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.TrimEnd() : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
