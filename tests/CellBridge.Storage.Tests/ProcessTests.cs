using System.Diagnostics;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestInfrastructure;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public class ProcessTests
{
    [Fact]
    public Task VolatileProviderConformance() => ProviderConformance.VerifyAsync(StorageTestDocuments.Memory());

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
        var root = Path.Combine(Path.GetTempPath(), "cellbridge-process-" + Guid.NewGuid().ToString("N"));
        try { await CheckPublicationBoundary(root); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
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
        RepositoryTestDatabase.WithDatabase(new(), source => CheckPublicationBoundary(source, contentRoot));

    private static async Task CheckPublicationBoundary(NpgsqlDataSource source, string? contentRoot)
    {
        // DataSource.ConnectionString redacts the password. Retain the test
        // credential only in process environment, never in arguments or reports.
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!)
        { Database = new NpgsqlConnectionStringBuilder(source.ConnectionString).Database };
        IContentStore Content(NpgsqlDataSource db) => contentRoot is null
            ? new PostgreSqlContentStore(db) : new PostgreSqlFileSystemContentStore(db, contentRoot);
        var content = Content(source);
        var provider = new StorageProvider(new PostgreSqlStateStore(source, validationContent: content), content);
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/process-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.True(await new ExternalRevisionPublisher(provider, new UnusedDestination()).BindAsync(initial.ResourceId,
            initial.LifecycleGeneration, initial.StateVersion, Guid.NewGuid(), "process-destination", "baseline"));
        var baseline = (await provider.Documents.ReadControlAsync(initial.ResourceId))!;
        var first = SyntheticWorkload.Capture(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "save-first");
        await AttachExpectedIndexAsync(provider, initial, first);
        await KillAtBoundary(initial.ResourceId, "before", contentRoot, connection.ConnectionString);
        var beforeCommit = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
        Assert.Equal(initial.ContentVersion, beforeCommit.ContentVersion);
        Assert.Equal(0, beforeCommit.Counters.SaveReceipts);
        Assert.Equal(0, beforeCommit.Counters.PendingDeliveries);
        Assert.Equal(initial.Package, beforeCommit.Package);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(baseline.Partitions),
            System.Text.Json.JsonSerializer.Serialize((await provider.Documents.ReadControlAsync(initial.ResourceId))!.Partitions));
        Assert.Equal(await StorageTestDocuments.PackageAsync(provider, initial.Package),
            await StorageTestDocuments.PackageAsync(provider, beforeCommit.Package));
        await KillAtBoundary(initial.ResourceId, "during", contentRoot, connection.ConnectionString);
        var rolledBack = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
        Assert.Equal(beforeCommit.StateVersion, rolledBack.StateVersion);
        Assert.Equal(beforeCommit.Package, rolledBack.Package);
        Assert.Equal(0, rolledBack.Counters.SaveReceipts);
        Assert.Equal(0, rolledBack.Counters.PendingDeliveries);
        await KillAtBoundary(initial.ResourceId, "after", contentRoot, connection.ConnectionString);
        // Acquire through a separately pooled instance after the writer process died.
        await using var restartedSource = NpgsqlDataSource.Create(connection.ConnectionString);
        var restartedContent = Content(restartedSource);
        var restartedProvider = new StorageProvider(new PostgreSqlStateStore(restartedSource, validationContent: restartedContent), restartedContent);
        var restarted = new CellBridgeDocumentService(restartedProvider);
        // Verify the committed state BEFORE retry. Otherwise a fresh retry commit
        // could mask loss of the original publication.
        var committed = (await restartedProvider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
        Assert.Equal(initial.ContentVersion + 1, committed.ContentVersion);
        Assert.Equal(1, committed.Counters.SaveReceipts);
        Assert.Equal(1, committed.Counters.PendingDeliveries);
        Assert.NotEqual(initial.Package, committed.Package);
        var committedGraph = await StorageTestDocuments.GraphAsync(restarted, committed);
        Assert.Equal(await StorageTestDocuments.PackageAsync(restartedProvider, committed.Package), committedGraph.Materialize());
        var committedSnapshot = (await restartedProvider.Documents.ExportDocumentAsync(initial.ResourceId))!;
        var retry = await restarted.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, first, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(retry.Response.SubResponses).Status);
        Assert.Equal(initial.ContentVersion + 1, retry.State.ContentVersion);
        Assert.Equal(1, retry.State.Counters.SaveReceipts);
        Assert.Equal(1, retry.State.Counters.PendingDeliveries);
        Assert.Equal(committed.StateVersion, retry.State.StateVersion);
        Assert.Equal(committed.Package, retry.State.Package);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(committedSnapshot.Receipts),
            System.Text.Json.JsonSerializer.Serialize((await restartedProvider.Documents.ExportDocumentAsync(initial.ResourceId))!.Receipts));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(committedSnapshot.Deliveries),
            System.Text.Json.JsonSerializer.Serialize((await restartedProvider.Documents.ExportDocumentAsync(initial.ResourceId))!.Deliveries));
        var continued = await restarted.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            await StorageTestDocuments.SaveRequestAsync(restarted, retry.State, MinimalDocx.Create("continued after crash")), new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(continued.Response.SubResponses).Status);
        Assert.Equal(2, continued.State.Counters.PendingDeliveries);
        var graph = await StorageTestDocuments.GraphAsync(restarted, continued.State);
        Assert.Equal(await StorageTestDocuments.PackageAsync(restartedProvider, continued.State.Package), graph.Materialize());
        Assert.Equal(initial.ResourceId, continued.State.ResourceId);
    }

    private static async Task AttachExpectedIndexAsync(StorageProvider provider, DocumentHead head, FsshttpbCellRequest request)
    {
        var partition = (await provider.Documents.ReadPartitionAsync(head.ResourceId, new(0)))!.Partition;
        var index = partition.StorageIndex!; var serial = partition.StorageIndexSerial!;
        var mappings = await provider.Documents.ReadMappingsAsync(new(head.ResourceId, head.LifecycleGeneration, 0), partition.PublicationSequence);
        var expected = new DataElement(DataElementType.StorageIndexDataElementData, StorageIds.Restore(index), new(serial.Guid, serial.Value))
        { Data = StorageIndexPatch.Serialize(mappings.Select(mapping => new StorageIndexMapping(
            mapping.Key.Kind == IndexedMappingKind.Manifest ? StreamObjectTypeHeaderStart.StorageIndexManifestMapping :
            mapping.Key.Kind == IndexedMappingKind.Cell ? StreamObjectTypeHeaderStart.StorageIndexCellMapping : StreamObjectTypeHeaderStart.StorageIndexRevisionMapping,
            mapping.Key.Revision is { } revision ? StorageIds.Restore(revision) : null,
            mapping.Key.Cell is { } cell ? new CellId(StorageIds.Restore(cell.LongId), StorageIds.Restore(cell.ShortId)) : null,
            StorageIds.Restore(mapping.Target), new(mapping.Serial.Guid, mapping.Serial.Value)))) };
        ((PutChangesSubRequestData)request.SubRequests.Single().Data!).ExpectedStorageIndex = StorageIds.Restore(index);
        request.DataElementPackage!.DataElements.RemoveAll(element => element.DataElementExtendedGuid == expected.DataElementExtendedGuid);
        request.DataElementPackage.DataElements.Add(expected);
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
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CELLBRIDGE_QUALIFICATION_DIRECTORY")))
            Skip = "Set CELLBRIDGE_QUALIFICATION_DIRECTORY for deployment-root qualification.";
    }
}
