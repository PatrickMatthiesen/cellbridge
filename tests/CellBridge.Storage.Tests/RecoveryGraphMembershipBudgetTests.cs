using System.Collections.Immutable;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.Tests;

public sealed class RecoveryGraphMembershipBudgetTests
{
    [Fact]
    public void AdmissionBoundsAggregateMembershipEvenWhenSnapshotsShareBackingArrays()
    {
        const long recoveryBudget = 128L * 1024 * 1024;
        const int snapshotCount = 129;
        const int membershipsPerState = 128;
        var elementCount = checked((int)(recoveryBudget / 128 / membershipsPerState / IntPtr.Size));
        var state = State(elementCount);
        state = state with
        {
            Revisions = Enumerable.Range(1, membershipsPerState - 1).Select(number => new DocumentRevision(
                state.ResourceId, state.LifecycleGeneration, (ulong)number, state.ContentVersion,
                state.ModifiedUtc, new("author", "author", "Author"), state.Content, state.Partitions)).ToImmutableArray(),
        };
        var snapshots = Enumerable.Range(0, snapshotCount)
            .Select(version => state with { StateVersion = version }).ToImmutableArray();
        var snapshot = new ProviderSnapshot([new(state.ResourceId, snapshotCount - 1)], snapshots, []);

        // The fixture owns only one membership array. Each state would decode
        // its own array from the compact persisted representation.
        Assert.True(snapshots[0].Partitions[0].Elements == snapshots[^1].Partitions[0].Elements);
        Assert.Equal(recoveryBudget / 128, StorageLimits.GetDecodedGraphMembershipBytes(state));
        var exception = Assert.Throws<StorageQuotaExceededException>(() => ProviderRecovery.CheckAdmission(snapshot,
            new() { MaxGraphElements = elementCount, MaxRetainedStateSnapshots = snapshotCount }, DateTime.UtcNow));

        Assert.Equal("recovery decoded graph membership bytes", exception.Budget);
        Assert.Equal(recoveryBudget + recoveryBudget / 128, exception.Used);
        Assert.Equal(recoveryBudget, exception.Limit);
    }

    [Fact]
    public void AdmissionAppliesConfiguredPerStateBudgetToCurrentAndHistoricalMemberships()
    {
        var state = State(4);
        state = state with
        {
            Revisions = [new(state.ResourceId, state.LifecycleGeneration, 1, state.ContentVersion,
                state.ModifiedUtc, new("author", "author", "Author"), state.Content, state.Partitions)],
        };
        var snapshot = new ProviderSnapshot([new(state.ResourceId, state.StateVersion)], [state], []);
        var membershipBytes = 8L * IntPtr.Size;
        var limits = new StorageLimits { MaxDecodedGraphMembershipBytes = membershipBytes - 1 };

        var exception = Assert.Throws<StorageQuotaExceededException>(() =>
            ProviderRecovery.CheckAdmission(snapshot, limits, DateTime.UtcNow));

        Assert.Equal(membershipBytes, exception.Used);
        Assert.Equal(membershipBytes - 1, exception.Limit);
        ProviderRecovery.CheckAdmission(snapshot, limits with { MaxDecodedGraphMembershipBytes = membershipBytes }, DateTime.UtcNow);
        Assert.Single(snapshot.Snapshots[0].Revisions);
        Assert.Equal(4, snapshot.Snapshots[0].Revisions[0].Partitions[0].Elements.Length);
    }

    private static DocumentState State(int elementCount)
    {
        var id = new ExtendedId(1, Guid.NewGuid());
        var payload = new ContentHandle("empty", 0, new string('a', 64));
        var serialGuid = Guid.NewGuid();
        var elements = Enumerable.Range(1, elementCount).Select(number => new GraphElementState(
            new((uint)number, id.Guid), 1, new(serialGuid, (ulong)number), payload) { MappingSerials = [] }).ToImmutableArray();
        var identity = new PartitionIdentity(id, id, id, id, id, id, id, id, id, Guid.NewGuid());
        var partition = new PartitionState(0, identity, 1, payload, id, elements, InlineContent: []);
        return new(DocumentState.CurrentFormat, Guid.NewGuid(), "/recovery.docx", "/recovery.docx",
            DateTime.UnixEpoch, DateTime.UnixEpoch, 1, 0, payload, [partition], [], CoordinationState.Empty, []);
    }
}
