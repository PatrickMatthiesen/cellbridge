using System.Collections.Immutable;

namespace CellBridge.Storage.Abstractions;

public sealed record IndexedGraphScope(Guid ResourceId, long LifecycleGeneration, int TargetPartition);
public sealed record IndexedCellId(ExtendedId LongId, ExtendedId ShortId);
public enum IndexedMappingKind { Manifest, Cell, Revision }
public sealed record IndexedMappingKey(IndexedMappingKind Kind, IndexedCellId? Cell = null, ExtendedId? Revision = null);
public sealed record IndexedStorageMapping(IndexedGraphScope Scope, ExtendedId StorageIndex,
    IndexedMappingKey Key, ExtendedId Target, SerialId Serial);
public sealed record IndexedMappingValue(ExtendedId Target, SerialId Serial);
public sealed record IndexedMappingChange(IndexedMappingKey Key, IndexedMappingValue? Value);
public sealed record IndexedRevisionRoot(ExtendedId Root, ExtendedId Object);
public sealed record IndexedStorageRoot(ExtendedId Root, IndexedCellId Cell);
public sealed record IndexedStorageManifest(IndexedGraphScope Scope, ExtendedId ElementId,
    Guid Schema, ImmutableArray<IndexedStorageRoot> Roots);
public sealed record IndexedCellManifest(IndexedGraphScope Scope, ExtendedId ElementId, ExtendedId RevisionId);
public sealed record IndexedRevisionManifest(IndexedGraphScope Scope, ExtendedId ElementId,
    ExtendedId RevisionId, ExtendedId BaseRevisionId, ImmutableArray<IndexedRevisionRoot> Roots,
    ImmutableArray<ExtendedId> ObjectGroups);

/// <summary>ObjectPartitionId is the object's partition, not the outer target partition.</summary>
public sealed record IndexedObjectDefinition(IndexedGraphScope Scope, ExtendedId GroupElement,
    int Ordinal, ExtendedId ObjectId, ulong ObjectPartitionId,
    ImmutableArray<ExtendedId> ObjectReferences, ImmutableArray<IndexedCellId> CellReferences,
    ExtendedId? BlobElement = null)
{
    /// <summary>Digest of declaration, ordered references and raw content; excludes the owning group identity.</summary>
    public required string DefinitionDigest { get; init; }
}
public sealed record IndexedBindingKey(IndexedGraphScope Scope, IndexedCellId Cell,
    ExtendedId ObjectId, ulong ObjectPartitionId);
public sealed record IndexedObjectBinding(IndexedBindingKey Key, ExtendedId GroupElement, int ObjectOrdinal);
public sealed record IndexedBindingChange(IndexedBindingKey Key, IndexedObjectBinding? Value);

/// <summary>Effective difference from the preceding published map, including branch reversions and removals.</summary>
public sealed record IndexedBindingDelta(IndexedGraphScope Scope, long PreviousSequence, long Sequence,
    ImmutableArray<IndexedBindingChange> Changes, long EncodedBytes);
public sealed record IndexedGraphCheckpoint(IndexedGraphScope Scope, long Sequence,
    ImmutableArray<IndexedObjectBinding> Bindings, long EncodedBytes);
public sealed record IndexedCheckpointPolicy(int MaxReplayChanges = 128, long MaxReplayBytes = 1024 * 1024)
{
    public static IndexedCheckpointPolicy Default { get; } = new();
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxReplayChanges);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxReplayBytes);
    }
}

public sealed record IndexedReplayPosition(long CheckpointSequence, int Changes, long Bytes);

/// <summary>Indexed metadata access. Every lookup is scoped to one document incarnation and partition.</summary>
public interface IIndexedGraphSource
{
    ValueTask<IndexedStorageMapping?> FindMappingAsync(IndexedGraphScope scope, ExtendedId storageIndex,
        IndexedMappingKey key, CancellationToken cancellationToken = default);
    ValueTask<IndexedRevisionManifest?> ReadRevisionAsync(IndexedGraphScope scope, ExtendedId manifest,
        CancellationToken cancellationToken = default);
    ValueTask<ImmutableArray<IndexedObjectDefinition>> ReadGroupAsync(IndexedGraphScope scope, ExtendedId group,
        CancellationToken cancellationToken = default);
}

/// <summary>Targeted immutable lookups for save preparation and selected mapping reads for queries.</summary>
public interface IIndexedSaveSource : IIndexedGraphSource
{
    ValueTask<ImmutableArray<GraphElementState>> ReadElementsAsync(IndexedGraphScope scope,
        IReadOnlyCollection<ExtendedId> ids, CancellationToken cancellationToken = default);
    /// <summary>Visibility of requested immutable identities at the exact prepared partition publication.</summary>
    ValueTask<ImmutableArray<ExtendedId>> ReadVisibleElementIdsAsync(IndexedGraphScope scope, long publicationSequence,
        IReadOnlyCollection<ExtendedId> elementIds, CancellationToken cancellationToken = default);
    ValueTask<ImmutableArray<IndexedStorageMapping>> ReadMappingClaimsAsync(IndexedGraphScope scope,
        IReadOnlyCollection<SerialId> serials, CancellationToken cancellationToken = default);
    /// <summary>Full selected mapping enumeration for query/export, never routine save preparation.</summary>
    ValueTask<ImmutableArray<IndexedStorageMapping>> ReadMappingsAsync(IndexedGraphScope scope,
        long publicationSequence, CancellationToken cancellationToken = default);
}

public sealed record IndexedResolvedCell(IndexedGraphScope Scope, IndexedCellId Cell, ExtendedId Revision,
    ImmutableArray<IndexedRevisionRoot> Roots, ImmutableArray<IndexedObjectBinding> Bindings);

public sealed record IndexedElementMetadata
{
    public ExtendedId? ObjectGroupElement { get; init; }
    public ImmutableArray<IndexedStorageMapping> Mappings { get; init; } = [];
    public IndexedStorageManifest? StorageManifest { get; init; }
    public IndexedCellManifest? CellManifest { get; init; }
    public IndexedRevisionManifest? Revision { get; init; }
    public ImmutableArray<IndexedObjectDefinition> Objects { get; init; } = [];
}
