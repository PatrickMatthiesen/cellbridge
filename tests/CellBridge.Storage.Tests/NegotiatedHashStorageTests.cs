using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Tests;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed class NegotiatedHashStorageTests
{
    private static readonly ProtocolHashingOptions Configuration = new(new byte[32]);

    [Fact]
    public Task MemoryHashQueriesPreserveStorageAndReceiptIntegrity() => Check(new(new InMemoryStateStore(), new InMemoryContentStore()));

    [PostgreSqlFact]
    public async Task PostgreSqlHashQueriesPreserveStorageAndReceiptIntegrity()
    {
        await using var data = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await Check(new(new PostgreSqlStateStore(data), new PostgreSqlContentStore(data)));
    }

    private static async Task Check(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider, hashing: Configuration);
        var initial = (await service.CreateAsync("/hash-" + Guid.NewGuid() + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var fixture = new GraphFixture([1, 2, 3]);
        var upload = MetadataPublicationTests.Initial(fixture);
        var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.Metadata, upload, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        var before = JsonSerializer.Serialize(saved.State);
        var receiptBytes = saved.Response.ToByteArray();
        foreach (bool instead in new[] { false, true })
        {
            var query = new FsshttpbCellRequest { HashOptions = new(1, instead, true),
                SubRequests = { Query(1, StoredDocument.MetadataPartitionId), Query(2, Guid.Empty), Query(3, StoredDocument.MetadataPartitionId) } };
            var result = await new CellBridgeDocumentService(provider, hashing: Configuration).ExecuteAsync(initial.ResourceId,
                DocumentPartitionKind.FileContents, query, new Dictionary<string, string>(), TestActor.Value);
            Assert.All(result.Response.SubResponses, s => Assert.False(s.Status, s.Error?.ErrorMessage));
            Assert.Contains(result.Response.DataElementPackage!.DataElements, IsHash);
            Assert.Equal(before, JsonSerializer.Serialize(result.State));
            foreach (var element in result.Response.DataElementPackage.DataElements.Where(IsHash))
            {
                Assert.Equal(1UL, DataElementWireHash.Deserialize(new(element.Data!)).Schema);
                Assert.Throws<InvalidDataException>(() => ObjectGroupDataElement.ParseOpaque(element));
            }
        }
        // Negotiation can accompany a save but must not rewrite the persisted receipt.
        upload.HashOptions = new(HashesInsteadOfData: true);
        var replay = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.Metadata, upload, new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal(receiptBytes, replay.Response.ToByteArray());
        Assert.Equal(before, JsonSerializer.Serialize(replay.State));
        foreach (var partition in replay.State.Partitions)
            foreach (var element in partition.Elements) _ = await provider.Content.ReadVerifiedAsync(element.Payload);
        var malformed = new FsshttpbCellRequest { HashOptions = new(2, true, true), DataElementPackage = upload.DataElementPackage,
            SubRequests = { upload.SubRequests[0], Query(7, Guid.Empty) } };
        var rejected = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.Metadata, malformed, new Dictionary<string, string>(), TestActor.Value);
        Assert.True(rejected.Response.Status);
        Assert.Equal(ErrorType.Cell, rejected.Response.Error!.Type);
        Assert.Equal((ulong)CellErrorCode.RequestStreamSchemaError, rejected.Response.Error!.ErrorCode);
        Assert.Empty(rejected.AcceptedSaves);
        Assert.Equal(before, JsonSerializer.Serialize(rejected.State));
        // Hash-bearing and excluded uploads remain unsupported before publication.
        var badUpload = MetadataPublicationTests.Initial(new GraphFixture([1, 2, 3]));
        var group = badUpload.DataElementPackage!.DataElements.First(e => e.DataElementType == DataElementType.ObjectGroupDataElementData);
        var projected = ObjectGroupWireHash.Project(group, new(HashesInsteadOfData: true), Configuration);
        group.Data = projected.Data;
        var unsupported = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.Metadata, badUpload, new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(unsupported.Response.SubResponses).Status);
        Assert.Equal(before, JsonSerializer.Serialize(unsupported.State));
    }

    private static FsshttpbCellSubRequest Query(ulong id, Guid partition) => new(RequestTypes.QueryChanges)
    { RequestId = id, TargetPartitionId = partition, Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } };
    private static bool IsHash(DataElement element) => element.DataElementType == DataElementType.ObjectGroupDataElementData &&
        StreamObjectHeaderStart.Parse(new(element.Data!)).Type == StreamObjectTypeHeaderStart.DataElementHash;
}
