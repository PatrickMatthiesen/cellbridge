using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using Npgsql;

var id = Guid.Parse(args[0]);
var phase = args[1];
var signal = args[2];
await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")
    ?? throw new InvalidOperationException("A disposable PostgreSQL database is required."));
var state = new PostgreSqlStateStore(source);
IContentStore content = Environment.GetEnvironmentVariable("CELLBRIDGE_PROCESS_CONTENT_ROOT") is { Length: > 0 } root
    ? new PostgreSqlFileSystemContentStore(source, root)
    : new PostgreSqlContentStore(source);
var before = await state.FindByResourceIdAsync(id) ?? throw new InvalidOperationException("Probe document is missing.");
if (args.Length > 3)
{
    var metadataRequest = FsshttpbCellRequest.Deserialize(new(await File.ReadAllBytesAsync(args[3])));
    var metadataService = new CellBridgeDocumentService(new(new PausedStateStore(state, phase, signal), content));
    var metadata = await metadataService.ExecuteAsync(id, DocumentPartitionKind.Metadata, metadataRequest,
        new Dictionary<string, string>(), new CellBridgeActor(new SubjectIdentity("tests:writer", "test-writer", "Test writer"), CanCreate: true));
    if (metadata.Response.SubResponses.Any(r => r.Status)) throw new InvalidOperationException("Probe metadata save failed.");
    return;
}
using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "save-first.json")));
var request = fixture.RootElement.GetProperty("request");
var parts = MtomMessageParser.Parse(Convert.FromBase64String(request.GetProperty("bodyBase64").GetString()!), request.GetProperty("contentType").GetString()!);
var binary = parts.Single(p => p.ContentType.Contains("application/octet-stream"));
var cell = FsshttpbCellRequest.Deserialize(new BinaryReaderEx(binary.Content));
((PutChangesSubRequestData)cell.SubRequests.Single().Data!).ExpectedStorageIndex =
    (await StoredDocument.RestoreAsync(before, content)).FilePartition.FileGraph.StorageIndex;
var service = new CellBridgeDocumentService(new(new PausedStateStore(state, phase, signal), content));
var result = await service.ExecuteAsync(id, DocumentPartitionKind.FileContents, cell, new Dictionary<string, string>(), new CellBridgeActor(new SubjectIdentity("tests:writer", "test-writer", "Test writer"), CanCreate: true));
if (result.Response.SubResponses.Any(r => r.Status)) throw new InvalidOperationException("Probe save failed.");

sealed class PausedStateStore(IDocumentStateStore inner, string phase, string signal) : IDocumentStateStore
{
    public bool Durable => inner.Durable;
    public bool Shared => inner.Shared;
    public ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken ct = default) => inner.FindByResourceIdAsync(id, ct);
    public ValueTask<DocumentState?> FindByPathKeyAsync(string key, CancellationToken ct = default) => inner.FindByPathKeyAsync(key, ct);
    public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken ct = default) => inner.ListAsync(offset, limit, ct);
    public ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken ct = default) => inner.TryCreateAsync(state, ct);
    public ValueTask CheckHealthAsync(CancellationToken ct = default) => inner.CheckHealthAsync(ct);
    public async ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition, CancellationToken ct = default)
    {
        bool mutates = false;
        T result;
        try
        {
            result = await inner.TransitionAsync(id, (current, now) =>
            {
                var proposed = transition(current, now);
                mutates = proposed.Next is not null;
                if (mutates && phase == "during")
                {
                    // Fault-injection only: hold the real provider transaction
                    // open until the parent terminates this process.
                    File.WriteAllText(signal, "ready");
                    Thread.Sleep(Timeout.Infinite);
                }
                if (mutates && phase == "before") throw new BeforePublication();
                return proposed;
            }, ct);
        }
        catch (BeforePublication)
        {
            // Pause after rollback. The deterministic callback did no I/O and no state was published.
            await Pause();
            throw;
        }
        if (mutates && phase == "after") await Pause();
        return result;
    }
    private sealed class BeforePublication : Exception;
    private async Task Pause()
    {
        await File.WriteAllTextAsync(signal, "ready");
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}
