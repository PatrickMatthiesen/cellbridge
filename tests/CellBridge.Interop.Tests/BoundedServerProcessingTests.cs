using System.Text.Json;
using System.IO.Compression;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Xunit.Abstractions;
using Reference = Microsoft.Protocols.TestSuites.SharedAdapter;

namespace CellBridge.Interop.Tests;

public sealed class BoundedServerProcessingTests(ITestOutputHelper output)
{
    private const int Seed = 0x18_34;
    private static readonly CellBridgeActor Actor = new(new("bounded:writer", "writer", "Bounded writer"), CanCreate: true);

    [Fact]
    public async Task MicrosoftWrittenMixedRequestsPreserveStateCorrelateResponsesAndContinueAfterRejection()
    {
        var random = new Random(Seed);
        var namespaces = new HashSet<Guid>();
        for (int sample = 0; sample < 8; sample++)
        {
            var provider = Memory();
            var service = new CellBridgeDocumentService(provider);
            byte[] content = new byte[random.Next(1, 1025)]; random.NextBytes(content);
            var initial = await service.CreateAsync($"/bounded-{sample}.bin", content, Actor);
            Assert.NotNull(initial);
            string unchanged = JsonSerializer.Serialize(initial);
            var file = await GraphAsync(service,initial);
            ulong count = (ulong)random.Next(1, 1001), firstId = (ulong)(sample * 10 + 1);
            var firstQuery = Query(firstId + 1);
            var lastQuery = Query(firstId + 4);
            firstQuery.IncludeStorageManifest = lastQuery.IncludeStorageManifest = sample % 2;
            firstQuery.IncludeCellChanges = lastQuery.IncludeCellChanges = sample / 2 % 2;
            var unsupported = Query(firstId + 3);
            unsupported.IsPartitionIDGUIDUsed = true;
            unsupported.PartitionIdGUID = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
            var reference = Request([
                new Reference.QueryAccessCellSubRequest(firstId), firstQuery,
                new Reference.AllocateExtendedGuidRangeCellSubRequest(new Reference.Compact64bitInt(count), firstId + 2),
                unsupported, lastQuery,
                new Reference.AllocateExtendedGuidRangeCellSubRequest(new Reference.Compact64bitInt(count + 1), firstId + 5),
            ]);
            byte[] wire = reference.SerializeToByteList().ToArray(); Assert.InRange(wire.Length, 1, 4096);
            var decoded = Decode(wire);
            Assert.Equal(reference.SubRequests.Select(s => s.RequestID), decoded.SubRequests.Select(s => s.RequestId));
            Assert.Equal(unsupported.PartitionIdGUID, decoded.SubRequests[3].TargetPartitionId);
            var executed = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
                decoded, new Dictionary<string, string>(), Actor);
            Assert.Null(executed.LockError); Assert.Empty(executed.AcceptedSaves);
            var response = Reference.FsshttpbResponse.DeserializeResponseFromByteArray(
                executed.Response.ToByteArray(FsshttpbSerializationProfile.SharePoint13_11), 0);
            Assert.False(response.Status);
            Assert.Equal(6, response.CellSubResponses.Count);
            for (int i = 0; i < reference.SubRequests.Count; i++)
            {
                Assert.Equal(reference.SubRequests[i].RequestID, response.CellSubResponses[i].RequestID.DecodedValue);
                Assert.Equal(reference.SubRequests[i].RequestType, response.CellSubResponses[i].RequestType.DecodedValue);
                Assert.Equal(i == 3, response.CellSubResponses[i].Status);
            }
            var access = response.CellSubResponses[0].GetSubResponseData<Reference.QueryAccessSubResponseData>();
            Assert.Equal(0, access.ReadAccessResponse.ReadResponseError.GetErrorData<Reference.HRESULTError>().ErrorCode);
            Assert.Equal(0, access.WriteAccessResponse.WriteResponseError.GetErrorData<Reference.HRESULTError>().ErrorCode);
            Assert.Equal(Guid.Parse(Reference.ResponseError.CellErrorGuid), response.CellSubResponses[3].ResponseError.ErrorTypeGUID);
            Assert.Equal(Reference.CellErrorCode.Requestnotsupported, response.CellSubResponses[3].ResponseError.GetErrorData<Reference.CellError>().ErrorCode);
            foreach (int i in new[] { 1, 4 }) Equal(StorageIds.Capture(file.StorageIndex), response.CellSubResponses[i]
                .GetSubResponseData<Reference.QueryChangesSubResponseData>().StorageIndexExtendedGUID);
            foreach (int i in new[] { 2, 5 })
            {
                var allocated = response.CellSubResponses[i].GetSubResponseData<Reference.AllocateExtendedGuidRangeSubResponseData>();
                Assert.NotEqual(Guid.Empty, allocated.GUIDComponent);
                Assert.True(namespaces.Add(allocated.GUIDComponent));
                Assert.Equal(i == 2 ? count : count + 1, allocated.IntegerRangeMax.DecodedValue - allocated.IntegerRangeMin.DecodedValue);
            }
            var elements = response.DataElementPackage.DataElements;
            var expected = file.Elements.Where(e =>
                (firstQuery.IncludeStorageManifest != 0 || e.DataElementType != DataElementType.StorageManifestDataElementData) &&
                (firstQuery.IncludeCellChanges != 0 || e.DataElementType != DataElementType.CellManifestDataElementData)).ToArray();
            Assert.Equal(expected.Length, elements.Count);
            foreach (var element in expected)
                Assert.Single(elements, e => Same(StorageIds.Capture(element.DataElementExtendedGuid), e.DataElementExtendedGUID));
            var group = Assert.Single(elements, e => e.DataElementType == Reference.DataElementType.ObjectGroupDataElementData)
                .GetData<Reference.ObjectGroupDataElementData>();
            Assert.Contains(group.ObjectGroupData.ObjectGroupObjectDataList, item => item.Data.Content.SequenceEqual(content));
            await Unchanged(provider, initial.ResourceId, unchanged, content);
            output.WriteLine($"seed={Seed} sample={sample} bytes={wire.Length} include-manifest={firstQuery.IncludeStorageManifest} include-cell={firstQuery.IncludeCellChanges} mutation=mixed-operations/unknown-target");
        }
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("partial-last")]
    [InlineData("continuation")]
    [InlineData("hierarchy-ignored")]
    [InlineData("hierarchy-fail")]
    public async Task MicrosoftWrittenControlsLeaveStateUnchangedAndFollowingQuerySucceeds(string control)
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        byte[] content = DocumentBytes();
        var initial = await service.CreateAsync("/controls.docx", content, Actor);
        Assert.NotNull(initial);
        var file = await GraphAsync(service,initial);
        Reference.FsshttpbCellSubRequest operation;
        if (control.StartsWith("partial", StringComparison.Ordinal))
            operation = new Reference.PutChangesCellSubRequest(2, new Reference.ExGuid(file.StorageIndex.Value, file.StorageIndex.Guid))
            { ExpectedStorageIndexExtendedGUID = new(file.StorageIndex.Value, file.StorageIndex.Guid),
                Partial = control == "partial" ? 1 : 0, PartialLast = control == "partial-last" ? 1 : 0 };
        else
        {
            var query = control == "hierarchy-fail" ? new FailingHierarchyQuery(2) : Query(2);
            if (control == "continuation") query.MaxDataElements = new Reference.Compact64bitInt(1);
            if (control.StartsWith("hierarchy", StringComparison.Ordinal))
                query.QueryChangeFilters.Add(new Reference.HierarchyFilter([]) { Depth = Reference.HierarchyFilterDepth.Deep });
            operation = query;
        }
        var reference = Request([Query(1), operation, Query(3)]);
        if (operation is Reference.PutChangesCellSubRequest)
            reference.DataElementPackage = StoredPackage(file);
        byte[] wire = reference.SerializeToByteList().ToArray();
        Assert.InRange(wire.Length, 1, 4096);
        var decoded = Decode(wire);
        if (decoded.SubRequests[1].Data is PutChangesSubRequestData put)
            Assert.Equal(control == "partial" ? (byte)2 : (byte)4, (byte)(put.Flags & 6));
        else
        {
            var query = Assert.IsType<QueryChangesSubRequestData>(decoded.SubRequests[1].Data);
            Assert.Equal(control == "hierarchy-fail", query.HasUnsupportedQueryControls);
            if (control == "continuation") Assert.Equal(1UL, query.MaxDataElements);
        }
        string unchanged = JsonSerializer.Serialize(initial);
        var executed = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            decoded, new Dictionary<string, string>(), Actor);
        Assert.Null(executed.LockError); Assert.Empty(executed.AcceptedSaves);
        var response = Reference.FsshttpbResponse.DeserializeResponseFromByteArray(
            executed.Response.ToByteArray(FsshttpbSerializationProfile.SharePoint13_11), 0);
        Assert.Equal(new ulong[] { 1, 2, 3 }, response.CellSubResponses.Select(s => s.RequestID.DecodedValue));
        Assert.False(response.CellSubResponses[0].Status); Assert.False(response.CellSubResponses[2].Status);
        var middle = response.CellSubResponses[1];
        Assert.Equal(control is not ("hierarchy-ignored" or "continuation"), middle.Status);
        if (middle.Status)
        {
            Assert.Equal(Guid.Parse(Reference.ResponseError.CellErrorGuid), middle.ResponseError.ErrorTypeGUID);
            Assert.Equal(Reference.CellErrorCode.Requestnotsupported, middle.ResponseError.GetErrorData<Reference.CellError>().ErrorCode);
        }
        var expectedIndex = file.StorageIndex;
        foreach (var sub in response.CellSubResponses.Where(s => !s.Status))
        {
            var data = sub.GetSubResponseData<Reference.QueryChangesSubResponseData>();
            bool partial = control == "continuation" && sub.RequestID.DecodedValue == 2;
            Assert.Equal(partial, data.PartialResult);
            Equal(StorageIds.Capture(partial ? ExGuid.Null : expectedIndex), data.StorageIndexExtendedGUID);
        }
        await Unchanged(provider, initial.ResourceId, unchanged, content);
        if (control == "hierarchy-ignored")
        {
            // Neighboring unrestricted queries cannot mask a filtered result here.
            var isolated = Decode(Request([operation]).SerializeToByteList().ToArray());
            var result = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
                isolated, new Dictionary<string, string>(), Actor);
            var full = Reference.FsshttpbResponse.DeserializeResponseFromByteArray(
                result.Response.ToByteArray(FsshttpbSerializationProfile.SharePoint13_11), 0);
            Assert.False(Assert.Single(full.CellSubResponses).Status);
            Assert.Equal(file.Elements.Count(), full.DataElementPackage.DataElements.Count);
            foreach (var expected in file.Elements)
                Assert.Single(full.DataElementPackage.DataElements, e => Same(StorageIds.Capture(expected.DataElementExtendedGuid), e.DataElementExtendedGUID));
            var group = Assert.Single(full.DataElementPackage.DataElements, e => e.DataElementType == Reference.DataElementType.ObjectGroupDataElementData)
                .GetData<Reference.ObjectGroupDataElementData>();
            Assert.Contains(group.ObjectGroupData.ObjectGroupObjectDataList, item => item.Data.Content.SequenceEqual(content));
            await Unchanged(provider, initial.ResourceId, unchanged, content);
        }
        if (operation is Reference.PutChangesCellSubRequest complete)
        {
            // Prove this same Microsoft-written graph is otherwise a valid save.
            // Clearing only the tested flags must reach an accepted complete-save path.
            complete.Partial = complete.PartialLast = 0;
            var valid = Request([complete]); valid.DataElementPackage = reference.DataElementPackage;
            var accepted = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
                Decode(valid.SerializeToByteList().ToArray()), new Dictionary<string, string>(), Actor);
            Assert.Null(accepted.LockError);
            var acknowledgement = Reference.FsshttpbResponse.DeserializeResponseFromByteArray(
                accepted.Response.ToByteArray(FsshttpbSerializationProfile.SharePoint13_11), 0);
            Assert.False(Assert.Single(acknowledgement.CellSubResponses).Status, accepted.Response.SubResponses[0].Error?.ErrorMessage);
            Assert.Equal(initial,accepted.State);
            Assert.Equal(0,accepted.State.Counters.SaveReceipts);
        }
        output.WriteLine($"seed={Seed} bytes={wire.Length} mutation={control} rejected={middle.Status}");
    }

    [Fact]
    public async Task MicrosoftWrittenFollowupsAssembleCompleteElementsThroughTheFinalIndex()
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        byte[] content = DocumentBytes();
        var initial = await service.CreateAsync("/continuation.docx", content, Actor);
        Assert.NotNull(initial);
        string unchanged = JsonSerializer.Serialize(initial);
        var file = await GraphAsync(service, initial);
        var received = new Dictionary<ExGuid, DataElement>();
        Reference.Knowledge knowledge = null;
        bool completed = false;
        for (int page = 0; page < file.Elements.Count(); page++)
        {
            var query = Query((ulong)page + 1);
            query.MaxDataElements = new(1);
            query.Knowledge = knowledge;
            var executed = await new CellBridgeDocumentService(provider).ExecuteAsync(initial.ResourceId,
                DocumentPartitionKind.FileContents, Decode(Request([query]).SerializeToByteList().ToArray()),
                new Dictionary<string, string>(), Actor);
            Assert.Empty(executed.AcceptedSaves);
            var parsed = Reference.FsshttpbResponse.DeserializeResponseFromByteArray(
                executed.Response.ToByteArray(FsshttpbSerializationProfile.SharePoint13_11), 0);
            var sub = Assert.Single(parsed.CellSubResponses);
            Assert.False(sub.Status);
            Assert.Equal(query.RequestID, sub.RequestID.DecodedValue);
            var data = sub.GetSubResponseData<Reference.QueryChangesSubResponseData>();
            var element = Assert.Single(executed.Response.DataElementPackage!.DataElements);
            var referenceElement = Assert.Single(parsed.DataElementPackage.DataElements);
            Equal(StorageIds.Capture(element.DataElementExtendedGuid), referenceElement.DataElementExtendedGUID);
            Assert.True(received.TryAdd(element.DataElementExtendedGuid, element));
            knowledge = data.Knowledge;
            var possession = ClientKnowledge.Deserialize(new(knowledge.SerializeToByteList().ToArray()));
            foreach (var expected in file.Elements)
                Assert.Equal(received.ContainsKey(expected.DataElementExtendedGuid), possession.Contains(expected.SerialNumber));
            if (data.PartialResult)
            {
                Equal(StorageIds.Capture(ExGuid.Null), data.StorageIndexExtendedGUID);
                Assert.NotEqual(DataElementType.StorageIndexDataElementData, element.DataElementType);
                continue;
            }
            Equal(StorageIds.Capture(file.StorageIndex), data.StorageIndexExtendedGUID);
            completed = true;
            break;
        }
        Assert.True(completed);
        Assert.Equal(file.Elements.Count(), received.Count);
        Assert.Equal(content, PartitionGraphSnapshot.Create(received.Values, file.StorageIndex).Materialize());
        await Unchanged(provider, initial.ResourceId, unchanged, content);
    }

    private static Reference.DataElementPackage StoredPackage(PartitionGraphSnapshot file)
    {
        var package = new Reference.DataElementPackage();
        foreach (var element in file.Elements)
        {
            byte[] bytes = element.Data!;
            Reference.DataElementData data = element.DataElementType switch
            {
                DataElementType.StorageIndexDataElementData => new Reference.StorageIndexDataElementData(),
                DataElementType.StorageManifestDataElementData => new Reference.StorageManifestDataElementData(),
                DataElementType.CellManifestDataElementData => new Reference.CellManifestDataElementData(),
                DataElementType.RevisionManifestDataElementData => new Reference.RevisionManifestDataElementData(),
                DataElementType.ObjectGroupDataElementData => new Reference.ObjectGroupDataElementData(),
                _ => throw new InvalidDataException("Unexpected stored fixture element type."),
            };
            Assert.Equal(bytes.Length, data.DeserializeDataElementDataFromByteArray([..bytes, ..new Reference.StreamObjectHeaderEnd8bit(1).SerializeToByteList()], 0));
            package.DataElements.Add(new Reference.DataElement
            {
                // Existing element identities require their original payload bytes.
                // Microsoft parsing above validates them; its envelope writer below
                // carries them unchanged rather than choosing new inner header widths.
                DataElementType = (Reference.DataElementType)element.DataElementType, Data = new StoredPayload(bytes),
                DataElementExtendedGUID = new(element.DataElementExtendedGuid.Value, element.DataElementExtendedGuid.Guid),
                SerialNumber = new(element.SerialNumber.Guid, element.SerialNumber.Value),
            });
        }
        return package;
    }

    private sealed class StoredPayload(byte[] bytes) : Reference.DataElementData
    {
        public override List<byte> SerializeToByteList() => [..bytes];
        public override int DeserializeDataElementDataFromByteArray(byte[] byteArray, int startIndex) =>
            throw new NotSupportedException("This test adapter only preserves an independently parsed stored payload.");
    }

    private static byte[] DocumentBytes()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var types = zip.CreateEntry("[Content_Types].xml");
            types.LastWriteTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using (var writer = new StreamWriter(types.Open()))
                writer.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            var document = zip.CreateEntry("word/document.xml");
            document.LastWriteTime = types.LastWriteTime;
            using (var writer = new StreamWriter(document.Open()))
                writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body/></w:document>");
        }
        return stream.ToArray();
    }

    private static Reference.FsshttpbCellRequest Request(List<Reference.FsshttpbCellSubRequest> operations) => new()
    { ProtocolVersion = 13, MinimumVersion = 11, Signature = FsshttpbCellRequest.RequestSignature,
        GUID = Reference.FsshttpbCellRequest.UserAgentGuid, Version = 1, SubRequests = operations };
    private static Reference.QueryChangesCellSubRequest Query(ulong id) => new(id) { IncludeStorageManifest = 1, IncludeCellChanges = 1 };
    private static FsshttpbCellRequest Decode(byte[] wire)
    {
        var reader = new BinaryReaderEx(wire); var request = FsshttpbCellRequest.Deserialize(reader);
        Assert.Equal(0, reader.Remaining); return request;
    }
    private static bool Same(ExtendedId expected, Reference.ExGuid actual) => expected.Value == actual.Value && expected.Guid == actual.GUID;
    private static void Equal(ExtendedId expected, Reference.ExGuid actual) => Assert.True(Same(expected, actual));
    private static async Task Unchanged(StorageProvider provider, Guid resourceId, string expectedState, byte[] expectedContent)
    {
        var persisted = await provider.Documents.FindAsync(new DocumentIdentity.Resource(resourceId)); Assert.NotNull(persisted);
        Assert.Equal(expectedState, JsonSerializer.Serialize(persisted));
        await using var stream = await provider.Packages.OpenReadAsync(persisted.Package);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal(expectedContent,copy.ToArray());
    }

    private static StorageProvider Memory()
    {
        var content = new InMemoryContentStore();
        return new(new InMemoryDocumentRepository(content),content);
    }
    private static async Task<PartitionGraphSnapshot> GraphAsync(CellBridgeDocumentService service,DocumentHead head)
    {
        var response = await service.ExecuteAsync(head.ResourceId,DocumentPartitionKind.FileContents,
            new() { SubRequests = { new(RequestTypes.QueryChanges) { RequestId = 1,Data = new QueryChangesSubRequestData
                { IncludeStorageManifest = true,IncludeCellChanges = true } } } },new Dictionary<string,string>(),Actor);
        Assert.False(Assert.Single(response.Response.SubResponses).Status);
        return PartitionGraphSnapshot.Create(response.Response.DataElementPackage!.DataElements,
            Assert.IsType<QueryChangesSubResponseData>(response.Response.SubResponses[0].Data).StorageIndexExtendedGuid);
    }

    private sealed class FailingHierarchyQuery(ulong id) : Reference.QueryChangesCellSubRequest(id)
    {
        public override List<byte> SerializeToByteList()
        {
            // The vendored writer intentionally omits filter flags. Append that
            // object with Microsoft's header writer, keeping input independent of CellBridge.
            var bytes = base.SerializeToByteList(); var end = bytes.GetRange(bytes.Count - 2, 2);
            bytes.RemoveRange(bytes.Count - 2, 2);
            bytes.AddRange(new Reference.StreamObjectHeaderStart32bit(Reference.StreamObjectTypeHeaderStart.QueryChangesFilterFlags, 1).SerializeToByteList());
            bytes.Add(1); bytes.AddRange(end); return bytes;
        }
    }
}
