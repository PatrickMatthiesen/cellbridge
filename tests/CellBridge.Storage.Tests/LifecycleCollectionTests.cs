using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class LifecycleCollectionTests
{
    [PostgreSqlFact]
    public Task PostgreSqlCollectionPreservesDeletedAndRecreatedDocuments() => Check(fileSystem: false);

    [PostgreSqlLinuxFact]
    public Task FileSystemCollectionPreservesDeletedAndRecreatedDocuments() => Check(fileSystem: true);

    private static Task Check(bool fileSystem) => RepositoryTestDatabase.WithDatabase(new(), async source =>
    {
        var root = fileSystem ? Path.Combine(Path.GetTempPath(), "cellbridge-lifecycle-collection-" + Guid.NewGuid().ToString("N")) : null;
        try
        {
            var provider = Provider(source, new(), root); var service = new CellBridgeDocumentService(provider);
            var originalBytes = MinimalDocx.Create("original history");
            var original = (await service.CreateAsync("/retired.docx", originalBytes, TestActor.Value))!;
            var saved = await RepositoryTestPublications.SaveAsync(service, original, MinimalDocx.Create("last content before delete"));
            Assert.Equal(2, saved.Counters.Revisions); Assert.NotEqual(original.Package, saved.Package);
            var savedSnapshot = (await provider.Documents.ExportDocumentAsync(saved.ResourceId))!;
            Assert.NotEmpty(savedSnapshot.Elements); Assert.NotNull(Assert.Single(savedSnapshot.Receipts).Response);

            // An independent delivery queue must prevent deletion until all accepted saves are delivered.
            var queued = (await service.CreateAsync("/pending.docx", MinimalDocx.Create("external baseline"), TestActor.Value))!;
            var publisher = new ExternalRevisionPublisher(provider, new UnusedDestination());
            Assert.True(await publisher.BindAsync(queued.ResourceId, queued.LifecycleGeneration, queued.StateVersion,
                Guid.NewGuid(), "synthetic-destination", "revision-0"));
            queued = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(queued.ResourceId)))!;
            queued = await RepositoryTestPublications.SaveAsync(service, queued, MinimalDocx.Create("first undelivered save"));
            queued = await RepositoryTestPublications.SaveAsync(service, queued, MinimalDocx.Create("second undelivered save"));
            var queuedSnapshot = (await provider.Documents.ExportDocumentAsync(queued.ResourceId))!;
            Assert.Equal(2, queued.Counters.PendingDeliveries);
            Assert.NotEqual(queued.Package, queuedSnapshot.Deliveries[0].Package);

            // Current controls can advance independently of immutable history and pending delivery references.
            saved = await AdvanceControls(provider, saved); queued = await AdvanceControls(provider, queued);
            Assert.False((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(queued.ResourceId,
                queued.LifecycleGeneration, queued.StateVersion))).Applied);
            Assert.True((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(saved.ResourceId,
                saved.LifecycleGeneration, saved.StateVersion))).Applied);
            var retired = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(saved.ResourceId, true)))!;
            Assert.True(retired.IsDeleted); Assert.Null(await provider.Documents.FindAsync(new DocumentIdentity.Resource(retired.ResourceId)));
            var expected = await ReadReferences(provider, retired, queued);
            Assert.Equal(originalBytes, await StorageTestDocuments.PackageAsync(provider, original.Package));
            await CollectAndVerify(source, provider, root, retired, queued, expected, orphanByte: 91);

            var replacementBytes = MinimalDocx.Create("new incarnation");
            var replacement = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), retired.Path, replacementBytes,
                retired.Security, provider.Content, provider.Limits, lifecycleGeneration: retired.LifecycleGeneration + 1);
            Assert.True((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(retired.ResourceId,
                retired.LifecycleGeneration, retired.StateVersion, replacement))).Applied);
            var recreated = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(replacement.Head.ResourceId)))!;
            retired = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(retired.ResourceId, true)))!;
            Assert.Equal(recreated.ResourceId, retired.ReplacedBy); Assert.NotEqual(retired.ResourceId, recreated.ResourceId);
            Assert.Equal(2, recreated.LifecycleGeneration);
            Assert.Equal(recreated.ResourceId, Assert.Single((await provider.Documents.ReadRevisionsAsync(recreated.ResourceId, 0, 256))!.Revisions).ResourceId);
            foreach (var pair in await ReadReferences(provider, recreated)) expected[pair.Key] = pair.Value;
            Assert.Equal(replacementBytes, await StorageTestDocuments.PackageAsync(provider, recreated.Package));
            await CollectAndVerify(source, provider, root, retired, queued, expected, orphanByte: 92, recreated);
        }
        finally { if (root is not null && Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    });

    private static StorageProvider Provider(NpgsqlDataSource source, StorageLimits limits, string? root)
    {
        IContentStore content = root is null ? new PostgreSqlContentStore(source) : new PostgreSqlFileSystemContentStore(source, root, shared: true);
        return new(new PostgreSqlStateStore(source, limits, validationContent: content), content, limits);
    }

    private static async Task<DocumentHead> AdvanceControls(StorageProvider provider, DocumentHead state)
    {
        var locks = new SharedDocumentLocks(new(provider));
        for (var i = 0; i < 3; i++)
        {
            Assert.True((await locks.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "control-" + i, TestActor.Value)).Success);
            Assert.True((await locks.ApplyAsync(state.ResourceId, HostLockOperation.Release, "control-" + i, TestActor.Value)).Success);
        }
        return (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
    }

    private static async Task<Dictionary<ContentHandle, byte[]>> ReadReferences(StorageProvider provider, params DocumentHead[] heads)
    {
        // Derive expected physical objects from immutable domain records independently of the maintenance reference table.
        var expected = new Dictionary<ContentHandle, byte[]>();
        foreach (var head in heads)
        {
            var snapshot = (await provider.Documents.ExportDocumentAsync(head.ResourceId))!;
            foreach (var handle in snapshot.Elements.Select(element => element.Element.Payload)
                .Concat(snapshot.Receipts.Where(receipt => receipt.Response is not null).Select(receipt => receipt.Response!)).Distinct())
                expected.TryAdd(handle, await provider.Content.ReadVerifiedAsync(handle));
        }
        return expected;
    }

    private static async Task CollectAndVerify(NpgsqlDataSource source, StorageProvider provider, string? root,
        DocumentHead retired, DocumentHead queued, Dictionary<ContentHandle, byte[]> expected, byte orphanByte, DocumentHead? replacement = null)
    {
        // Normal saves retain a single normalized head and no legacy aggregate snapshot that could conceal lost references.
        await using (var heads = source.CreateCommand("SELECT COUNT(*),bool_and(\"IsDeleted\") FROM cellbridge_repository_heads WHERE \"ResourceId\"=$1"))
        {
            heads.Parameters.AddWithValue(retired.ResourceId); await using var rows = await heads.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync()); Assert.Equal(1L, rows.GetInt64(0)); Assert.True(rows.GetBoolean(1));
        }
        await using (var legacy = source.CreateCommand("SELECT COUNT(*) FROM cellbridge_states WHERE resource_id=$1"))
        { legacy.Parameters.AddWithValue(retired.ResourceId); Assert.Equal(0L, await legacy.ExecuteScalarAsync()); }
        var orphan = await provider.Content.WriteAsync(new MemoryStream(new[] { orphanByte })); Assert.DoesNotContain(orphan, expected.Keys);
        var maintenance = new StorageMaintenance(source);
        var dry = await maintenance.CollectOrphansAsync(fileSystemRoot: root);
        Assert.False(dry.Applied); Assert.True(dry.OrphanObjects >= 1); Assert.True(dry.OrphanBytes >= orphan.Length);
        Assert.Equal(new[] { orphanByte }, await provider.Content.ReadVerifiedAsync(orphan));
        // No host, reader, writer or publisher runs against this disposable database/root during collection.
        var applied = await maintenance.CollectOrphansAsync(apply: true, quiescent: true, fileSystemRoot: root);
        Assert.True(applied.Applied); Assert.Equal(dry.OrphanObjects, applied.OrphanObjects); Assert.Equal(dry.OrphanBytes, applied.OrphanBytes);
        Assert.Equal(dry.StoredBytes - dry.OrphanBytes, applied.StoredBytes); Assert.Equal(dry.ReferencedObjects, applied.ReferencedObjects);
        await Assert.ThrowsAsync<StorageCorruptionException>(() => provider.Content.OpenReadAsync(orphan).AsTask());
        var empty = await maintenance.CollectOrphansAsync(fileSystemRoot: root); Assert.Equal(0, empty.OrphanObjects); Assert.Equal(applied.StoredBytes, empty.StoredBytes);
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!)
            { Database = new NpgsqlConnectionStringBuilder(source.ConnectionString).Database };
        await using var restartedSource = NpgsqlDataSource.Create(connection.ConnectionString);
        var restarted = Provider(restartedSource, provider.Limits, root);
        Assert.Null(await restarted.Documents.FindAsync(new DocumentIdentity.Resource(retired.ResourceId)));
        Assert.Equal(JsonSerializer.Serialize(retired), JsonSerializer.Serialize(await restarted.Documents.FindAsync(new DocumentIdentity.Resource(retired.ResourceId, true))));
        Assert.Equal(JsonSerializer.Serialize(queued), JsonSerializer.Serialize(await restarted.Documents.FindAsync(new DocumentIdentity.Resource(queued.ResourceId))));
        if (replacement is not null)
        {
            Assert.Equal(JsonSerializer.Serialize(replacement), JsonSerializer.Serialize(await restarted.Documents.FindAsync(new DocumentIdentity.Path(replacement.PathKey))));
            Assert.Equal(await StorageTestDocuments.PackageAsync(provider, replacement.Package), await StorageTestDocuments.PackageAsync(restarted, replacement.Package));
        }
        foreach (var pair in expected) Assert.Equal(pair.Value, await restarted.Content.ReadVerifiedAsync(pair.Key));
        var queuedSnapshot = (await restarted.Documents.ExportDocumentAsync(queued.ResourceId))!;
        Assert.Equal(2, queuedSnapshot.Deliveries.Length);
        foreach (var delivery in queuedSnapshot.Deliveries)
            Assert.Equal(await StorageTestDocuments.PackageAsync(provider, delivery.Package), await StorageTestDocuments.PackageAsync(restarted, delivery.Package));
    }

    private sealed class UnusedDestination : IExternalRevisionDestination
    {
        public ValueTask<ExternalDeliveryResult> CompareExchangeAsync(ExternalDeliveryRequest request, Stream verifiedContent,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("This test must not publish externally.");
    }
}
