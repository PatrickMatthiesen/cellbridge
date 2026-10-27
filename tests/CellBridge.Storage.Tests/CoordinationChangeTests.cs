using System.Collections.Immutable;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class CoordinationChangeTests
{
    [Fact]
    public async Task MetadataReadDoesNotWriteAnotherStateOrChangeHistory()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/read-history.docx", MinimalDocx.Create(), TestActor.Value))!;
        var file = HostIntegrationTests.File(state.ResourceId, SubRequestType.WhoAmI, SubRequestType.LockStatus);

        var result = await new CellBridgeRequestProcessor(service).ExecuteAsync(
            new() { Requests = { file } }, "https://host.test", TestActor.Value);

        Assert.All(result.Response.Responses[0].SubResponses, response => Assert.Equal("Success", response.ErrorCode));
        var after = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal(state.StateVersion, after.StateVersion);
        Assert.Equal(state.Content, after.Content);
        Assert.Equal(state.Revisions, after.Revisions);
    }

    [Fact]
    public async Task ClonedEditorAndLockValuesAreUnchangedButNewValuesAreDetected()
    {
        var document = new DocumentStore().GetOrAdd("/editors.docx");
        var client = Guid.NewGuid();
        document.JoinEditingSession(client, 3600, asEditor: true);
        document.UpdateEditorMetadata(client, "Platform", "Win32"u8.ToArray());
        var state = await document.CaptureAsync(new InMemoryContentStore());
        state = state with { Coordination = state.Coordination with
        {
            SchemaOwners = [new("schema", client.ToString(), DateTime.UtcNow.AddHours(1), 0, "schema")],
            CoauthorClients = [client.ToString()],
        } };
        var clone = JsonSerializer.Deserialize<DocumentState>(JsonSerializer.Serialize(state))!;
        Assert.False(CellBridgeRequestProcessor.CoordinationChanged(state, clone));

        var editor = clone.Editors[0];
        Assert.True(CellBridgeRequestProcessor.CoordinationChanged(state, clone with
        {
            Editors = [editor with { Metadata = editor.Metadata.SetItem("Platform", [1, 2, 3]) }],
        }));
        Assert.True(CellBridgeRequestProcessor.CoordinationChanged(state, clone with
        {
            Editors = [editor with { ExpiresUtc = editor.ExpiresUtc.AddSeconds(1) }],
        }));
        Assert.True(CellBridgeRequestProcessor.CoordinationChanged(state, clone with
        {
            Coordination = clone.Coordination with { SchemaOwners = [], CoauthorClients = [] },
        }));
        Assert.True(CellBridgeRequestProcessor.CoordinationChanged(state, clone with
        {
            Coordination = clone.Coordination with { HostLock = new("token", "owner", DateTime.UtcNow.AddHours(1)) },
        }));
        Assert.True(CellBridgeRequestProcessor.CoordinationChanged(state, clone with
        {
            Partitions = clone.Partitions.Select(partition => partition.Kind == 2
                ? partition with { Knowledge = partition.Knowledge + 1 } : partition).ToImmutableArray(),
        }));
    }
}
