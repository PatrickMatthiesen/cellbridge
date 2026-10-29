using System.Collections.Immutable;
using CellBridge.FssHttp;

namespace CellBridge.Storage.Abstractions;

/// <summary>A lookup has exactly one identity. A missing resource never falls back to a path.</summary>
public abstract record DocumentIdentity
{
    public sealed record Resource(Guid ResourceId, bool IncludeDeleted = false) : DocumentIdentity;
    public sealed record Path(string PathKey) : DocumentIdentity;
}

/// <summary>Current scalar metadata. No collection grows with application history.</summary>
public sealed record DocumentHead(Guid ResourceId, string Path, string PathKey,
    DateTime CreatedUtc, DateTime ModifiedUtc, uint ContentVersion, long StateVersion,
    long LifecycleGeneration, bool IsDeleted, DocumentPackage Package, DocumentSecurity Security,
    ulong LatestRevisionNumber, DocumentCounters Counters)
{
    public long AuthorityGeneration { get; init; }
    public long CoordinationGeneration { get; init; }
    public long? DeletedFromStateVersion { get; init; }
    public Guid? ReplacedBy { get; init; }
    public DateTime? NextEditorExpiryUtc { get; init; }
    public int ActiveEditors { get; init; }
    public DocumentDeliveryBinding? Delivery { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Etag => $"\"{{{ResourceId.ToString("D").ToUpperInvariant()}}},{ContentVersion}\"";
    [System.Text.Json.Serialization.JsonIgnore]
    public DocumentAuthorizationView Authorization => new(ResourceId, Path, PathKey,
        LifecycleGeneration, StateVersion, IsDeleted, Security, ContentVersion, Package.Length);
}

/// <summary>Maintained under the affected document's publication guard, not recomputed by scanning history.</summary>
public sealed record DocumentCounters
{
    public long StoredBytes { get; init; }
    public long GraphBytes { get; init; }
    public int GraphElements { get; init; }
    public int Revisions { get; init; }
    public int SaveReceipts { get; init; }
    public int RestoreReceipts { get; init; }
    public int WholeFileSaveReceipts { get; init; }
    public int RetiredPaths { get; init; }
    public int PendingDeliveries { get; init; }
    public long PendingDeliveryBytes { get; init; }
}

public sealed record PartitionHead(int Kind, PartitionIdentity Identity, ulong Knowledge,
    ExtendedId? StorageIndex, long PublicationSequence, ImmutableArray<byte> InlineContent)
{
    public int ElementCount { get; init; }
    public long ElementBytes { get; init; }
    public SerialId? StorageIndexSerial { get; init; }
}

/// <summary>A coherent authority and coordination read with the provider's clock.</summary>
public sealed record DocumentControlRead(DocumentHead Head, DateTime UtcNow,
    CoordinationState Coordination, ImmutableArray<EditorState> Editors,
    ImmutableArray<PartitionHead> Partitions);

/// <summary>External evidence is valid only for this exact resource, lifecycle and binding.</summary>
public sealed record DocumentActorAdmission(SubjectIdentity Actor, Guid ResourceId,
    long LifecycleGeneration, DocumentAuthorizationBinding? Binding, DocumentAccess Access)
{
    public long? AuthorityGeneration { get; init; }
    /// <summary>The exact current metadata evaluated by the trusted host policy.</summary>
    public long? StateVersion { get; init; }
}

public enum SaveOperationFamily { Protocol, WholeFile, Restore }
public sealed record SaveOperationIdentity(long LifecycleGeneration, SaveOperationFamily Family,
    int PartitionKind, string OperationKey);

public sealed record DocumentOperationReceipt(SaveOperationIdentity Operation, string Digest,
    string? OwnerSubject, uint ContentVersion, ulong RevisionNumber, ExtendedId? AcceptedStorageIndex,
    ContentHandle? Response);
public sealed record SaveReceiptRead(DocumentHead Current, DocumentOperationReceipt? Receipt);

public sealed record DocumentRevisionRecord(Guid ResourceId, long LifecycleGeneration,
    ulong RevisionNumber, uint ContentVersion, DateTime CreatedUtc, SubjectIdentity Author,
    DocumentPackage Package, ImmutableArray<PartitionHead> Partitions);
public sealed record DocumentRevisionSelection(DocumentHead Current, DocumentRevisionRecord? Revision);
public sealed record DocumentRevisionPage(DocumentHead Current,
    ImmutableArray<DocumentRevisionRecord> Revisions, int? NextOffset);

public sealed record PartitionReadRequest(int PartitionKind)
{
    public ulong? RevisionNumber { get; init; }
    public ExtendedId? StorageIndex { get; init; }
    public ImmutableArray<ExtendedId> ElementIds { get; init; } = [];
    public ImmutableArray<GraphSerialRange> SerialRanges { get; init; } = [];
    public bool IncludeCatalog { get; init; }
    public bool IncludeBindings { get; init; }
}
public sealed record GraphSerialRange(Guid Namespace, ulong First, ulong Last);

/// <summary>Metadata selected from one coherent root. Payloads remain in the immutable content store.</summary>
public sealed record PartitionRead(DocumentHead Current, PartitionHead Partition,
    ImmutableArray<GraphElementState> Elements, ImmutableArray<IndexedObjectBinding> Bindings)
{
    public ImmutableArray<IndexedRevisionManifest> Revisions { get; init; } = [];
    public ImmutableArray<IndexedStorageMapping> Mappings { get; init; } = [];
}

public sealed record SavePreparationRequest(int PartitionKind, SaveOperationIdentity Operation,
    ImmutableArray<IndexedMappingKey> AffectedMappings)
{
    public bool IncludeCatalog { get; init; }
    public bool IncludeBindings { get; init; }
}
public sealed record SavePreparationRead(DocumentControlRead Control, PartitionRead Partition,
    DocumentOperationReceipt? Receipt)
{
    public required IndexedReplayPosition ReplayPosition { get; init; }
}

/// <summary>The expected value is null when a mapping must not exist.</summary>
public sealed record MappingExpectation(IndexedMappingKey Key, IndexedStorageMapping? Expected)
{
    public bool EnforceCoherency { get; init; } = true;
}
public sealed record DocumentPublicationFence(long LifecycleGeneration, long AuthorityGeneration,
    long CoordinationGeneration, long PreparedStateVersion, uint PreparedContentVersion,
    ImmutableArray<MappingExpectation> ExpectedMappings)
{
    public string? ExpectedEtag { get; init; }
    public ulong? ExpectedRevisionNumber { get; init; }
    public CoordinationState? PreparedCoordination { get; init; }
}

/// <summary>A publication references only complete durable physical objects.</summary>
public sealed record PreparedDocumentPublication(Guid ResourceId, DocumentActorAdmission Admission,
    DocumentPublicationFence Fence, DocumentOperationReceipt Receipt,
    ImmutableArray<PreparedPartitionPublication> Partitions)
{
    public DocumentPackage? Package { get; init; }
    public DocumentRevisionRecord? Revision { get; init; }
    public DateTime? RequestedModifiedUtc { get; init; }
    public ImmutableDictionary<string, string> LockAttributes { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableArray<ContentHandle> NewDocumentReferences { get; init; } = [];
    public DocumentCounterChange CounterChange { get; init; } = new();
    public Guid? DeliveryOperationId { get; init; }
    public string? HostLockToken { get; init; }
}
public sealed record DocumentCounterChange
{
    /// <summary>Additional logical metadata bytes only. Providers charge unique physical document references separately.</summary>
    public long StoredBytes { get; init; }
    public long GraphBytes { get; init; }
    public int GraphElements { get; init; }
}
public sealed record PreparedPartitionPublication(PartitionHead Before, PartitionHead After,
    ImmutableArray<GraphElementState> AddedElements,
    ImmutableArray<ExtendedId> RetiredElements,
    ImmutableArray<IndexedStorageMapping> Mappings,
    ImmutableArray<IndexedRevisionManifest> Revisions,
    ImmutableArray<IndexedObjectDefinition> Objects,
    IndexedBindingDelta EffectiveChanges)
{
    public IndexedGraphCheckpoint? Checkpoint { get; init; }
    public ImmutableArray<IndexedMappingChange> MappingChanges { get; init; } = [];
    public ImmutableArray<IndexedCellManifest> Cells { get; init; } = [];
    public ImmutableArray<IndexedStorageManifest> StorageManifests { get; init; } = [];
}

public enum PublicationStatus { Accepted, Replay, Reprepare, ProtocolError }
public sealed record PublicationResult(PublicationStatus Status, DocumentHead Current)
{
    public DocumentOperationReceipt? Receipt { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record PreparedDocumentCreate(DocumentHead Head, CoordinationState Coordination,
    ImmutableArray<EditorState> Editors, ImmutableArray<PreparedPartitionPublication> Partitions,
    DocumentRevisionRecord InitialRevision);
public sealed record CreateResult(bool Created, DocumentHead? Current);

/// <summary>Providers dispatch concrete commands through bounded core logic under the document guard.</summary>
public abstract record DocumentControlCommand(Guid ResourceId, DocumentActorAdmission? Admission)
{
    public sealed record Office(Guid Id, DocumentActorAdmission Actor, FssHttpSubRequest Request)
        : DocumentControlCommand(Id, Actor);
    public sealed record ExpireEditors(Guid Id, DocumentActorAdmission Actor)
        : DocumentControlCommand(Id, Actor);
    public sealed record SetPermissions(Guid Id, DocumentActorAdmission Actor, DocumentSecurity Security,
        long ExpectedAuthorityGeneration) : DocumentControlCommand(Id, Actor);
    public sealed record BindAuthorization(Guid Id, DocumentAuthorizationBinding? Expected,
        DocumentAuthorizationBinding Next, ImmutableDictionary<string, DocumentAccess> ParticipantAccess)
        : DocumentControlCommand(Id, null)
    {
        public long? ExpectedStateVersion { get; init; }
    }
    public sealed record SetHostLease(Guid Id, DocumentActorAdmission Actor, HostLease? Lease,
        string? ExpectedToken) : DocumentControlCommand(Id, Actor);
}
public sealed record ControlResult(DocumentControlRead Current, bool Changed)
{
    public string? ErrorCode { get; init; }
    public FssHttpSubResponse? Response { get; init; }
}
public sealed record DocumentPathCommand(Guid ResourceId, DocumentActorAdmission Admission,
    string NewPath, string NewPathKey, ImmutableDictionary<string, string> LockAttributes);
public sealed record PathChangeResult(DocumentHead Current, bool Changed, string? ErrorCode = null);

public abstract record DocumentLifecycleCommand(Guid ResourceId, long ExpectedGeneration, long ExpectedStateVersion)
{
    public sealed record Delete(Guid Id, long Generation, long Version)
        : DocumentLifecycleCommand(Id, Generation, Version);
    public sealed record Recreate(Guid Id, long Generation, long Version, PreparedDocumentCreate Replacement)
        : DocumentLifecycleCommand(Id, Generation, Version);
}
public sealed record LifecycleResult(bool Applied, DocumentHead? Current);

public sealed record DocumentDeliveryBinding(Guid BindingId, string Destination, string ExpectedRevision,
    long NextSequence, string? BlockedReason = null);
public sealed record DocumentDelivery(Guid OperationId, Guid ResourceId, long LifecycleGeneration,
    long Sequence, uint ContentVersion, DocumentPackage Package);
public sealed record DeliveryRead(DocumentHead Current, DocumentDeliveryBinding Binding, DocumentDelivery Revision);
public abstract record DocumentDeliveryCommand(Guid ResourceId)
{
    public sealed record Bind(Guid Id, long Lifecycle, long StateVersion, DocumentDeliveryBinding Binding)
        : DocumentDeliveryCommand(Id);
    public sealed record Retry(Guid Id, Guid BindingId, Guid OperationId) : DocumentDeliveryCommand(Id);
    public sealed record Complete(Guid Id, DeliveryRead Expected, string ExternalRevision) : DocumentDeliveryCommand(Id);
    public sealed record Block(Guid Id, DeliveryRead Expected, string Reason) : DocumentDeliveryCommand(Id);
}
public sealed record DeliveryResult(bool Applied, DocumentHead? Current);

/// <summary>
/// Required ordinary-operation contract. Multi-record reads are coherent. Mutations
/// lock only the affected document and required path claims, then obtain provider time.
/// Publication atomically accepts roots, receipts, counters and delivery records.
/// </summary>
public interface IDocumentRepository : IIndexedSaveSource, IIndexedRepositoryRecovery
{
    bool Durable { get; }
    bool Shared { get; }
    ValueTask<DocumentHead?> FindAsync(DocumentIdentity identity, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<DocumentHead>> ListAsync(int offset, int limit, CancellationToken cancellationToken = default);
    ValueTask<DocumentControlRead?> ReadControlAsync(Guid id, CancellationToken cancellationToken = default);
    ValueTask<DocumentRevisionPage?> ReadRevisionsAsync(Guid id, int offset, int limit, CancellationToken cancellationToken = default);
    ValueTask<DocumentRevisionSelection?> ReadRevisionAsync(Guid id, ulong number, CancellationToken cancellationToken = default);
    ValueTask<SaveReceiptRead?> ReadReceiptAsync(Guid id, SaveOperationIdentity operation, CancellationToken cancellationToken = default);
    ValueTask<PartitionRead?> ReadPartitionAsync(Guid id, PartitionReadRequest request, CancellationToken cancellationToken = default);
    ValueTask<SavePreparationRead?> PrepareSaveAsync(Guid id, SavePreparationRequest request, CancellationToken cancellationToken = default);
    /// <summary>Reserves document capacity for at most 64 handles before physical staging. False means current write authority changed.
    /// Reservations charge unique handles and their ownership metadata, survive failed writes, and do not advance document versions.</summary>
    ValueTask<bool> ReserveContentAsync(DocumentActorAdmission admission, IReadOnlyCollection<ContentHandle> handles, CancellationToken cancellationToken = default);
    ValueTask<CreateResult> CreateAsync(PreparedDocumentCreate create, CancellationToken cancellationToken = default);
    ValueTask<ControlResult> ExecuteControlAsync(DocumentControlCommand command, CancellationToken cancellationToken = default);
    ValueTask<PublicationResult> PublishAsync(PreparedDocumentPublication publication, CancellationToken cancellationToken = default);
    ValueTask<PathChangeResult> ChangePathAsync(DocumentPathCommand command, CancellationToken cancellationToken = default);
    ValueTask<LifecycleResult> ChangeLifecycleAsync(DocumentLifecycleCommand command, CancellationToken cancellationToken = default);
    ValueTask<DeliveryRead?> ReadNextDeliveryAsync(Guid id, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<Guid>> ListPendingDeliveriesAsync(int limit, CancellationToken cancellationToken = default);
    ValueTask<DeliveryResult> UpdateDeliveryAsync(DocumentDeliveryCommand command, CancellationToken cancellationToken = default);
    ValueTask CheckHealthAsync(CancellationToken cancellationToken = default);
}
