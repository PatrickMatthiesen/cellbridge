using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class AuthorizationRecoveryIntegrationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task HostBindingInAnyNormalizedDocumentRejectsRecoveryBeforeDestinationMutation(int boundDocument)
    {
        var root = Path.Combine(Path.GetTempPath(), "cellbridge-auth-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = StorageTestDocuments.Memory();
            var service = new CellBridgeDocumentService(source);
            for (var index = 0; index < 2; index++)
            {
                var head = (await service.CreateAsync("/recovery-" + index + ".docx", MinimalDocx.Create("initial"), TestActor.Value))!;
                await RepositoryTestPublications.SaveAsync(service, head, MinimalDocx.Create("saved"));
            }
            var path = Path.Combine(root, "bound.zip");
            var context = new RecoveryContext(RecoveryAuthorizationContext.StoredGrants("test-subjects"), []);
            var portable = new PortableArchive();
            await portable.ExportAsync(source, path, context);
            await portable.ValidateAsync(path, context);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("manifest.json")!;
                JsonNode manifest;
                using (var reader = new StreamReader(entry.Open())) manifest = JsonNode.Parse(await reader.ReadToEndAsync())!;
                entry.Delete();
                var snapshots = manifest["State"]!["Snapshots"]!.AsArray();
                var document = snapshots[boundDocument]!.Deserialize<NormalizedDocumentSnapshot>(RecoveryJson.Options)!;
                var bound = document with { Current = document.Current with { Head = document.Current.Head with
                    { Security = document.Current.Head.Security with { AuthorizationPolicy = new("host-permissions", 1, 1) } } } };
                bound = bound with { Current = bound.Current with { Head = bound.Current.Head with
                    { Counters = bound.Current.Head.Counters with { StoredBytes = NormalizedDocumentRecovery.StoredBytes(bound) } } } };
                snapshots[boundDocument] = JsonSerializer.SerializeToNode(bound);
                using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
                await writer.WriteAsync(manifest.ToJsonString());
            }
            var writes = new CountWrites(new InMemoryContentStore());
            var repository = new InMemoryDocumentRepository(writes);
            var destination = new StorageProvider(repository, writes);
            var before = await repository.CaptureRecoveryAsync();
            await Assert.ThrowsAsync<NotSupportedException>(() => portable.ImportAsync(destination, path, context).AsTask());
            Assert.Equal(0, writes.Writes);
            Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await repository.CaptureRecoveryAsync()));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class CountWrites(IContentStore inner) : IContentStore
    {
        public int Writes { get; private set; }
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken ct = default)
        { Writes++; return await inner.WriteAsync(source, ct); }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken ct = default) => inner.OpenReadAsync(handle, ct);
    }
}
