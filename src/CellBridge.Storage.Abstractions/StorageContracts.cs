using System.Collections.Immutable;

namespace CellBridge.Storage.Abstractions;

public sealed record StorageCapabilities(bool Durable, bool SharedState, bool SharedContent)
{
    public bool AtomicBudgets { get; init; }
}
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
    ContentHandle Content, ExtendedId? StorageIndex, ImmutableArray<GraphElementState> Elements,
    ImmutableArray<byte> InlineContent = default);
public sealed record EditorState(Guid ClientId, string? UserName, DateTime JoinedUtc,
    DateTime LastSeenUtc, DateTime ExpiresUtc, int TimeoutSeconds, bool AsEditor, int EditorNumber,
    ImmutableDictionary<string, ImmutableArray<byte>> Metadata, SubjectIdentity? Owner = null);
public sealed record LeaseState(string Id, string? Client, DateTime ExpiresUtc, int Kind, string? SchemaId,
    string? OwnerSubject = null);
public sealed record CoordinationState(string? SchemaId, ImmutableArray<LeaseState> SchemaOwners,
    LeaseState? Exclusive, long Generation)
{
    public ImmutableArray<string> CoauthorClients { get; init; } = [];
    public bool CoauthorTransitionPending { get; init; }
    public static CoordinationState Empty { get; } = new(null, [], null, 0);
}
public sealed record SaveReceipt(string OperationKey, string Digest, uint ContentVersion,
    ContentHandle? Response, string? OwnerSubject = null);

/// <summary>One detached state version. Referenced content is immutable; reclamation requires quiescent maintenance.</summary>
public sealed record DocumentState(int FormatVersion, Guid ResourceId, string Path, string PathKey,
    DateTime CreatedUtc, DateTime ModifiedUtc, uint ContentVersion, long StateVersion,
    ContentHandle Content, ImmutableArray<PartitionState> Partitions,
    ImmutableArray<EditorState> Editors, CoordinationState Coordination,
    ImmutableArray<SaveReceipt> Receipts)
{
    public const int CurrentFormat = 2;
    public DocumentSecurity Security { get; init; } = DocumentSecurity.Empty;
    public string Etag => $"\"{{{ResourceId.ToString("D").ToUpperInvariant()}}},{ContentVersion}\"";
}

/// <summary>Metadata only; listing does not fetch package or graph bytes.</summary>
public sealed record DocumentSummary(Guid ResourceId, string Path, long Length,
    uint ContentVersion, DateTime ModifiedUtc, int ActiveEditors)
{
    public DocumentSecurity Security { get; init; } = DocumentSecurity.Empty;
}

/// <summary>Content writes must complete durably before their handles can be published.</summary>
public interface IContentStore
{
    bool Durable { get; }
    bool Shared { get; }
    ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default);
    ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default);
}

public sealed record StateTransition<T>(DocumentState? Next, T Result);

/// <summary>
/// Atomic document state operations. Transition callbacks must be deterministic,
/// bounded and perform no I/O. They run with exclusive per-document coordination
/// and authoritative time obtained after acquiring it. Null Next is a read/no-op.
/// Providers must not silently retry callbacks or fall back to volatile storage.
/// </summary>
public interface IDocumentStateStore
{
    bool Durable { get; }
    bool Shared { get; }
    ValueTask<DocumentState?> FindByResourceIdAsync(Guid resourceId, CancellationToken cancellationToken = default);
    ValueTask<DocumentState?> FindByPathKeyAsync(string pathKey, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken cancellationToken = default);
    ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken cancellationToken = default);
    ValueTask<T> TransitionAsync<T>(Guid resourceId,
        Func<DocumentState, DateTime, StateTransition<T>> transition,
        CancellationToken cancellationToken = default);
    ValueTask CheckHealthAsync(CancellationToken cancellationToken = default);
}

public sealed class StorageUnavailableException(string message, Exception? inner = null) : IOException(message, inner);
public sealed class StorageCorruptionException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>The selected state and binary stores, with guarantees for their complete configuration.</summary>
public sealed class StorageProvider(IDocumentStateStore state, IContentStore content, StorageLimits? limits = null)
{
    public StorageLimits Limits { get; } = limits ?? (state as ILocalStorageBudgetParticipant)?.Budget.Limits ?? new StorageLimits();
    public IDocumentStateStore State { get; } = state;
    public IContentStore Content { get; } = Compose(state, content, limits);
    public StorageCapabilities Capabilities { get; } = new(state.Durable && content.Durable,
        state.Shared, content.Shared)
    {
        AtomicBudgets = state is IStorageBudgetParticipant s && content is IStorageBudgetParticipant c &&
            ReferenceEquals(s.BudgetScope, c.BudgetScope),
    };

    private static IContentStore Compose(IDocumentStateStore state, IContentStore content, StorageLimits? limits)
    {
        if (state is ILocalStorageBudgetParticipant s && content is ILocalStorageBudgetParticipant c)
        {
            var budget = limits is null ? s.Budget : new StorageBudget(limits);
            s.UseBudget(budget);
            c.UseBudget(budget);
        }
        return content;
    }

    public void Require(bool durable, bool multipleInstances)
    {
        Limits.Validate();
        if (limits is not null && !Capabilities.AtomicBudgets)
            throw new InvalidOperationException("Explicit storage limits require state and content stores with shared atomic accounting.");
        if (durable && !Capabilities.Durable)
            throw new InvalidOperationException("This storage configuration does not provide durable saves.");
        if (multipleInstances && (!Capabilities.SharedState || !Capabilities.SharedContent))
            throw new InvalidOperationException("Multiple service instances require shared state and shared content.");
    }

    /// <summary>Checks the selected configuration, including a bounded content write/read probe.</summary>
    public async ValueTask CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        await State.CheckHealthAsync(cancellationToken);
        using var source = new MemoryStream(new byte[] { 67, 66 });
        var handle = await Content.WriteAsync(source, cancellationToken);
        var bytes = await Content.ReadVerifiedAsync(handle, 2, cancellationToken);
        if (!bytes.SequenceEqual(new byte[] { 67, 66 }))
            throw new StorageCorruptionException("The content health probe failed.");
    }
}
