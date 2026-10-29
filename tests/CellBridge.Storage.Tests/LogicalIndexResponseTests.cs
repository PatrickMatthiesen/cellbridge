using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Tests;

namespace CellBridge.Storage.Tests;

public sealed class LogicalIndexResponseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AppliedDeltaAndFullQueryMergeSameLogicalIndexInEitherOrder(bool queryFirst)
    {
        var full = new GraphFixture([1,2,3]).Elements.Single(e => e.DataElementType == DataElementType.StorageIndexDataElementData);
        var mappings = StorageIndexPatch.Read(full);
        var delta = Index(full,StorageIndexPatch.Serialize([mappings[0]]));
        var result = new FsshttpbResponse();
        var assembler = new QueryChangesResponseAssembler(result,long.MaxValue);
        if (queryFirst) assembler.Append(Response(full,RequestTypes.QueryChanges));
        Assert.True(assembler.CanAppend(Response(delta,RequestTypes.PutChanges).DataElementPackage));
        assembler.AppendSave(Response(delta,RequestTypes.PutChanges));
        if (!queryFirst) assembler.Append(Response(full,RequestTypes.QueryChanges));
        Assert.All(result.SubResponses,response => Assert.False(response.Status));
        var merged = Assert.Single(result.DataElementPackage!.DataElements);
        Assert.Equal(full.DataElementExtendedGuid,merged.DataElementExtendedGuid);
        Assert.Equal(mappings.OrderBy(m => m.Type).ThenBy(m => m.Target.Value),
            StorageIndexPatch.Read(merged).OrderBy(m => m.Type).ThenBy(m => m.Target.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConflictingMappingOrOversizeQueryKeepsAlreadyAcceptedDelta(bool conflict)
    {
        var full = new GraphFixture([1,2,3]).Elements.Single(e => e.DataElementType == DataElementType.StorageIndexDataElementData);
        var mappings = StorageIndexPatch.Read(full);
        var delta = Index(full,StorageIndexPatch.Serialize([mappings[0]]));
        var encoded = new BinaryWriterEx(); full.Serialize(encoded);
        var result = new FsshttpbResponse();
        var assembler = new QueryChangesResponseAssembler(result,conflict ? long.MaxValue : encoded.Length-1);
        assembler.AppendSave(Response(delta,RequestTypes.PutChanges));
        if (conflict) full = Index(full,StorageIndexPatch.Serialize([mappings[0] with { Target = new ExGuid(1,Guid.NewGuid()) }]));
        assembler.Append(Response(full,RequestTypes.QueryChanges));
        Assert.False(result.SubResponses[0].Status);
        Assert.True(result.SubResponses[1].Status);
        Assert.Equal(delta.Data,Assert.Single(result.DataElementPackage!.DataElements).Data);
    }

    private static DataElement Index(DataElement template,byte[] payload) => new(template.DataElementType,
        template.DataElementExtendedGuid,template.SerialNumber) { Data=payload };
    private static FsshttpbResponse Response(DataElement index,RequestTypes type) => new()
    {
        DataElementPackage = new() { DataElements = [index] },
        SubResponses = { new() { RequestId = type == RequestTypes.PutChanges ? 1UL : 2UL,RequestType=type,
            Data = type == RequestTypes.PutChanges ? new PutChangesSubResponseData { PutChangesResponse = new() { AppliedStorageIndexID=index.DataElementExtendedGuid } }
                : new QueryChangesSubResponseData { StorageIndexExtendedGuid=index.DataElementExtendedGuid } } },
    };
}
