using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class MovedPathMetadataTests
{
    [Theory]
    [InlineData(SubRequestType.Coauth, false)]
    [InlineData(SubRequestType.Coauth, true)]
    [InlineData(SubRequestType.SchemaLock, false)]
    [InlineData(SubRequestType.SchemaLock, true)]
    [InlineData(SubRequestType.EditorsTable, false)]
    [InlineData(SubRequestType.EditorsTable, true)]
    public async Task MetadataOperationsUseResolvedDocumentAfterLiteralPercentMove(SubRequestType type, bool useId)
    {
        foreach (var name in new[] { "literal%2Fname.docx", "literal%20name.docx", "literal%23name.docx" })
        {
            var provider = StorageTestDocuments.Memory();
            var service = new CellBridgeDocumentService(provider);
            var original = (await service.CreateAsync("/shared/original.docx", MinimalDocx.Create(), TestActor.Value))!;
            var moved = await service.MoveDocumentAsync(original.ResourceId, "/shared/team/" + name, TestActor.Value);
            var file = HostIntegrationTests.File(original.ResourceId, type);
            file.UseResourceId = useId;
            file.Url = useId ? original.Path : string.Join('/', moved.Path.Split('/').Select(Uri.EscapeDataString));
            var attrs = file.SubRequests[0].SubRequestDataAttributes;
            attrs["ClientID"] = Guid.NewGuid().ToString();
            attrs["Timeout"] = "60";
            if (type == SubRequestType.Coauth)
            {
                attrs["CoauthRequestType"] = "JoinCoauthoring";
                attrs["SchemaLockID"] = Guid.NewGuid().ToString();
            }
            else if (type == SubRequestType.SchemaLock)
            {
                attrs["SchemaLockRequestType"] = "GetLock";
                attrs["SchemaLockID"] = Guid.NewGuid().ToString();
            }
            else
            {
                attrs["EditorsTableRequestType"] = "JoinEditingSession";
                attrs["AsEditor"] = "true";
            }
            var result = await new CellBridgeRequestProcessor(service).ExecuteAsync(
                new() { Requests = { file } }, "https://host.test", TestActor.Value);
            Assert.Equal("Success", Assert.Single(Assert.Single(result.Response.Responses).SubResponses).ErrorCode);
            var current = (await provider.Documents.ReadControlAsync(original.ResourceId))!;
            Assert.Equal(moved.Path, current.Head.Path);
            Assert.Equal(moved.Package, current.Head.Package);
            if (type is SubRequestType.Coauth or SubRequestType.EditorsTable) Assert.Single(current.Editors);
            if (type is SubRequestType.Coauth or SubRequestType.SchemaLock) Assert.Single(current.Coordination.SchemaOwners);
        }
    }

    [Theory]
    [InlineData("_vti_bin")]
    [InlineData("_VTI_INF.HTML")]
    [InlineData("_CellBridge")]
    public async Task ApiAndProtocolRenameRejectReservedRouteNamesWithoutChangingState(string name)
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var original = (await service.CreateAsync("/shared/team/file.docx", MinimalDocx.Create(), TestActor.Value))!;
        await Assert.ThrowsAsync<ArgumentException>(() => service.RenameDocumentAsync(original.ResourceId, name, TestActor.Value).AsTask());
        var file = HostIntegrationTests.File(original.ResourceId, SubRequestType.FileOperation);
        file.SubRequests[0].SubRequestDataAttributes["FileOperation"] = "Rename";
        file.SubRequests[0].SubRequestDataAttributes["NewFileName"] = name;
        var result = await new CellBridgeRequestProcessor(service).ExecuteAsync(
            new() { Requests = { file } }, "https://host.test", TestActor.Value);
        Assert.Equal("InvalidArgument", Assert.Single(Assert.Single(result.Response.Responses).SubResponses).ErrorCode);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await provider.Documents.FindAsync(new DocumentIdentity.Resource(original.ResourceId))));
        Assert.Equal(original.ResourceId, (await provider.Documents.FindAsync(new DocumentIdentity.Path(original.PathKey)))!.ResourceId);
    }
}
