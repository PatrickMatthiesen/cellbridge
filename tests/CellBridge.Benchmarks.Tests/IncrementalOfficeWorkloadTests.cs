using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;

namespace CellBridge.Benchmarks.Tests;

public sealed class IncrementalOfficeWorkloadTests
{
    [Theory]
    [InlineData(256, 4)]
    [InlineData(1536, 4)]
    [InlineData(256, 16)]
    [InlineData(1536, 64)]
    public async Task TwoSmallEditsAndTheirRetriesReuseUnchangedPartsAndPersistChangedPackages(int packageKiB, int changedPartKiB)
    {
        var workload = new IncrementalOfficeWorkload(packageKiB, changedPartKiB: changedPartKiB);
        var provider = BenchmarkTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var actor = new CellBridgeActor(new SubjectIdentity("synthetic:writer", "writer", "Synthetic writer"), CanCreate: true);
        var state = (await service.CreateAsync("/incremental.docx", workload.CreateContent(), actor))!;
        // An imported file initially has one whole-file object. Install independently reusable
        // chunks once before measuring small edits, as the HTTP benchmark does during setup.
        var current = await BenchmarkTestDocuments.GraphAsync(service, state, actor);
        var seed = workload.CreateSave(current, 1);
        Assert.Equal(0, seed.ReusedChunks);
        Assert.Equal(seed.Content.Length, seed.ChangedChunkBytes);
        var seeded = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            Decode(seed.Binary), new Dictionary<string, string>(), actor);
        AssertSuccess(seeded.Response);
        Assert.True(seeded.State.ContentVersion > state.ContentVersion);
        state = seeded.State;
        var file = (await provider.Documents.ReadPartitionAsync(state.ResourceId, new(0) { IncludeCatalog = true }))!;
        var stableGroups = seed.Request.DataElementPackage!.DataElements
            .Where(element => element.DataElementType == DataElementType.ObjectGroupDataElementData &&
                ObjectGroupDataElement.Parse(element).Objects.Any(item => item.PayloadKind == ObjectGroupPayloadKind.Raw &&
                    item.Content.Length > workload.ChangedDocumentPartBytes + 4096))
            .Select(element => StorageIds.Capture(element.DataElementExtendedGuid)).ToArray();
        Assert.NotEmpty(stableGroups);
        var stableHandles = file.Elements.Where(element => stableGroups.Contains(element.Id))
            .ToDictionary(element => element.Id, element => element.Payload);

        for (var revision = 2; revision <= 3; revision++)
        {
            current = await BenchmarkTestDocuments.GraphAsync(service, state, actor);
            var save = workload.CreateSave(current, revision);
            Assert.True(save.ReusedChunks > 0);
            Assert.Equal(1, save.SentChunks);
            Assert.InRange(save.ChangedChunkBytes, workload.ChangedDocumentPartBytes, workload.ChangedDocumentPartBytes + 4096);
            Assert.True(save.Binary.Length < workload.ChangedDocumentPartBytes + 8192);
            Assert.True(save.Binary.Length < save.Content.Length / 2,
                $"The {save.Binary.Length}-byte save should be much smaller than the {save.Content.Length}-byte package.");
            Assert.NotEqual(state.Package.Sha256, Digest(save.Content));
            foreach (var id in stableGroups)
                Assert.DoesNotContain(save.Request.DataElementPackage!.DataElements,
                    element => StorageIds.Capture(element.DataElementExtendedGuid) == id);
            var previous = state;
            var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
                Decode(save.Binary), new Dictionary<string, string>(), actor);
            AssertSuccess(result.Response);
            var applied = ((PutChangesSubResponseData)result.Response.SubResponses.Single().Data!).PutChangesResponse!.AppliedStorageIndexID;
            var clientGraph = BenchmarkClientGraph.ApplyAcceptedGraph(current, save.Request, result.Response, applied);
            Assert.Equal(save.Content, clientGraph.Materialize());
            Assert.True(clientGraph.AnalyzeRetention().Complete);
            state = result.State;
            Assert.True(state.ContentVersion > previous.ContentVersion);
            Assert.True(state.StateVersion > previous.StateVersion);
            Assert.Equal(Digest(save.Content), state.Package.Sha256);
            var persisted = await BenchmarkTestDocuments.PackageAsync(provider, state.Package);
            Assert.Equal(save.Content, persisted);
            AssertPackage(persisted, packageKiB, revision, workload.ChangedDocumentPartBytes);
            var restored = await BenchmarkTestDocuments.GraphAsync(service, state, actor);
            Assert.Equal(save.Content, restored.Materialize());
            Assert.True(restored.AnalyzeRetention().Complete);
            var savedPartition = (await provider.Documents.ReadPartitionAsync(state.ResourceId, new(0) { IncludeCatalog = true }))!;
            foreach (var retained in savedPartition.Elements
                .Where(element => stableHandles.ContainsKey(element.Id)))
                Assert.Equal(stableHandles[retained.Id], retained.Payload);
            Assert.Equal(stableHandles.Count, savedPartition.Elements
                .Count(element => stableHandles.ContainsKey(element.Id)));

            var retry = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
                Decode(save.Binary), new Dictionary<string, string>(), actor);
            AssertSuccess(retry.Response);
            Assert.Equal(state.StateVersion, retry.State.StateVersion);
            Assert.Equal(state.ContentVersion, retry.State.ContentVersion);
            Assert.Equal(state.Package, retry.State.Package);
        }
    }

    [Theory]
    [InlineData(256, 4)]
    [InlineData(1536, 4)]
    [InlineData(256, 16)]
    [InlineData(1536, 64)]
    public void CorpusHasExactSizeDeterministicStoredEntriesAndOnlyItsSmallPartChanges(int packageKiB, int changedPartKiB)
    {
        var first = new IncrementalOfficeWorkload(packageKiB, 42, changedPartKiB);
        var second = new IncrementalOfficeWorkload(packageKiB, 42, changedPartKiB);
        Assert.Equal(changedPartKiB * 1024, first.ChangedDocumentPartBytes);
        var initial = first.CreateContent();
        Assert.Equal(initial, second.CreateContent());
        var updated = first.CreateContent(1);
        AssertPackage(initial, packageKiB, 0, first.ChangedDocumentPartBytes);
        AssertPackage(updated, packageKiB, 1, first.ChangedDocumentPartBytes);
        Assert.NotEqual(Digest(initial), Digest(updated));
        Assert.InRange(initial.Zip(updated).Count(pair => pair.First != pair.Second), 1, 32);
        using var before = new ZipArchive(new MemoryStream(initial), ZipArchiveMode.Read);
        using var after = new ZipArchive(new MemoryStream(updated), ZipArchiveMode.Read);
        foreach (var entry in before.Entries.Where(entry => entry.FullName != "word/document.xml"))
        {
            using var original = entry.Open();
            using var replacement = after.GetEntry(entry.FullName)!.Open();
            using var originalBytes = new MemoryStream();
            using var replacementBytes = new MemoryStream();
            original.CopyTo(originalBytes);
            replacement.CopyTo(replacementBytes);
            Assert.Equal(originalBytes.ToArray(), replacementBytes.ToArray());
        }
    }

    [Theory]
    [InlineData(256, 0)]
    [InlineData(256, -1)]
    [InlineData(256, 256)]
    [InlineData(256, 257)]
    [InlineData(16, 15)]
    public void ChangedPartMustBePositiveAndLeaveRoomForZipMetadataAndPadding(int packageKiB, int changedPartKiB)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IncrementalOfficeWorkload(packageKiB, changedPartKiB: changedPartKiB));
    }

    [Fact]
    public void DefaultChangedPartRemainsFourKiB()
    {
        Assert.Equal(IncrementalOfficeWorkload.DocumentPartBytes, new IncrementalOfficeWorkload(256).ChangedDocumentPartBytes);
    }

    private static void AssertPackage(byte[] bytes, int packageKiB, int revision, int documentPartBytes)
    {
        Assert.Equal(packageKiB * 1024, bytes.Length);
        using var package = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Equal(["[Content_Types].xml", "_rels/.rels", "word/padding.bin", "word/document.xml"],
            package.Entries.Select(entry => entry.FullName));
        Assert.All(package.Entries, entry =>
        {
            Assert.Equal(entry.Length, entry.CompressedLength);
            Assert.Equal(new DateTime(2000, 1, 1), entry.LastWriteTime.DateTime);
        });
        var document = package.GetEntry("word/document.xml")!;
        Assert.Equal(documentPartBytes, document.Length);
        using var xml = document.Open();
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        Assert.Equal("CellBridge synthetic revision " + revision.ToString("D8"),
            Assert.Single(XDocument.Load(xml).Descendants(word + "t")).Value);
        using var relationships = package.GetEntry("_rels/.rels")!.Open();
        Assert.Contains(XDocument.Load(relationships).Root!.Elements(), element =>
            (string?)element.Attribute("Target") == "word/document.xml");
        using var contentTypes = package.GetEntry("[Content_Types].xml")!.Open();
        Assert.Contains(XDocument.Load(contentTypes).Root!.Elements(), element =>
            (string?)element.Attribute("PartName") == "/word/document.xml" &&
            (string?)element.Attribute("ContentType") == "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");
    }

    private static FsshttpbCellRequest Decode(byte[] bytes)
    {
        var reader = new BinaryReaderEx(bytes);
        var request = FsshttpbCellRequest.Deserialize(reader);
        Assert.Equal(0, reader.Remaining);
        return request;
    }

    private static void AssertSuccess(FsshttpbResponse response)
    {
        Assert.False(response.Status);
        Assert.False(Assert.Single(response.SubResponses).Status);
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
