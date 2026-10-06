using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed class LifecycleCollectionTests
{
    [PostgreSqlFact]
    public Task PostgreSqlCollectionPreservesDeletedAndRecreatedDocuments() => Check(fileSystem: false);

    [PostgreSqlLinuxFact]
    public Task FileSystemCollectionPreservesDeletedAndRecreatedDocuments() => Check(fileSystem: true);

    private static Task Check(bool fileSystem)
    {
        // One snapshot lets deletion prune every live root. Recreation can retain
        // another tombstone snapshot without masking the lifecycle references.
        var limits = new StorageLimits { MaxRetainedStateSnapshots = 1 };
        return BudgetAndQueryTests.WithDatabase(limits, async source =>
        {
            var root = fileSystem ? Path.Combine(Path.GetTempPath(), "cellbridge-lifecycle-collection-" + Guid.NewGuid().ToString("N")) : null;
            try
            {
                var provider = Provider(source, limits, root);
                var service = new CellBridgeDocumentService(provider);
                var lifecycle = (IDocumentLifecycleStore)provider.State;
                var originalBytes = MinimalDocx.Create("original history");
                var original = (await service.CreateAsync("/retired.docx", originalBytes, TestActor.Value))!;
                var saved = await RevisionHistoryTests.Save(service, original, "last content before delete");
                Assert.Equal(2, saved.Revisions.Length);
                Assert.NotEqual(original.Content, saved.Content);
                Assert.NotEmpty(saved.Partitions.SelectMany(p => p.Elements));
                Assert.NotNull(Assert.Single(saved.Receipts).Response);

                // A separate live queue is required: deletion must reject undelivered saves.
                var queued = (await service.CreateAsync("/pending.docx", MinimalDocx.Create("external baseline"), TestActor.Value))!;
                var publisher = new ExternalRevisionPublisher(provider, new UnusedDestination());
                Assert.True(await publisher.BindAsync(queued.ResourceId, queued.LifecycleGeneration, queued.StateVersion,
                    Guid.NewGuid(), "synthetic-destination", "revision-0"));
                queued = (await provider.State.FindByResourceIdAsync(queued.ResourceId))!;
                queued = await RevisionHistoryTests.Save(service, queued, "first undelivered save");
                queued = await RevisionHistoryTests.Save(service, queued, "second undelivered save");
                Assert.Equal(2, queued.Publication!.Pending.Length);
                Assert.NotEqual(queued.Content, queued.Publication.Pending[0].Content);

                // Age out earlier metadata snapshots; immutable file history stays in current state.
                saved = await AdvanceSnapshots(provider, saved);
                queued = await AdvanceSnapshots(provider, queued);
                Assert.False(await lifecycle.TryDeleteAsync(queued.ResourceId, queued.LifecycleGeneration, queued.StateVersion));
                Assert.True(await lifecycle.TryDeleteAsync(saved.ResourceId, saved.LifecycleGeneration, saved.StateVersion));
                var retired = (await lifecycle.FindLifecycleAsync(saved.ResourceId))!;
                Assert.True(retired.IsDeleted);
                Assert.Null(await provider.State.FindByResourceIdAsync(retired.ResourceId));
                var expected = await ReadReferences(provider.Content, retired, queued);
                Assert.Equal(originalBytes, expected[original.Content]);
                await CollectAndVerify(source, provider, root, retired, queued, expected, orphanByte: 91);

                var replacementBytes = MinimalDocx.Create("new incarnation");
                var replacement = await new DocumentStore().Put(retired.Path, replacementBytes).CaptureAsync(provider.Content);
                replacement = RevisionHistory.Initialize(replacement with
                    { LifecycleGeneration = retired.LifecycleGeneration + 1, Security = retired.Security });
                Assert.True(await lifecycle.TryRecreateAsync(retired.ResourceId, retired.LifecycleGeneration, retired.StateVersion, replacement));
                replacement = (await provider.State.FindByResourceIdAsync(replacement.ResourceId))!;
                retired = (await lifecycle.FindLifecycleAsync(retired.ResourceId))!;
                Assert.Equal(replacement.ResourceId, retired.ReplacedBy);
                Assert.NotEqual(retired.ResourceId, replacement.ResourceId);
                Assert.Equal(2, replacement.LifecycleGeneration);
                Assert.Equal(replacement.ResourceId, Assert.Single(replacement.Revisions).ResourceId);

                foreach (var pair in await ReadReferences(provider.Content, replacement)) expected[pair.Key] = pair.Value;
                Assert.Equal(replacementBytes, expected[replacement.Content]);
                await CollectAndVerify(source, provider, root, retired, queued, expected, orphanByte: 92, replacement);
            }
            finally
            {
                if (root is not null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
    }

    private static StorageProvider Provider(NpgsqlDataSource source, StorageLimits limits, string? root) =>
        new(new PostgreSqlStateStore(source, limits), root is null
            ? new PostgreSqlContentStore(source)
            : new PostgreSqlFileSystemContentStore(source, root, shared: true), limits);

    private static async Task<DocumentState> AdvanceSnapshots(StorageProvider provider, DocumentState state)
    {
        for (var i = 0; i < 3; i++)
            await provider.State.TransitionAsync(state.ResourceId, (current, now) =>
                new StateTransition<bool>(current with { ModifiedUtc = now }, true));
        return (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
    }

    private static async Task<Dictionary<ContentHandle, byte[]>> ReadReferences(IContentStore content, params DocumentState[] states)
    {
        // Enumerate expected roots independently of StorageReferences, which maintenance uses.
        var handles = states.SelectMany(state => new[] { state.Content }
            .Concat(state.Partitions.SelectMany(p => new[] { p.Content }.Concat(p.Elements.Select(e => e.Payload))))
            .Concat(state.Revisions.SelectMany(r => new[] { r.Content }
                .Concat(r.Partitions.SelectMany(p => new[] { p.Content }.Concat(p.Elements.Select(e => e.Payload))))))
            .Concat(state.Receipts.Where(r => r.Response is not null).Select(r => r.Response!))
            .Concat(state.Publication?.Pending.Select(r => r.Content) ?? []));
        var expected = new Dictionary<ContentHandle, byte[]>();
        foreach (var handle in handles.Distinct()) expected.Add(handle, await content.ReadVerifiedAsync(handle));
        return expected;
    }

    private static async Task CollectAndVerify(NpgsqlDataSource source, StorageProvider provider, string? root,
        DocumentState retired, DocumentState queued, Dictionary<ContentHandle, byte[]> expected,
        byte orphanByte, DocumentState? replacement = null)
    {
        // A pre-delete live snapshot must not conceal a missing tombstone reference.
        await using (var snapshots = source.CreateCommand("SELECT COUNT(*),bool_and((state_json::jsonb->>'IsDeleted')::boolean) FROM cellbridge_states WHERE resource_id=$1"))
        {
            snapshots.Parameters.Add(new NpgsqlParameter { Value = retired.ResourceId });
            await using var rows = await snapshots.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.True(rows.GetInt64(0) > 0);
            Assert.True(rows.GetBoolean(1));
        }
        var orphan = await provider.Content.WriteAsync(new MemoryStream(new[] { orphanByte }));
        Assert.DoesNotContain(orphan, expected.Keys);
        var maintenance = new StorageMaintenance(source);
        var dry = await maintenance.CollectOrphansAsync(fileSystemRoot: root);
        Assert.False(dry.Applied);
        // Superseded response receipts may also become unreferenced after pruning.
        Assert.True(dry.OrphanObjects >= 1);
        Assert.True(dry.OrphanBytes >= orphan.Length);
        Assert.Equal(new[] { orphanByte }, await provider.Content.ReadVerifiedAsync(orphan));
        // No host, reader, writer or publisher runs against this disposable database/root.
        var applied = await maintenance.CollectOrphansAsync(apply: true, quiescent: true, fileSystemRoot: root);
        Assert.True(applied.Applied);
        Assert.Equal(dry.OrphanObjects, applied.OrphanObjects);
        Assert.Equal(dry.OrphanBytes, applied.OrphanBytes);
        Assert.Equal(dry.StoredBytes - dry.OrphanBytes, applied.StoredBytes);
        Assert.Equal(dry.ReferencedObjects, applied.ReferencedObjects);
        await Assert.ThrowsAsync<StorageCorruptionException>(() => provider.Content.OpenReadAsync(orphan).AsTask());
        var empty = await maintenance.CollectOrphansAsync(fileSystemRoot: root);
        Assert.Equal(0, empty.OrphanObjects);
        Assert.Equal(applied.StoredBytes, empty.StoredBytes);

        // Npgsql's exposed ConnectionString omits its password after opening.
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!)
            { Database = new NpgsqlConnectionStringBuilder(source.ConnectionString).Database };
        await using var restartedSource = NpgsqlDataSource.Create(connection.ConnectionString);
        var restarted = Provider(restartedSource, provider.Limits, root);
        var lifecycle = (IDocumentLifecycleStore)restarted.State;
        Assert.Null(await restarted.State.FindByResourceIdAsync(retired.ResourceId));
        Assert.Equal(JsonSerializer.Serialize(retired), JsonSerializer.Serialize(await lifecycle.FindLifecycleAsync(retired.ResourceId)));
        Assert.Equal(JsonSerializer.Serialize(queued), JsonSerializer.Serialize(await restarted.State.FindByResourceIdAsync(queued.ResourceId)));
        if (replacement is not null)
        {
            Assert.Equal(JsonSerializer.Serialize(replacement), JsonSerializer.Serialize(await restarted.State.FindByPathKeyAsync(replacement.PathKey)));
            Assert.Equal(expected[replacement.Content], (await StoredDocument.RestoreAsync(replacement, restarted.Content)).Content);
        }
        foreach (var pair in expected) Assert.Equal(pair.Value, await restarted.Content.ReadVerifiedAsync(pair.Key));
        // Queued save content also has history roots; this checks survival, not an exclusive pin.
        foreach (var revision in queued.Publication!.Pending)
            Assert.Equal(expected[revision.Content], await restarted.Content.ReadVerifiedAsync(revision.Content));
    }

    private sealed class UnusedDestination : IExternalRevisionDestination
    {
        public ValueTask<ExternalDeliveryResult> CompareExchangeAsync(ExternalDeliveryRequest request, Stream verifiedContent,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("This test must not publish externally.");
    }
}
