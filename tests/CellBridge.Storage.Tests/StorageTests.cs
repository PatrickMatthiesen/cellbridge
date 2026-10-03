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
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

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
    public async Task AtomicCreationAndTransitionsPreventLostUpdates()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.CreateAsync("/same.docx", MinimalDocx.Create()).AsTask()));
        var initial = Assert.Single(outcomes, x => x is not null)!;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => provider.State.TransitionAsync(initial.ResourceId,
            (current, _) => new StateTransition<bool>(current with { ContentVersion = current.ContentVersion + 1 }, true)).AsTask()));
        var final = await provider.State.FindByResourceIdAsync(initial.ResourceId);
        Assert.Equal(initial.ContentVersion + 32, final!.ContentVersion);
        Assert.Equal(32, final.StateVersion);
    }

    [Fact]
    public async Task ReferenceSavesRetryAndContinueAfterRestoration()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        await CheckReferenceSaves(provider);
    }

    internal static async Task CheckReferenceSaves(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create()))!;
        var first = Fixture("save-first");
        var second = Fixture("save-second");
        var initial = await StoredDocument.RestoreAsync(state, provider.Content);
        ((PutChangesSubRequestData)first.SubRequests.Single().Data!).ExpectedStorageIndex = initial.FilePartition.FileGraph.StorageIndex;
        var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, first, new Dictionary<string, string>());
        Assert.All(saved.Response.SubResponses, r => Assert.False(r.Status));
        Assert.Equal(state.StateVersion + 1, saved.State.StateVersion);
        Assert.Equal((await provider.State.FindByResourceIdAsync(state.ResourceId))!.StateVersion, saved.State.StateVersion);
        var initialVersion = saved.State.ContentVersion;
        var retried = await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, first, new Dictionary<string, string>());
        Assert.All(retried.Response.SubResponses, r => Assert.False(r.Status));
        Assert.Equal(initialVersion, retried.State.ContentVersion);
        Assert.Equal(saved.State.StateVersion, retried.State.StateVersion);
        Assert.Equal(saved.Response.ToByteArray(), retried.Response.ToByteArray());
        var advanced = await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, second, new Dictionary<string, string>());
        Assert.All(advanced.Response.SubResponses, r => Assert.False(r.Status));
        Assert.Equal(initialVersion + 1, advanced.State.ContentVersion);
        var delayed = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, first, new Dictionary<string, string>());
        Assert.True(Assert.Single(delayed.Response.SubResponses).Status);
        Assert.Equal((ulong)CellErrorCode.CoherencyFailure, delayed.Response.SubResponses[0].Error!.ErrorCode);
        var final = await provider.State.FindByResourceIdAsync(state.ResourceId);
        Assert.Equal(final!.StateVersion, advanced.State.StateVersion);
        var restored = await StoredDocument.RestoreAsync(final!, provider.Content);
        Assert.Equal(advanced.State.ContentVersion, restored.ContentVersion);
        Assert.Equal(restored.Content, restored.FilePartition.FileGraph.Materialize());
        Assert.Null(await service.ResolveAsync(new FssHttpRequest { ResourceId = Guid.NewGuid().ToString(), UseResourceId = true, Url = state.Path }));
    }

    [Fact]
    public async Task ContentFailureCannotPublishOrAcknowledgeASave()
    {
        var state = new InMemoryStateStore();
        var blobs = new InMemoryContentStore();
        var service = new CellBridgeDocumentService(new(state, blobs));
        var document = (await service.CreateAsync("/failure.docx", MinimalDocx.Create()))!;
        var failed = new CellBridgeDocumentService(new(state, new FailingWrites(blobs)));
        var request = Fixture("save-first");
        ((PutChangesSubRequestData)request.SubRequests.Single().Data!).ExpectedStorageIndex =
            (await StoredDocument.RestoreAsync(document, blobs)).FilePartition.FileGraph.StorageIndex;
        await Assert.ThrowsAsync<IOException>(() => failed.ExecuteAsync(document.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>()).AsTask());
        Assert.Equal(document.ContentVersion, (await state.FindByResourceIdAsync(document.ResourceId))!.ContentVersion);
    }

    [Fact]
    public async Task LostCommitReplyResolvesReceiptWithoutApplyingTwice()
    {
        var inner = new InMemoryStateStore();
        var provider = new StorageProvider(new LostReplyState(inner), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/lost.docx", MinimalDocx.Create()))!;
        var request = Fixture("save-first");
        ((PutChangesSubRequestData)request.SubRequests.Single().Data!).ExpectedStorageIndex =
            (await StoredDocument.RestoreAsync(state, provider.Content)).FilePartition.FileGraph.StorageIndex;
        var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>());
        Assert.All(saved.Response.SubResponses, r => Assert.False(r.Status));
        Assert.Equal(state.ContentVersion + 1, (await inner.FindByResourceIdAsync(state.ResourceId))!.ContentVersion);
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
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
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
    private sealed class LostReplyState(IDocumentStateStore inner) : IDocumentStateStore
    {
        public bool Durable => false; public bool Shared => false;
        public ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken ct = default) => inner.FindByResourceIdAsync(id, ct);
        public ValueTask<DocumentState?> FindByPathKeyAsync(string key, CancellationToken ct = default) => inner.FindByPathKeyAsync(key, ct);
        public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken ct = default) => inner.ListAsync(offset, limit, ct);
        public ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken ct = default) => inner.TryCreateAsync(state, ct);
        public ValueTask CheckHealthAsync(CancellationToken ct = default) => inner.CheckHealthAsync(ct);
        public async ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition, CancellationToken ct = default)
        {
            await inner.TransitionAsync(id, transition, ct);
            throw new StorageUnavailableException("Injected lost commit reply.");
        }
    }
}

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")))
            Skip = "Set ConnectionStrings__cellbridge to a migrated PostgreSQL test database.";
    }
}

public sealed class PostgreSqlTests
{
    [PostgreSqlFact]
    public async Task ReferenceSavesAcrossIndependentProviders()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        var state = new PostgreSqlStateStore(source);
        await state.CheckHealthAsync();
        await StorageTests.CheckReferenceSaves(new(state, new PostgreSqlContentStore(source)));
    }

    [PostgreSqlFact]
    public async Task FileSystemProviderUsesSameDurableStateContract()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        var root = Path.Combine(Path.GetTempPath(), "cellbridge-postgres-test-" + Guid.NewGuid().ToString("N"));
        // Published test state permanently references these objects. Keep them in
        // this disposable test root until the disposable test database is removed.
        await StorageTests.CheckReferenceSaves(new(new PostgreSqlStateStore(source), new FileSystemContentStore(root)));
    }

    [PostgreSqlFact]
    public async Task DifferentDocumentCanCommitWhileAnotherRowIsLocked()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        var provider = new StorageProvider(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));
        var service = new CellBridgeDocumentService(provider);
        var first = (await service.CreateAsync("/" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create()))!;
        var second = (await service.CreateAsync("/" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create()))!;
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SELECT resource_id FROM cellbridge_documents WHERE resource_id=$1 FOR UPDATE", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = first.ResourceId });
        await command.ExecuteScalarAsync();
        await provider.State.TransitionAsync(second.ResourceId, (current, _) =>
            new StateTransition<bool>(current with { ContentVersion = current.ContentVersion + 1 }, true)).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }
}
