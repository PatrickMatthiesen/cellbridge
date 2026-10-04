using CellBridge.AspNetCore;
using System.IO.Compression;
using System.Text.Json;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Web;

namespace CellBridge.FssHttp.Tests;

public sealed class FilePartitionSaveTests
{
    [Fact]
    public void RecordedExcelSaveIgnoresReservedBitsAndReopensWorkbookGraph()
    {
        var store = new DocumentStore();
        Assert.True(DocumentCreation.TryCreate(store, "Excel test", "xlsx").Created);
        var document = store.Get("/shared/Excel%20test.xlsx")!;
        var request = FsshttpbCellRequest.Deserialize(new BinaryReaderEx(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "excel-save-20260928.bin"))));
        var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data);
        Assert.Equal(0x09, put.Flags);
        Assert.Equal(0x8004, put.AdditionalFlagsBits);
        // The captured base identity belongs to a previous in-memory server.
        put.ExpectedStorageIndex = document.FilePartition.FileGraph.StorageIndex;
        var response = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);
        Assert.False(response.SubResponses[0].Status, response.SubResponses[0].Error?.ErrorMessage);
        Assert.Equal(2u, document.ContentVersion);
        Assert.Equal(document.Content, document.FilePartition.FileGraph.Materialize());
        using var workbook = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(new MemoryStream(document.Content), false);
        Assert.Empty(new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(workbook));
        Assert.Contains("asdf", workbook.WorkbookPart!.SharedStringTablePart!.SharedStringTable.InnerText);
        var retry = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);
        Assert.False(retry.SubResponses[0].Status);
        Assert.Equal(2u, document.ContentVersion);
    }

    [Fact]
    public void IgnoredLegacyFieldsDoNotPreventWordSave()
    {
        var document = new DocumentStore().Put("/test.docx", MinimalDocx.Create());
        var request = Proposal(document, MinimalDocx.Create("legacy flags"));
        var put = (PutChangesSubRequestData)request.SubRequests[0].Data!;
        put.Flags |= 0x10;
        put.AdditionalFlagsBits = 0xffc0;
        put.ContentVersionCoherencyCheck = [1, 2, 3];
        Assert.False(CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write).SubResponses[0].Status);
    }

    [Fact]
    public void WrongOfficePackageTypeDoesNotReplaceDocument()
    {
        var store = new DocumentStore();
        DocumentCreation.TryCreate(store, "book", "xlsx");
        var document = store.Get("/shared/book.xlsx")!;
        var before = document.Content;
        var result = CellBinaryRequestExecutor.Execute(document, document.FilePartition,
            Proposal(document, MinimalDocx.Create()), CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);
        Assert.True(result.SubResponses[0].Status);
        Assert.Equal(before, document.Content);
        Assert.Equal(1u, document.ContentVersion);
    }

    [Fact]
    public void FreshDesktopSaveAppliesAgainstTheExactDownloadedGraph()
    {
        var initial = FsshttpbResponse.Deserialize(new BinaryReaderEx(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "word-save-fresh-base.bin"))));
        var query = Assert.IsType<QueryChangesSubResponseData>(initial.SubResponses[0].Data);
        var graph = PartitionGraphSnapshot.Create(initial.DataElementPackage!.DataElements, query.StorageIndexExtendedGuid);
        var document = new DocumentStore().Put("/shared/save-test.docx", graph.Materialize());
        document.CommitFileRevision(graph, graph.Materialize(), 73508);
        var before = document.Content.ToArray();
        var request = FsshttpbCellRequest.Deserialize(new BinaryReaderEx(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "word-save-fresh.bin"))));
        var result = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);
        Assert.False(result.SubResponses[0].Status, result.SubResponses[0].Error?.ErrorMessage);
        Assert.False(before.SequenceEqual(document.Content));
        Assert.Equal(document.Content, document.FilePartition.FileGraph.Materialize());
    }

    [Fact]
    public void EquivalentExpectedMappingCanUseADifferentIndexIdentifier()
    {
        var document = new DocumentStore().Put("/test.docx", MinimalDocx.Create());
        var current = document.FilePartition.FileGraph;
        var index = current.Elements.Single(x => x.DataElementExtendedGuid.Equals(current.StorageIndex));
        var alias = new ExGuid(1, Guid.NewGuid());
        var request = Proposal(document, MinimalDocx.Create("equivalent base"));
        request.DataElementPackage!.DataElements.Add(new DataElement(DataElementType.StorageIndexDataElementData,
            alias, SerialNumber.Null) { Data = index.Data!.ToArray() });
        ((PutChangesSubRequestData)request.SubRequests[0].Data!).ExpectedStorageIndex = alias;
        var result = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);
        Assert.False(result.SubResponses[0].Status, result.SubResponses[0].Error?.ErrorMessage);
    }

    [Fact]
    public void DesktopSavePayloadMaterializesAfterRebasingItsForeignExpectedIndex()
    {
        var document = new DocumentStore().Put("/test.docx", MinimalDocx.Create());
        var request = FsshttpbCellRequest.Deserialize(new BinaryReaderEx(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "word-save-current.bin"))));
        var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data);
        var before = document.Content.ToArray();
        var stale = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);
        Assert.Equal((ulong)CellErrorCode.CoherencyFailure, stale.SubResponses[0].Error!.ErrorCode);
        Assert.Equal(before, document.Content);
        put.ExpectedStorageIndex = document.FilePartition.FileGraph.StorageIndex;
        var result = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);
        Assert.False(result.SubResponses[0].Status, result.SubResponses[0].Error?.ErrorMessage);
        Assert.Equal(document.Content, document.FilePartition.FileGraph.Materialize());
    }

    [Fact]
    public void SaveUpdatesDownloadAndSubsequentQueryGraph()
    {
        var document = new DocumentStore().Put("/test.docx", MinimalDocx.Create("before"));
        var next = MinimalDocx.Create("after");
        var request = Proposal(document, next);
        var before = document.ContentVersion;
        var response = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);
        Assert.False(Assert.Single(response.SubResponses).Status);
        Assert.Equal(next, document.Content);
        Assert.Equal(before + 1, document.ContentVersion);
        var query = new FsshttpbCellRequest();
        query.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.QueryChanges)
        {
            RequestId = 9,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true },
        });
        var download = CellBinaryRequestExecutor.Execute(document, document.FilePartition, query, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);
        var data = Assert.IsType<QueryChangesSubResponseData>(download.SubResponses[0].Data);
        var reopened = PartitionGraphSnapshot.Create(download.DataElementPackage!.DataElements, data.StorageIndexExtendedGuid);
        Assert.Equal(next, reopened.Materialize());
    }

    [Fact]
    public void StaleExpectedIndexDoesNotOverwriteNewerSave()
    {
        var document = new DocumentStore().Put("/test.docx", MinimalDocx.Create("before"));
        var first = Proposal(document, MinimalDocx.Create("first"));
        var stale = Proposal(document, MinimalDocx.Create("stale"));
        Assert.False(CellBinaryRequestExecutor.Execute(document, document.FilePartition, first, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write).SubResponses[0].Status);
        var bytes = document.Content.ToArray();
        var version = document.ContentVersion;
        var result = CellBinaryRequestExecutor.Execute(document, document.FilePartition, stale, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write).SubResponses[0];
        Assert.True(result.Status);
        Assert.Equal((ulong)ProtocolErrorCode.CoherencyFailure, result.Error!.ErrorCode);
        Assert.Equal(bytes, document.Content);
        Assert.Equal(version, document.ContentVersion);
    }

    [Fact]
    public void RetriedSaveDoesNotAdvanceDocumentVersionTwice()
    {
        var document = new DocumentStore().Put("/test.docx", MinimalDocx.Create());
        var request = Proposal(document, MinimalDocx.Create("saved"));
        var first = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);
        var version = document.ContentVersion;
        var second = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);
        Assert.False(first.SubResponses[0].Status);
        Assert.False(second.SubResponses[0].Status);
        Assert.Equal(version, document.ContentVersion);
        Assert.Equal(first.ToByteArray(), second.ToByteArray());
    }

    [Fact]
    public void MissingObjectsRejectSaveWithoutChangingContentOrGraph()
    {
        var document = new DocumentStore().Put("/test.docx", MinimalDocx.Create());
        var before = document.FilePartition.FileGraph;
        var bytes = document.Content.ToArray();
        var request = Proposal(document, MinimalDocx.Create("broken"));
        request.DataElementPackage!.DataElements.RemoveAll(x => x.DataElementType == DataElementType.ObjectGroupDataElementData);
        Assert.True(CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write).SubResponses[0].Status);
        Assert.Same(before, document.FilePartition.FileGraph);
        Assert.Equal(bytes, document.Content);
    }

    [Fact]
    public void CapturedSavesApplyInSequenceIncludingRetainedDeltaObjects()
    {
        var document = new DocumentStore().Put("/test.docx", MinimalDocx.Create());
        byte[]? first = null;
        foreach (var name in new[] { "save-first", "save-second" })
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json")));
            var side = fixture.RootElement.GetProperty("request");
            var parts = MtomMessageParser.Parse(Convert.FromBase64String(side.GetProperty("bodyBase64").GetString()!), side.GetProperty("contentType").GetString()!);
            var binary = Assert.Single(parts, p => p.ContentType.Contains("application/octet-stream"));
            var request = FsshttpbCellRequest.Deserialize(new BinaryReaderEx(binary.Content));
            // The farm's starting storage index is foreign to this in-memory document.
            var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data);
            put.ExpectedStorageIndex = document.FilePartition.FileGraph.StorageIndex;
            var result = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);
            Assert.False(result.SubResponses[0].Status, result.SubResponses[0].Error?.ErrorMessage);
            using var zip = new ZipArchive(new MemoryStream(document.Content));
            Assert.NotNull(zip.GetEntry("word/document.xml"));
            if (first is null) first = document.Content.ToArray();
            else Assert.False(first.SequenceEqual(document.Content));
        }
    }

    private static FsshttpbCellRequest Proposal(StoredDocument document, byte[] bytes)
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
                StorageIndex = ((QueryChangesSubResponseData)generated.SubResponses[0].Data!).StorageIndexExtendedGuid,
                ExpectedStorageIndex = document.FilePartition.FileGraph.StorageIndex,
            },
        });
        return request;
    }
}
