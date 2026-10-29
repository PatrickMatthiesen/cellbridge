using System.Collections.Immutable;
using System.Diagnostics;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Tests;

public static class NormalizedProcessProbeScenario
{
    public static async Task RunAsync(string connectionString, string probeDll, string fixtureRoot, string temporaryDirectory)
    {
        Directory.CreateDirectory(temporaryDirectory);
        await using var source = NpgsqlDataSource.Create(connectionString);
        foreach (var filesystem in new[] { false, true })
        foreach (var metadata in new[] { false, true })
        foreach (var phase in new[] { "before", "during", "after" })
        {
            var root = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                IContentStore content = filesystem ? new PostgreSqlFileSystemContentStore(source, root) : new PostgreSqlContentStore(source);
                IDocumentRepository repository = new PostgreSqlStateStore(source, validationContent: content);
                var provider = new StorageProvider(repository, content);
                var actor = new CellBridgeActor(new("tests:writer", "test-writer", "Test writer"), CanCreate: true);
                var service = new CellBridgeDocumentService(provider);
                var bytes = MinimalDocx.Create("process baseline");
                var created = (await service.CreateAsync("/process-" + Guid.NewGuid().ToString("N") + ".docx", bytes, actor))!;
                var baseline = (await repository.ReadControlAsync(created.ResourceId))!;
                FsshttpbCellRequest request;
                string? requestPath = null;
                if (metadata)
                {
                    var fixture = new GraphFixture("process metadata"u8.ToArray());
                    request = new() { DataElementPackage = new(), SubRequests =
                        { new(RequestTypes.PutChanges) { RequestId = 1, Data = new PutChangesSubRequestData { StorageIndex = fixture.Index } } } };
                    request.DataElementPackage.DataElements.AddRange(fixture.Elements);
                    var wire = new BinaryWriterEx(); request.Serialize(wire);
                    requestPath = Path.Combine(root, "metadata.bin"); await File.WriteAllBytesAsync(requestPath, wire.ToArray());
                }
                else request = SyntheticWorkload.Capture(fixtureRoot, "save-first");
                var partition = baseline.Partitions.Single(value => value.Kind == (metadata ? 1 : 0));
                await AddExpectedIndexAsync(request, repository, baseline.Head, partition);
                var signal = Path.Combine(root, "ready");
                var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var argument in new[] { probeDll, created.ResourceId.ToString(), phase, signal }) start.ArgumentList.Add(argument);
                if (requestPath is not null) start.ArgumentList.Add(requestPath);
                start.Environment["ConnectionStrings__cellbridge"] = connectionString;
                if (filesystem) start.Environment["CELLBRIDGE_PROCESS_CONTENT_ROOT"] = root;
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Process probe did not start.");
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                var watch = Stopwatch.StartNew();
                var reached = false;
                try
                {
                    while (!File.Exists(signal) && !process.HasExited && watch.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(25);
                    reached = File.Exists(signal);
                }
                finally
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                if (!reached) throw new InvalidOperationException($"Probe {filesystem}/{metadata}/{phase} did not reach its real publication boundary.\n" + await stdout + await stderr);
                var interrupted = (await repository.ReadControlAsync(created.ResourceId))!.Head;
                var committed = phase == "after";
                if (interrupted.Counters.SaveReceipts != (committed ? 1 : 0) || interrupted.LatestRevisionNumber != (committed ? 2UL : 1UL) ||
                    interrupted.ContentVersion != created.ContentVersion + (committed && !metadata ? 1u : 0u))
                    throw new InvalidOperationException("Terminated process exposed a partial or lost normalized publication.");
                await NormalizedProviderScenarios.CheckSnapshotAsync(repository, created.ResourceId);
                var retried = await service.ExecuteAsync(created.ResourceId, metadata ? DocumentPartitionKind.Metadata : DocumentPartitionKind.FileContents,
                    request, ImmutableDictionary<string, string>.Empty, actor);
                if (retried.Response.SubResponses.Any(response => response.Status) ||
                    (metadata ? !retried.AcceptedSaves.IsEmpty : retried.AcceptedSaves.Length != 1 || retried.AcceptedSaves[0].IsReplay != committed))
                    throw new InvalidOperationException("Fresh service could not resolve the interrupted process through its operation receipt.");
                var resolved = await NormalizedProviderScenarios.CheckSnapshotAsync(repository, created.ResourceId);
                if (resolved.Current.Head.Counters.SaveReceipts != 1 || resolved.Current.Head.Counters.Revisions != 2)
                    throw new InvalidOperationException("Process retry duplicated immutable publication records.");
                await using var historical = await provider.Packages.OpenReadAsync(created.Package);
                using var output = new MemoryStream(); await historical.CopyToAsync(output);
                if (!output.ToArray().SequenceEqual(bytes)) throw new InvalidOperationException("Process crash damaged the original package.");
                Console.WriteLine($"Process publication boundary passed: filesystem={filesystem}, metadata={metadata}, phase={phase}.");
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }
    private static async Task AddExpectedIndexAsync(FsshttpbCellRequest request, IDocumentRepository repository, DocumentHead head, PartitionHead partition)
    {
        var put = request.SubRequests.Select(operation => operation.Data).OfType<PutChangesSubRequestData>().Single();
        if (partition.StorageIndex is not { } index) { put.ExpectedStorageIndex = ExGuid.Null; return; }
        var mappings = await repository.ReadMappingsAsync(new(head.ResourceId, head.LifecycleGeneration, partition.Kind), partition.PublicationSequence);
        var serial = partition.StorageIndexSerial ?? throw new InvalidOperationException("Missing logical index serial.");
        var expected = new DataElement(DataElementType.StorageIndexDataElementData, StorageIds.Restore(index), new(serial.Guid, serial.Value))
        { Data = StorageIndexPatch.Serialize(mappings.Select(mapping => new StorageIndexMapping(
            mapping.Key.Kind == IndexedMappingKind.Manifest ? StreamObjectTypeHeaderStart.StorageIndexManifestMapping :
            mapping.Key.Kind == IndexedMappingKind.Cell ? StreamObjectTypeHeaderStart.StorageIndexCellMapping : StreamObjectTypeHeaderStart.StorageIndexRevisionMapping,
            mapping.Key.Revision is { } revision ? StorageIds.Restore(revision) : null,
            mapping.Key.Cell is { } cell ? new CellId(StorageIds.Restore(cell.LongId), StorageIds.Restore(cell.ShortId)) : null,
            StorageIds.Restore(mapping.Target), new(mapping.Serial.Guid, mapping.Serial.Value)))) };
        put.ExpectedStorageIndex = StorageIds.Restore(index);
        request.DataElementPackage!.DataElements.RemoveAll(element => element.DataElementExtendedGuid == expected.DataElementExtendedGuid);
        request.DataElementPackage.DataElements.Add(expected);
    }
}
