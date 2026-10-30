using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using CellBridge.Tests;
using System.Security.Cryptography;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class AtomicDocumentPublicationTests
{
    [PostgreSqlFact]
    public Task AtomicContentRollsBackOnReplayReprepareDenialAndQuota() => BudgetAndQueryTests.WithDatabase(new(), async source =>
    {
        var content = new PostgreSqlContentStore(source);
        var documents = new PostgreSqlStateStore(source);
        var provider = new StorageProvider(documents, content);
        var atomic = (IAtomicDocumentPublisher)documents;
        Assert.True(atomic.SupportsAtomicContent(content));
        var created = (await new CellBridgeDocumentService(provider).CreateAsync("/atomic.docx", MinimalDocx.Create(), TestActor.Value))!;
        var initial = (await provider.Documents.ReadControlAsync(created.ResourceId))!.Head;
        var bytes = RandomNumberGenerator.GetBytes(4096);
        var handle = DocumentStagingScenario.Handle(bytes);
        var admission = StorageTestDocuments.Admission(initial);
        var publication = new PreparedDocumentPublication(initial.ResourceId, admission,
            new(initial.LifecycleGeneration, initial.AuthorityGeneration, initial.CoordinationGeneration, initial.StateVersion, initial.ContentVersion, []),
            new(new(initial.LifecycleGeneration, SaveOperationFamily.Protocol, 0, "atomic"), new string('a', 64), TestActor.Value.Identity.Subject,
                initial.ContentVersion, initial.LatestRevisionNumber, null, null), []) { NewDocumentReferences = [handle] };
        var before = await InventoryAsync();
        var stale = await atomic.PublishWithContentAsync(publication with { Admission = admission with { StateVersion = initial.StateVersion + 1 } }, content, [bytes]);
        Assert.Equal(PublicationStatus.Reprepare, stale.Status);
        Assert.Equal(before, await InventoryAsync());
        var denied = await atomic.PublishWithContentAsync(publication with { Admission = admission with { Access = DocumentAccess.Read } }, content, [bytes]);
        Assert.Equal(PublicationStatus.ProtocolError, denied.Status);
        Assert.Equal(before, await InventoryAsync());
        var constrained = new PostgreSqlStateStore(source, new() { MaxDocumentStoredBytes = initial.Counters.StoredBytes + 100 });
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => ((IAtomicDocumentPublisher)constrained).PublishWithContentAsync(publication, content, [bytes]).AsTask());
        Assert.Equal(before, await InventoryAsync());
        Assert.Equal(initial, (await provider.Documents.ReadControlAsync(initial.ResourceId))!.Head);

        using (var cancellation = new CancellationTokenSource())
        {
            var gate = new CancelPublicationRead(cancellation);
            var options = new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source).AddInterceptors(gate).Options;
            var cancelled = (IAtomicDocumentPublisher)new PostgreSqlStateStore(options);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.PublishWithContentAsync(publication, content, [bytes], cancellation.Token).AsTask());
            Assert.True(gate.Reached);
            Assert.Equal(before, await InventoryAsync());
            Assert.Equal(initial, (await provider.Documents.ReadControlAsync(initial.ResourceId))!.Head);
        }
        var accepted = await atomic.PublishWithContentAsync(publication, content, [bytes]);
        Assert.Equal(PublicationStatus.Accepted, accepted.Status);
        Assert.Equal(bytes, await content.ReadVerifiedAsync(handle));
        var snapshot = (await provider.Documents.ExportDocumentAsync(initial.ResourceId))!;
        Assert.False(Assert.Single(snapshot.ContentReferences.Where(reference => reference.Handle == handle)).IsPending);
        Assert.Equal(snapshot.Current.Head.Counters.StoredBytes, NormalizedDocumentRecovery.StoredBytes(snapshot));
        var after = await InventoryAsync();
        var extra = RandomNumberGenerator.GetBytes(4096);
        var replay = await atomic.PublishWithContentAsync(publication with
        {
            Admission = StorageTestDocuments.Admission(accepted.Current),
            NewDocumentReferences = [handle, DocumentStagingScenario.Handle(extra)]
        }, content, [bytes, extra]);
        Assert.Equal(PublicationStatus.Replay, replay.Status);
        Assert.Equal(after, await InventoryAsync());
        Assert.Equal(accepted.Current, (await provider.Documents.ReadControlAsync(initial.ResourceId))!.Head);

        async Task<string> InventoryAsync()
        {
            await using var command = source.CreateCommand("SELECT ROW((SELECT count(*) FROM cellbridge_objects),(SELECT count(*) FROM cellbridge_chunks),(SELECT COALESCE(sum(length),0) FROM cellbridge_objects))::text");
            return (string)(await command.ExecuteScalarAsync())!;
        }
    });

    [PostgreSqlFact]
    public Task ServiceUsesAtomicPublicationAndStillReplaysExactContent() => BudgetAndQueryTests.WithDatabase(new(), async source =>
    {
        var content = new PostgreSqlContentStore(source);
        var provider = new StorageProvider(new PostgreSqlStateStore(source), content);
        await DocumentStagingScenario.SuccessfulServiceSaveCompletesReservationsAsync(provider);
        await NormalizedProviderScenarios.CreateSaveMetadataRestoreAsync(provider);
    });
    [PostgreSqlFact]
    public Task ProvisionalOverflowFallsBackToChargedDurableStaging() => BudgetAndQueryTests.WithDatabase(new(), async source =>
    {
        var content = new PostgreSqlContentStore(source);
        var documents = new PostgreSqlStateStore(source);
        var provider = new StorageProvider(documents, content);
        var initial = (await new CellBridgeDocumentService(provider).CreateAsync("/overflow.docx", MinimalDocx.Create(), TestActor.Value))!;
        using var staging = new DocumentStagingContent(documents, content, StorageTestDocuments.Admission(initial), provider.Limits);
        var small = await staging.WriteManyAsync([new byte[] { 11, 22 }]);
        Assert.Equal(initial, (await provider.Documents.ReadControlAsync(initial.ResourceId))!.Head);
        await Assert.ThrowsAsync<StorageCorruptionException>(() => content.OpenReadAsync(small[0]).AsTask());
        var big = new byte[(int)PendingSaveContent.MaxBytes + 1];
        var large = await staging.WriteManyAsync([big]);
        var pending = (await provider.Documents.ExportDocumentAsync(initial.ResourceId))!;
        Assert.Equal(2, pending.ContentReferences.Count(reference => reference.IsPending));
        Assert.Equal(initial.StateVersion, pending.Current.Head.StateVersion);
        Assert.True(pending.Current.Head.Counters.StoredBytes > initial.Counters.StoredBytes + big.Length);
        var publication = new PreparedDocumentPublication(initial.ResourceId, StorageTestDocuments.Admission(initial),
            new(initial.LifecycleGeneration, initial.AuthorityGeneration, initial.CoordinationGeneration, initial.StateVersion, initial.ContentVersion, []),
            new(new(initial.LifecycleGeneration, SaveOperationFamily.Protocol, 0, "overflow"), new string('b', 64), TestActor.Value.Identity.Subject,
                initial.ContentVersion, initial.LatestRevisionNumber, null, null), []) { NewDocumentReferences = [small[0], large[0]] };
        Assert.Equal(PublicationStatus.Accepted, (await staging.PublishAsync(publication, CancellationToken.None)).Status);
        var complete = (await provider.Documents.ExportDocumentAsync(initial.ResourceId))!;
        Assert.DoesNotContain(complete.ContentReferences, reference => reference.IsPending);
        Assert.Equal(complete.Current.Head.Counters.StoredBytes, NormalizedDocumentRecovery.StoredBytes(complete));
        Assert.Equal(big, await content.ReadVerifiedAsync(large[0]));
    });

    private sealed class CancelPublicationRead(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public bool Reached;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.Transaction is not null && command.CommandText.Contains("FROM cellbridge_repository_heads", StringComparison.Ordinal))
            {
                Reached = true;
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return ValueTask.FromResult(result);
        }
    }

}
