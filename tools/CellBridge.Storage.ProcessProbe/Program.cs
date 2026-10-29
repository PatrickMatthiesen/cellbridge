using System.Data.Common;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

var id = Guid.Parse(args[0]);
var phase = args[1];
if (phase is not ("before" or "during" or "after")) throw new ArgumentException("Unknown publication crash phase.");
var pause = new PublicationPause(phase, args[2]);
await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")
    ?? throw new InvalidOperationException("A disposable PostgreSQL database is required."));
var options = new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source)
    .AddInterceptors(new PublicationCommands(pause), new PublicationTransactions(pause)).Options;
IContentStore content = Environment.GetEnvironmentVariable("CELLBRIDGE_PROCESS_CONTENT_ROOT") is { Length: > 0 } root
    ? new PostgreSqlFileSystemContentStore(options, root)
    : new PostgreSqlContentStore(options);
IDocumentRepository documents = new PostgreSqlStateStore(options, validationContent: content);
var control = await documents.ReadControlAsync(id) ?? throw new InvalidOperationException("Probe document is missing.");
FsshttpbCellRequest request;
var partitionKind = args.Length > 3 ? 1 : 0;
if (args.Length > 3)
    request = FsshttpbCellRequest.Deserialize(new(await File.ReadAllBytesAsync(args[3])));
else
{
    using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "save-first.json")));
    var wire = fixture.RootElement.GetProperty("request");
    var parts = MtomMessageParser.Parse(Convert.FromBase64String(wire.GetProperty("bodyBase64").GetString()!), wire.GetProperty("contentType").GetString()!);
    var binary = parts.Single(part => part.ContentType.Contains("application/octet-stream", StringComparison.OrdinalIgnoreCase));
    request = FsshttpbCellRequest.Deserialize(new BinaryReaderEx(binary.Content));
}
var partition = control.Partitions.Single(value => value.Kind == partitionKind);
var put = request.SubRequests.Select(operation => operation.Data).OfType<PutChangesSubRequestData>().Single();
if (partition.StorageIndex is { } index)
{
    var serial = partition.StorageIndexSerial ?? throw new InvalidOperationException("Probe partition has no logical root serial.");
    var mappings = await documents.ReadMappingsAsync(new(id, control.Head.LifecycleGeneration, partitionKind), partition.PublicationSequence);
    var expected = new DataElement(DataElementType.StorageIndexDataElementData, new(index.Value, index.Guid), new(serial.Guid, serial.Value))
    {
        Data = StorageIndexPatch.Serialize(mappings.Select(mapping => new StorageIndexMapping(
            mapping.Key.Kind == IndexedMappingKind.Manifest ? StreamObjectTypeHeaderStart.StorageIndexManifestMapping :
            mapping.Key.Kind == IndexedMappingKind.Cell ? StreamObjectTypeHeaderStart.StorageIndexCellMapping : StreamObjectTypeHeaderStart.StorageIndexRevisionMapping,
            mapping.Key.Revision is { } revision ? new ExGuid(revision.Value, revision.Guid) : null,
            mapping.Key.Cell is { } cell ? new CellId(new(cell.LongId.Value, cell.LongId.Guid), new(cell.ShortId.Value, cell.ShortId.Guid)) : null,
            new(mapping.Target.Value, mapping.Target.Guid), new(mapping.Serial.Guid, mapping.Serial.Value))))
    };
    put.ExpectedStorageIndex = new(index.Value, index.Guid);
    var package = request.DataElementPackage ?? throw new InvalidOperationException("Probe save has no data-element package.");
    package.DataElements.RemoveAll(element => element.DataElementExtendedGuid == expected.DataElementExtendedGuid);
    package.DataElements.Add(expected);
}
else put.ExpectedStorageIndex = ExGuid.Null;
var proxy = DispatchProxy.Create<IDocumentRepository, PublicationPauseProxy>();
((PublicationPauseProxy)(object)proxy).Configure(documents, pause);
var service = new CellBridgeDocumentService(new(proxy, content));
var result = await service.ExecuteAsync(id, (DocumentPartitionKind)partitionKind, request,
    new Dictionary<string, string>(), new(new("tests:writer", "test-writer", "Test writer"), CanCreate: true));
if (result.Response.SubResponses.Any(response => response.Status)) throw new InvalidOperationException("Probe save failed.");

public class PublicationPauseProxy : DispatchProxy
{
    private IDocumentRepository inner = null!;
    private PublicationPause pause = null!;
    public void Configure(IDocumentRepository repository, PublicationPause publicationPause) { inner = repository; pause = publicationPause; }
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        if (method is null) throw new MissingMethodException();
        if (method.Name == nameof(IDocumentRepository.PublishAsync))
            return PublishAsync((PreparedDocumentPublication)arguments![0]!, (CancellationToken)arguments[1]!);
        try { return method.Invoke(inner, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is { } cause)
        { ExceptionDispatchInfo.Capture(cause).Throw(); throw; }
    }
    private async ValueTask<PublicationResult> PublishAsync(PreparedDocumentPublication publication, CancellationToken ct)
    {
        if (pause.Phase == "before") await pause.WaitAsync(ct);
        using var scope = pause.Enter();
        return await inner.PublishAsync(publication, ct);
    }
}

public sealed class PublicationPause(string phase, string signal)
{
    private readonly AsyncLocal<int> active = new();
    private readonly HashSet<DbTransaction> transactions = new(ReferenceEqualityComparer.Instance);
    public string Phase => phase;
    public IDisposable Enter() { active.Value++; return new Scope(this); }
    private sealed class Scope(PublicationPause owner) : IDisposable { public void Dispose() => owner.active.Value--; }
    public void Observe(DbCommand command)
    {
        if (active.Value == 0 || command.Transaction is not { } transaction) return;
        if (command.CommandText.Contains("UPDATE cellbridge_repository_heads", StringComparison.OrdinalIgnoreCase) ||
            command.CommandText.Contains("UPDATE \"cellbridge_repository_heads\"", StringComparison.OrdinalIgnoreCase))
            lock (transactions) transactions.Add(transaction);
    }
    public bool Matches(DbTransaction transaction) { lock (transactions) return transactions.Contains(transaction); }
    public async Task WaitAsync(CancellationToken ct)
    {
        await File.WriteAllTextAsync(signal, "ready", ct);
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }
}

sealed class PublicationCommands(PublicationPause pause) : DbCommandInterceptor
{
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { pause.Observe(command); return ValueTask.FromResult(result); }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { pause.Observe(command); return ValueTask.FromResult(result); }
}
sealed class PublicationTransactions(PublicationPause pause) : DbTransactionInterceptor
{
    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
        InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (pause.Phase == "during" && pause.Matches(transaction)) await pause.WaitAsync(cancellationToken);
        return result;
    }
    public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (pause.Phase == "after" && pause.Matches(transaction)) await pause.WaitAsync(cancellationToken);
    }
}
