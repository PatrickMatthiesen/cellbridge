using System.Collections.Immutable;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.Tests;

public static class PublicationContentReadScenario
{
    public static async Task SingleSlotAsync(Func<IInterceptor[], StorageProvider> paired)
    {
        var reads = new Reads(); var provider = paired([reads]);
        var actor = new CellBridgeActor(new("pool:owner", "pool-owner", "Pool owner"), CanCreate: true);
        var service = new CellBridgeDocumentService(provider);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var head = (await service.CreateAsync("/single-slot.docx", MinimalDocx.Create("before"), actor, deadline.Token))!;
        Assert.NotNull(head);
        // The selected database content reader independently checks its headers:
        // one repository header query, then its own header and chunk queries.
        Assert.Equal(2, reads.Metadata.Count); Assert.Equal(1, reads.Chunks);
        var query = new FsshttpbCellRequest { SubRequests = { new(RequestTypes.QueryChanges)
            { RequestId = 1, Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } } } };
        var current = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents, query,
            new Dictionary<string, string>(), actor, deadline.Token);
        Assert.False(Assert.Single(current.Response.SubResponses).Status);
        var graph = PartitionGraphSnapshot.Create(current.Response.DataElementPackage!.DataElements,
            ((QueryChangesSubResponseData)current.Response.SubResponses[0].Data!).StorageIndexExtendedGuid);
        var bytes = MinimalDocx.Create("after");
        var put = SyntheticWorkload.Save(graph, bytes, "changed-full", 0, new Random(19));
        var saved = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents, put,
            new Dictionary<string, string>(), actor, deadline.Token);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status); Assert.Single(saved.AcceptedSaves);

        var prepared = await PrepareAsync(provider.Content, [16]);
        using var cancellation = new CancellationTokenSource();
        var cancel = new CancelChunks(cancellation); var interrupted = paired([cancel]);
        Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            interrupted.Documents.CreateAsync(prepared, cancellation.Token).AsTask())).CancellationToken);
        Assert.True(cancel.Triggered);
        Assert.Null(await provider.Documents.FindAsync(new DocumentIdentity.Resource(prepared.Head.ResourceId), deadline.Token));
        Assert.True((await interrupted.Documents.CreateAsync(prepared, deadline.Token)).Created);
        Assert.Equal(saved.State.ContentVersion, (await provider.Documents.FindAsync(new DocumentIdentity.Resource(head.ResourceId), deadline.Token))!.ContentVersion);
        await using var package = await provider.Packages.OpenReadAsync(saved.State.Package, deadline.Token);
        using var restored = new MemoryStream(); await package.CopyToAsync(restored, deadline.Token);
        Assert.Equal(bytes, restored.ToArray());
    }

    public static async Task RunAsync(IContentStore content,
        Func<IInterceptor[], IContentStore?, IDocumentRepository> repository,
        Func<ContentHandle, Task> corruptChunks)
    {
        var plan = await PrepareAsync(content, Enumerable.Repeat(16, 70).Concat([300_000, 300_000, 300_000, 300_000, 1024 * 1024 + 17, 16]));
        var commands = new Reads(); var store = repository([commands], null);
        var created = await store.CreateAsync(plan);
        Assert.True(created.Created);
        var lengths = ContentStoreReader.UniqueHandles(plan.Partitions.SelectMany(p => p.AddedElements).Select(e => e.Payload)).ToDictionary(h => h.Key, h => h.Length);
        Assert.True(commands.Metadata.Count > 1);
        Assert.Equal(commands.Metadata.Count, commands.Chunks);
        Assert.Contains(commands.Metadata, batch => batch.Length == 64);
        Assert.Contains(commands.Metadata, batch => batch.Length == 1 && lengths[batch[0]] > 1024 * 1024);
        Assert.All(commands.Metadata, batch =>
        {
            Assert.InRange(batch.Length, 1, 64);
            Assert.True(batch.Sum(key => lengths[key]) <= 1024 * 1024 || batch.Length == 1);
        });
        Assert.All(commands.Timeouts, timeout => Assert.Equal(45, timeout));
        var snapshot = (await store.ExportDocumentAsync(plan.Head.ResourceId))!;
        NormalizedDocumentRecovery.Validate(snapshot, new());
        var before = JsonSerializer.Serialize(snapshot.Current.Head);

        var corrupt = await PrepareAsync(content, [16]);
        await corruptChunks(corrupt.Partitions[0].AddedElements[^1].Payload);
        await Assert.ThrowsAsync<StorageCorruptionException>(() => store.CreateAsync(corrupt).AsTask());
        Assert.Null(await store.FindAsync(new DocumentIdentity.Resource(corrupt.Head.ResourceId)));

        var cancelled = await PrepareAsync(content, [16]);
        using var cancellation = new CancellationTokenSource();
        var pause = new CancelChunks(cancellation);
        var interrupted = repository([pause], null);
        Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            interrupted.CreateAsync(cancelled, cancellation.Token).AsTask())).CancellationToken);
        Assert.True(pause.Triggered);
        Assert.Null(await store.FindAsync(new DocumentIdentity.Resource(cancelled.Head.ResourceId)));
        Assert.True((await store.CreateAsync(cancelled)).Created);
        Assert.Equal(before, JsonSerializer.Serialize((await store.FindAsync(new DocumentIdentity.Resource(plan.Head.ResourceId)))!));
    }

    public static async Task<PreparedDocumentCreate> PrepareAsync(IContentStore content, IEnumerable<int> blobSizes)
    {
        var owner = new SubjectIdentity("batch:owner", "batch-owner", "Batch owner");
        var plan = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), "/content-" + Guid.NewGuid().ToString("N") + ".docx",
            MinimalDocx.Create("publication bytes"), DocumentSecurity.Create(owner), content, new());
        var extra = ImmutableArray.CreateBuilder<GraphElementState>();
        foreach (var length in blobSizes)
        {
            var bytes = new byte[length]; RandomNumberGenerator.Fill(bytes.AsSpan(0, Math.Min(16, bytes.Length)));
            var blob = StorageManifestBuilder.BuildObjectDataBlobDataElement(new(1, Guid.NewGuid()), new(Guid.NewGuid(), 1), bytes);
            var handle = await content.WriteAsync(new MemoryStream(blob.Data!, writable: false));
            extra.Add(new(IndexedDocumentGraph.Capture(blob.DataElementExtendedGuid), (uint)blob.DataElementType,
                new(blob.SerialNumber.Guid, blob.SerialNumber.Value), handle) { MappingSerials = [] });
        }
        var original = plan.Partitions.Single(p => p.After.Kind == 0);
        var file = original with { AddedElements = original.AddedElements.AddRange(extra), After = original.After with
            { ElementCount = original.After.ElementCount + extra.Count, ElementBytes = original.After.ElementBytes + extra.Sum(e => e.Payload.Length) } };
        return plan with
        {
            Partitions = plan.Partitions.Select(p => p.After.Kind == 0 ? file : p).ToImmutableArray(),
            InitialRevision = plan.InitialRevision with { Partitions = plan.InitialRevision.Partitions.Select(p => p.Kind == 0 ? file.After : p).ToImmutableArray() },
        };
    }

    public sealed class Reads : DbCommandInterceptor
    {
        public List<string[]> Metadata { get; } = [];
        public List<int> Timeouts { get; } = [];
        public int Chunks { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.Transaction is null && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                if (IsObjects(command.CommandText))
                {
                    var keys = command.Parameters.Cast<DbParameter>().SelectMany(parameter => parameter.Value switch
                    {
                        string key when key.Length == 64 => [key],
                        IEnumerable<string> values => values,
                        _ => Array.Empty<string>(),
                    }).Distinct(StringComparer.Ordinal).ToArray();
                    Metadata.Add(keys); Timeouts.Add(command.CommandTimeout);
                }
                if (IsChunks(command.CommandText)) { Chunks++; Timeouts.Add(command.CommandTimeout); }
            }
            return ValueTask.FromResult(result);
        }
        private static bool IsObjects(string sql) => sql.Contains("cellbridge_objects", StringComparison.Ordinal) || sql.Contains("[objects]", StringComparison.Ordinal);
    }
    private static bool IsChunks(string sql) => sql.Contains("cellbridge_chunks", StringComparison.Ordinal) || sql.Contains("[chunks]", StringComparison.Ordinal);
    private sealed class CancelChunks(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
        {
            if (!Triggered && IsChunks(command.CommandText)) { Triggered = true; cancellation.Cancel(); }
            return ValueTask.FromResult(result);
        }
    }
}
