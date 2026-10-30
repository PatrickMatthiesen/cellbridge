using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class NegotiatedHashExecutionTests
{
    private static readonly ProtocolHashingOptions Configuration = new(new byte[32]);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReturnFileHashIsByteEquivalentIgnoredIndependentlyOfDataElementHashNegotiation(
        bool postgreSql, bool negotiated)
    {
        await using var scope = ProtocolExecutionTestProvider.Create(postgreSql);
        var service = new CellBridgeDocumentService(scope.Provider, hashing: Configuration);
        var document = (await service.CreateAsync($"/file-hash-{Guid.NewGuid():N}.docx", [1, 2, 3], TestActor.Value))!;
        var reader = TestActor.Value with { AccessLimit = new(document.ResourceId, DocumentAccess.Read) };
        var request = new FsshttpbCellRequest
        {
            HashOptions = negotiated ? new(IncludeHashes: true) : null,
            SubRequests = { Query(1) },
        };

        var ordinary = (await service.ExecuteAsync(document.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), reader)).Response.ToByteArray(FsshttpbSerializationProfile.SharePoint13_11);
        Assert.IsType<QueryChangesSubRequestData>(request.SubRequests[0].Data).ReturnFileHash = true;
        var withFileHash = (await service.ExecuteAsync(document.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), reader)).Response.ToByteArray(FsshttpbSerializationProfile.SharePoint13_11);

        Assert.Equal(ordinary, withFileHash);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task RequestWideNegotiationAppliesToRepeatedQueriesWithoutChangingStoredGraph(
        bool postgreSql, bool instead, bool include)
    {
        await using var scope = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = scope.Provider;
        var service = new CellBridgeDocumentService(provider, hashing: Configuration);
        var document = (await service.CreateAsync($"/hash-{Guid.NewGuid():N}.docx", new byte[300], TestActor.Value))!;
        var request = new FsshttpbCellRequest
        {
            HashOptions = new(1, instead, include),
            SubRequests = { Query(1), Query(2), new(RequestTypes.QueryAccess) { RequestId = 3 } },
        };
        var graph = await StorageTestDocuments.GraphAsync(service,document);
        var snapshot = JsonSerializer.Serialize(graph.Elements);

        var response = (await service.ExecuteAsync(document.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value)).Response;

        Assert.All(response.SubResponses, s => Assert.False(s.Status));
        var group = Assert.Single(response.DataElementPackage!.DataElements, e => e.DataElementType == DataElementType.ObjectGroupDataElementData);
        Assert.Equal(instead || include, IsHash(group));
        var persisted = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(document.ResourceId)))!;
        var storedGraph = await StorageTestDocuments.GraphAsync(service,persisted);
        Assert.Equal(snapshot, JsonSerializer.Serialize(storedGraph.Elements));
        Assert.Equal(JsonSerializer.Serialize(document), JsonSerializer.Serialize(persisted));

        var disabled = new CellBridgeDocumentService(provider, hashing: ProtocolHashingOptions.Disabled);
        var reader = TestActor.Value with { AccessLimit = new(document.ResourceId, DocumentAccess.Read) };
        var ordinary = (await disabled.ExecuteAsync(document.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), reader)).Response;
        Assert.False(IsHash(Assert.Single(ordinary.DataElementPackage!.DataElements, e => e.DataElementType == DataElementType.ObjectGroupDataElementData)));
    }

    [Theory]
    [InlineData(false, 10, 11, 1)]
    [InlineData(false, 15, 11, 1)]
    [InlineData(false, 13, 12, 1)]
    [InlineData(false, 13, 11, 2)]
    [InlineData(true, 10, 11, 1)]
    [InlineData(true, 15, 11, 1)]
    [InlineData(true, 13, 12, 1)]
    [InlineData(true, 13, 11, 2)]
    public async Task InvalidEnvelopeRejectsBeforeAnyOperation(bool postgreSql, int version, int minimum, int schema)
    {
        await using var scope = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = scope.Provider;
        var service = new CellBridgeDocumentService(provider, hashing: ProtocolHashingOptions.Disabled);
        var document = (await service.CreateAsync($"/bad-{Guid.NewGuid():N}.docx", [1], TestActor.Value))!;
        var request = new FsshttpbCellRequest
        {
            ProtocolVersion = (ushort)version,
            MinimumVersion = (ushort)minimum,
            HashOptions = new((ulong)schema),
            SubRequests = { new(RequestTypes.PutChanges) { Data = new PutChangesSubRequestData() }, Query(2) },
        };

        var execution = await service.ExecuteAsync(document.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        var response = execution.Response;

        Assert.True(response.Status);
        Assert.Empty(response.SubResponses);
        Assert.Equal(ErrorType.Cell, response.Error!.Type);
        Assert.Equal((ulong)(version is not (12 or 13 or 14) || minimum != 11
            ? CellErrorCode.IncompatibleProtocolVersion : CellErrorCode.RequestStreamSchemaError), response.Error.ErrorCode);
        var persisted = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(document.ResourceId)))!;
        Assert.Equal(new byte[] { 1 }, await StorageTestDocuments.PackageAsync(provider,persisted.Package));
        Assert.Equal(JsonSerializer.Serialize(document), JsonSerializer.Serialize(persisted));
        Assert.Empty(execution.AcceptedSaves);
    }

    private static FsshttpbCellSubRequest Query(ulong id) => new(RequestTypes.QueryChanges)
    {
        RequestId = id,
        Data = new QueryChangesSubRequestData { IncludeCellChanges = true, IncludeStorageManifest = true },
    };

    private static bool IsHash(DataElement element) =>
        StreamObjectHeaderStart.Parse(new(element.Data!)).Type == StreamObjectTypeHeaderStart.DataElementHash;
}
