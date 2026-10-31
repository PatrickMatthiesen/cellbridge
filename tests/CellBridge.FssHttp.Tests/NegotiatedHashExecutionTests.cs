using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;

namespace CellBridge.FssHttp.Tests;

public sealed class NegotiatedHashExecutionTests
{
    private static readonly ProtocolHashingOptions Configuration = new(new byte[32]);

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterSaveAndSerialUpgradeCannotViolateEarlierProjectedBudget(bool tinyThreshold)
    {
        var raw = RawGroup(1024);
        var projected = ObjectGroupWireHash.Project(raw, new(HashesInsteadOfData: true), Configuration);
        var response = new FsshttpbResponse();
        var assembler = new QueryChangesResponseAssembler(response, 10000, new(HashesInsteadOfData: true), Configuration);
        assembler.Append(Result(raw, RequestTypes.QueryChanges, 1), new()
            { MaxDataElements = tinyThreshold ? 1UL : (ulong)Size(projected) });
        var originalWire = response.ToByteArray();
        Assert.False(assembler.CanAppend(Result(raw, RequestTypes.PutChanges, 2).DataElementPackage));
        Assert.Equal(originalWire, response.ToByteArray());
        var upgraded = new DataElement(raw.DataElementType, raw.DataElementExtendedGuid, new(Guid.NewGuid(), 1)) { Data = raw.Data };
        assembler.Append(Result(upgraded, RequestTypes.QueryChanges, 3));
        Assert.False(response.SubResponses[0].Status);
        Assert.True(response.SubResponses[1].Status);
        Assert.True(Assert.Single(response.DataElementPackage!.DataElements).SerialNumber.IsNull);
    }

    private static DataElement RawGroup(int length)
    {
        var element = Assert.Single(StorageManifestBuilder.BuildQueryChangesResponse(1,
            new byte[length]).DataElementPackage!.DataElements,
            element => element.DataElementType == DataElementType.ObjectGroupDataElementData);
        return new(element.DataElementType, element.DataElementExtendedGuid, SerialNumber.Null) { Data = element.Data };
    }
    private static FsshttpbResponse Result(DataElement element, RequestTypes type, ulong id) => new()
    { DataElementPackage = new() { DataElements = { element } }, SubResponses = { new() { RequestId = id, RequestType = type } } };
    private static long Size(DataElement element) { var writer = new BinaryWriterEx(); element.Serialize(writer); return writer.Length; }
    private static bool IsHash(DataElement element) => StreamObjectHeaderStart.Parse(new(element.Data!)).Type == StreamObjectTypeHeaderStart.DataElementHash;
}
