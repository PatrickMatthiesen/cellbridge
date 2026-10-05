using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Web;

namespace CellBridge.Storage.Tests;

public sealed class HistoryProfileFallbackTests
{
    [Fact]
    public async Task SharePointProfileIgnoresVersioningExtensionAndReturnsCurrentGraphWithoutVersionTokens()
    {
        var writer = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.QueryChangesRequest, 1).Serialize(writer);
        writer.WriteByte(0);
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.QueryChangesVersioning, 2).Serialize(writer);
        writer.WriteBytes([0xff, 0x7f]);
        new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.SubRequest).Serialize(writer);
        var query = QueryChangesSubRequestData.Deserialize(new BinaryReaderEx(writer.ToArray()));
        Assert.True(query.IgnoredQueryChangesVersioning);
        Assert.False(query.HasUnsupportedQueryControls);
        Assert.Null(query.Waterline);
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/profile.docx", MinimalDocx.Create(), TestActor.Value))!;
        async Task<byte[]> Execute(QueryChangesSubRequestData data)
        {
            var request = new FsshttpbCellRequest { SubRequests = { new(RequestTypes.QueryChanges) { RequestId = 1, Data = data } } };
            var response = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request,
                new Dictionary<string, string>(), TestActor.Value);
            Assert.False(response.Response.SubResponses[0].Status);
            return response.Response.ToByteArray();
        }
        Assert.Equal(await Execute(new()), await Execute(query));
        Assert.Throws<NotSupportedException>(() => new QueryChangesSubRequestData { Waterline = 1 }.Serialize(new()));
    }
}
