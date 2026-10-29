using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

public sealed class NormalizedPortableArchiveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArchiveRoundTripRemapsPhysicalKeysAndRetainsReceiptsDeliveryAndDerivedPackage(bool loseCommitReply)
    {
        var sourceContent = new MemoryContent("source/");
        var document = await Snapshot(sourceContent);
        var source = Repository(document);
        var destination = Repository();
        ((RepositoryProxy)(object)destination).LoseCommitReply = loseCommitReply;
        var destinationContent = new MemoryContent("destination-long-prefix/");
        var context = Context(document);
        var archive = new PortableArchive();
        var path = Path.Combine(Path.GetTempPath(), "cellbridge-normalized-portable-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var info = await archive.ExportAsync(new(source, sourceContent), path, context);
            var receipt = await archive.ImportAsync(new(destination, destinationContent), path, context);
            Assert.Equal(info.ArchiveId, receipt.OperationId);
            var writes = destinationContent.Writes;
            Assert.Equal(receipt, await archive.ImportAsync(new(destination, destinationContent), path, context));
            Assert.Equal(writes, destinationContent.Writes);
            var recovered = Assert.Single(((RepositoryProxy)(object)destination).Snapshot.Snapshots);
            Assert.Equal(document.Current.Head.Package, recovered.Current.Head.Package);
            Assert.Equal(document.Receipts[0].Digest, recovered.Receipts[0].Digest);
            Assert.Equal(Assert.Single(document.Deliveries), Assert.Single(recovered.Deliveries));
            Assert.All(recovered.ContentReferences, reference => Assert.StartsWith("destination-long-prefix/", reference.Handle.Key));
            Assert.NotEqual(document.Current.Head.Counters.StoredBytes, recovered.Current.Head.Counters.StoredBytes);
            Assert.Equal(NormalizedDocumentRecovery.StoredBytes(recovered), recovered.Current.Head.Counters.StoredBytes);
            var reader = new NormalizedSnapshotGraphSource(recovered, destinationContent, new()).PackageReader();
            await using var stream = await reader.OpenReadAsync(recovered.Current.Head.Package);
            Assert.Equal(recovered.Current.Head.Package.Length, stream.Length);
            Assert.Single(((RepositoryProxy)(object)destination).Snapshot.Receipts);
            using var zip = ZipFile.OpenRead(path);
            using var manifest = new StreamReader(zip.GetEntry("manifest.json")!.Open());
            var json = JsonNode.Parse(await manifest.ReadToEndAsync())!;
            Assert.Equal(2, json["ArchiveVersion"]!.GetValue<int>());
            Assert.Null(json["State"]!["Heads"]);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("object")]
    [InlineData("package")]
    [InlineData("duplicate")]
    public async Task CorruptArchiveDoesNotStageDestinationContentOrPublishState(string corruption)
    {
        var content = new MemoryContent("source/");
        var document = await Snapshot(content);
        var archive = new PortableArchive();
        var path = Path.Combine(Path.GetTempPath(), "cellbridge-normalized-corrupt-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await archive.ExportAsync(new(Repository(document), content), path, Context(document));
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("manifest.json")!;
                string text;
                using (var reader = new StreamReader(entry.Open())) text = await reader.ReadToEndAsync();
                entry.Delete();
                var json = JsonNode.Parse(text)!;
                if (corruption == "object")
                    json["State"]!["Snapshots"]![0]!["Objects"]![0]!["DefinitionDigest"] = new string('0', 64);
                else if (corruption == "package")
                {
                    json["State"]!["Snapshots"]![0]!["Current"]!["Head"]!["Package"]!["Sha256"] = new string('0', 64);
                    json["State"]!["Snapshots"]![0]!["ApplicationRevisions"]![0]!["Package"]!["Sha256"] = new string('0', 64);
                    json["State"]!["Snapshots"]![0]!["Deliveries"]![0]!["Package"]!["Sha256"] = new string('0', 64);
                }
                else
                {
                    using var duplicate = new StreamWriter(zip.CreateEntry("objects/00000000").Open());
                    duplicate.Write("duplicate");
                }
                using var output = new StreamWriter(zip.CreateEntry("manifest.json").Open());
                await output.WriteAsync(json.ToJsonString());
            }
            var target = Repository(); var targetContent = new MemoryContent("target/");
            await Assert.ThrowsAsync<StorageCorruptionException>(async () =>
                await archive.ImportAsync(new(target, targetContent), path, Context(document)));
            Assert.Equal(0, targetContent.Writes);
            Assert.Empty(((RepositoryProxy)(object)target).Snapshot.Snapshots);
            Assert.Empty(((RepositoryProxy)(object)target).Snapshot.Receipts);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ActiveLeaseAndMismatchedDeliveryContextRejectBeforeDestinationStaging()
    {
        var content = new MemoryContent("source/");
        var document = await Snapshot(content);
        document = document with { Current = document.Current with { Coordination = document.Current.Coordination with
            { Exclusive = new("lease", null, DateTime.UtcNow.AddHours(1), 0, null, "owner") } } };
        document = document with { Current = document.Current with { Head = document.Current.Head with
            { Counters = document.Current.Head.Counters with { StoredBytes = NormalizedDocumentRecovery.StoredBytes(document) } } } };
        var path = Path.Combine(Path.GetTempPath(), "cellbridge-normalized-lease-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var archive = new PortableArchive();
            await archive.ExportAsync(new(Repository(document), content), path, Context(document));
            var destination = Repository(); var target = new MemoryContent("target/");
            await Assert.ThrowsAsync<RecoveryConflictException>(async () =>
                await archive.ImportAsync(new(destination, target), path, Context(document)));
            await Assert.ThrowsAsync<RecoveryConflictException>(async () =>
                await archive.ValidateAsync(path, Context(document) with { Authorization = RecoveryAuthorizationContext.StoredGrants("different-subjects") }));
            Assert.Equal(0, target.Writes);
            Assert.Empty(((RepositoryProxy)(object)destination).Snapshot.Snapshots);
        }
        finally { File.Delete(path); }
    }

    private static RecoveryContext Context(NormalizedDocumentSnapshot snapshot) => new(
        RecoveryAuthorizationContext.StoredGrants("test-subjects"),
        [new(snapshot.Current.Head.Delivery!.BindingId, "destination", "test-delivery-receipts")]);

    private static async Task<NormalizedDocumentSnapshot> Snapshot(MemoryContent content)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in new[] { "[Content_Types].xml", "word/document.xml" })
            {
                using var entry = new StreamWriter(zip.CreateEntry(name).Open()); entry.Write("<root />");
            }
        var owner = new SubjectIdentity("owner", "owner", "Owner");
        var created = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), "/archive.docx", bytes.ToArray(),
            DocumentSecurity.Create(owner), content, new());
        var response = new FsshttpbResponse { SubResponses = { new() { RequestId = 1, RequestType = RequestTypes.PutChanges,
            Data = new PutChangesSubResponseData { PutChangesResponse = new(), KnowledgeBytes = BinaryKnowledgeBuilder.FromRanges([], ExGuid.Null, 0) } } } };
        var responseHandle = await content.WriteAsync(new MemoryStream(response.ToByteArray()));
        var head = created.Head with
        {
            Delivery = new(Guid.NewGuid(), "destination", "external-1", 2),
            Counters = created.Head.Counters with { SaveReceipts = 1, PendingDeliveries = 1, PendingDeliveryBytes = created.Head.Package.Length },
        };
        var parts = created.Partitions;
        var physical = parts.SelectMany(partition => partition.AddedElements).Select(element => element.Payload).Append(responseHandle).DistinctBy(handle => handle.Key);
        var snapshot = new NormalizedDocumentSnapshot(new(head, head.ModifiedUtc, created.Coordination, created.Editors,
            parts.Select(partition => partition.After).ToImmutableArray()),
            parts.Select(partition => new NormalizedPartitionRoot(partition.After, new(partition.After.PublicationSequence, 0, 0))).ToImmutableArray(),
            parts.SelectMany(partition => partition.AddedElements.Select(element => new ScopedGraphElement(partition.EffectiveChanges.Scope, element))).ToImmutableArray(),
            parts.SelectMany(partition => partition.AddedElements.Select(element => new NormalizedElementVisibility(partition.EffectiveChanges.Scope, element.Id, 1, null))).ToImmutableArray(),
            parts.SelectMany(partition => partition.Revisions).ToImmutableArray(), parts.SelectMany(partition => partition.Objects).ToImmutableArray(),
            parts.SelectMany(partition => partition.Cells).ToImmutableArray(), parts.SelectMany(partition => partition.StorageManifests).ToImmutableArray(),
            parts.SelectMany(partition => partition.Mappings.Select(mapping => new NormalizedMappingInterval(mapping, 1, null))).ToImmutableArray(),
            parts.Where(partition => partition.After.StorageIndex is not null).Select(partition => new NormalizedMappingRoot(partition.EffectiveChanges.Scope,
                partition.After.StorageIndex!, partition.After.StorageIndexSerial!, 1) { Head = partition.After }).ToImmutableArray(),
            parts.SelectMany(partition => partition.Mappings).ToImmutableArray(), parts.SelectMany(partition => partition.Checkpoint!.Bindings).ToImmutableArray(),
            parts.Select(partition => partition.EffectiveChanges).ToImmutableArray(), parts.Select(partition => partition.Checkpoint!).ToImmutableArray(),
            [created.InitialRevision], [new(new(1, SaveOperationFamily.Protocol, 0, "retained-save"), "digest", owner.Subject, head.ContentVersion, 1, null, responseHandle)],
            [new(Guid.NewGuid(), head.ResourceId, 1, 1, head.ContentVersion, head.Package)], [head.PathKey],
            physical.Select(handle => new NormalizedContentReference(handle, 1)).ToImmutableArray());
        return snapshot with { Current = snapshot.Current with { Head = head with
            { Counters = head.Counters with { StoredBytes = NormalizedDocumentRecovery.StoredBytes(snapshot) } } } };
    }

    public interface IRecoveryRepository : IDocumentRepository, IIndexedRepositoryRecovery;
    public class RepositoryProxy : DispatchProxy
    {
        public ProviderSnapshot Snapshot = new([], []);
        public bool LoseCommitReply;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method!.Name switch
        {
            "get_Durable" or "get_Shared" => false,
            nameof(IProviderRecoveryStore.CheckRecoveryAsync) => ValueTask.CompletedTask,
            nameof(IProviderRecoveryStore.CaptureRecoveryAsync) => ValueTask.FromResult(Snapshot),
            nameof(IProviderRecoveryStore.FindRecoveryReceiptAsync) => ValueTask.FromResult(Snapshot.Receipts.SingleOrDefault(receipt => receipt.OperationId == (Guid)arguments![0]!)),
            nameof(IProviderRecoveryStore.ImportRecoveryAsync) => Import((ProviderSnapshot)arguments![0]!, (RecoveryReceipt)arguments[1]!),
            _ => throw new NotSupportedException(method.Name),
        };
        private ValueTask<RecoveryReceipt> Import(ProviderSnapshot snapshot, RecoveryReceipt receipt)
        {
            ProviderRecovery.CheckAdmission(snapshot, new(), DateTime.UtcNow);
            if (!Snapshot.Snapshots.IsEmpty || !Snapshot.Receipts.IsEmpty) throw new RecoveryConflictException("Destination is not empty.");
            Snapshot = snapshot with { Receipts = snapshot.Receipts.Add(receipt) };
            if (LoseCommitReply) throw new StorageUnavailableException("The commit reply was lost.");
            return ValueTask.FromResult(receipt);
        }
    }
    private static IRecoveryRepository Repository(params NormalizedDocumentSnapshot[] snapshots)
    {
        var proxy = DispatchProxy.Create<IRecoveryRepository, RepositoryProxy>();
        ((RepositoryProxy)(object)proxy).Snapshot = new(snapshots.ToImmutableArray(), []);
        return proxy;
    }
    private sealed class MemoryContent(string prefix) : IContentStore
    {
        private readonly Dictionary<string, byte[]> values = [];
        public int Writes { get; private set; }
        public bool Durable => false;
        public bool Shared => false;
        public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream(); await source.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray(); var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            values[prefix + hash] = bytes; Writes++;
            return new(prefix + hash, bytes.Length, hash);
        }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(values[handle.Key], writable: false));
    }
}
