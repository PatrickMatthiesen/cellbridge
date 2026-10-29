using System.IO.Compression;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class FilePartitionSaveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordedExcelSaveIgnoresReservedBitsAndReopensWorkbookGraph(bool postgreSql)
    {
        await using var context = new ProviderContext(postgreSql);
        var document = await context.CreateAsync("/shared/Excel%20test.xlsx", MinimalXlsx.Create());
        var request = RecordedRequest("excel-save-20260928.bin");
        var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data);
        Assert.Equal(0x09, put.Flags);
        Assert.Equal(0x8004, put.AdditionalFlagsBits);
        // The captured base identity belongs to a previous server.
        Rebase(request, document.Graph);
        var saved = await context.ExecuteAsync(document, request);
        AssertSucceeded(saved);
        document = await context.ReadAsync(document.Head.ResourceId);
        Assert.Equal(2u, document.Head.ContentVersion);
        Assert.Equal(document.Content, document.Graph.Materialize());
        using var workbook = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(new MemoryStream(document.Content), false);
        Assert.Empty(new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(workbook));
        Assert.Contains("asdf", workbook.WorkbookPart!.SharedStringTablePart!.SharedStringTable.InnerText);
        var retry = await context.ExecuteAsync(document, request);
        AssertSucceeded(retry);
        Assert.True(Assert.Single(retry.AcceptedSaves).IsReplay);
        Assert.Equal(saved.State.StateVersion, retry.State.StateVersion);
        Assert.Equal(2u, retry.State.ContentVersion);
        Assert.Equal(saved.Response.ToByteArray(), retry.Response.ToByteArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IgnoredLegacyFieldsDoNotPreventWordSave(bool postgreSql)
    {
        await using var context = new ProviderContext(postgreSql);
        var document = await context.CreateAsync("/test.docx", MinimalDocx.Create());
        var next = MinimalDocx.Create("legacy flags");
        var request = Proposal(document, next);
        var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data);
        put.Flags |= 0x10;
        put.AdditionalFlagsBits = 0xffc0;
        put.ContentVersionCoherencyCheck = [1, 2, 3];
        AssertSucceeded(await context.ExecuteAsync(document, request));
        var saved = await context.ReadAsync(document.Head.ResourceId);
        Assert.Equal(next, saved.Content);
        Assert.Equal(next, saved.Graph.Materialize());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongOfficePackageTypeDoesNotReplaceDocument(bool postgreSql)
    {
        await using var context = new ProviderContext(postgreSql);
        var document = await context.CreateAsync("/shared/book.xlsx", MinimalXlsx.Create());
        var before = await context.StateJsonAsync(document);
        var result = await context.ExecuteAsync(document, Proposal(document, MinimalDocx.Create()));
        Assert.True(Assert.Single(result.Response.SubResponses).Status);
        await AssertUnchangedAsync(context, document, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshDesktopSaveAppliesAgainstTheExactDownloadedGraph(bool postgreSql)
    {
        await using var context = new ProviderContext(postgreSql);
        var initial = FsshttpbResponse.Deserialize(new BinaryReaderEx(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "word-save-fresh-base.bin"))));
        var query = Assert.IsType<QueryChangesSubResponseData>(initial.SubResponses[0].Data);
        var graph = PartitionGraphSnapshot.Create(initial.DataElementPackage!.DataElements, query.StorageIndexExtendedGuid);
        // This offline fixture stages the exact captured base graph before normal typed operations.
        var document = await context.CreateRecordedAsync("/shared/save-test.docx", graph, 73508);
        var before = document.Content.ToArray();
        var recorded = RecordedRequest("word-save-fresh.bin");
        Rebase(recorded, document.Graph);
        var result = await context.ExecuteAsync(document, recorded);
        AssertSucceeded(result);
        var saved = await context.ReadAsync(document.Head.ResourceId);
        Assert.False(before.SequenceEqual(saved.Content));
        Assert.Equal(saved.Content, saved.Graph.Materialize());
        Assert.True(saved.Partition.Knowledge > 73508);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EquivalentExpectedMappingCanUseADifferentIndexIdentifier(bool postgreSql)
    {
        await using var context = new ProviderContext(postgreSql);
        var document = await context.CreateAsync("/test.docx", MinimalDocx.Create());
        var current = document.Graph;
        var index = current.Elements.Single(x => x.DataElementExtendedGuid.Equals(current.StorageIndex));
        var alias = new ExGuid(1, Guid.NewGuid());
        var next = MinimalDocx.Create("equivalent base");
        var request = Proposal(document, next);
        request.DataElementPackage!.DataElements.Add(new DataElement(DataElementType.StorageIndexDataElementData,
            alias, SerialNumber.Null) { Data = index.Data!.ToArray() });
        Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data).ExpectedStorageIndex = alias;
        AssertSucceeded(await context.ExecuteAsync(document, request));
        Assert.Equal(next, (await context.ReadAsync(document.Head.ResourceId)).Graph.Materialize());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DesktopSavePayloadMaterializesAfterRebasingItsForeignExpectedIndex(bool postgreSql)
    {
        await using var context = new ProviderContext(postgreSql);
        var document = await context.CreateAsync("/test.docx", MinimalDocx.Create());
        var request = RecordedRequest("word-save-current.bin");
        var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data);
        var before = await context.StateJsonAsync(document);
        var stale = await context.ExecuteAsync(document, request);
        Assert.Equal((ulong)CellErrorCode.CoherencyFailure, Assert.Single(stale.Response.SubResponses).Error!.ErrorCode);
        await AssertUnchangedAsync(context, document, before);
        Rebase(request, document.Graph);
        AssertSucceeded(await context.ExecuteAsync(document, request));
        var saved = await context.ReadAsync(document.Head.ResourceId);
        Assert.Equal(saved.Content, saved.Graph.Materialize());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveUpdatesDownloadAndSubsequentQueryGraph(bool postgreSql)
    {
        await using var context = new ProviderContext(postgreSql);
        var document = await context.CreateAsync("/test.docx", MinimalDocx.Create("before"));
        var next = MinimalDocx.Create("after");
        var response = await context.ExecuteAsync(document, Proposal(document, next));
        AssertSucceeded(response);
        var saved = await context.ReadAsync(document.Head.ResourceId);
        Assert.Equal(next, saved.Content);
        Assert.Equal(document.Head.ContentVersion + 1, saved.Head.ContentVersion);
        var query = new FsshttpbCellRequest();
        query.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.QueryChanges)
        {
            RequestId = 9,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true },
        });
        var download = await context.ExecuteAsync(saved, query);
        AssertSucceeded(download);
        var data = Assert.IsType<QueryChangesSubResponseData>(download.Response.SubResponses[0].Data);
        var reopened = PartitionGraphSnapshot.Create(download.Response.DataElementPackage!.DataElements, data.StorageIndexExtendedGuid);
        Assert.Equal(next, reopened.Materialize());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleExpectedIndexDoesNotOverwriteNewerSave(bool postgreSql)
    {
        await using var context = new ProviderContext(postgreSql);
        var document = await context.CreateAsync("/test.docx", MinimalDocx.Create("before"));
        var first = Proposal(document, MinimalDocx.Create("first"));
        var stale = Proposal(document, MinimalDocx.Create("stale"));
        AssertSucceeded(await context.ExecuteAsync(document, first));
        var saved = await context.ReadAsync(document.Head.ResourceId);
        var before = await context.StateJsonAsync(saved);
        var result = Assert.Single((await context.ExecuteAsync(saved, stale)).Response.SubResponses);
        Assert.True(result.Status);
        Assert.Equal((ulong)CellErrorCode.CoherencyFailure, result.Error!.ErrorCode);
        await AssertUnchangedAsync(context, saved, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetriedSaveReplaysFullReceiptWithoutAdvancingDocumentOrGraph(bool postgreSql)
    {
        await using var context = new ProviderContext(postgreSql);
        var document = await context.CreateAsync("/test.docx", MinimalDocx.Create());
        var request = Proposal(document, MinimalDocx.Create("saved"));
        var existingIds = document.Graph.Elements.Select(e => e.DataElementExtendedGuid).ToHashSet();
        var additions = request.DataElementPackage!.DataElements.Where(e => e.DataElementType != DataElementType.StorageIndexDataElementData).Select(e => e.DataElementExtendedGuid)
            .Where(id => !existingIds.Contains(id)).Distinct().ToArray();
        var first = await context.ExecuteAsync(document, request);
        AssertSucceeded(first);
        Assert.False(Assert.Single(first.AcceptedSaves).IsReplay);
        var saved = await context.ReadAsync(document.Head.ResourceId);
        var before = await context.StateJsonAsync(saved);
        var second = await context.ExecuteAsync(saved, request);
        AssertSucceeded(second);
        Assert.True(Assert.Single(second.AcceptedSaves).IsReplay);
        Assert.Equal(first.State.StateVersion, second.State.StateVersion);
        Assert.Equal(first.Response.ToByteArray(), second.Response.ToByteArray());
        await AssertUnchangedAsync(context, saved, before);
        Assert.Equal(first.Response.SubResponses[0].RequestId, second.Response.SubResponses[0].RequestId);
        Assert.Equal(first.Response.SubResponses[0].RequestType, second.Response.SubResponses[0].RequestType);
        var original = Assert.IsType<PutChangesSubResponseData>(first.Response.SubResponses[0].Data);
        var repeat = Assert.IsType<PutChangesSubResponseData>(second.Response.SubResponses[0].Data);
        Assert.Equal(original.SerialNumberReassignAllBytes, repeat.SerialNumberReassignAllBytes);
        Assert.Equal(original.KnowledgeBytes, repeat.KnowledgeBytes);
        Assert.Equal(original.PutChangesResponse!.AppliedStorageIndexID, repeat.PutChangesResponse!.AppliedStorageIndexID);
        Assert.NotEmpty(additions);
        Assert.Equal(additions, original.PutChangesResponse.DataElementAdded);
        Assert.Equal(additions, repeat.PutChangesResponse.DataElementAdded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingObjectsRejectSaveWithoutChangingContentOrGraph(bool postgreSql)
    {
        await using var context = new ProviderContext(postgreSql);
        var document = await context.CreateAsync("/test.docx", MinimalDocx.Create());
        var before = await context.StateJsonAsync(document);
        var request = Proposal(document, MinimalDocx.Create("broken"));
        request.DataElementPackage!.DataElements.RemoveAll(x => x.DataElementType == DataElementType.ObjectGroupDataElementData);
        Assert.True(Assert.Single((await context.ExecuteAsync(document, request)).Response.SubResponses).Status);
        await AssertUnchangedAsync(context, document, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MismatchedRevisionManifestRejectsSaveWithoutPublishingState(bool postgreSql)
    {
        await using var context = new ProviderContext(postgreSql);
        var document = await context.CreateAsync("/test.docx", MinimalDocx.Create("before"));
        var before = await context.StateJsonAsync(document);
        var request = Proposal(document, MinimalDocx.Create("rejected"));
        var manifest = request.DataElementPackage!.DataElements.Single(
            e => e.DataElementType == DataElementType.RevisionManifestDataElementData);
        var reader = new BinaryReaderEx(manifest.Data!);
        _ = StreamObjectHeaderStart.Parse(reader);
        var offset = reader.Position;
        var revision = ExGuid.Deserialize(reader);
        var wrongId = new BinaryWriterEx();
        new ExGuid(revision.Value, Guid.NewGuid()).Serialize(wrongId);
        wrongId.ToArray().CopyTo(manifest.Data!, offset);

        var result = Assert.Single((await context.ExecuteAsync(document, request)).Response.SubResponses);

        Assert.True(result.Status);
        Assert.Equal((ulong)CellErrorCode.InvalidObject, result.Error!.ErrorCode);
        await AssertUnchangedAsync(context, document, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedSavesApplyInSequenceIncludingRetainedDeltaObjects(bool postgreSql)
    {
        await using var context = new ProviderContext(postgreSql);
        var document = await context.CreateAsync("/test.docx", MinimalDocx.Create());
        byte[]? first = null;
        foreach (var name in new[] { "save-first", "save-second" })
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json")));
            var side = fixture.RootElement.GetProperty("request");
            var parts = MtomMessageParser.Parse(Convert.FromBase64String(side.GetProperty("bodyBase64").GetString()!), side.GetProperty("contentType").GetString()!);
            var binary = Assert.Single(parts, p => p.ContentType.Contains("application/octet-stream"));
            var request = FsshttpbCellRequest.Deserialize(new BinaryReaderEx(binary.Content));
            // The captures contain farm-specific expected indexes. Rebase each save
            // onto the accepted graph, retaining its objects for the next delta.
            var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data);
            Rebase(request, document.Graph);
            var version = document.Head.ContentVersion;
            var knowledge = document.Partition.Knowledge;
            AssertSucceeded(await context.ExecuteAsync(document, request));
            document = await context.ReadAsync(document.Head.ResourceId);
            Assert.Equal(version + 1, document.Head.ContentVersion);
            Assert.True(document.Partition.Knowledge > knowledge);
            Assert.Equal(document.Content, document.Graph.Materialize());
            using var zip = new ZipArchive(new MemoryStream(document.Content));
            Assert.NotNull(zip.GetEntry("word/document.xml"));
            if (first is null) first = document.Content.ToArray();
            else Assert.False(first.SequenceEqual(document.Content));
        }
    }

    [Theory]
    [InlineData(false, "partial")]
    [InlineData(true, "partial")]
    [InlineData(false, "last-partial")]
    [InlineData(true, "last-partial")]
    [InlineData(false, "alternate-coherency")]
    [InlineData(true, "alternate-coherency")]
    [InlineData(false, "editors")]
    [InlineData(true, "editors")]
    public async Task UnsupportedUploadRejectsWithoutPublishingState(bool postgreSql, string mode)
    {
        await using var context = new ProviderContext(postgreSql);
        var document = await context.CreateAsync("/test.docx", MinimalDocx.Create());
        var before = await context.StateJsonAsync(document);
        var request = Proposal(document, MinimalDocx.Create("unsupported"));
        var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data);
        switch (mode)
        {
            case "partial": put.Flags = 0x02; break;
            case "last-partial": put.Flags = 0x04; break;
            case "alternate-coherency": put.AdditionalFlagsBits = 0x08; break;
            case "editors": request.SubRequests[0].TargetPartitionId = StoredDocument.EditorsTablePartitionId; break;
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
        var result = await context.ExecuteAsync(document, request);
        var response = Assert.Single(result.Response.SubResponses);
        Assert.True(response.Status);
        Assert.Equal((ulong)CellErrorCode.RequestNotSupported, response.Error!.ErrorCode);
        Assert.Empty(result.AcceptedSaves);
        await AssertUnchangedAsync(context, document, before);
    }

    private static void AssertSucceeded(CellExecution result)
    {
        Assert.Null(result.LockError);
        var response = Assert.Single(result.Response.SubResponses);
        Assert.False(response.Status, response.Error?.ErrorMessage);
    }

    private static async Task AssertUnchangedAsync(ProviderContext context, ClientDocument before, string state)
    {
        Assert.Equal(state, await context.StateJsonAsync(before));
        var after = await context.ReadAsync(before.Head.ResourceId);
        Assert.Equal(before.Content, after.Content);
        Assert.Equal(before.Head.ContentVersion, after.Head.ContentVersion);
        Assert.Equal(before.Partition.Knowledge, after.Partition.Knowledge);
        Assert.Equal(before.Graph.StorageIndex, after.Graph.StorageIndex);
        Assert.Equal(before.Graph.Materialize(), after.Graph.Materialize());
    }

    private static FsshttpbCellRequest RecordedRequest(string name) => FsshttpbCellRequest.Deserialize(new BinaryReaderEx(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name))));

    private static FsshttpbCellRequest Proposal(ClientDocument document, byte[] bytes)
    {
        var identity = DocumentStorageIdentity.Create();
        var stable = new StorageManifestBuilder.StableIdentity(identity.StorageManifestGuid, identity.CellManifestGuid,
            identity.RevisionManifestGuid, identity.ObjectGroupGuid, identity.ObjectDataBlobGuid,
            identity.ObjectGuid, identity.RevisionId, identity.CellId, identity.SerialGuid);
        var generated = FileContentPartitionBuilder.BuildQueryChangesResponse(1, bytes, stable, 73510);
        var request = new FsshttpbCellRequest { DataElementPackage = generated.DataElementPackage };
        request.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.PutChanges)
        {
            RequestId = 1,
            Data = new PutChangesSubRequestData
            {
                HasAdditionalFlags = true, AdditionalFlagsBits = 1,
                StorageIndex = ((QueryChangesSubResponseData)generated.SubResponses[0].Data!).StorageIndexExtendedGuid,
                ExpectedStorageIndex = document.Graph.StorageIndex,
            },
        });
        Rebase(request, document.Graph);
        return request;
    }

    private static void Rebase(FsshttpbCellRequest request, PartitionGraphSnapshot graph)
    {
        var expected = graph.Elements.Single(e => e.DataElementExtendedGuid.Equals(graph.StorageIndex));
        if (!request.DataElementPackage!.DataElements.Any(e => e.DataElementExtendedGuid.Equals(graph.StorageIndex)))
            request.DataElementPackage.DataElements.Add(expected);
        Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data).ExpectedStorageIndex = graph.StorageIndex;
    }

    private sealed record ClientDocument(DocumentHead Head, PartitionHead Partition, PartitionGraphSnapshot Graph, byte[] Content);

    private sealed class ProviderContext : IAsyncDisposable
    {
        private readonly ProtocolExecutionTestProvider provider;
        private readonly string pathPrefix = "/" + Guid.NewGuid().ToString("N");
        public StorageProvider Provider => provider.Provider;

        public ProviderContext(bool postgreSql)
        {
            provider = ProtocolExecutionTestProvider.Create(postgreSql);
        }

        public async Task<ClientDocument> CreateAsync(string path, byte[] bytes)
        {
            var head = (await new CellBridgeDocumentService(Provider).CreateAsync(pathPrefix + path, bytes, TestActor.Value))!;
            return await ReadAsync(head.ResourceId);
        }

        public async Task<ClientDocument> CreateRecordedAsync(string path, PartitionGraphSnapshot graph, ulong knowledge)
        {
            var legacy = new DocumentStore().Put(pathPrefix + path, graph.Materialize());
            legacy.Security = DocumentSecurity.Create(TestActor.Value.Identity);
            legacy.CommitFileRevision(graph, graph.Materialize(), knowledge);
            var state = RevisionHistory.Initialize((await legacy.CaptureAsync(Provider.Content)) with { Revisions = [] });
            var bundle = await LegacyDocumentProjection.PrepareAsync(state, Provider.Content, Provider.Limits);
            var revision = Assert.Single(bundle.Revisions);
            var prepared = bundle.Current with { Partitions = revision.Partitions.Add(bundle.Current.Partitions.Single(partition => partition.After.Kind == 2)), InitialRevision = revision.Revision,
                Head = bundle.Current.Head with { Package = revision.Revision.Package } };
            Assert.True((await Provider.Documents.CreateAsync(prepared)).Created);
            return await ReadAsync(state.ResourceId);
        }

        public async Task<ClientDocument> ReadAsync(Guid id)
        {
            var head = (await Provider.Documents.FindAsync(new DocumentIdentity.Resource(id)))!;
            var graph = await StorageTestDocuments.GraphAsync(new(Provider), head);
            var partition = (await Provider.Documents.ReadPartitionAsync(id, new(0)))!.Partition;
            return new(head, partition, graph, await StorageTestDocuments.PackageAsync(Provider, head.Package));
        }

        public async Task<string> StateJsonAsync(ClientDocument document)
        {
            var snapshot = (await Provider.Documents.ExportDocumentAsync(document.Head.ResourceId))!;
            return JsonSerializer.Serialize(snapshot with { Current = snapshot.Current with { UtcNow = DateTime.UnixEpoch } });
        }

        public ValueTask<CellExecution> ExecuteAsync(ClientDocument document, FsshttpbCellRequest request) =>
            new CellBridgeDocumentService(Provider).ExecuteAsync(document.Head.ResourceId, DocumentPartitionKind.FileContents,
                request, new Dictionary<string, string>(), TestActor.Value);

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }
}
