using Server = CellBridge.FssHttpB;
using Reference = Microsoft.Protocols.TestSuites.SharedAdapter;

namespace CellBridge.Interop.Tests;

[Collection("Allocation concurrency")]
public sealed class GuidAllocationInteropTests
{
    [MultiInstanceFact]
    public async Task ConcurrentHttpAllocationAcrossTwoHostsReturnsDistinctNamespaces()
    {
        var first = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")!);
        var peer = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_PEER")!);
        using var http = LiveInteropHttp.Create();
        byte[] before = await http.GetByteArrayAsync(new Uri(first, "/shared/test.docx"));
        var ranges = await Task.WhenAll(Enumerable.Range(0, 16).Select(async index =>
        {
            var request = new Reference.FsshttpbCellRequest
            {
                ProtocolVersion = 13, MinimumVersion = 11, Signature = Server.FsshttpbCellRequest.RequestSignature,
                SubRequests = [new Reference.AllocateExtendedGuidRangeCellSubRequest(new Reference.Compact64bitInt(1000), 14)
                    { IsPartitionIDGUIDUsed = true, PartitionIdGUID = Guid.Empty }],
            };
            var call = await new CellStorageClient(http, index % 2 == 0 ? first : peer)
                .SendCellAsync(new Uri(first, "/shared/test.docx").ToString(), request, Guid.Empty);
            Assert.Equal(System.Net.HttpStatusCode.OK, call.StatusCode);
            var response = call.ParseBinaryResponse();
            var operation = Assert.Single(response.CellSubResponses);
            Assert.False(operation.Status);
            var range = operation.GetSubResponseData<Reference.AllocateExtendedGuidRangeSubResponseData>();
            Assert.Equal(1000UL, range.IntegerRangeMax.DecodedValue - range.IntegerRangeMin.DecodedValue);
            return range.GUIDComponent;
        }));
        Assert.Equal(ranges.Length, ranges.Distinct().Count());
        Assert.Equal(before, await http.GetByteArrayAsync(new Uri(peer, "/shared/test.docx")));
    }

    [Fact]
    public void MicrosoftTargetPartitionPrecedingAllocationIsPreserved()
    {
        Guid target = Guid.NewGuid();
        var reference = new Reference.FsshttpbCellRequest
        {
            ProtocolVersion = 13, MinimumVersion = 11, Signature = Server.FsshttpbCellRequest.RequestSignature,
            SubRequests = [new Reference.AllocateExtendedGuidRangeCellSubRequest(new Reference.Compact64bitInt(1), 14)
                { IsPartitionIDGUIDUsed = true, PartitionIdGUID = target }],
        };
        var parsed = Server.FsshttpbCellRequest.Deserialize(new(reference.SerializeToByteList().ToArray()));
        var operation = Assert.Single(parsed.SubRequests);
        Assert.Equal(target, operation.TargetPartitionId);
        Assert.Equal(1UL, Assert.IsType<Server.AllocateExtendedGuidRangeSubRequestData>(operation.Data).RequestIdCount);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(1000UL)]
    [InlineData(100_000UL)]
    public void MicrosoftAllocationRequestIsDecodedByServer(ulong count)
    {
        var reference = new Reference.FsshttpbCellRequest
        {
            ProtocolVersion = 13, MinimumVersion = 11, Signature = Server.FsshttpbCellRequest.RequestSignature,
            GUID = Guid.NewGuid(),
            SubRequests = [new Reference.AllocateExtendedGuidRangeCellSubRequest(new Reference.Compact64bitInt(count), 14)
                { Reserved = 255 }],
        };
        var parsed = Server.FsshttpbCellRequest.Deserialize(new Server.BinaryReaderEx(reference.SerializeToByteList().ToArray()));
        var operation = Assert.Single(parsed.SubRequests);
        Assert.Equal(14UL, operation.RequestId);
        Assert.Equal(count, Assert.IsType<Server.AllocateExtendedGuidRangeSubRequestData>(operation.Data).RequestIdCount);
    }

    [Theory]
    [InlineData(999UL, 1000UL)]
    [InlineData(0UL, 1000UL)]
    [InlineData(0UL, 100_000UL)]
    public void ServerAllocationResponsePassesMicrosoftParser(ulong minimum, ulong maximum)
    {
        Guid guid = Guid.NewGuid();
        var response = new Server.FsshttpbResponse();
        response.SubResponses.Add(new() { RequestId = 14, RequestType = Server.RequestTypes.AllocateExtendedGuidRange,
            Data = new Server.AllocateExtendedGuidRangeSubResponseData
            { GuidComponent = guid, IntegerRangeMin = minimum, IntegerRangeMax = maximum } });
        byte[] bytes = response.ToByteArray();
        var parsed = Reference.FsshttpbResponse.DeserializeResponseFromByteArray(bytes, 0);
        var operation = Assert.Single(parsed.CellSubResponses);
        Assert.Equal(14UL, operation.RequestID.DecodedValue);
        Assert.Equal((ulong)Reference.RequestTypes.AllocateExtendedGuidRange, operation.RequestType.DecodedValue);
        var range = operation.GetSubResponseData<Reference.AllocateExtendedGuidRangeSubResponseData>();
        Assert.Equal(guid, range.GUIDComponent);
        Assert.Equal(minimum, range.IntegerRangeMin.DecodedValue);
        Assert.Equal(maximum, range.IntegerRangeMax.DecodedValue);
        var inspection = Server.FsshttpbResponseInspector.Inspect(bytes);
        Assert.Equal(Server.RequestTypes.AllocateExtendedGuidRange, Assert.Single(inspection.SubResponses).RequestType);
    }
}

// This test fills both hosts' request budgets. Other live collections must not
// consume those slots while we assert successful concurrent allocations.
[CollectionDefinition("Allocation concurrency", DisableParallelization = true)]
public sealed class AllocationConcurrencyCollection;
