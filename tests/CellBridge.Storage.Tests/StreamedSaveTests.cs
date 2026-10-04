using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Web;

namespace CellBridge.Storage.Tests;

public sealed class StreamedSaveTests
{
    [Fact]
    public async Task StreamedSaveMatchesBufferedGraphResponseAndMetadataWithoutReadingOldPackage()
    {
        var stateStore = new InMemoryStateStore();
        var inner = new InMemoryContentStore();
        var tracking = new TrackingContent(inner);
        var provider = new StorageProvider(stateStore, tracking);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/streamed.docx", MinimalDocx.Create(), TestActor.Value))!;
        var buffered = await StoredDocument.RestoreAsync(state, inner);
        var request = Fixture(state);
        var expectedResponse = FilePartitionSaveHandler.Apply(buffered, buffered.FilePartition,
            request.SubRequests.Single(), request.DataElementPackage, provider.Limits);
        Assert.False(expectedResponse.Status);
        var expected = await buffered.CaptureAsync(inner);
        tracking.ForbiddenReadKey = state.Content.Key;
        var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        var wrapped = new FsshttpbResponse(); wrapped.SubResponses.Add(expectedResponse);
        Assert.Equal(wrapped.ToByteArray(), saved.Response.ToByteArray());
        Assert.Equal(expected.Content, saved.State.Content);
        Assert.Equal(expected.ContentVersion, saved.State.ContentVersion);
        foreach (var partition in expected.Partitions)
        {
            var actual = saved.State.Partitions.Single(p => p.Kind == partition.Kind);
            Assert.Equal(partition.Identity, actual.Identity);
            Assert.Equal(partition.Knowledge, actual.Knowledge);
            Assert.Equal(partition.StorageIndex, actual.StorageIndex);
            Assert.Equal(partition.Content, actual.Content);
            Assert.Equal(partition.Elements.Select(e => (e.Id, e.Serial, e.Payload)),
                actual.Elements.Select(e => (e.Id, e.Serial, e.Payload)));
        }
        var metadata = XDocument.Parse(Encoding.UTF8.GetString(saved.State.Partitions.Single(p => p.Kind == 1).InlineContent.AsSpan()));
        Assert.Equal(saved.State.ContentVersion.ToString(), (string?)metadata.Root!.Attribute("ContentVersion"));
        Assert.Equal(saved.State.ModifiedUtc.Ticks.ToString(), (string?)metadata.Root.Attribute("Modified"));
        Assert.Equal(1, tracking.StagedWrites);
        Assert.False(File.Exists(tracking.LastStagingPath));
        var restored = await StoredDocument.RestoreAsync(saved.State, inner);
        Assert.Equal(buffered.Content, restored.Content);
        Assert.Equal(restored.Content, restored.FilePartition.FileGraph.Materialize());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationDuringContentIngestionDeletesStagingAndPreservesState(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var tracking = new TrackingContent(new InMemoryContentStore());
        var provider = new StorageProvider(new InMemoryStateStore(), tracking);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/failed-stage.docx", MinimalDocx.Create(), TestActor.Value))!;
        tracking.OnStaging = cancel ? () => cancellation.Cancel() : () => throw new IOException("Injected staging upload failure.");
        var saving = service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, Fixture(state),
            new Dictionary<string, string>(), TestActor.Value, cancellation.Token).AsTask();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saving);
        else await Assert.ThrowsAsync<IOException>(() => saving);
        Assert.Equal(state, await provider.State.FindByResourceIdAsync(state.ResourceId));
        Assert.Equal(1, tracking.StagedWrites);
        Assert.False(File.Exists(tracking.LastStagingPath));
    }

    [Fact]
    public async Task InvalidPackageCannotReachDurableContentIngestion()
    {
        var tracking = new TrackingContent(new InMemoryContentStore());
        var provider = new StorageProvider(new InMemoryStateStore(), tracking);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/bad-zip.docx", MinimalDocx.Create(), TestActor.Value))!;
        var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, Proposal(state, [1, 2, 3]),
            new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal((ulong)CellErrorCode.InvalidObject, Assert.Single(saved.Response.SubResponses).Error!.ErrorCode);
        Assert.Equal(0, tracking.StagedWrites);
        Assert.Equal(state, await provider.State.FindByResourceIdAsync(state.ResourceId));
    }

    [Fact]
    public async Task InvalidCompressedMemberCannotReachDurableContentIngestion()
    {
        var tracking = new TrackingContent(new InMemoryContentStore());
        var provider = new StorageProvider(new InMemoryStateStore(), tracking);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/bad-member.docx", MinimalDocx.Create(), TestActor.Value))!;
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var member = zip.CreateEntry("[Content_Types].xml", CompressionLevel.NoCompression).Open())
                member.Write(Encoding.UTF8.GetBytes("<Types/>"));
            using (var member = zip.CreateEntry("word/document.xml", CompressionLevel.Optimal).Open())
                member.Write(Encoding.UTF8.GetBytes("<document>" + new string('a', 8192) + "</document>"));
        }
        var bytes = output.ToArray();
        int name = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("word/document.xml"));
        Assert.True(name >= 30);
        // The local header's extra-field length follows its filename length.
        int extraLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(name - 2, 2));
        int compressedStart = name + "word/document.xml".Length + extraLength;
        bytes[compressedStart] = 0x07; // DEFLATE's reserved block type.
        var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            Proposal(state, bytes), new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal((ulong)CellErrorCode.InvalidObject, Assert.Single(saved.Response.SubResponses).Error!.ErrorCode);
        Assert.Equal(0, tracking.StagedWrites);
        Assert.Equal(state, await provider.State.FindByResourceIdAsync(state.ResourceId));
    }

    [Fact]
    public async Task RepeatWithoutReceiptPreservesOriginalGraphKnowledgeAndVersion()
    {
        var tracking = new TrackingContent(new InMemoryContentStore());
        var provider = new StorageProvider(new InMemoryStateStore(), tracking);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/repeat.docx", MinimalDocx.Create(), TestActor.Value))!;
        var graph = await DocumentPartition.RestoreFileGraphAsync(state, tracking, provider.Limits);
        var request = new FsshttpbCellRequest { DataElementPackage = new() { DataElements = graph.Elements.ToList() },
            SubRequests = { new(RequestTypes.PutChanges) { RequestId = 1, Data = new PutChangesSubRequestData
                { StorageIndex = graph.StorageIndex, ExpectedStorageIndex = graph.StorageIndex } } } };
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        Assert.Equal(state.Content, result.State.Content);
        Assert.Equal(state.ContentVersion, result.State.ContentVersion);
        Assert.Equal(state.ModifiedUtc, result.State.ModifiedUtc);
        Assert.Equal(state.Partitions[0], result.State.Partitions[0]);
        Assert.Equal(state.Partitions.Single(p => p.Kind == 1), result.State.Partitions.Single(p => p.Kind == 1));
        Assert.Equal(0, tracking.StagedWrites);
        Assert.Single(result.State.Receipts);
    }

    [Fact]
    public async Task CorruptRetainedGraphIsRejectedBeforeStaging()
    {
        var tracking = new TrackingContent(new InMemoryContentStore());
        var provider = new StorageProvider(new InMemoryStateStore(), tracking);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/corrupt-graph.docx", MinimalDocx.Create(), TestActor.Value))!;
        tracking.DamagedReadKey = state.Partitions.Single(p => p.Kind == 0).Elements[0].Payload.Key;
        await Assert.ThrowsAsync<StorageCorruptionException>(() => service.ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, Fixture(state), new Dictionary<string, string>(), TestActor.Value).AsTask());
        Assert.Equal(state, await provider.State.FindByResourceIdAsync(state.ResourceId));
        Assert.Equal(0, tracking.StagedWrites);
    }

    [Fact]
    public async Task ExpandedPackageBudgetRejectsBeforeDurableContentIngestion()
    {
        var tracking = new TrackingContent(new InMemoryContentStore());
        var seedProvider = new StorageProvider(new InMemoryStateStore(), tracking);
        var state = (await new CellBridgeDocumentService(seedProvider).CreateAsync("/expanded.docx", MinimalDocx.Create(), TestActor.Value))!;
        var limits = new StorageLimits { MaxDocumentBytes = 8192 };
        var provider = new StorageProvider(seedProvider.State, tracking, limits);
        using var package = new MemoryStream();
        using (var zip = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string name in new[] { "[Content_Types].xml", "word/document.xml" })
            {
                using var member = zip.CreateEntry(name).Open();
                member.Write(new byte[8192]);
            }
        }
        Assert.True(package.Length < limits.MaxDocumentBytes);
        var saved = await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, Proposal(state, package.ToArray()), new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(saved.Response.SubResponses).Status);
        Assert.Equal(112UL, saved.Response.SubResponses[0].Error!.ErrorCode);
        Assert.Equal(0, tracking.StagedWrites);
        Assert.Equal(state, await provider.State.FindByResourceIdAsync(state.ResourceId));
    }

    [Fact]
    public async Task RestoredGraphOwnsVerifiedBytesAndSupportsRepeatedMaterialization()
    {
        var content = new InMemoryContentStore();
        var state = await new DocumentStore().Put("/graph.docx", MinimalDocx.Create()).CaptureAsync(content);
        var graph = await DocumentPartition.RestoreFileGraphAsync(state, content, new());
        var first = graph.Materialize();
        graph.Elements.First().Data!.AsSpan().Clear();
        Assert.Equal(first, graph.Materialize());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DocumentPartition.RestoreFileGraphAsync(state, content, new(), new(true)).AsTask());
    }

    private static FsshttpbCellRequest Fixture(DocumentState state)
    {
        var request = StorageTests.Fixture("save-first");
        ((PutChangesSubRequestData)request.SubRequests.Single().Data!).ExpectedStorageIndex =
            StorageIds.Restore(state.Partitions.Single(p => p.Kind == 0).StorageIndex!);
        return request;
    }

    private static FsshttpbCellRequest Proposal(DocumentState state, byte[] bytes)
    {
        var id = DocumentStorageIdentity.Create();
        var identity = new StorageManifestBuilder.StableIdentity(id.StorageManifestGuid, id.CellManifestGuid,
            id.RevisionManifestGuid, id.ObjectGroupGuid, id.ObjectDataBlobGuid, id.ObjectGuid, id.RevisionId, id.CellId, id.SerialGuid);
        var generated = FileContentPartitionBuilder.BuildQueryChangesResponse(1, bytes, identity, 1);
        return new() { DataElementPackage = generated.DataElementPackage, SubRequests =
            { new(RequestTypes.PutChanges) { RequestId = 1, Data = new PutChangesSubRequestData
                { StorageIndex = id.ObjectDataBlobGuid, ExpectedStorageIndex = StorageIds.Restore(state.Partitions.Single(p => p.Kind == 0).StorageIndex!) } } } };
    }

    private sealed class TrackingContent(IContentStore inner) : IContentStore
    {
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public string? ForbiddenReadKey { get; set; }
        public string? DamagedReadKey { get; set; }
        public string? LastStagingPath { get; private set; }
        public int StagedWrites { get; private set; }
        public Action? OnStaging { get; set; }
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
        {
            if (source is FileStream file)
            {
                StagedWrites++;
                LastStagingPath = file.Name;
                Assert.Equal(0, file.Position);
                Assert.True(file.CanSeek);
                if (!OperatingSystem.IsWindows())
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file.Name));
                OnStaging?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return inner.WriteAsync(source, cancellationToken);
        }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
        {
            Assert.NotEqual(ForbiddenReadKey, handle.Key);
            if (handle.Key == DamagedReadKey) return ValueTask.FromResult<Stream>(new MemoryStream([]));
            return inner.OpenReadAsync(handle, cancellationToken);
        }
    }
}
