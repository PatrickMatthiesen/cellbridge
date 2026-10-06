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
    public Task ProcessDeathBeforeAndAfterCommitPreservesPublicationBoundary() => CheckPublicationBoundary(null);

    [PostgreSqlFact]
    public async Task FileSystemProcessDeathBeforeAndAfterCommitPreservesPublicationBoundary()
    {
        using var files = new PortabilityTests.TemporaryFiles();
        await CheckPublicationBoundary(files.Path("content"));
    }

    [QualificationFact]
    public async Task ConfiguredFileSystemProcessDeathBeforeAndAfterCommitPreservesPublicationBoundary()
    {
        var configured = Environment.GetEnvironmentVariable("CELLBRIDGE_QUALIFICATION_DIRECTORY");
        Assert.False(string.IsNullOrEmpty(configured));
        var root = Path.Combine(configured, "process-content-" + Guid.NewGuid().ToString("N"));
        try { await CheckPublicationBoundary(root); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static Task CheckPublicationBoundary(string? contentRoot) =>
        BudgetAndQueryTests.WithDatabase(new(), source => CheckPublicationBoundary(source, contentRoot));

    private static async Task CheckPublicationBoundary(NpgsqlDataSource source, string? contentRoot)
    {
        // DataSource.ConnectionString redacts the password. Retain the test
        // credential only in process environment, never in arguments or reports.
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!)
        { Database = new NpgsqlConnectionStringBuilder(source.ConnectionString).Database };
        IContentStore Content(NpgsqlDataSource db) => contentRoot is null
            ? new PostgreSqlContentStore(db) : new PostgreSqlFileSystemContentStore(db, contentRoot);
        var provider = new StorageProvider(new PostgreSqlStateStore(source), Content(source));
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/process-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.True(await new ExternalRevisionPublisher(provider, new UnusedDestination()).BindAsync(initial.ResourceId,
            initial.LifecycleGeneration, initial.StateVersion, Guid.NewGuid(), "process-destination", "baseline"));
        var first = StorageTests.Fixture("save-first");
        ((PutChangesSubRequestData)first.SubRequests.Single().Data!).ExpectedStorageIndex =
            (await StoredDocument.RestoreAsync(initial, provider.Content)).FilePartition.FileGraph.StorageIndex;
        await KillAtBoundary(initial.ResourceId, "before", contentRoot, connection.ConnectionString);
        var beforeCommit = (await provider.State.FindByResourceIdAsync(initial.ResourceId))!;
        Assert.Equal(initial.ContentVersion, beforeCommit.ContentVersion);
        Assert.Empty(beforeCommit.Receipts);
        Assert.Empty(beforeCommit.Publication!.Pending);
        Assert.Equal(initial.Content, beforeCommit.Content);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(initial.Partitions),
            System.Text.Json.JsonSerializer.Serialize(beforeCommit.Partitions));
        Assert.Equal(await provider.Content.ReadVerifiedAsync(initial.Content),
            await provider.Content.ReadVerifiedAsync(beforeCommit.Content));
        await KillAtBoundary(initial.ResourceId, "during", contentRoot, connection.ConnectionString);
        var rolledBack = (await provider.State.FindByResourceIdAsync(initial.ResourceId))!;
        Assert.Equal(beforeCommit.StateVersion, rolledBack.StateVersion);
        Assert.Equal(beforeCommit.Content, rolledBack.Content);
        Assert.Empty(rolledBack.Receipts);
        Assert.Empty(rolledBack.Publication!.Pending);
        await KillAtBoundary(initial.ResourceId, "after", contentRoot, connection.ConnectionString);
        // Acquire through a separately pooled instance after the writer process died.
        await using var restartedSource = NpgsqlDataSource.Create(connection.ConnectionString);
        var restartedProvider = new StorageProvider(new PostgreSqlStateStore(restartedSource), Content(restartedSource));
        var restarted = new CellBridgeDocumentService(restartedProvider);
        // Verify the committed state BEFORE retry. Otherwise a fresh retry commit
        // could mask loss of the original publication.
        var committed = (await restartedProvider.State.FindByResourceIdAsync(initial.ResourceId))!;
        Assert.Equal(initial.ContentVersion + 1, committed.ContentVersion);
        Assert.Single(committed.Receipts);
        Assert.Single(committed.Publication!.Pending);
        Assert.NotEqual(initial.Content, committed.Content);
        var committedDocument = await StoredDocument.RestoreAsync(committed, restartedProvider.Content);
        Assert.Equal(await restartedProvider.Content.ReadVerifiedAsync(committed.Content), committedDocument.Content);
        Assert.Equal(committedDocument.Content, committedDocument.FilePartition.FileGraph.Materialize());
        var retry = await restarted.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, first, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(retry.Response.SubResponses).Status);
        Assert.Equal(initial.ContentVersion + 1, retry.State.ContentVersion);
        Assert.Single(retry.State.Receipts);
        Assert.Single(retry.State.Publication!.Pending);
        Assert.Equal(committed.StateVersion, retry.State.StateVersion);
        Assert.Equal(committed.Content, retry.State.Content);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(committed.Receipts),
            System.Text.Json.JsonSerializer.Serialize(retry.State.Receipts));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(committed.Publication),
            System.Text.Json.JsonSerializer.Serialize(retry.State.Publication));
        var continued = await restarted.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            StorageTests.Fixture("save-second"), new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(continued.Response.SubResponses).Status);
        Assert.Equal(2, continued.State.Publication!.Pending.Length);
        var restored = await StoredDocument.RestoreAsync(continued.State, restartedProvider.Content);
        Assert.Equal(restored.Content, restored.FilePartition.FileGraph.Materialize());
        Assert.Equal(initial.ResourceId, restored.TransitionId);
    }

    private sealed class UnusedDestination : IExternalRevisionDestination
    {
        public ValueTask<ExternalDeliveryResult> CompareExchangeAsync(ExternalDeliveryRequest request, Stream content,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Crash probe must only queue content.");
    }

    private static async Task KillAtBoundary(Guid id, string phase, string? contentRoot = null, string? connectionString = null)
    {
        var signal = Path.Combine(Path.GetTempPath(), "cellbridge-probe-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Remove("CELLBRIDGE_PROCESS_CONTENT_ROOT");
        if (connectionString is not null) start.Environment["ConnectionStrings__cellbridge"] = connectionString;
        if (contentRoot is not null) start.Environment["CELLBRIDGE_PROCESS_CONTENT_ROOT"] = contentRoot;
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

public sealed class QualificationFactAttribute : FactAttribute
{
    public QualificationFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CELLBRIDGE_QUALIFICATION_DIRECTORY")))
            Skip = "Set the disposable database and CELLBRIDGE_QUALIFICATION_DIRECTORY for deployment-root qualification.";
    }
}
