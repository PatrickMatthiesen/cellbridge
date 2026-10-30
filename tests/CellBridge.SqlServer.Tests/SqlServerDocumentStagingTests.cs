using CellBridge.Tests;

namespace CellBridge.SqlServer.Tests;

[Collection("SQL Server")]
public sealed class SqlServerDocumentStagingTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task RejectedServiceSavesStayBoundedAndQuiescentCollectionReleasesReservations()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture, DocumentStagingScenario.Limits);
        var provider = database.Provider();
        await DocumentStagingScenario.RejectedServiceSavesStayBoundedAsync(provider);
        var before = await provider.Documents.CaptureRecoveryAsync();
        Assert.Contains(before.Snapshots[0].ContentReferences, reference => reference.IsPending);
        var maintenance = new CellBridge.Storage.SqlServer.StorageMaintenance(database.Options(admin: true), database.Limits);
        await maintenance.CollectOrphansAsync();
        Assert.Equal(before.Snapshots[0].Current.Head, (await provider.Documents.CaptureRecoveryAsync()).Snapshots[0].Current.Head);
        await maintenance.CollectOrphansAsync(true, true);
        var after = await provider.Documents.CaptureRecoveryAsync();
        Assert.DoesNotContain(after.Snapshots[0].ContentReferences, reference => reference.IsPending);
        Assert.Equal(before.Snapshots[0].Current.Head.StateVersion, after.Snapshots[0].Current.Head.StateVersion);
        Assert.True(after.Snapshots[0].Current.Head.Counters.StoredBytes < before.Snapshots[0].Current.Head.Counters.StoredBytes);
        Assert.Equal(0, (await maintenance.CollectOrphansAsync()).OrphanObjects);
    }

    [Fact]
    public async Task ReservationsDeduplicateAndAcceptedServiceSaveCompletesThem()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        await DocumentStagingScenario.ReservationDeduplicatesAndDoesNotAdvanceVersionsAsync(database.Provider());
        await DocumentStagingScenario.SuccessfulServiceSaveCompletesReservationsAsync(database.Provider());
    }

    [Fact]
    public async Task RecoveryPreservesMissingPendingReservations()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        await DocumentStagingScenario.MissingPendingContentSurvivesPortableRecoveryAsync(database.Provider());
    }

    [Fact]
    public async Task ConcurrentReservationsStayWithinDocumentCapacity()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture, DocumentStagingScenario.Limits);
        await DocumentStagingScenario.ConcurrentReservationsStayWithinDocumentCapacityAsync(database.Provider());
    }
}
