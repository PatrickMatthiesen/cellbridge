using System.Collections.Immutable;

namespace CellBridge.Storage.Abstractions;

public sealed record StorageCapabilities(bool Durable, bool SharedState, bool SharedContent);
public sealed record ContentHandle(string Key, long Length, string Sha256);
public sealed record ExtendedId(uint Value, Guid Guid);
public sealed record SerialId(Guid Guid, ulong Value);
public sealed record PartitionIdentity(ExtendedId StorageManifest, ExtendedId CellManifest,
    ExtendedId RevisionManifest, ExtendedId ObjectGroup, ExtendedId ObjectDataBlob,
    ExtendedId Object, ExtendedId Revision, ExtendedId CellLong, ExtendedId CellShort, Guid SerialGuid);
public sealed record GraphElementState(ExtendedId Id, uint Type, SerialId Serial, ContentHandle Payload)
{
    public required ImmutableArray<SerialId> MappingSerials { get; init; }
}
public sealed record PartitionState(int Kind, PartitionIdentity Identity, ulong Knowledge,
    ContentHandle Content, ExtendedId? StorageIndex, GraphElementCollection Elements,
    ImmutableArray<byte> InlineContent = default);
public sealed record EditorState(Guid ClientId, string? UserName, DateTime JoinedUtc,
    DateTime LastSeenUtc, DateTime ExpiresUtc, int TimeoutSeconds, bool AsEditor, int EditorNumber,
    ImmutableDictionary<string, ImmutableArray<byte>> Metadata, SubjectIdentity? Owner = null);
public sealed record LeaseState(string Id, string? Client, DateTime ExpiresUtc, int Kind, string? SchemaId,
    string? OwnerSubject = null)
{
    // Older persisted leases have only an expiry and deserialize with null.
    public int? TimeoutSeconds { get; init; }
}
public sealed record CoordinationState(string? SchemaId, ImmutableArray<LeaseState> SchemaOwners,
    LeaseState? Exclusive, long Generation)
{
    public HostLease? HostLock { get; init; }
    public ImmutableArray<string> CoauthorClients { get; init; } = [];
    public bool CoauthorTransitionPending { get; init; }
    public static CoordinationState Empty { get; } = new(null, [], null, 0);
}
public sealed record SaveReceipt(string OperationKey, string Digest, uint ContentVersion,
    ContentHandle? Response, string? OwnerSubject = null)
{
    /// <summary>Legacy receipts default to the file partition and its content-version semantics.</summary>
    public int PartitionKind { get; init; }
    public ExtendedId? AcceptedStorageIndex { get; init; }
    public long LifecycleGeneration { get; init; } = 1;
}

/// <summary>An immutable publication. Access is always evaluated against the current document.</summary>
public sealed record DocumentRevision(Guid ResourceId, long LifecycleGeneration, ulong RevisionNumber,
    uint ContentVersion, DateTime CreatedUtc, SubjectIdentity Author, ContentHandle Content,
    ImmutableArray<PartitionState> Partitions);
public sealed record RestoreReceipt(long LifecycleGeneration, string OwnerSubject, string OperationKey,
    string Digest, ulong RevisionNumber);
public sealed record WholeFileSaveReceipt(long LifecycleGeneration, string OwnerSubject, string OperationKey,
    string Digest, ulong RevisionNumber);

/// <summary>One detached state version. Referenced content is immutable; reclamation requires quiescent maintenance.</summary>
public sealed record DocumentState(int FormatVersion, Guid ResourceId, string Path, string PathKey,
    DateTime CreatedUtc, DateTime ModifiedUtc, uint ContentVersion, long StateVersion,
    ContentHandle Content, ImmutableArray<PartitionState> Partitions,
    ImmutableArray<EditorState> Editors, CoordinationState Coordination,
    ImmutableArray<SaveReceipt> Receipts)
{
    public const int CurrentFormat = 2;
    /// <summary>Incarnation of a host path. Prepared mutations must retain and revalidate this value.</summary>
    public long LifecycleGeneration { get; init; } = 1;
    public bool IsDeleted { get; init; }
    public long? DeletedFromStateVersion { get; init; }
    public Guid? ReplacedBy { get; init; }
    public ExternalPublicationState? Publication { get; init; }
    public DocumentSecurity Security { get; init; } = DocumentSecurity.Empty;
    public ImmutableArray<DocumentRevision> Revisions { get; init; } = [];
    public ImmutableArray<string> RetiredPathKeys { get; init; } = [];
    public ImmutableArray<RestoreReceipt> RestoreReceipts { get; init; } = [];
    public ImmutableArray<WholeFileSaveReceipt> WholeFileSaveReceipts { get; init; } = [];
    public string Etag => $"\"{{{ResourceId.ToString("D").ToUpperInvariant()}}},{ContentVersion}\"";
}

/// <summary>Metadata only; listing does not fetch package or graph bytes.</summary>
public sealed record DocumentSummary(Guid ResourceId, string Path, long Length,
    uint ContentVersion, DateTime ModifiedUtc, int ActiveEditors)
{
    public DocumentSecurity Security { get; init; } = DocumentSecurity.Empty;
    public int PendingDeliveryCount { get; init; }
    public string? DeliveryBlock { get; init; }
}

/// <summary>Content writes must satisfy the store's declared persistence guarantees before their handles can be published.</summary>
public interface IContentStore
{
    bool Durable { get; }
    bool Shared { get; }
    ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default);
    ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default);
}

public sealed class StorageUnavailableException(string message, Exception? inner = null) : IOException(message, inner);
public sealed class StorageCorruptionException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>The selected state and binary stores, with guarantees for their complete configuration.</summary>
public sealed class StorageProvider(IDocumentRepository documents, IContentStore content, StorageLimits? limits = null)
{
    public IDocumentRepository Documents { get; } = documents;
    public IDocumentPackageReader Packages => new GraphDocumentPackageReader(async (package, ct) =>
    {
        var selected = await Documents.ReadRevisionAsync(package.ResourceId, package.RevisionNumber, ct)
            ?? throw new StorageCorruptionException("The package document does not exist.");
        if (selected.Revision?.Package != package) throw new StorageCorruptionException("The package does not match its immutable revision.");
        var partition = await Documents.ReadPartitionAsync(package.ResourceId,
            new PartitionReadRequest(0) { RevisionNumber = package.RevisionNumber, IncludeBindings = true }, ct)
            ?? throw new StorageCorruptionException("The package bindings do not exist.");
        return partition.Bindings;
    }, async (scope, id, ct) =>
    {
        var elements = await Documents.ReadElementsAsync(scope, [id], ct);
        var element = elements.SingleOrDefault() ?? throw new StorageCorruptionException("The package element does not exist.");
        return new CellBridge.FssHttpB.DataElement((CellBridge.FssHttpB.DataElementType)element.Type,
            new CellBridge.FssHttpB.ExGuid(element.Id.Value, element.Id.Guid), new(element.Serial.Guid, element.Serial.Value))
        { Data = await Content.ReadVerifiedAsync(element.Payload, Limits.MaxObjectBytes, ct) };
    }, Limits.MaxDocumentBytes);
    public StorageLimits Limits { get; } = limits ?? new StorageLimits();
    public IContentStore Content { get; } = content;
    public StorageCapabilities Capabilities { get; } = new(documents.Durable && content.Durable,
        documents.Shared, content.Shared);

    public void Require(bool durable, bool multipleInstances)
    {
        Limits.Validate();
        if (durable && !Capabilities.Durable)
            throw new InvalidOperationException("This storage configuration does not preserve saved changes after a restart.");
        if (multipleInstances && (!Capabilities.SharedState || !Capabilities.SharedContent))
            throw new InvalidOperationException("Multiple service instances require shared state and shared content.");
    }

    /// <summary>Checks the selected configuration, including a bounded content write/read probe.</summary>
    public async ValueTask CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        await Documents.CheckHealthAsync(cancellationToken);
        using var source = new MemoryStream(new byte[] { 67, 66 });
        var handle = await Content.WriteAsync(source, cancellationToken);
        var bytes = await Content.ReadVerifiedAsync(handle, 2, cancellationToken);
        if (!bytes.SequenceEqual(new byte[] { 67, 66 }))
            throw new StorageCorruptionException("The content health probe failed.");
    }
}
