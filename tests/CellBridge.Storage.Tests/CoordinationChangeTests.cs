using System.Collections.Immutable;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class CoordinationChangeTests
{
    [Fact]
    public async Task MetadataReadDoesNotLoadHistoryOrChangeThePublishedPackage()
    {
        var memory = StorageTestDocuments.Memory();
        var provider = new StorageProvider(new ControlOnlyRepository(memory.Documents), memory.Content);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/read-history.docx", MinimalDocx.Create(), TestActor.Value))!;
        var file = HostIntegrationTests.File(state.ResourceId, SubRequestType.WhoAmI, SubRequestType.LockStatus);
        var result = await new CellBridgeRequestProcessor(service).ExecuteAsync(new() { Requests = { file } }, "https://host.test", TestActor.Value);
        Assert.All(result.Response.Responses[0].SubResponses, response => Assert.Equal("Success", response.ErrorCode));
        var after = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.Equal(state.StateVersion, after.StateVersion);
        Assert.Equal(state.Package, after.Package);
        Assert.Equal(state.Counters.Revisions, after.Counters.Revisions);
    }

    [Fact]
    public async Task ClonedControlValuesStayUnchangedUntilARealEditorOrLeaseMutation()
    {
        var provider = StorageTestDocuments.Memory();
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/editors.docx", MinimalDocx.Create(), TestActor.Value))!;
        var client = Guid.NewGuid().ToString();
        await Execute(Editor("JoinEditingSession", ("Timeout", "3600"), ("AsEditor", "true")));
        await Execute(Editor("UpdateEditorMetadata", ("Key", "Platform"), ("Value", Convert.ToBase64String("Win32"u8))));
        var original = (await provider.Documents.ReadControlAsync(state.ResourceId))!;
        var clone = JsonSerializer.Deserialize<DocumentControlRead>(JsonSerializer.Serialize(original))!;
        var noOp = DocumentControlReducer.Apply(clone,
            new DocumentControlCommand.Office(state.ResourceId, TestStorage.Admit(clone.Head), new() { Type = SubRequestType.ServerTime }), provider.Limits);
        Assert.False(noOp.Changed);
        Assert.Equal(JsonSerializer.Serialize(original.Head), JsonSerializer.Serialize(noOp.Current.Head));
        Assert.Equal("Win32"u8.ToArray(), Assert.Single(noOp.Current.Editors).Metadata["Platform"].ToArray());

        var metadata = DocumentControlReducer.Apply(clone, new DocumentControlCommand.Office(state.ResourceId,
            TestStorage.Admit(clone.Head), Editor("UpdateEditorMetadata", ("Key", "Platform"), ("Value", "AQID"))), provider.Limits);
        Assert.True(metadata.Changed);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(metadata.Current.Editors).Metadata["Platform"].ToArray());
        Assert.True(metadata.Current.Partitions.Single(partition => partition.Kind == 2).Knowledge > clone.Partitions.Single(partition => partition.Kind == 2).Knowledge);
        var refreshed = DocumentControlReducer.Apply(clone, new DocumentControlCommand.Office(state.ResourceId,
            TestStorage.Admit(clone.Head), Editor("RefreshEditingSession", ("Timeout", "7200"), ("AsEditor", "true"))), provider.Limits);
        Assert.True(refreshed.Changed);
        Assert.True(Assert.Single(refreshed.Current.Editors).ExpiresUtc > Assert.Single(clone.Editors).ExpiresUtc);
        var lease = new HostLease("token", TestActor.Value.Identity.Subject, clone.UtcNow.AddHours(1));
        var locked = DocumentControlReducer.Apply(clone,
            new DocumentControlCommand.SetHostLease(state.ResourceId, TestStorage.Admit(clone.Head), lease, null), provider.Limits);
        Assert.True(locked.Changed);
        Assert.Equal(lease, locked.Current.Coordination.HostLock);
        Assert.Null(clone.Coordination.HostLock);

        FssHttpSubRequest Editor(string operation, params (string Key, string Value)[] attributes)
        {
            var request = new FssHttpSubRequest { Type = SubRequestType.EditorsTable,
                SubRequestDataAttributes = { ["EditorsTableRequestType"] = operation, ["ClientID"] = client } };
            foreach (var attribute in attributes) request.SubRequestDataAttributes[attribute.Key] = attribute.Value;
            return request;
        }
        async Task Execute(FssHttpSubRequest request)
        {
            var current = (await provider.Documents.ReadControlAsync(state.ResourceId))!;
            var result = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.Office(state.ResourceId, TestStorage.Admit(current.Head), request));
            Assert.Null(result.ErrorCode);
        }
    }

    private sealed class ControlOnlyRepository(IDocumentRepository inner) : RepositoryTestProxy(inner)
    {
        public override ValueTask<DocumentRevisionPage?> ReadRevisionsAsync(Guid id, int offset, int limit, CancellationToken ct = default) => throw new InvalidOperationException("Metadata reads must not load history.");
        public override ValueTask<DocumentRevisionSelection?> ReadRevisionAsync(Guid id, ulong number, CancellationToken ct = default) => throw new InvalidOperationException("Metadata reads must not load history.");
        public override ValueTask<PartitionRead?> ReadPartitionAsync(Guid id, PartitionReadRequest request, CancellationToken ct = default) => throw new InvalidOperationException("Metadata reads must not load graph records.");
    }
}
