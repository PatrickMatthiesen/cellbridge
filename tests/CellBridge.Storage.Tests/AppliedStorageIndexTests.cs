using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class AppliedStorageIndexTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppliedIndexPackageSurvivesServiceAndProviderRecreation(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/applied-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = await Save(service, initial);
        var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        var index = CheckIndex(saved.Response);
        var stored = (await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(0) { IncludeCatalog = true }))!;
        Assert.Equal(IndexedDocumentGraph.Capture(index.DataElementExtendedGuid), stored.Partition.StorageIndex);
        Assert.Equal(new SerialNumber(stored.Partition.StorageIndexSerial!.Guid, stored.Partition.StorageIndexSerial.Value), index.SerialNumber);
        Assert.DoesNotContain(stored.Elements, element => element.Type == (uint)DataElementType.StorageIndexDataElementData);
        var mappings = await provider.Documents.ReadMappingsAsync(new(initial.ResourceId, initial.LifecycleGeneration, 0), stored.Partition.PublicationSequence);
        foreach (var mapping in IndexedDocumentGraph.ReadMetadata(new(initial.ResourceId, initial.LifecycleGeneration, 0), index).Mappings)
            Assert.Contains(mappings, current => current.Key == mapping.Key && current.Target == mapping.Target && current.Serial == mapping.Serial);
        await using var recreated = ProtocolExecutionTestProvider.Create(postgreSql);
        var retry = await new CellBridgeDocumentService(postgreSql ? recreated.Provider : provider).ExecuteAsync(initial.ResourceId,
            DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal(saved.State.StateVersion, retry.State.StateVersion);
        Assert.Equal(saved.Response.ToByteArray(), retry.Response.ToByteArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedQueriesAndSaveShareAppliedIndexWithoutDuplicates(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/mixed-applied-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = await Save(service, initial);
        request.SubRequests.Insert(0, Query(10));
        request.SubRequests.Add(Query(20));
        var response = (await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value)).Response;
        Assert.All(response.SubResponses, s => Assert.False(s.Status, s.Error?.ErrorMessage));
        var decoded = FsshttpbResponse.Deserialize(new(response.ToByteArray()));
        var index = Assert.IsType<PutChangesSubResponseData>(decoded.SubResponses[1].Data).PutChangesResponse!.AppliedStorageIndexID;
        Assert.Equal(index, Assert.IsType<QueryChangesSubResponseData>(decoded.SubResponses[2].Data).StorageIndexExtendedGuid);
        Assert.Single(decoded.DataElementPackage!.DataElements, e => e.DataElementExtendedGuid.Equals(index));
        Assert.Equal(decoded.DataElementPackage.DataElements.Count,
            decoded.DataElementPackage.DataElements.Select(e => e.DataElementExtendedGuid).Distinct().Count());
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(true, -1)]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    public async Task MandatoryIndexPreflightHasExactBoundaryAndCannotPublishOnFailure(bool postgreSql, int offset)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/bounded-applied-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var first = (await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            new() { SubRequests = { Query(10) } }, new Dictionary<string, string>(), TestActor.Value)).Response;
        var partition = (await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(0) { IncludeCatalog = true }))!;
        var oldIndex = first.DataElementPackage!.DataElements.Single(e =>
            e.DataElementExtendedGuid.Equals(StorageIds.Restore(partition.Partition.StorageIndex!)));
        var newId = new ExGuid(1, Guid.NewGuid());
        // A new index over the existing graph keeps the storage quota valid while
        // its mandatory response consumes the last bytes after the earlier query.
        var index = new DataElement(oldIndex.DataElementType, newId,
            new SerialNumber(partition.Partition.Identity.SerialGuid, partition.Partition.Knowledge + 1)) { Data = oldIndex.Data };
        var writer = new BinaryWriterEx();
        index.Serialize(writer);
        long earlierBytes = first.DataElementPackage.DataElements.Sum(e =>
        {
            var serialized = new BinaryWriterEx(); e.Serialize(serialized); return (long)serialized.Length;
        });
        long budget = earlierBytes + writer.Length + offset;
        Assert.True(budget >= partition.Elements.Sum(e => e.Payload.Length) + index.Data!.LongLength);
        // This tests the service's cumulative wire budget over an already valid repository.
        var bounded = new StorageProvider(provider.Documents, provider.Content, provider.Limits with { MaxGraphBytes = budget });
        var request = new FsshttpbCellRequest
        {
            DataElementPackage = new() { DataElements = { new(oldIndex.DataElementType, newId, SerialNumber.Null) { Data = oldIndex.Data }, oldIndex } },
            SubRequests =
            {
                Query(10),
                new(RequestTypes.PutChanges) { RequestId = 20, Data = new PutChangesSubRequestData
                {
                    StorageIndex = newId, ExpectedStorageIndex = oldIndex.DataElementExtendedGuid,
                    Flags = 1, AdditionalFlagsBits = 1,
                } },
            },
        };
        var result = await new CellBridgeDocumentService(bounded).ExecuteAsync(initial.ResourceId,
            DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        var response = FsshttpbResponse.Deserialize(new(result.Response.ToByteArray()));
        Assert.False(response.SubResponses[0].Status);
        Assert.Equal(offset < 0, response.SubResponses[1].Status);
        Assert.Equal(initial.ContentVersion + (offset < 0 ? 0u : 1u), result.State.ContentVersion);
        if (offset < 0)
        {
            Assert.Equal((ulong)CellErrorCode.RequestNotSupported, response.SubResponses[1].Error!.ErrorCode);
            Assert.Null(response.SubResponses[1].Data);
            Assert.Empty(result.AcceptedSaves);
            Assert.Equal(0, result.State.Counters.SaveReceipts);
            Assert.Equal(JsonSerializer.Serialize(initial), JsonSerializer.Serialize(result.State));
            Assert.DoesNotContain(response.DataElementPackage!.DataElements, e => e.DataElementExtendedGuid.Equals(newId));
        }
        else
        {
            var applied = Assert.IsType<PutChangesSubResponseData>(response.SubResponses[1].Data).PutChangesResponse!;
            Assert.NotEqual(newId, applied.AppliedStorageIndexID);
            var returned = Assert.Single(response.DataElementPackage!.DataElements, e => e.DataElementExtendedGuid.Equals(applied.AppliedStorageIndexID));
            var serialized = new BinaryWriterEx(); returned.Serialize(serialized);
            Assert.Equal(writer.Length, serialized.Length);
            Assert.Single(result.AcceptedSaves);
            Assert.Equal(1, result.State.Counters.SaveReceipts);
        }
        Assert.All(first.DataElementPackage.DataElements, element =>
            Assert.Contains(response.DataElementPackage!.DataElements, e => e.DataElementExtendedGuid.Equals(element.DataElementExtendedGuid)));
        long responseBytes = response.DataElementPackage!.DataElements.Sum(element =>
        {
            var serialized = new BinaryWriterEx(); element.Serialize(serialized); return (long)serialized.Length;
        });
        Assert.Equal(earlierBytes + (offset < 0 ? 0 : writer.Length), responseBytes);
        Assert.Equal(JsonSerializer.Serialize(result.State),
            JsonSerializer.Serialize((await bounded.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!));
    }

    private static DataElement CheckIndex(FsshttpbResponse response)
    {
        var decoded = FsshttpbResponse.Deserialize(new(response.ToByteArray()));
        var sub = Assert.Single(decoded.SubResponses);
        Assert.False(sub.Status, sub.Error?.ErrorMessage);
        var id = Assert.IsType<PutChangesSubResponseData>(sub.Data).PutChangesResponse!.AppliedStorageIndexID;
        Assert.False(id.IsNull);
        var index = Assert.Single(decoded.DataElementPackage!.DataElements);
        Assert.Equal(id, index.DataElementExtendedGuid);
        Assert.Equal(DataElementType.StorageIndexDataElementData, index.DataElementType);
        Assert.NotEmpty(StorageIndexMappingSerials.ReadMappings(index.Data!));
        return index;
    }

    private static async Task<FsshttpbCellRequest> Save(CellBridgeDocumentService service, DocumentHead state)
    {
        var request = StorageTests.Fixture("save-first");
        var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data);
        StorageTests.Rebase(request, await StorageTestDocuments.GraphAsync(service, state));
        put.AdditionalFlagsBits |= 1;
        return request;
    }

    private static FsshttpbCellSubRequest Query(ulong id) => new(RequestTypes.QueryChanges)
    { RequestId = id, Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } };
}
