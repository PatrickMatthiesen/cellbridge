using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.FssHttp.Tests;

public sealed class NegotiatedHashExecutionTests
{
    private static readonly ProtocolHashingOptions Configuration = new(new byte[32]);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ReturnFileHashIsByteEquivalentIgnoredIndependentlyOfDataElementHashNegotiation(bool negotiated)
    {
        var document = new DocumentStore().Put("/file-hash.docx", [1, 2, 3]);
        var request = new FsshttpbCellRequest { HashOptions = negotiated ? new(IncludeHashes: true) : null,
            SubRequests = { Query(1) } };
        byte[] Execute() => CellBinaryRequestExecutor.Execute(document, document.FilePartition, request,
            DocumentAccess.Read, hashing: Configuration).ToByteArray(FsshttpbSerializationProfile.SharePoint13_11);
        var ordinary = Execute();
        Assert.IsType<QueryChangesSubRequestData>(request.SubRequests[0].Data).ReturnFileHash = true;
        Assert.Equal(ordinary, Execute());
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void RequestWideNegotiationAppliesToRepeatedQueriesWithoutChangingStoredGraph(bool instead, bool include)
    {
        var document = new DocumentStore().Put("/hash.docx", new byte[300]);
        var request = new FsshttpbCellRequest { HashOptions = new(1, instead, include),
            SubRequests = { Query(1), Query(2), new(RequestTypes.QueryAccess) { RequestId = 3 } } };
        var snapshot = System.Text.Json.JsonSerializer.Serialize(document.FilePartition.FileGraph.Elements);
        var response = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request,
            DocumentAccess.Read | DocumentAccess.Write, hashing: Configuration);
        Assert.All(response.SubResponses, s => Assert.False(s.Status));
        var group = Assert.Single(response.DataElementPackage!.DataElements.Where(e => e.DataElementType == DataElementType.ObjectGroupDataElementData));
        Assert.Equal(instead || include, IsHash(group));
        Assert.Equal(snapshot, System.Text.Json.JsonSerializer.Serialize(document.FilePartition.FileGraph.Elements));
        var ordinary = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request,
            DocumentAccess.Read, hashing: ProtocolHashingOptions.Disabled);
        Assert.False(IsHash(Assert.Single(ordinary.DataElementPackage!.DataElements.Where(e => e.DataElementType == DataElementType.ObjectGroupDataElementData))));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void SaveSharedGroupsStayFullRegardlessOfOperationOrder(bool saveFirst)
    {
        var raw = RawGroup(1024);
        var response = new FsshttpbResponse();
        var assembler = new QueryChangesResponseAssembler(response, 10000, new(HashesInsteadOfData: true), Configuration);
        var query = Result(raw, RequestTypes.QueryChanges, 1);
        var save = Result(raw, RequestTypes.PutChanges, 2);
        if (saveFirst) { Assert.True(assembler.CanAppend(save.DataElementPackage)); assembler.AppendSave(save); assembler.Append(query); }
        else { assembler.Append(query); Assert.True(IsHash(Assert.Single(response.DataElementPackage!.DataElements)));
            Assert.True(assembler.CanAppend(save.DataElementPackage)); assembler.AppendSave(save); }
        Assert.Equal(raw.Data, Assert.Single(response.DataElementPackage!.DataElements).Data);
        Assert.Equal(2, response.SubResponses.Count);
        Assert.False(IsHash(raw));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void ProjectionOverheadRejectsOnlyLaterQueryAndHonorsServerAndQueryBudgets(bool instead)
    {
        var raw = RawGroup(1);
        ulong rawSize = (ulong)Size(raw);
        var response = new FsshttpbResponse();
        var assembler = new QueryChangesResponseAssembler(response, 10000, new(1, instead, true), Configuration);
        var prior = new DataElement(DataElementType.StorageIndexDataElementData, new(2, Guid.NewGuid()), SerialNumber.Null) { Data = [] };
        assembler.AppendSave(Result(prior, RequestTypes.PutChanges, 1));
        assembler.Append(Result(raw, RequestTypes.QueryChanges, 2), new() { MaxDataElements = rawSize });
        Assert.False(response.SubResponses[0].Status);
        Assert.True(response.SubResponses[1].Status);
        Assert.Equal(ErrorType.Cell, response.SubResponses[1].Error!.Type);
        Assert.Equal((ulong)CellErrorCode.RequestNotSupported, response.SubResponses[1].Error!.ErrorCode);
        Assert.Same(prior, Assert.Single(response.DataElementPackage!.DataElements));
        var boundedResponse = new FsshttpbResponse();
        var bounded = new QueryChangesResponseAssembler(boundedResponse, (long)rawSize, new(1, instead, true), Configuration);
        bounded.Append(Result(raw, RequestTypes.QueryChanges, 3));
        Assert.True(Assert.Single(boundedResponse.SubResponses).Status);
        Assert.Null(boundedResponse.DataElementPackage);
    }

    [Fact]
    public void LaterSaveAndSerialUpgradeCannotViolateEarlierProjectedBudget()
    {
        var raw = RawGroup(1024);
        var projected = ObjectGroupWireHash.Project(raw, new(HashesInsteadOfData: true), Configuration);
        var response = new FsshttpbResponse();
        var assembler = new QueryChangesResponseAssembler(response, 10000, new(HashesInsteadOfData: true), Configuration);
        assembler.Append(Result(raw, RequestTypes.QueryChanges, 1), new() { MaxDataElements = (ulong)Size(projected) });
        var originalWire = response.ToByteArray();
        Assert.False(assembler.CanAppend(Result(raw, RequestTypes.PutChanges, 2).DataElementPackage));
        Assert.Equal(originalWire, response.ToByteArray());
        var upgraded = new DataElement(raw.DataElementType, raw.DataElementExtendedGuid, new(Guid.NewGuid(), 1)) { Data = raw.Data };
        assembler.Append(Result(upgraded, RequestTypes.QueryChanges, 3));
        Assert.False(response.SubResponses[0].Status);
        Assert.True(response.SubResponses[1].Status);
        Assert.True(Assert.Single(response.DataElementPackage!.DataElements).SerialNumber.IsNull);
    }

    [Theory]
    [InlineData(10, 11, 1)] [InlineData(15, 11, 1)] [InlineData(13, 12, 1)] [InlineData(13, 11, 2)]
    public void InvalidEnvelopeRejectsBeforeAnyOperation(int version, int minimum, int schema)
    {
        var document = new DocumentStore().Put("/bad.docx", [1]);
        var request = new FsshttpbCellRequest { ProtocolVersion = (ushort)version, MinimumVersion = (ushort)minimum,
            HashOptions = new((ulong)schema), SubRequests = { new(RequestTypes.PutChanges) { Data = new PutChangesSubRequestData() }, Query(2) } };
        var response = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, DocumentAccess.Read | DocumentAccess.Write,
            hashing: ProtocolHashingOptions.Disabled);
        Assert.True(response.Status);
        Assert.Empty(response.SubResponses);
        Assert.Equal(ErrorType.Cell, response.Error!.Type);
        Assert.Equal((ulong)(version is not (12 or 13 or 14) || minimum != 11
            ? CellErrorCode.IncompatibleProtocolVersion : CellErrorCode.RequestStreamSchemaError), response.Error.ErrorCode);
        Assert.Equal(new byte[] { 1 }, document.Content);
    }

    private static FsshttpbCellSubRequest Query(ulong id) => new(RequestTypes.QueryChanges)
    { RequestId = id, Data = new QueryChangesSubRequestData { IncludeCellChanges = true, IncludeStorageManifest = true } };
    private static DataElement RawGroup(int length) => Assert.Single(StorageManifestBuilder.BuildQueryChangesResponse(1,
        new byte[length]).DataElementPackage!.DataElements
        .Where(e => e.DataElementType == DataElementType.ObjectGroupDataElementData)) is { } element
        ? new(element.DataElementType, element.DataElementExtendedGuid, SerialNumber.Null) { Data = element.Data } : throw new Exception();
    private static FsshttpbResponse Result(DataElement element, RequestTypes type, ulong id) => new()
    { DataElementPackage = new() { DataElements = { element } }, SubResponses = { new() { RequestId = id, RequestType = type } } };
    private static long Size(DataElement element) { var writer = new BinaryWriterEx(); element.Serialize(writer); return writer.Length; }
    private static bool IsHash(DataElement element) => StreamObjectHeaderStart.Parse(new(element.Data!)).Type == StreamObjectTypeHeaderStart.DataElementHash;
}
