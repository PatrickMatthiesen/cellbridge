using System.Collections.Immutable;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage;

/// <summary>Pure publication helpers; call only after constructing the final committed partition state.</summary>
public static class RevisionHistory
{
    public static DocumentState Initialize(DocumentState state) => state.Revisions.IsEmpty
        ? state with { Revisions = [Capture(state, Math.Max(1UL, state.ContentVersion),
            state.Security.ModifiedBy ?? state.Security.CreatedBy ?? new("unknown", "unknown", "unknown"), state.ModifiedUtc)] }
        : state;

    public static DocumentState Append(DocumentState current, DocumentState next, SubjectIdentity author, DateTime now)
    {
        current = Initialize(current);
        return next with { Revisions = current.Revisions.Add(Capture(next,
            checked(current.Revisions[^1].RevisionNumber + 1), author, now)) };
    }

    public static ulong Latest(DocumentState state) => Initialize(state).Revisions[^1].RevisionNumber;

    public static DocumentRevision Capture(DocumentState state, ulong revisionNumber, SubjectIdentity author, DateTime now) =>
        new(state.ResourceId, state.LifecycleGeneration, revisionNumber, state.ContentVersion, now, author,
            state.Content, state.Partitions.Where(p => p.Kind != 2).ToImmutableArray());
}
