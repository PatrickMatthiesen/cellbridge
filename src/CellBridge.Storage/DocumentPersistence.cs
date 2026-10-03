using System.Collections.Immutable;
using System.Security.Cryptography;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage;

/// <summary>Explicit codecs preserve unsigned protocol values without regenerating identities.</summary>
public static class StorageIds
{
    public static ExtendedId Capture(ExGuid id) => new(id.Value, id.Guid);
    public static ExGuid Restore(ExtendedId id) => new(id.Value, id.Guid);
    public static string PathKey(string canonicalPath) => canonicalPath.ToUpperInvariant();
}

public sealed partial class StoredDocument
{
    private DateTime? _authoritativeNow;
    private DocumentState? _sourceState;
    private long? _metadataContentLength;
    public DocumentSecurity Security { get; set; } = DocumentSecurity.Empty;
    private DateTime UtcNow => _authoritativeNow ?? DateTime.UtcNow;
    public void UseAuthoritativeTime(DateTime now) => _authoritativeNow = now;

    private StoredDocument(DocumentState state, IReadOnlyDictionary<string, byte[]> payloads, bool metadataOnly = false)
    {
        if (state.FormatVersion != DocumentState.CurrentFormat || state.PathKey != StorageIds.PathKey(state.Path) ||
            state.Partitions.Length != 3 || state.Partitions.Select(p => p.Kind).Distinct().Count() != 3)
            throw new StorageCorruptionException("Invalid or unsupported document state.");
        Url = state.Path;
        TransitionId = state.ResourceId;
        CreatedUtc = state.CreatedUtc;
        _lastModifiedUtc = state.ModifiedUtc;
        _contentVersion = state.ContentVersion;
        _sourceState = state;
        Security = state.Security;
        _metadataContentLength = metadataOnly ? state.Content.Length : null;
        _content = metadataOnly ? [] : payloads[state.Content.Key].ToArray();
        FilePartition = new DocumentPartition(state.Partitions.Single(p => p.Kind == 0), payloads, metadataOnly);
        MetadataPartition = new DocumentPartition(state.Partitions.Single(p => p.Kind == 1), payloads, metadataOnly);
        EditorsTablePartition = new DocumentPartition(state.Partitions.Single(p => p.Kind == 2), payloads, metadataOnly);
        if (!metadataOnly && !_content.SequenceEqual(FilePartition.Content))
            throw new StorageCorruptionException("Document and file partition content disagree.");
        _sessions.AddRange(state.Editors.Select(CoauthSession.Restore));
    }

    /// <summary>Lease/session transitions need no file or graph I/O under their coordination lock.</summary>
    public static StoredDocument RestoreMetadata(DocumentState state, DateTime now)
    {
        var document = new StoredDocument(state, new Dictionary<string, byte[]>(), metadataOnly: true);
        document.UseAuthoritativeTime(now);
        return document;
    }

    public DocumentState CaptureCoordination(DocumentState current, CoordinationState coordination)
    {
        // Session expiry is a real state transition with editors knowledge advancement.
        _ = Sessions;
        return current with
        {
            Editors = _sessions.Select(s => s.Capture()).ToImmutableArray(),
            Coordination = coordination,
            Partitions = current.Partitions.Select(p => p.Kind == 2 ? p with
            {
                Knowledge = EditorsTablePartition.KnowledgeSequence,
                InlineContent = EditorsTablePartition.Content.ToImmutableArray(),
            } : p).ToImmutableArray(),
        };
    }

    public static async ValueTask<StoredDocument> RestoreAsync(DocumentState state, IContentStore content,
        CancellationToken cancellationToken = default)
    {
        var handles = state.Partitions.Where(p => p.Kind == 0).SelectMany(p => p.Elements.Select(e => e.Payload).Append(p.Content))
            .Append(state.Content).DistinctBy(h => h.Key);
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var handle in handles)
        {
            await using var source = await content.OpenReadAsync(handle, cancellationToken);
            using var target = new MemoryStream();
            await source.CopyToAsync(target, cancellationToken);
            var bytes = target.ToArray();
            if (bytes.LongLength != handle.Length || Convert.ToHexStringLower(SHA256.HashData(bytes)) != handle.Sha256)
                throw new StorageCorruptionException("Referenced content failed integrity verification.");
            payloads.Add(handle.Key, bytes);
        }
        return new StoredDocument(state, payloads);
    }

    public async ValueTask<DocumentState> CaptureAsync(IContentStore content, CoordinationState? coordination = null,
        ImmutableArray<SaveReceipt> receipts = default, CancellationToken cancellationToken = default)
    {
        // Detached instances are used for preparation; no authoritative state is mutated here.
        byte[] bytes;
        EditorState[] editors;
        lock (this)
        {
            bytes = _content;
            editors = _sessions.Select(s => s.Capture()).ToArray();
        }
        var contentHandle = await WriteAsync(content, bytes, cancellationToken, _sourceState?.Content);
        var partitions = ImmutableArray.CreateBuilder<PartitionState>();
        foreach (var partition in new[] { FilePartition, MetadataPartition, EditorsTablePartition })
            partitions.Add(await partition.CaptureAsync(content, cancellationToken));
        return new DocumentState(DocumentState.CurrentFormat, TransitionId, Url, StorageIds.PathKey(Url),
            CreatedUtc, LastModifiedUtc, ContentVersion, 0, contentHandle, partitions.ToImmutable(),
            editors.ToImmutableArray(), coordination ?? CoordinationState.Empty, receipts.IsDefault ? [] : receipts)
            { Security = Security };
    }

    internal static async ValueTask<ContentHandle> WriteAsync(IContentStore content, byte[] bytes, CancellationToken cancellationToken,
        ContentHandle? previous = null)
    {
        if (previous is not null && previous.Length == bytes.LongLength &&
            previous.Sha256 == Convert.ToHexStringLower(SHA256.HashData(bytes))) return previous;
        using var stream = new MemoryStream(bytes, writable: false);
        return await content.WriteAsync(stream, cancellationToken);
    }
}

public sealed partial class DocumentPartition
{
    private PartitionState? _sourceState;
    internal DocumentPartition(PartitionState state, IReadOnlyDictionary<string, byte[]> payloads, bool metadataOnly = false)
    {
        Kind = (DocumentPartitionKind)state.Kind;
        ProtocolIdentity = DocumentStorageIdentity.Restore(state.Identity);
        _knowledgeSequence = state.Knowledge;
        _sourceState = state;
        _content = Kind != DocumentPartitionKind.FileContents ? state.InlineContent.ToArray()
            : metadataOnly ? [] : payloads[state.Content.Key].ToArray();
        if (Kind == DocumentPartitionKind.FileContents && !metadataOnly)
        {
            if (state.StorageIndex is null) throw new StorageCorruptionException("Missing selected file storage index.");
            _graph = PartitionGraphSnapshot.Create(state.Elements.Select(e =>
                new DataElement((DataElementType)e.Type, StorageIds.Restore(e.Id), new SerialNumber(e.Serial.Guid, e.Serial.Value))
                { Data = payloads[e.Payload.Key] }), StorageIds.Restore(state.StorageIndex));
        }
    }
    internal async ValueTask<PartitionState> CaptureAsync(IContentStore content, CancellationToken cancellationToken)
    {
        var payload = Kind == DocumentPartitionKind.FileContents
            ? await StoredDocument.WriteAsync(content, _content, cancellationToken, _sourceState?.Content)
            : _sourceState?.Content ?? await StoredDocument.WriteAsync(content, [], cancellationToken);
        var elements = ImmutableArray.CreateBuilder<GraphElementState>();
        var previous = _sourceState?.Elements.ToDictionary(e => e.Id);
        var graph = Kind == DocumentPartitionKind.FileContents ? FileGraph : null;
        var mappingSerials = graph?.MappingSerials;
        if (graph is not null)
            await graph.VisitElementsAsync(async (element, stream) =>
            {
                var id = StorageIds.Capture(element.DataElementExtendedGuid);
                // Merge rejects changes to an existing element ID, so a restored
                // immutable payload's durable handle can be reused without I/O.
                var retained = previous?.GetValueOrDefault(id)?.Payload;
                ContentHandle handle;
                if (retained is not null && retained.Length == stream.Length &&
                    retained.Sha256 == Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)))
                    handle = retained;
                else
                {
                    stream.Position = 0;
                    handle = await content.WriteAsync(stream, cancellationToken);
                }
                elements.Add(new GraphElementState(id, (uint)element.DataElementType,
                    new(element.SerialNumber.Guid, element.SerialNumber.Value), handle)
                {
                    MappingSerials = mappingSerials?.TryGetValue(element.DataElementExtendedGuid, out var serials) == true
                        ? serials.Select(s => new SerialId(s.Guid, s.Value)).ToImmutableArray() : [],
                });
            });
        return new((int)Kind, ProtocolIdentity.Capture(), KnowledgeSequence, payload,
            graph is null ? null : StorageIds.Capture(graph.StorageIndex), elements.ToImmutable(),
            graph is null ? Content.ToImmutableArray() : []);
    }
}

public sealed partial class DocumentStorageIdentity
{
    internal PartitionIdentity Capture() => new(StorageIds.Capture(StorageManifestGuid), StorageIds.Capture(CellManifestGuid),
        StorageIds.Capture(RevisionManifestGuid), StorageIds.Capture(ObjectGroupGuid), StorageIds.Capture(ObjectDataBlobGuid),
        StorageIds.Capture(ObjectGuid), StorageIds.Capture(RevisionId), StorageIds.Capture(CellId.LongId),
        StorageIds.Capture(CellId.ShortId), SerialGuid);
    internal static DocumentStorageIdentity Restore(PartitionIdentity state) => new(
        StorageIds.Restore(state.StorageManifest), StorageIds.Restore(state.CellManifest),
        StorageIds.Restore(state.RevisionManifest), StorageIds.Restore(state.ObjectGroup),
        StorageIds.Restore(state.ObjectDataBlob), StorageIds.Restore(state.Object), StorageIds.Restore(state.Revision),
        new CellId(StorageIds.Restore(state.CellLong), StorageIds.Restore(state.CellShort)), state.SerialGuid);
}

public sealed partial class CoauthSession
{
    internal EditorState Capture() => new(ClientId, UserName, JoinedUtc, LastSeenUtc, ExpiresUtc,
        TimeoutSeconds, AsEditor, EditorNumber, Metadata.ToImmutableDictionary(p => p.Key, p => p.Value.ToImmutableArray(), StringComparer.Ordinal), Owner);
    private CoauthSession(EditorState state)
    {
        ClientId = state.ClientId; UserName = state.UserName; JoinedUtc = state.JoinedUtc;
        Owner = state.Owner;
        LastSeenUtc = state.LastSeenUtc; ExpiresUtc = state.ExpiresUtc;
        TimeoutSeconds = state.TimeoutSeconds; AsEditor = state.AsEditor; EditorNumber = state.EditorNumber;
        foreach (var pair in state.Metadata) Metadata.Add(pair.Key, pair.Value.ToArray());
    }
    internal static CoauthSession Restore(EditorState state) => new(state);
}
