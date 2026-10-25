using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using CellBridge.TestInfrastructure;

namespace CellBridge.Storage.Tests;

public sealed class DatabaseRecoveryFixture : IAsyncLifetime
{
    private DistributedApplication? application;
    private IDistributedApplicationTestingBuilder? testingBuilder;
    private readonly string marker = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, string?> previous = new();
    private string? containerId;

    public async Task InitializeAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var builder = testingBuilder = await PostgreSqlEnvironment.CreateBuilderAsync(timeout.Token);
        builder.Resources.Remove(builder.Resources.Single(resource => resource.Name == "webdemo"));
        // Each recovery test creates and initializes its own database. This
        // fixture needs only the server and the database used to connect to it.
        builder.Resources.Remove(builder.Resources.Single(resource => resource.Name == "storage-init"));
        // The fault tests restart this exact container. PGDATA uses its writable
        // layer, and the ownership checks below limit cleanup to this run.
        builder.CreateResourceBuilder<PostgresServerResource>("storage")
            .WithContainerName($"cellbridge-recovery-{marker}")
            .WithLifetime(ContainerLifetime.Persistent)
            .WithEnvironment("PGDATA", $"/cellbridge-recovery-data/{marker}")
            .WithEnvironment("CELLBRIDGE_RECOVERY_RUN", marker)
            .WithContainerRuntimeArgs("--label", $"cellbridge.recovery={marker}",
                "--tmpfs", "/var/lib/postgresql:rw");
        application = await builder.BuildAsync(timeout.Token);
        try
        {
            await application.StartAsync(timeout.Token);
            await application.ResourceNotifications.WaitForResourceHealthyAsync("cellbridge", timeout.Token);
            containerId = (await Docker("inspect", "--format", "{{.Id}}", "cellbridge-recovery-" + marker)).Trim();
            var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/recovery", marker));
            Directory.CreateDirectory(directory);
            Set("CELLBRIDGE_RECOVERY_RUN", marker);
            Set("CELLBRIDGE_RECOVERY_CONTAINER", containerId);
            Set("CELLBRIDGE_RECOVERY_OUTPUT", directory);
            Set("ConnectionStrings__cellbridge", (await application.GetConnectionStringAsync("cellbridge", timeout.Token))!);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    private void Set(string name, string value)
    {
        previous.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    public async Task DisposeAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var ownedBuilder = testingBuilder;
        testingBuilder = null;
        foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
        try
        {
            if (application is not null) { await application.DisposeAsync(); application = null; }
        }
        finally
        {
            // An interrupted startup may have created the container before its ID was read.
            var ids = await Docker("ps", "-aq", "--filter", "label=cellbridge.recovery=" + marker);
            foreach (var candidate in ids.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var owner = (await Docker("inspect", "--format", "{{index .Config.Labels \"cellbridge.recovery\"}}", candidate)).Trim();
                if (owner != marker) throw new InvalidOperationException("Recovery container ownership changed.");
                if (containerId is not null && candidate != containerId && !containerId.StartsWith(candidate, StringComparison.Ordinal))
                    throw new InvalidOperationException("An unexpected recovery container appeared.");
                await Docker("rm", "--force", candidate);
            }
        }
    }

    private static async Task<string> Docker(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            throw;
        }
        await error;
        if (process.ExitCode != 0) throw new InvalidOperationException($"Recovery container command failed with exit code {process.ExitCode}.");
        return await output;
    }
}
