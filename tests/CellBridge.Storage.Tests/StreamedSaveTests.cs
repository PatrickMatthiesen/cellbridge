using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class StreamedSaveTests
{
    [Fact]
    public async Task StreamedSaveMatchesProposedGraphAndMetadataWithoutReadingOldPackage()
    {
        var inner = new InMemoryContentStore();
        var tracking = new TrackingContent(inner);
        var provider = StorageTestDocuments.Memory(tracking);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/streamed.docx", MinimalDocx.Create(), TestActor.Value))!;
        tracking.Reset();
        var currentGraph = await StorageTestDocuments.GraphAsync(service, state);
        var request = await Fixture(service, state);
        var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests.Single().Data);
        var expectedBytes = currentGraph.Merge(request.DataElementPackage!.DataElements, put.StorageIndex).Materialize();
        var addedIds = request.DataElementPackage.DataElements.Where(e => e.DataElementType != DataElementType.StorageIndexDataElementData).Select(e => e.DataElementExtendedGuid)
            .Except(currentGraph.Elements.Select(e => e.DataElementExtendedGuid)).ToArray();
        var before = (await provider.Documents.ReadControlAsync(state.ResourceId))!;
        tracking.Reset();
        var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value);
        var response = Assert.Single(FsshttpbResponse.Deserialize(new(saved.Response.ToByteArray())).SubResponses);
        Assert.False(response.Status);
        Assert.Equal(request.SubRequests[0].RequestId, response.RequestId);
        var acknowledgement = Assert.IsType<PutChangesSubResponseData>(response.Data);
        Assert.Equal(addedIds, acknowledgement.PutChangesResponse!.DataElementAdded);
        Assert.Equal(state.ContentVersion + 1, saved.State.ContentVersion);
        var after = (await provider.Documents.ReadControlAsync(state.ResourceId))!;
        foreach (var partition in before.Partitions)
            Assert.Equal(partition.Identity, after.Partitions.Single(p => p.Kind == partition.Kind).Identity);
        var applied = Assert.IsType<PutChangesSubResponseData>(response.Data).PutChangesResponse!.AppliedStorageIndexID;
        Assert.Equal(StorageIds.Capture(applied!), after.Partitions.Single(p => p.Kind == 0).StorageIndex);
        Assert.NotEqual(put.StorageIndex, applied);
        var knowledge = ClientKnowledge.Deserialize(new(acknowledgement.KnowledgeBytes!));
        var added = (await provider.Documents.ReadElementsAsync(new(state.ResourceId,state.LifecycleGeneration,0),
            addedIds.Select(StorageIds.Capture).ToArray()));
        Assert.All(added, element => Assert.True(knowledge.Contains(new SerialNumber(element.Serial.Guid,element.Serial.Value))));
        var metadata = XDocument.Parse(Encoding.UTF8.GetString(after.Partitions.Single(p => p.Kind == 1).InlineContent.AsSpan()));
        Assert.Equal(saved.State.ContentVersion.ToString(), (string?)metadata.Root!.Attribute("ContentVersion"));
        Assert.Equal(saved.State.ModifiedUtc.Ticks.ToString(), (string?)metadata.Root.Attribute("Modified"));
        Assert.True(tracking.StagedWrites > 0);
        Assert.Equal(0, tracking.FileWrites);
        Assert.Equal(expectedBytes, await StorageTestDocuments.PackageAsync(provider,saved.State.Package));
        Assert.Equal(expectedBytes, (await StorageTestDocuments.GraphAsync(service,saved.State)).Materialize());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationDuringContentIngestionDeletesStagingAndPreservesState(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var tracking = new TrackingContent(new InMemoryContentStore());
        var provider = StorageTestDocuments.Memory(tracking);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/failed-stage.docx", MinimalDocx.Create(), TestActor.Value))!;
        tracking.Reset();
        var request = await Fixture(service,state);
        tracking.Reset();
        tracking.OnStaging = cancel ? () => cancellation.Cancel() : () => throw new IOException("Injected staging upload failure.");
        var saving = service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value, cancellation.Token).AsTask();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saving);
        else await Assert.ThrowsAsync<IOException>(() => saving);
        Assert.Equal(state, await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
        Assert.Equal(1, tracking.StagedWrites);
        Assert.Equal(0, tracking.FileWrites);
    }

    [Fact]
    public async Task InvalidPackageCannotReachDurableContentIngestion()
    {
        var tracking = new TrackingContent(new InMemoryContentStore());
        var provider = StorageTestDocuments.Memory(tracking);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/bad-zip.docx", MinimalDocx.Create(), TestActor.Value))!;
        tracking.Reset();
        var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, await Proposal(service, state, [1, 2, 3]),
            new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal((ulong)CellErrorCode.InvalidObject, Assert.Single(saved.Response.SubResponses).Error!.ErrorCode);
        Assert.Equal(0, tracking.StagedWrites);
        Assert.Equal(state, await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
    }

    [Fact]
    public async Task InvalidCompressedMemberCannotReachDurableContentIngestion()
    {
        var tracking = new TrackingContent(new InMemoryContentStore());
        var provider = StorageTestDocuments.Memory(tracking);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/bad-member.docx", MinimalDocx.Create(), TestActor.Value))!;
        tracking.Reset();
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
            await Proposal(service, state, bytes), new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal((ulong)CellErrorCode.InvalidObject, Assert.Single(saved.Response.SubResponses).Error!.ErrorCode);
        Assert.Equal(0, tracking.StagedWrites);
        Assert.Equal(state, await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
    }

    [Fact]
    public async Task RepeatWithoutReceiptPreservesOriginalGraphKnowledgeAndVersion()
    {
        var tracking = new TrackingContent(new InMemoryContentStore());
        var provider = StorageTestDocuments.Memory(tracking);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/repeat.docx", MinimalDocx.Create(), TestActor.Value))!;
        tracking.Reset();
        var graph = await StorageTestDocuments.GraphAsync(service, state);
        var request = new FsshttpbCellRequest { DataElementPackage = new() { DataElements = graph.Elements.ToList() },
            SubRequests = { new(RequestTypes.PutChanges) { RequestId = 1, Data = new PutChangesSubRequestData
                { StorageIndex = graph.StorageIndex, ExpectedStorageIndex = graph.StorageIndex } } } };
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        Assert.Equal(state.Package, result.State.Package);
        Assert.Equal(state.ContentVersion, result.State.ContentVersion);
        Assert.Equal(state.ModifiedUtc, result.State.ModifiedUtc);
        Assert.Equal(state.ContentVersion, result.State.ContentVersion);
        Assert.Equal(0, result.State.Counters.SaveReceipts);
    }

    [Fact]
    public async Task CorruptSelectedObjectIsRejectedBeforeStaging()
    {
        var tracking = new TrackingContent(new InMemoryContentStore());
        var provider = StorageTestDocuments.Memory(tracking);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/corrupt-graph.docx", MinimalDocx.Create(), TestActor.Value))!;
        tracking.Reset();
        var corpus = new IncrementalOfficeWorkload(256);
        var first = corpus.CreateSave(await StorageTestDocuments.GraphAsync(service,state),1);
        state = (await service.ExecuteAsync(state.ResourceId,DocumentPartitionKind.FileContents,first.Request,
            new Dictionary<string,string>(),TestActor.Value)).State;
        var current = await StorageTestDocuments.GraphAsync(service,state);
        var request = corpus.CreateSave(current,2).Request;
        var referenced = (await provider.Documents.ReadPartitionAsync(state.ResourceId,new(0) { IncludeBindings = true }))!.Bindings
            .Select(b => b.GroupElement).Distinct().ToArray();
        tracking.Reset();
        tracking.DamagedReadKey = (await provider.Documents.ReadPartitionAsync(state.ResourceId,new(0) { IncludeCatalog = true }))!.Elements
            .First(element => element.Type == (uint)DataElementType.ObjectGroupDataElementData && referenced.Contains(element.Id)).Payload.Key;
        await Assert.ThrowsAsync<StorageCorruptionException>(() => service.ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents,request,new Dictionary<string,string>(),TestActor.Value).AsTask());
        Assert.Equal(state, await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
        Assert.Equal(0, tracking.StagedWrites);
    }

    [Fact]
    public async Task UnselectedHistoricalManifestIsVerifiedWhenItsGraphIsRead()
    {
        var tracking = new TrackingContent(new InMemoryContentStore());
        var provider = StorageTestDocuments.Memory(tracking);
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/old-manifest.docx", MinimalDocx.Create(), TestActor.Value))!;
        tracking.Reset();
        var historical = (await provider.Documents.ReadPartitionAsync(initial.ResourceId,new(0) { IncludeCatalog = true }))!;
        var request = StorageTestDocuments.MetadataRequest(new CellBridge.Tests.GraphFixture(MinimalDocx.Create("replacement"),blob:true));
        StorageTests.Rebase(request,await StorageTestDocuments.GraphAsync(service,initial));
        tracking.DamagedReadKey = historical.Elements.Single(element => element.Type == (uint)DataElementType.StorageManifestDataElementData).Payload.Key;
        var saved = await service.ExecuteAsync(initial.ResourceId,DocumentPartitionKind.FileContents,request,
            new Dictionary<string,string>(),TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        Assert.Equal(initial.ContentVersion + 1,saved.State.ContentVersion);
        Assert.Equal(initial.Package,(await provider.Documents.ReadRevisionAsync(initial.ResourceId,1))!.Revision!.Package);
        await Assert.ThrowsAsync<StorageCorruptionException>(() => tracking.ReadVerifiedAsync(historical.Elements
            .Single(element => element.Type == (uint)DataElementType.StorageManifestDataElementData).Payload).AsTask());
        Assert.NotEmpty(await StorageTestDocuments.PackageAsync(provider,saved.State.Package));
    }

    [Fact]
    public async Task ExpandedPackageBudgetRejectsBeforeDurableContentIngestion()
    {
        var tracking = new TrackingContent(new InMemoryContentStore());
        var seedProvider = StorageTestDocuments.Memory(tracking);
        var state = (await new CellBridgeDocumentService(seedProvider).CreateAsync("/expanded.docx", MinimalDocx.Create(), TestActor.Value))!;
        tracking.Reset();
        var limits = new StorageLimits { MaxDocumentBytes = 8192 };
        var provider = new StorageProvider(seedProvider.Documents, tracking, limits);
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
            DocumentPartitionKind.FileContents, await Proposal(new CellBridgeDocumentService(provider), state, package.ToArray()), new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(saved.Response.SubResponses).Status);
        Assert.Equal(112UL, saved.Response.SubResponses[0].Error!.ErrorCode);
        Assert.Equal(0, tracking.StagedWrites);
        Assert.Equal(state, await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
    }

    [Fact]
    public async Task LegacyRestoredGraphOwnsVerifiedBytesAndSupportsRepeatedMaterialization()
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

    private static async Task<FsshttpbCellRequest> Fixture(CellBridgeDocumentService service, DocumentHead state)
    {
        var request = StorageTests.Fixture("save-first");
        var put = (PutChangesSubRequestData)request.SubRequests.Single().Data!;
        put.HasAdditionalFlags = true;
        put.AdditionalFlagsBits |= 1;
        StorageTests.Rebase(request,await StorageTestDocuments.GraphAsync(service,state));
        return request;
    }

    private static async Task<FsshttpbCellRequest> Proposal(CellBridgeDocumentService service, DocumentHead state, byte[] bytes) =>
        await StorageTestDocuments.SaveRequestAsync(service,state,bytes);

    private sealed class TrackingContent(IContentStore inner) : IContentStore
    {
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public string? ForbiddenReadKey { get; set; }
        public string? DamagedReadKey { get; set; }
        public int StagedWrites { get; private set; }
        public int FileWrites { get; private set; }
        public Action? OnStaging { get; set; }
        public void Reset() { StagedWrites = 0; FileWrites = 0; }
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
        {
            StagedWrites++;
            if (source is FileStream) FileWrites++;
            OnStaging?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return inner.WriteAsync(source,cancellationToken);
        }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
        {
            Assert.NotEqual(ForbiddenReadKey, handle.Key);
            if (handle.Key == DamagedReadKey) return ValueTask.FromResult<Stream>(new MemoryStream([]));
            return inner.OpenReadAsync(handle, cancellationToken);
        }
    }
}
