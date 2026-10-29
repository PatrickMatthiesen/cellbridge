using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class NormalizedDocumentRecoveryValidationTests
{
    [Fact]
    public async Task PackageRevisionLabelMustMatchItsSelectedLogicalRootEvenWhenItsBytesAndDigestAreUnchanged()
    {
        var snapshot = await SnapshotAsync();
        NormalizedDocumentRecovery.Validate(snapshot, new());
        var package = snapshot.Current.Head.Package;
        var forged = package with { Root = package.Root with { Revision = new(1, Guid.NewGuid()) } };
        var changed = snapshot with
        {
            Current = snapshot.Current with { Head = snapshot.Current.Head with { Package = forged } },
            ApplicationRevisions = snapshot.ApplicationRevisions.Select(revision => revision.RevisionNumber == forged.RevisionNumber
                ? revision with { Package = forged } : revision).ToImmutableArray(),
        };
        changed = Recount(changed);
        Assert.Equal(package.Sha256, forged.Sha256);
        Assert.Throws<StorageCorruptionException>(() => NormalizedDocumentRecovery.Validate(changed, new()));
    }
    [Fact]
    public async Task PureAdmissionChecksEveryHistoricalPackageAndOwnedObjectBeforeContentStaging()
    {
        var snapshot = await SnapshotAsync();
        var older = snapshot.ApplicationRevisions[0];
        var invalid = snapshot with { ApplicationRevisions = snapshot.ApplicationRevisions.SetItem(0,
            older with { Package = older.Package with { Length = 129L * 1024 * 1024 } }) };
        Assert.Throws<StorageQuotaExceededException>(() => NormalizedDocumentRecovery.Validate(invalid, new()));
        Assert.Throws<StorageQuotaExceededException>(() => NormalizedDocumentRecovery.Validate(snapshot, new() { MaxObjectBytes = 1 }));
    }
    private static async Task<NormalizedDocumentSnapshot> SnapshotAsync()
    {
        var content = new InMemoryContentStore();
        var repository = new InMemoryDocumentRepository(content);
        var service = new CellBridgeDocumentService(new(repository, content));
        var actor = new CellBridgeActor(new("recovery:owner", "owner", "Owner"), CanCreate: true);
        var head = (await service.CreateAsync("/root-validation.docx", MinimalDocx.Create("before"), actor))!;
        var locks = new SharedDocumentLocks(service);
        Assert.True((await locks.ApplyAsync(head.ResourceId, HostLockOperation.Acquire, "host", actor)).Success);
        using var replacement = new MemoryStream(MinimalDocx.Create("after"));
        await service.SaveWholeFileAsync(head.ResourceId, replacement, "replace", actor, "host");
        Assert.True((await locks.ApplyAsync(head.ResourceId, HostLockOperation.Release, "host", actor)).Success);
        return (await repository.ExportDocumentAsync(head.ResourceId))!;
    }
    private static NormalizedDocumentSnapshot Recount(NormalizedDocumentSnapshot snapshot) => snapshot with
    { Current = snapshot.Current with { Head = snapshot.Current.Head with { Counters = snapshot.Current.Head.Counters with
        { StoredBytes = NormalizedDocumentRecovery.StoredBytes(snapshot) } } } };
}
