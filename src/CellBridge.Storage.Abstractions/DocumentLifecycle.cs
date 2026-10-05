using System.Text.Json;

namespace CellBridge.Storage.Abstractions;

/// <summary>Optional provider-wide lifecycle operations. Resource IDs are permanently retired on deletion.</summary>
public interface IDocumentLifecycleStore
{
    ValueTask<DocumentState?> FindLifecycleAsync(Guid resourceId, CancellationToken cancellationToken = default);
    ValueTask<bool> TryDeleteAsync(Guid resourceId, long expectedGeneration, long expectedStateVersion,
        CancellationToken cancellationToken = default);
    ValueTask<bool> TryRecreateAsync(Guid retiredResourceId, long expectedGeneration, long expectedStateVersion,
        DocumentState replacement, CancellationToken cancellationToken = default);
}

public static class DocumentLifecycle
{
    public static DocumentState? Delete(DocumentState current, long generation, long version)
    {
        if (current.LifecycleGeneration != generation) return null;
        if (current.IsDeleted) return current.DeletedFromStateVersion == version ? current : null;
        if (current.StateVersion != version || current.Publication is { Pending.Length: > 0 }) return null;
        return current with { IsDeleted = true, DeletedFromStateVersion = version };
    }

    public static bool CanRecreate(DocumentState retired, long generation, long version, DocumentState replacement) =>
        retired.IsDeleted && retired.LifecycleGeneration == generation && retired.StateVersion == version &&
        retired.ReplacedBy is null && retired.Publication is not { Pending.Length: > 0 } &&
        replacement.ResourceId != Guid.Empty && replacement.ResourceId != retired.ResourceId &&
        replacement.Path == retired.Path && replacement.PathKey == retired.PathKey &&
        replacement.LifecycleGeneration == checked(generation + 1) && !replacement.IsDeleted &&
        replacement.DeletedFromStateVersion is null && replacement.ReplacedBy is null &&
        replacement.Editors.IsEmpty && replacement.Receipts.IsEmpty && replacement.RetiredPathKeys.IsEmpty && replacement.Publication is null &&
        replacement.RestoreReceipts.IsEmpty && HasCoherentInitialHistory(replacement) &&
        replacement.Coordination == CoordinationState.Empty;

    private static bool HasCoherentInitialHistory(DocumentState replacement)
    {
        if (replacement.Revisions.IsEmpty) return true;
        if (replacement.Revisions.Length != 1) return false;
        var revision = replacement.Revisions[0];
        return revision.ResourceId == replacement.ResourceId && revision.LifecycleGeneration == replacement.LifecycleGeneration &&
            revision.RevisionNumber == Math.Max(1UL, replacement.ContentVersion) && revision.ContentVersion == replacement.ContentVersion &&
            revision.Content == replacement.Content && revision.CreatedUtc == replacement.ModifiedUtc &&
            revision.Author == (replacement.Security.ModifiedBy ?? replacement.Security.CreatedBy ?? new("unknown", "unknown", "unknown")) &&
            JsonSerializer.Serialize(revision.Partitions) == JsonSerializer.Serialize(replacement.Partitions.Where(p => p.Kind != 2));
    }

    public static void ValidateTransition(DocumentState current, DocumentState next)
    {
        if (next.ResourceId != current.ResourceId || next.PathKey != current.PathKey || next.Path != current.Path)
            throw new InvalidOperationException("A transition cannot change document identity or path.");
        if (next.LifecycleGeneration != current.LifecycleGeneration || next.IsDeleted != current.IsDeleted ||
            next.DeletedFromStateVersion != current.DeletedFromStateVersion || next.ReplacedBy != current.ReplacedBy)
            throw new InvalidOperationException("Lifecycle changes require provider lifecycle operations.");
    }
}
