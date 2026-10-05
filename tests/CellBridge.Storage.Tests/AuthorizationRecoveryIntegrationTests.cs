using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;

namespace CellBridge.Storage.Tests;

public sealed class AuthorizationRecoveryIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownHostBindingInAnySnapshotRejectsRecoveryBeforeDestinationMutation(bool historical)
    {
        using var files = new PortabilityTests.TemporaryFiles();
        var source = PortabilityTests.Memory();
        await PortabilityTests.History(source);
        var portable = new PortableArchive();
        await portable.ExportAsync(source, files.Path("valid.zip"), PortabilityTests.Context);
        File.Copy(files.Path("valid.zip"), files.Path("bound.zip"));
        using (var zip = ZipFile.Open(files.Path("bound.zip"), ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("manifest.json")!;
            JsonNode manifest;
            using (var reader = new StreamReader(entry.Open()))
                manifest = JsonNode.Parse(await reader.ReadToEndAsync())!;
            entry.Delete();
            var snapshots = manifest["State"]!["Snapshots"]!.AsArray();
            var bound = snapshots[0]!;
            if (historical)
            {
                bound = bound.DeepClone();
                bound["StateVersion"] = bound["StateVersion"]!.GetValue<long>() - 1;
                snapshots.Insert(0, bound);
            }
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            await writer.WriteAsync(manifest.ToJsonString());
        }
        // Establish that the historical-snapshot fixture is independently valid.
        await portable.ValidateAsync(files.Path("bound.zip"), PortabilityTests.Context);
        using (var zip = ZipFile.Open(files.Path("bound.zip"), ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("manifest.json")!;
            JsonNode manifest;
            using (var reader = new StreamReader(entry.Open()))
                manifest = JsonNode.Parse(await reader.ReadToEndAsync())!;
            entry.Delete();
            manifest["State"]!["Snapshots"]![0]!["Security"]!["AuthorizationPolicy"] =
                JsonSerializer.SerializeToNode(new DocumentAuthorizationBinding("host-permissions", 1, 1));
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            await writer.WriteAsync(manifest.ToJsonString());
        }
        var state = new InMemoryStateStore();
        var writes = new PortabilityTests.CountWrites(new InMemoryContentStore());
        var destination = new StorageProvider(state, writes);
        var before = await state.CaptureRecoveryAsync();
        long bytes = state.Budget.StoredBytes, documents = state.Budget.DocumentCount;
        await Assert.ThrowsAsync<NotSupportedException>(() => portable.ImportAsync(destination,
            files.Path("bound.zip"), PortabilityTests.Context).AsTask());
        Assert.Equal(0, writes.Writes);
        Assert.Equal(bytes, state.Budget.StoredBytes);
        Assert.Equal(documents, state.Budget.DocumentCount);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await state.CaptureRecoveryAsync()));
    }
}
