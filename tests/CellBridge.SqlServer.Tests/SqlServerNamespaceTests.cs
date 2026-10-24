using System.Data.Common;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.SqlServer;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.SqlServer.Tests;

[Collection("SQL Server")]
public sealed class SqlServerNamespaceTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task RepeatedRecreationPreservesVerifiedAncestorsAndRejectsUnrelatedRetiredReservations()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        var provider = database.Provider();
        var store = (SqlServerStateStore)provider.State;
        var a = (await new CellBridgeDocumentService(provider).CreateAsync("/a.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        await Rename(store, a.ResourceId, "/b.docx");
        await Rename(store, a.ResourceId, "/a.docx");
        a = await Delete(store, a.ResourceId);
        var b = Replacement(a);
        Assert.True(await store.TryRecreateAsync(a.ResourceId, a.LifecycleGeneration, a.StateVersion, b));
        b = await Delete(store, b.ResourceId);
        var beforeThirdGeneration = await store.CaptureRecoveryAsync();
        var c = Replacement(b);
        Assert.True(await store.TryRecreateAsync(b.ResourceId, b.LifecycleGeneration, b.StateVersion, c));
        Assert.True(await store.TryRecreateAsync(b.ResourceId, b.LifecycleGeneration, b.StateVersion, c));
        Assert.Equal(c.ResourceId, (await store.FindByPathKeyAsync("/A.DOCX"))!.ResourceId);
        Assert.Equal(b.ResourceId, (await store.FindLifecycleAsync(a.ResourceId))!.ReplacedBy);
        c = await Delete(store, c.ResourceId);
        var d = Replacement(c);
        Assert.True(await store.TryRecreateAsync(c.ResourceId, c.LifecycleGeneration, c.StateVersion, d));
        Assert.Equal(4, (await store.FindLifecycleAsync(d.ResourceId))!.LifecycleGeneration);
        Assert.False(await store.TryCreateAsync(d with { ResourceId = Guid.NewGuid(), Path = "/b.docx", PathKey = "/B.DOCX" }));
        await database.AssertAccountingAsync();

        // A separate valid retired lifecycle can enter through portable recovery.
        // Its reservation must not be exempted just because another ancestor is valid.
        var unrelatedDb = await SqlServerDocuments.CreateAsync(fixture);
        var unrelated = (SqlServerStateStore)unrelatedDb.Provider().State;
        var other = (await new CellBridgeDocumentService(unrelatedDb.Provider()).CreateAsync("/a.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        await Rename(unrelated, other.ResourceId, "/unrelated.docx");
        await Delete(unrelated, other.ResourceId);
        var unrelatedSnapshot = await unrelated.CaptureRecoveryAsync();
        var combined = new ProviderSnapshot(beforeThirdGeneration.Heads.AddRange(unrelatedSnapshot.Heads),
            beforeThirdGeneration.Snapshots.AddRange(unrelatedSnapshot.Snapshots), []);
        var targetDb = await SqlServerDocuments.CreateAsync(fixture);
        var target = targetDb.Provider();
        await CopyContent(provider, target, beforeThirdGeneration);
        await CopyContent(unrelatedDb.Provider(), target, unrelatedSnapshot);
        var recovered = (SqlServerStateStore)target.State;
        await recovered.ImportRecoveryAsync(combined, new(Guid.NewGuid(), new string('a', 64), combined.Heads.Length, combined.Snapshots.Length));
        Assert.False(await recovered.TryRecreateAsync(b.ResourceId, b.LifecycleGeneration, b.StateVersion, Replacement(b)));
        Assert.Null(await recovered.FindByPathKeyAsync("/A.DOCX"));
        Assert.Null((await recovered.FindLifecycleAsync(b.ResourceId))!.ReplacedBy);
        await recovered.CheckHealthAsync();
        await targetDb.AssertAccountingAsync();
    }

    [Theory]
    [InlineData("reservation-hash")]
    [InlineData("reservation-key")]
    [InlineData("reservation-missing")]
    [InlineData("pointer-hash")]
    public async Task NamespaceIndexTamperingFailsHealthAndAdmissionWithoutReadingContent(string fault)
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        var provider = database.Provider(); var store = (SqlServerStateStore)provider.State;
        var original = (await new CellBridgeDocumentService(provider).CreateAsync("/a.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        var other = (await new CellBridgeDocumentService(provider).CreateAsync("/other.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        await Rename(store, original.ResourceId, "/b.docx");
        await using var admin = new SqlServerDocumentDbContext(database.Options(admin: true));
        var reservations = admin.Reservations.Where(row => row.ResourceId == original.ResourceId);
        switch (fault)
        {
            case "reservation-hash": await reservations.ExecuteUpdateAsync(set => set.SetProperty(row => row.PathHash, new byte[32])); break;
            case "reservation-key": await reservations.ExecuteUpdateAsync(set => set.SetProperty(row => row.PathKey, "/WRONG.DOCX")); break;
            case "reservation-missing": await reservations.ExecuteDeleteAsync(); break;
            case "pointer-hash": await admin.Documents.Where(row => row.ResourceId == original.ResourceId)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.PathHash, new byte[32])); break;
        }
        Assert.Equal(fault == "reservation-missing" ? 0 : 1, await reservations.CountAsync());
        var observer = new RejectContentReads();
        var guarded = new SqlServerStateStore(database.Options(false, observer));
        await Assert.ThrowsAsync<StorageCorruptionException>(async () => await guarded.CheckHealthAsync());
        await Assert.ThrowsAsync<StorageCorruptionException>(async () => await guarded.FindByPathKeyAsync("/A.DOCX"));
        await Assert.ThrowsAsync<StorageCorruptionException>(async () => await guarded.FindReadMetadataByPathKeyAsync("/A.DOCX"));
        await Assert.ThrowsAsync<StorageCorruptionException>(async () => await guarded.TryCreateAsync(original with { ResourceId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<StorageCorruptionException>(async () => await Rename(guarded, other.ResourceId, "/a.docx"));
        Assert.Equal(2, await admin.Documents.CountAsync());
        Assert.Equal(other.PathKey, (await guarded.FindByResourceIdAsync(other.ResourceId))!.PathKey);
        Assert.Equal("/B.DOCX", (await guarded.FindByResourceIdAsync(original.ResourceId))!.PathKey);
        await database.AssertAccountingAsync();
    }

    [Theory]
    [InlineData("GraphMetadata")]
    [InlineData("Revisions")]
    public async Task NamespaceChecksDoNotDecodeUnrelatedCurrentOrHistoricalGraphs(string property)
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        var provider = database.Provider();
        var state = (await new CellBridgeDocumentService(provider)
            .CreateAsync("/namespace-graphs.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        await using var admin = new SqlServerDocumentDbContext(database.Options(admin: true));
        var path = "$." + property;
        var malformed = "{\"malformed\":true}";
        await admin.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE cellbridge_documents.snapshots
            SET Data=CONVERT(varbinary(max),JSON_MODIFY(CONVERT(varchar(max),Data),{path},JSON_QUERY({malformed})))
            WHERE ResourceId={state.ResourceId}
            """);
        await Assert.ThrowsAsync<StorageCorruptionException>(() => provider.State.FindByResourceIdAsync(state.ResourceId).AsTask());
        var guarded = new SqlServerStateStore(database.Options(false, new RejectContentReads()));
        await guarded.CheckHealthAsync();
        Assert.Null(await guarded.FindByPathKeyAsync("/MISSING.DOCX"));
        var candidate = await new DocumentStore().Put("/admitted.docx", MinimalDocx.Create()).CaptureAsync(provider.Content);
        Assert.True(await guarded.TryCreateAsync(candidate));
        await admin.Documents.Where(row => row.ResourceId == state.ResourceId)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.PathHash, new byte[32]));
        await Assert.ThrowsAsync<StorageCorruptionException>(() => guarded.CheckHealthAsync().AsTask());
    }

    private static async Task Rename(SqlServerStateStore store, Guid id, string path) =>
        await store.RenameAsync(id, (row, _) => new StateTransition<bool>(row with { Path = path, PathKey = path.ToUpperInvariant() }, true));

    private static async Task<DocumentState> Delete(SqlServerStateStore store, Guid id)
    {
        var row = (await store.FindLifecycleAsync(id))!;
        Assert.True(await store.TryDeleteAsync(id, row.LifecycleGeneration, row.StateVersion));
        return (await store.FindLifecycleAsync(id))!;
    }

    private static DocumentState Replacement(DocumentState retired) => retired with
    {
        ResourceId = Guid.NewGuid(), LifecycleGeneration = retired.LifecycleGeneration + 1, StateVersion = 0,
        IsDeleted = false, DeletedFromStateVersion = null, ReplacedBy = null, Revisions = [], Editors = [],
        Coordination = CoordinationState.Empty, Receipts = [], RetiredPathKeys = [], RestoreReceipts = [],
        WholeFileSaveReceipts = [], Publication = null
    };

    internal static async Task CopyContent(StorageProvider source, StorageProvider target, ProviderSnapshot snapshot)
    {
        foreach (var handle in ContentStoreReader.UniqueHandles(snapshot.Snapshots.SelectMany(StorageReferences.Handles)))
        {
            await using var stream = await source.Content.OpenReadAsync(handle);
            Assert.Equal(handle, await target.Content.WriteAsync(stream));
        }
    }

    private sealed class RejectContentReads : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Assert.DoesNotContain("[objects]", command.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain("[chunks]", command.CommandText, StringComparison.Ordinal);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
