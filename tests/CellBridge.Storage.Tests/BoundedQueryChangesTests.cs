using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class BoundedQueryChangesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FollowupsCompleteTheGraphWithoutAdvertisingUndeliveredElements(bool postgreSql)
    {
        await using var source = ProtocolExecutionTestProvider.Create(postgreSql);
        await using var peer = postgreSql ? ProtocolExecutionTestProvider.Create(true) : null;
        var provider = source.Provider;
        var service = new CellBridgeDocumentService(provider);
        var document = (await service.CreateAsync($"/bounded-{Guid.NewGuid():N}.docx", MinimalDocx.Create(), TestActor.Value))!;
        var full = await Read(service, document.ResourceId);
        var expected = full.DataElementPackage!.DataElements;
        const ulong maximum = 1;
        var received = new Dictionary<ExGuid, DataElement>();
        ClientKnowledge? knowledge = null;
        bool completed = false;
        int pages = 0;
        while (pages++ <= expected.Count)
        {
            // A new service instance and replay use the same persisted records,
            // rather than a process-local cursor or a retained response package.
            service = new CellBridgeDocumentService(pages % 2 == 0 ? peer?.Provider ?? provider : provider);
            var response = await Read(service, document.ResourceId, maximum, knowledge);
            var replay = await Read(new(provider), document.ResourceId, maximum, knowledge);
            Assert.Equal(response.ToByteArray(), replay.ToByteArray());
            var inspection = CellBridge.OfficeInspectors.OfficeInspector.ParseResponse(
                response.ToByteArray(FsshttpbSerializationProfile.SharePoint13_11));
            Assert.True(inspection.Parsed, inspection.Error);
            var result = Assert.Single(response.SubResponses);
            Assert.False(result.Status, result.Error?.ErrorMessage);
            var data = Assert.IsType<QueryChangesSubResponseData>(result.Data);
            var payloads = response.DataElementPackage!.DataElements;
            Assert.True((ulong)payloads.Sum(e => e.GetSerializedLength()) <=
                maximum + (ulong)payloads.Max(e => e.GetSerializedLength()));
            Assert.NotEmpty(payloads);
            foreach (var element in payloads)
                Assert.True(received.TryAdd(element.DataElementExtendedGuid, element));
            knowledge = ClientKnowledge.Deserialize(new(data.KnowledgeBytes!));
            foreach (var element in expected)
                Assert.Equal(received.ContainsKey(element.DataElementExtendedGuid), knowledge.Contains(element.SerialNumber));
            if (data.PartialResult)
            {
                Assert.Equal(0UL, data.Waterline);
                Assert.True(data.StorageIndexExtendedGuid.IsNull);
                Assert.DoesNotContain(payloads, e => e.DataElementType == DataElementType.StorageIndexDataElementData);
            }
            else
            {
                Assert.False(data.StorageIndexExtendedGuid.IsNull);
                completed = true;
                break;
            }
        }
        Assert.True(completed);
        Assert.True(pages > 1);
        Assert.Equal(expected.Count, received.Count);
        foreach (var element in expected)
            Assert.Equal(element.Data, received[element.DataElementExtendedGuid].Data);
        var unchanged = await Read(new(provider), document.ResourceId, maximum, knowledge);
        Assert.Empty(unchanged.DataElementPackage!.DataElements);
        Assert.False(Assert.IsType<QueryChangesSubResponseData>(Assert.Single(unchanged.SubResponses).Data).PartialResult);
    }

    [Fact]
    public async Task ExactFullBudgetReturnsTheOriginalResponse()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var document = (await service.CreateAsync("/exact.docx", MinimalDocx.Create(), TestActor.Value))!;
        var full = await Read(service, document.ResourceId);
        var maximum = (ulong)full.DataElementPackage!.DataElements.Sum(e => e.GetSerializedLength());
        Assert.Equal(full.ToByteArray(), (await Read(service, document.ResourceId, maximum)).ToByteArray());
    }

    [Fact]
    public async Task SaveBetweenPagesConvergesToTheLatestGraph()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var document = (await service.CreateAsync("/changing.docx", MinimalDocx.Create("before"), TestActor.Value))!;
        var first = await Read(service, document.ResourceId, 1);
        var received = first.DataElementPackage!.DataElements.ToDictionary(e => e.DataElementExtendedGuid);
        var firstData = Assert.IsType<QueryChangesSubResponseData>(Assert.Single(first.SubResponses).Data);
        Assert.True(firstData.PartialResult);
        var knowledge = ClientKnowledge.Deserialize(new(firstData.KnowledgeBytes!));
        var changed = MinimalDocx.Create("saved between pages");
        var save = await service.ExecuteAsync(document.ResourceId, DocumentPartitionKind.FileContents,
            await StorageTestDocuments.SaveRequestAsync(service, document, changed), new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(save.Response.SubResponses).Status);
        var latest = await Read(service, document.ResourceId);
        var maximum = (ulong)latest.DataElementPackage!.DataElements.Max(e => e.GetSerializedLength());
        for (int page = 0; page <= latest.DataElementPackage.DataElements.Count; page++)
        {
            var response = await Read(new(provider), document.ResourceId, maximum, knowledge);
            var result = Assert.Single(response.SubResponses);
            Assert.False(result.Status, result.Error?.ErrorMessage);
            foreach (var element in response.DataElementPackage!.DataElements)
                received[element.DataElementExtendedGuid] = element;
            var data = Assert.IsType<QueryChangesSubResponseData>(result.Data);
            knowledge = ClientKnowledge.Deserialize(new(data.KnowledgeBytes!));
            if (!data.PartialResult)
            {
                var graph = PartitionGraphSnapshot.Create(received.Values, data.StorageIndexExtendedGuid);
                Assert.Equal(changed, graph.Materialize());
                return;
            }
        }
        Assert.Fail("Bounded follow-ups did not complete after a concurrent publication.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnElementLargerThanTheThresholdIsReturnedWhole(bool allowFragments)
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var document = (await service.CreateAsync("/oversized.docx", MinimalDocx.Create(), TestActor.Value))!;
        var response = await Read(service, document.ResourceId, 1, allowFragments: allowFragments);
        var result = Assert.Single(response.SubResponses);
        Assert.False(result.Status, result.Error?.ErrorMessage);
        Assert.True(Assert.IsType<QueryChangesSubResponseData>(result.Data).PartialResult);
        var element = Assert.Single(response.DataElementPackage!.DataElements);
        Assert.True(element.GetSerializedLength() > 1);
        Assert.NotEqual(DataElementType.FragmentDataElementData, element.DataElementType);
    }

    [Fact]
    public async Task WholeCellRoundingDoesNotReturnAnEndlesslyRepeatingPartialPage()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var document = (await service.CreateAsync("/rounded.docx", MinimalDocx.Create(), TestActor.Value))!;
        var response = await Read(service, document.ResourceId, 1, round: true);
        Assert.True(Assert.Single(response.SubResponses).Status);
        Assert.Null(response.DataElementPackage);
    }

    [Fact]
    public void TheCompleteElementThatCrossesTheThresholdRemainsInThePage()
    {
        var guid = Guid.NewGuid();
        var elements = Enumerable.Range(1, 3).Select(i => new DataElement(
            DataElementType.ObjectDataBLOBDataElementData, new((uint)i, guid), new(guid, (ulong)i))
            { Data = new byte[i * 10] }).ToArray();
        var maximum = (ulong)(elements[0].GetSerializedLength() + elements[1].GetSerializedLength() - 1);
        var selection = FileQueryResponseBuilder.Select(elements, new(4, guid),
            new(new(5, guid), new(6, guid)), 3, new QueryChangesSubRequestData { MaxDataElements = maximum });

        Assert.Null(selection.Error);
        Assert.True(selection.Data.PartialResult);
        Assert.Equal(elements.Take(2).Select(e => e.DataElementExtendedGuid).ToHashSet(), selection.PayloadIds);
        Assert.True((ulong)elements.Take(2).Sum(e => e.GetSerializedLength()) > maximum);
        var knowledge = ClientKnowledge.Deserialize(new(selection.Data.KnowledgeBytes!));
        Assert.True(knowledge.Contains(elements[0].SerialNumber));
        Assert.True(knowledge.Contains(elements[1].SerialNumber));
        Assert.False(knowledge.Contains(elements[2].SerialNumber));
    }

    [Fact]
    public async Task ReadRevocationBetweenPagesRejectsTheFollowup()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var document = (await service.CreateAsync("/revoke.docx", MinimalDocx.Create(), TestActor.Value))!;
        const ulong maximum = 1;
        var first = await Read(service, document.ResourceId, maximum);
        var data = Assert.IsType<QueryChangesSubResponseData>(Assert.Single(first.SubResponses).Data);
        Assert.True(data.PartialResult);
        var current = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(document.ResourceId)))!;
        await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(document.ResourceId,
            StorageTestDocuments.Admission(current), current.Security with { Owner = "revoked" }, current.AuthorityGeneration));
        var denied = await Read(new(provider), document.ResourceId,
            maximum, ClientKnowledge.Deserialize(new(data.KnowledgeBytes!)));
        Assert.Null(denied.DataElementPackage);
        var failure = Assert.Single(denied.SubResponses);
        Assert.True(failure.Status);
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, failure.Error!.ErrorCode);
    }

    [Fact]
    public void AmbiguousSerialsCannotProduceAFalseProgressPage()
    {
        var guid = Guid.NewGuid();
        var serial = new SerialNumber(guid, 1);
        var elements = new[] { new DataElement(DataElementType.ObjectDataBLOBDataElementData, new(1, guid), serial) { Data = [1] },
            new DataElement(DataElementType.ObjectDataBLOBDataElementData, new(2, guid), serial) { Data = [2] } };
        var selection = FileQueryResponseBuilder.Select(elements, new(3, guid),
            new(new(4, guid), new(5, guid)), 2,
            new QueryChangesSubRequestData { MaxDataElements = (ulong)elements[0].GetSerializedLength() });
        var response = FileQueryResponseBuilder.Build(1, selection, [], null);
        Assert.True(Assert.Single(response.SubResponses).Status);
        Assert.Null(response.DataElementPackage);
    }

    [Fact]
    public void PartialKnowledgeIncludesFilteredElementsOnlyWhenRequested()
    {
        var guid = Guid.NewGuid();
        var elements = Enumerable.Range(1, 3).Select(i => new DataElement(
            i == 3 ? DataElementType.StorageManifestDataElementData : DataElementType.ObjectDataBLOBDataElementData,
            new((uint)i, guid), new(guid, (ulong)i)) { Data = [1] }).ToArray();
        foreach (bool includeFiltered in new[] { false, true })
        {
            var selection = FileQueryResponseBuilder.Select(elements, new(4, guid),
                new(new(5, guid), new(6, guid)), 3, new QueryChangesSubRequestData
                {
                    MaxDataElements = (ulong)elements[0].GetSerializedLength(),
                    IncludeFilteredOutDataElementsInKnowledge = includeFiltered,
                });
            Assert.Null(selection.Error);
            Assert.True(selection.Data.PartialResult);
            var knowledge = ClientKnowledge.Deserialize(new(selection.Data.KnowledgeBytes!));
            Assert.True(knowledge.Contains(elements[0].SerialNumber));
            Assert.False(knowledge.Contains(elements[1].SerialNumber));
            Assert.Equal(includeFiltered, knowledge.Contains(elements[2].SerialNumber));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedKnowledgeRequiresAFullResponseThatFits(bool rangeLimit)
    {
        var guid = Guid.NewGuid();
        var elements = Enumerable.Range(1, 2).Select(i => new DataElement(
            DataElementType.ObjectDataBLOBDataElementData, new((uint)i, guid), new(guid, (ulong)i)) { Data = [1] }).ToArray();
        var wire = rangeLimit
            ? BinaryKnowledgeBuilder.FromRanges(Enumerable.Range(0, 10_001).Select(i => (guid, (ulong)i * 2, (ulong)i * 2)), ExGuid.Null, 0)
            : BinaryKnowledgeBuilder.FromElements([], ExGuid.Null, 0, versionToken: [1]);
        var knowledge = ClientKnowledge.Deserialize(new(wire));
        Assert.True(knowledge.RequiresFullResponse);
        var selection = FileQueryResponseBuilder.Select(elements, new(3, guid), new(new(4, guid), new(5, guid)), 2,
            new QueryChangesSubRequestData { Knowledge = knowledge, MaxDataElements = (ulong)elements[0].GetSerializedLength() });
        Assert.NotNull(selection.Error);
        var complete = FileQueryResponseBuilder.Select(elements, new(3, guid), new(new(4, guid), new(5, guid)), 2,
            new QueryChangesSubRequestData { Knowledge = knowledge,
                MaxDataElements = (ulong)elements.Sum(e => e.GetSerializedLength()) });
        Assert.Null(complete.Error);
        Assert.False(complete.Data.PartialResult);
        Assert.Equal(elements.Length, complete.PayloadIds.Count);
    }

    private static async Task<FsshttpbResponse> Read(CellBridgeDocumentService service, Guid id,
        ulong? maximum = null, ClientKnowledge? knowledge = null, bool round = false, bool allowFragments = false)
    {
        var request = new FsshttpbCellRequest { SubRequests = { new(RequestTypes.QueryChanges)
        {
            RequestId = 1,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true,
                MaxDataElements = maximum, Knowledge = knowledge, RoundKnowledgeToWholeCellChanges = round,
                AllowFragments = allowFragments },
        } } };
        return (await service.ExecuteAsync(id, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value)).Response;
    }
}
