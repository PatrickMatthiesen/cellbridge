using CellBridge.OfficeInspectors;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Tests;
using CellBridge.Web;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage;
using Xunit.Abstractions;

namespace CellBridge.Storage.Tests;

public sealed class RetainedReplyFramingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(FsshttpbSerializationProfile.Current)]
    [InlineData(FsshttpbSerializationProfile.SharePoint13_11)]
    public void HistoryFixtureInlineGraphIsIndependentlyDecodable(FsshttpbSerializationProfile profile)
    {
        var fixture = new GraphFixture([8, 7, 6]);
        var response = new FsshttpbResponse { DataElementPackage = new() { DataElements = fixture.Elements } };
        var inspection = OfficeInspector.ParseResponse(response.ToByteArray(profile));
        Assert.True(inspection.Parsed, inspection.Error ?? inspection.Summary);
    }

    [Fact]
    public void HistoryFixtureFixedRecordsUseNormativeHeaders()
    {
        var fixture = new GraphFixture([8, 7, 6], blob: true);
        var index = fixture.Elements.Single(e => e.DataElementType == DataElementType.StorageIndexDataElementData);
        var reader = new BinaryReaderEx(index.Data!);
        var count = 0;
        while (reader.Remaining > 0)
        {
            var header = StreamObjectHeaderStart.Parse(reader);
            Assert.Equal(2, header.HeaderSize);
            Assert.Equal(0, header.Compound);
            reader.Skip(header.Length);
            count++;
        }
        Assert.Equal(6, count);
        foreach (var element in fixture.Elements.Where(e => e.DataElementType is
            DataElementType.StorageManifestDataElementData or DataElementType.CellManifestDataElementData or DataElementType.RevisionManifestDataElementData))
        {
            var fixedRecords = new BinaryReaderEx(element.Data!);
            while (fixedRecords.Remaining > 0)
            {
                var header = StreamObjectHeaderStart.Parse(fixedRecords);
                Assert.Equal(2, header.HeaderSize);
                Assert.Equal(0, header.Compound);
                fixedRecords.Skip(header.Length);
                count++;
            }
        }
        Assert.Equal(20, count);
        var response = new FsshttpbResponse { DataElementPackage = new() { DataElements = [index] } };
        var inspection = OfficeInspector.ParseResponse(response.ToByteArray());
        Assert.True(inspection.Parsed, inspection.Error ?? inspection.Summary);
    }

    [Fact]
    public async Task InMemoryHistoryRetainsIndependentlyDecodableSaveReplies()
    {
        var provider = PortabilityTests.Memory();
        var service = new CellBridgeDocumentService(provider);
        var original = (await service.CreateAsync("/history.docx", MinimalDocx.Create("initial"), TestActor.Value))!;
        var metadata = await service.ExecuteAsync(original.ResourceId, DocumentPartitionKind.Metadata,
            MetadataPublicationTests.Initial(new GraphFixture([8, 7, 6], blob: true)), new Dictionary<string, string>(), TestActor.Value);
        var file = MetadataPublicationTests.Initial(new GraphFixture(MinimalDocx.Create("inherited blob"), blob: true));
        ((PutChangesSubRequestData)file.SubRequests[0].Data!).ExpectedStorageIndex = StorageIds.Restore(original.Partitions.Single(p => p.Kind == 0).StorageIndex!);
        var saved = await service.ExecuteAsync(original.ResourceId, DocumentPartitionKind.FileContents, file, new Dictionary<string, string>(), TestActor.Value);
        var receipts = metadata.State.Receipts.Concat(saved.State.Receipts).Where(r => r.Response is not null)
            .DistinctBy(r => r.Response!.Key).ToArray();
        Assert.Equal(2, receipts.Length);
        await service.RestoreRevisionAsync(original.ResourceId, 2, RevisionHistory.Latest(saved.State), "before-recovery", TestActor.Value);
        foreach (var receipt in receipts)
        {
            await using var stream = await provider.Content.OpenReadAsync(receipt.Response!);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            var bytes = buffer.ToArray();
            if (Environment.GetEnvironmentVariable("CELLBRIDGE_REPLY_EVIDENCE") is { } directory)
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllBytesAsync(Path.Combine(directory, $"{receipt.Response!.Key}.bin"), bytes);
            }
            var inspection = OfficeInspector.ParseResponse(bytes);
            output.WriteLine(inspection.Summary);
            Assert.Equal(12, inspection.Response.ProtocolVersion);
            Assert.Equal(11, inspection.Response.MinimumVersion);
            Assert.True(inspection.Parsed, inspection.Error ?? inspection.Summary);
        }
    }
}
