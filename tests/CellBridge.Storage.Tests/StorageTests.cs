using System.Collections.Immutable;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Storage.FileSystem;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public class StorageTests
{
    [Fact]
    public async Task RestorePreservesIdentitiesGraphAndUnsignedKnowledge()
    {
        var blobs = new InMemoryContentStore();
        var original = new DocumentStore().Put("/shared/%2520%23%3F%C3%A6.docx", MinimalDocx.Create("before"));
        var state = await original.CaptureAsync(blobs);
        state = state with { Partitions = state.Partitions.Select(p => p with { Knowledge = ulong.MaxValue }).ToImmutableArray() };
        var encoded = JsonSerializer.Serialize(state);
        state = JsonSerializer.Deserialize<DocumentState>(encoded)!;
        var restored = await StoredDocument.RestoreAsync(state, blobs);
        Assert.Equal(original.Url, restored.Url);
        Assert.Equal(original.Etag, restored.Etag);
        Assert.Equal(original.CreatedUtc, restored.CreatedUtc);
        Assert.Equal(original.Content, restored.Content);
        Assert.Equal(original.FilePartition.FileGraph.Materialize(), restored.FilePartition.FileGraph.Materialize());
        Assert.Equal(ulong.MaxValue, restored.FilePartition.KnowledgeSequence);
        Assert.Equal(original.FilePartition.ProtocolIdentity.SerialGuid, restored.FilePartition.ProtocolIdentity.SerialGuid);
    }

    [Fact]
    public async Task AtomicCreationAndIndependentEditorCommandsPreventLostUpdates()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.CreateAsync("/same.docx", MinimalDocx.Create(), TestActor.Value).AsTask()));
        var initial = Assert.Single(outcomes, x => x is not null)!;
        var clients = Enumerable.Range(0, 32).Select(_ => Guid.NewGuid()).ToArray();
        var results = await Task.WhenAll(clients.Select(async client =>
        {
            for (var attempt = 0; attempt < 64; attempt++)
            {
                var head = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
                await Task.Yield();
                var result = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.Office(initial.ResourceId,
                    StorageTestDocuments.Admission(head), new() { Type = SubRequestType.EditorsTable, SubRequestDataAttributes =
                    { ["EditorsTableRequestType"] = "JoinEditingSession", ["ClientID"] = client.ToString(), ["Timeout"] = "3600", ["AsEditor"] = "true" } }));
                if (result.Changed) return result;
                Assert.Equal("FileUnauthorizedAccess", result.ErrorCode);
                Assert.NotEqual(head.StateVersion, result.Current.Head.StateVersion);
            }
            throw new InvalidOperationException("The independent editor command could not obtain a current admission.");
        }));
        Assert.All(results, result => { Assert.Null(result.ErrorCode); Assert.True(result.Changed); });
        var final = (await provider.Documents.ReadControlAsync(initial.ResourceId))!;
        Assert.Equal(initial.ContentVersion, final.Head.ContentVersion);
        Assert.Equal(32, final.Head.StateVersion);
        Assert.Equal(clients.Order(), final.Editors.Select(editor => editor.ClientId).Order());
        Assert.Equal(1, final.Head.Counters.Revisions);

    }

    [Fact]
    public async Task ReferenceSavesRetryAndContinueAfterRestoration()
    {
        var provider = StorageTestDocuments.Memory();
        await CheckReferenceSaves(provider);
    }

    internal static async Task CheckReferenceSaves(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var first = Fixture("save-first");
        var second = Fixture("save-second");
        Rebase(first, await StorageTestDocuments.GraphAsync(service, state));
        var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, first, new Dictionary<string, string>(), TestActor.Value);
        Assert.All(saved.Response.SubResponses, r => Assert.False(r.Status));
        Assert.Equal(state.StateVersion + 1, saved.State.StateVersion);
        Assert.Equal((await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!.StateVersion, saved.State.StateVersion);
        var initialVersion = saved.State.ContentVersion;
        var retried = await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, first, new Dictionary<string, string>(), TestActor.Value);
        Assert.All(retried.Response.SubResponses, r => Assert.False(r.Status));
        Assert.Equal(initialVersion, retried.State.ContentVersion);
        Assert.Equal(saved.State.StateVersion, retried.State.StateVersion);
        Assert.Equal(saved.Response.ToByteArray(), retried.Response.ToByteArray());
        Rebase(second, await StorageTestDocuments.GraphAsync(service, saved.State));
        var advanced = await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, second, new Dictionary<string, string>(), TestActor.Value);
        Assert.All(advanced.Response.SubResponses, r => Assert.False(r.Status));
        Assert.Equal(initialVersion + 1, advanced.State.ContentVersion);
        var delayed = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, first, new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(delayed.Response.SubResponses).Status);
        Assert.Equal((ulong)CellErrorCode.CoherencyFailure, delayed.Response.SubResponses[0].Error!.ErrorCode);
        var final = await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId));
        Assert.Equal(final!.StateVersion, advanced.State.StateVersion);
        Assert.Equal(advanced.State.ContentVersion, final.ContentVersion);
        Assert.Equal(await StorageTestDocuments.PackageAsync(provider, final.Package),
            (await StorageTestDocuments.GraphAsync(service, final)).Materialize());
        Assert.Null(await service.ResolveAsync(new FssHttpRequest { ResourceId = Guid.NewGuid().ToString(), UseResourceId = true, Url = state.Path }, TestActor.Value));
    }

    [Fact]
    public async Task ContentFailureCannotPublishOrAcknowledgeASave()
    {
        var blobs = new InMemoryContentStore();
        var state = new InMemoryDocumentRepository(blobs);
        var service = new CellBridgeDocumentService(new(state, blobs));
        var document = (await service.CreateAsync("/failure.docx", MinimalDocx.Create(), TestActor.Value))!;
        var failed = new CellBridgeDocumentService(new(state, new FailingWrites(blobs)));
        var request = Fixture("save-first");
        Rebase(request, await StorageTestDocuments.GraphAsync(service, document));
        await Assert.ThrowsAsync<IOException>(() => failed.ExecuteAsync(document.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value).AsTask());
        Assert.Equal(document.ContentVersion, (await state.FindAsync(new DocumentIdentity.Resource(document.ResourceId)))!.ContentVersion);
    }

    [Fact]
    public async Task LostCommitReplyResolvesReceiptWithoutApplyingTwice()
    {
        var content = new InMemoryContentStore();
        var inner = new InMemoryDocumentRepository(content);
        var provider = new StorageProvider(new LostReplyState(inner), content);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/lost.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = Fixture("save-first");
        Rebase(request, await StorageTestDocuments.GraphAsync(service, state));
        var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.All(saved.Response.SubResponses, r => Assert.False(r.Status));
        Assert.Equal(state.ContentVersion + 1, (await inner.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!.ContentVersion);
        var receipt = Assert.Single(saved.AcceptedSaves);
        Assert.True(receipt.IsReplay);
        Assert.Equal(saved.State.Package, receipt.Package);
        Assert.Equal(saved.State.ContentVersion, receipt.ContentVersion);
    }

    [Fact]
    public async Task FileSystemContentSurvivesRecreationAndRejectsCorruptionAndQuotas()
    {
        string root = Path.Combine(Path.GetTempPath(), "cellbridge-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var content = new FileSystemContentStore(root, maxObjectBytes: 100);
            using var input = new MemoryStream(new byte[] { 1, 2, 3 });
            var handle = await content.WriteAsync(input);
            await using (var output = await new FileSystemContentStore(root).OpenReadAsync(handle))
            {
                using var memory = new MemoryStream(); await output.CopyToAsync(memory);
                Assert.Equal(new byte[] { 1, 2, 3 }, memory.ToArray());
            }
            using var large = new MemoryStream(new byte[101]);
            await Assert.ThrowsAsync<StorageQuotaExceededException>(() => content.WriteAsync(large).AsTask());
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
            await File.WriteAllBytesAsync(Path.Combine(root, handle.Key + ".blob"), new byte[] { 3, 2, 1 });
            await Assert.ThrowsAsync<StorageCorruptionException>(() => content.OpenReadAsync(handle).AsTask());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CapabilitiesRejectVolatileOrUnsharedConfigurations()
    {
        var provider = StorageTestDocuments.Memory();
        Assert.Throws<InvalidOperationException>(() => provider.Require(true, false));
        Assert.Throws<InvalidOperationException>(() => provider.Require(false, true));
    }

    internal static FsshttpbCellRequest Fixture(string name)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json")));
        var request = fixture.RootElement.GetProperty("request");
        var parts = MtomMessageParser.Parse(Convert.FromBase64String(request.GetProperty("bodyBase64").GetString()!), request.GetProperty("contentType").GetString()!);
        var binary = parts.Single(x => x.ContentType.Contains("application/octet-stream", StringComparison.OrdinalIgnoreCase));
        return FsshttpbCellRequest.Deserialize(new BinaryReaderEx(binary.Content));
    }

    private sealed class FailingWrites(IContentStore inner) : IContentStore
    {
        public bool Durable => false; public bool Shared => false;
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) => throw new IOException("Injected flush failure.");
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) => inner.OpenReadAsync(handle, cancellationToken);
    }
    private sealed class LostReplyState(IDocumentRepository inner) : RepositoryTestProxy(inner)
    {
        public override async ValueTask<PublicationResult> PublishAsync(PreparedDocumentPublication publication, CancellationToken cancellationToken = default)
        {
            var result = await Inner.PublishAsync(publication, cancellationToken);
            if (result.Status == PublicationStatus.Accepted) throw new StorageUnavailableException("Injected lost commit reply.");
            return result;
        }
    }

    internal static void Rebase(FsshttpbCellRequest request, PartitionGraphSnapshot graph)
    {
        var index = graph.Elements.Single(element => element.DataElementExtendedGuid.Equals(graph.StorageIndex));
        if (!request.DataElementPackage!.DataElements.Any(element => element.DataElementExtendedGuid.Equals(graph.StorageIndex)))
            request.DataElementPackage.DataElements.Add(index);
        Assert.IsType<PutChangesSubRequestData>(request.SubRequests.Single(operation => operation.RequestType == RequestTypes.PutChanges).Data).ExpectedStorageIndex = graph.StorageIndex;
    }

}



[Collection("PostgreSQL")]
public sealed class PostgreSqlTests
{
    [PostgreSqlFact]
    public async Task ReferenceSavesAcrossIndependentProviders()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        var state = new PostgreSqlStateStore(source);
        await ((IDocumentRepository)state).CheckHealthAsync();
        await StorageTests.CheckReferenceSaves(new(state, new PostgreSqlContentStore(source)));
    }

    [PostgreSqlFact]
    public async Task FileSystemProviderUsesSameDurableStateContract()
    {
        var root = Path.Combine(Path.GetTempPath(), "cellbridge-postgres-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await RepositoryTestDatabase.WithDatabase(new(), async source =>
            {
                var content = new PostgreSqlFileSystemContentStore(source, root);
                await StorageTests.CheckReferenceSaves(new(new PostgreSqlStateStore(source, validationContent: content), content));
            });
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [PostgreSqlFact]
    public async Task DifferentDocumentCanCommitWhileAnotherRowIsLocked()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        var provider = new StorageProvider(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));
        var service = new CellBridgeDocumentService(provider);
        var first = (await service.CreateAsync("/" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var second = (await service.CreateAsync("/" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SELECT \"ResourceId\" FROM cellbridge_repository_heads WHERE \"ResourceId\"=$1 FOR UPDATE", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = first.ResourceId });
        await command.ExecuteScalarAsync();
        await RevisionHistoryTests.Save(service, second, "independent row").WaitAsync(TimeSpan.FromSeconds(3));
    }
}
