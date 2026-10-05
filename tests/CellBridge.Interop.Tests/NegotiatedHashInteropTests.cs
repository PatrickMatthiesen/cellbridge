using Microsoft.Protocols.TestSuites.SharedAdapter;
using Microsoft.Protocols.TestSuites.Common;
using Wire = CellBridge.FssHttpB;

namespace CellBridge.Interop.Tests;

public sealed class NegotiatedHashInteropTests
{
    [Theory]
    [InlineData(4)] [InlineData(15)] [InlineData(18)]
    public void RejectionUsesNormativeCellErrorFamilyAndCode(int code)
    {
        var response = new Wire.FsshttpbResponse { Status = true,
            Error = new Wire.ResponseError(Wire.ErrorType.Cell, (ulong)code, "rejected") };
        var parsed = FsshttpbResponse.DeserializeResponseFromByteArray(response.ToByteArray(Wire.FsshttpbSerializationProfile.SharePoint13_11), 0);
        Assert.True(parsed.Status);
        Assert.Equal(code, (int)parsed.ResponseError.GetErrorData<CellError>().ErrorCode);
    }
    [Fact]
    public void MicrosoftRequestNegotiationAndFullDataHashResponseDecodeIndependently()
    {
        var request = InteropRequestFactory.QueryChanges(1);
        request.IsRequestHashingOptionsUsed = true;
        request.RequestHashingSchema = new Compact64bitInt(1);
        request.RequestDataElementHashes = 1;
        var ours = Wire.FsshttpbCellRequest.Deserialize(new(request.SerializeToByteList().ToArray()));
        Assert.Equal(new Wire.RequestHashOptions(IncludeHashes: true), ours.HashOptions);
        var response = Wire.StorageManifestBuilder.BuildQueryChangesResponse(1, new byte[] { 1, 2, 3 });
        response.DataElementPackage!.DataElements = response.DataElementPackage.DataElements
            .Select(e => Wire.ObjectGroupWireHash.Project(e, ours.HashOptions, new(new byte[32]))).ToList();
        var independent = FsshttpbResponse.DeserializeResponseFromByteArray(response.ToByteArray(Wire.FsshttpbSerializationProfile.SharePoint13_11), 0);
        var group = Assert.Single(independent.DataElementPackage.DataElements, e => e.DataElementType == DataElementType.ObjectGroupDataElementData)
            .GetData<ObjectGroupDataElementData>();
        Assert.Equal(1UL, group.DataElementHash.DataElementHashScheme.DecodedValue);
        Assert.NotEmpty(group.DataElementHash.DataElementHashData.Content);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(group.ObjectGroupData.ObjectGroupObjectDataList).Data.Content);
    }

    [Fact]
    public void ExcludedRecordFieldsDecodeWithMicrosoftPrimitiveCodecs()
    {
        // The pinned Microsoft full ObjectGroupData parser has no excluded-data branch.
        // Independently decode its normative header, reference arrays and compact size instead.
        var id = new Wire.ExGuid(3, Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"));
        var cell = new Wire.CellId(id, new(4, id.Guid));
        var writer = new Wire.BinaryWriterEx();
        new Wire.ObjectGroupExcludedData([id], [cell], ulong.MaxValue).Serialize(writer);
        var bytes = writer.ToArray();
        // Literal 32-bit leaf header: discriminator2, type3, 62-byte body.
        Assert.Equal(new byte[] { 0x1a, 0x00, 0x7c, 0x00 }, bytes[..4]);
        int declared;
        using (var bits = new BitReader(bytes, 0))
        {
            Assert.Equal(2, bits.ReadInt32(2));
            Assert.Equal(0, bits.ReadInt32(1));
            Assert.Equal(3, bits.ReadInt32(14));
            declared = bits.ReadInt32(15);
        }
        int index = 4;
        var references = BasicObject.Parse<ExGUIDArray>(bytes, ref index);
        var cells = BasicObject.Parse<CellIDArray>(bytes, ref index);
        var size = BasicObject.Parse<Compact64bitInt>(bytes, ref index);
        Assert.Equal(ulong.MaxValue, size.DecodedValue);
        Assert.Equal(bytes.Length, index);
        Assert.Equal(bytes.Length - 4, declared);
        var idWriter = new Wire.BinaryWriterEx(); id.Serialize(idWriter);
        Assert.Equal(idWriter.ToArray(), references.SerializeToByteList().Skip(1).ToArray());
        Assert.NotEmpty(cells.SerializeToByteList());
    }

    [LiveInteropFact]
    public async Task NegotiatedFullDataHashResponsePassesMicrosoftMtomClient()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")!);
        using var http = LiveInteropHttp.Create(endpoint);
        var name = "hash-interop-" + Guid.NewGuid().ToString("N");
        using var created = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(http, "/api/documents", new { name, type = "docx" });
        created.EnsureSuccessStatusCode();
        var client = new CellStorageClient(http, endpoint);
        var request = InteropRequestFactory.QueryChanges(1);
        request.IsRequestHashingOptionsUsed = true;
        request.RequestHashingSchema = new Compact64bitInt(1);
        request.RequestDataElementHashes = 1;
        var result = (await client.SendCellAsync("/shared/" + name + ".docx", request, Guid.Empty)).ParseBinaryResponse();
        Assert.All(result.CellSubResponses, s => Assert.False(s.Status));
        var groups = result.DataElementPackage.DataElements.Where(e => e.DataElementType == DataElementType.ObjectGroupDataElementData)
            .Select(e => e.GetData<ObjectGroupDataElementData>()).ToArray();
        Assert.NotEmpty(groups);
        Assert.All(groups, g => Assert.Equal(1UL, g.DataElementHash.DataElementHashScheme.DecodedValue));
    }
}
