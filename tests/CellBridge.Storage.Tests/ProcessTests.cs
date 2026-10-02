using System.Diagnostics;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.Conformance;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public class ProcessTests
{
    [Fact]
    public Task VolatileProviderConformance() => ProviderConformance.VerifyAsync(new(new InMemoryStateStore(), new InMemoryContentStore()));

    [PostgreSqlFact]
    public async Task PostgreSqlProviderConformance()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await ProviderConformance.VerifyAsync(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)));
    }

    [PostgreSqlFact]
    public async Task ProcessDeathBeforeAndAfterCommitPreservesPublicationBoundary()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        var provider = new StorageProvider(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/process-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create()))!;
        var first = StorageTests.Fixture("save-first");
        ((PutChangesSubRequestData)first.SubRequests.Single().Data!).ExpectedStorageIndex =
            (await StoredDocument.RestoreAsync(initial, provider.Content)).FilePartition.FileGraph.StorageIndex;
        await KillAtBoundary(initial.ResourceId, "before");
        var beforeCommit = (await provider.State.FindByResourceIdAsync(initial.ResourceId))!;
        Assert.Equal(initial.ContentVersion, beforeCommit.ContentVersion);
        Assert.Empty(beforeCommit.Receipts);
        await KillAtBoundary(initial.ResourceId, "after");
        // Acquire through a separately pooled instance after the writer process died.
        await using var restartedSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        var restartedProvider = new StorageProvider(new PostgreSqlStateStore(restartedSource), new PostgreSqlContentStore(restartedSource));
        var restarted = new CellBridgeDocumentService(restartedProvider);
        var retry = await restarted.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, first, new Dictionary<string, string>());
        Assert.False(Assert.Single(retry.Response.SubResponses).Status);
        Assert.Equal(initial.ContentVersion + 1, retry.State.ContentVersion);
        Assert.Single(retry.State.Receipts);
        var continued = await restarted.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            StorageTests.Fixture("save-second"), new Dictionary<string, string>());
        Assert.False(Assert.Single(continued.Response.SubResponses).Status);
        var restored = await StoredDocument.RestoreAsync(continued.State, restartedProvider.Content);
        Assert.Equal(restored.Content, restored.FilePartition.FileGraph.Materialize());
        Assert.Equal(initial.ResourceId, restored.TransitionId);
    }

    private static async Task KillAtBoundary(Guid id, string phase)
    {
        var signal = Path.Combine(Path.GetTempPath(), "cellbridge-probe-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Probe", "CellBridge.Storage.ProcessProbe.dll"));
        start.ArgumentList.Add(id.ToString()); start.ArgumentList.Add(phase); start.ArgumentList.Add(signal);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(signal))
            {
                if (process.HasExited) throw new InvalidOperationException("Probe exited before its boundary: " + await errors);
                await Task.Delay(25, timeout.Token);
            }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await output; await errors;
            if (File.Exists(signal)) File.Delete(signal);
        }
    }
}
