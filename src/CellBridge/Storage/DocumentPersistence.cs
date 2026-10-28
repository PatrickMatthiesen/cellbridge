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
        if (!metadataOnly && !FilePartition.ContentEquals(_content))
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
        _ = ContentStoreReader.UniqueHandles(StorageReferences.Handles(state));
        var handles = ContentStoreReader.UniqueHandles(state.Partitions.Where(p => p.Kind == 0)
            .SelectMany(p => p.Elements.Select(e => e.Payload).Append(p.Content)).Append(state.Content));
        var payloads = await content.ReadManyVerifiedAsync(handles, cancellationToken: cancellationToken);
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
        var captured = new DocumentState(DocumentState.CurrentFormat, TransitionId, Url, StorageIds.PathKey(Url),
            CreatedUtc, LastModifiedUtc, ContentVersion, 0, contentHandle, partitions.ToImmutable(),
            editors.ToImmutableArray(), coordination ?? CoordinationState.Empty, receipts.IsDefault ? [] : receipts)
            { Security = Security };
        return _sourceState is null ? captured : _sourceState with
        {
            Path = captured.Path, PathKey = captured.PathKey, ModifiedUtc = captured.ModifiedUtc,
            ContentVersion = captured.ContentVersion, Content = captured.Content, Partitions = captured.Partitions,
            Editors = captured.Editors, Security = captured.Security,
            Coordination = coordination ?? _sourceState.Coordination,
            Receipts = receipts.IsDefault ? _sourceState.Receipts : receipts,
        };
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

    /// <summary>Restores an opaque graph independently of any application file adapter.</summary>
    public static async ValueTask<GenericPartitionGraphSnapshot> RestoreGraphAsync(PartitionState partition,
        IContentStore content, StorageLimits limits, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        limits.Validate();
        StorageLimits.Check("graph elements", partition.Elements.Length, limits.MaxGraphElements);
        StorageLimits.Check("graph bytes", partition.Elements.Sum(e => e.Payload.Length), limits.MaxGraphBytes);
        _ = ContentStoreReader.UniqueHandles(partition.Elements.Select(e => e.Payload));
        if (partition.StorageIndex is null) throw new StorageCorruptionException("Missing selected storage index.");
        var payloads = await content.ReadManyVerifiedAsync(partition.Elements.Select(e => e.Payload),
            limits.MaxObjectBytes, cancellationToken);
        var elements = new List<DataElement>();
        foreach (var stored in partition.Elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = payloads[stored.Payload.Key];
            if (stored.Type == (uint)DataElementType.StorageIndexDataElementData &&
                (stored.MappingSerials.IsDefault || !stored.MappingSerials.SequenceEqual(
                    StorageIndexMappingSerials.Read(bytes).Select(s => new SerialId(s.Guid, s.Value)))))
                throw new StorageCorruptionException("Persisted mapping serials disagree with the storage index.");
            elements.Add(new DataElement((DataElementType)stored.Type, StorageIds.Restore(stored.Id),
                new SerialNumber(stored.Serial.Guid, stored.Serial.Value)) { Data = bytes });
        }
        cancellationToken.ThrowIfCancellationRequested();
        return GenericPartitionGraphSnapshot.Create(elements, StorageIds.Restore(partition.StorageIndex),
            new GenericGraphLimits(MaxElements: limits.MaxGraphElements, MaxPayloadBytes: limits.MaxGraphBytes));
    }

    /// <summary>Stores all retained graph elements. The caller must still check access and commit the document state.</summary>
    public static async ValueTask<PartitionState> CaptureGraphAsync(PartitionState source,
        GenericPartitionGraphSnapshot graph, ulong knowledgeSequence, IContentStore content,
        StorageLimits limits, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        limits.Validate();
        StorageLimits.Check("graph elements", graph.ElementCount, limits.MaxGraphElements);
        StorageLimits.Check("graph bytes", graph.PayloadBytes, limits.MaxGraphBytes);
        var elements = await CaptureElementsAsync(graph.VisitElementsAsync, graph.MappingSerials, source,
            content, cancellationToken, limits.MaxObjectBytes, rejectIdentityReuse: true);
        return source with { Knowledge = knowledgeSequence, StorageIndex = StorageIds.Capture(graph.StorageIndex), Elements = elements };
    }

    /// <summary>Verifies the retained graph without loading the previous materialized package.</summary>
    public static async ValueTask<PartitionGraphSnapshot> RestoreFileGraphAsync(DocumentState state,
        IContentStore content, StorageLimits limits, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        limits.CheckDocument(state);
        _ = ContentStoreReader.UniqueHandles(StorageReferences.Handles(state));
        var partition = state.Partitions.Single(p => p.Kind == 0);
        if (state.Content.Length != partition.Content.Length || state.Content.Sha256 != partition.Content.Sha256)
            throw new StorageCorruptionException("Document and file partition content disagree.");
        if (partition.StorageIndex is null)
            throw new StorageCorruptionException("Missing selected file storage index.");
        var payloads = await content.ReadManyVerifiedAsync(partition.Elements.Select(e => e.Payload),
            limits.MaxObjectBytes, cancellationToken);
        var elements = new List<DataElement>();
        foreach (var stored in partition.Elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = payloads[stored.Payload.Key];
            elements.Add(new DataElement((DataElementType)stored.Type, StorageIds.Restore(stored.Id),
                new SerialNumber(stored.Serial.Guid, stored.Serial.Value)) { Data = bytes });
        }
        cancellationToken.ThrowIfCancellationRequested();
        return PartitionGraphSnapshot.Create(elements, StorageIds.Restore(partition.StorageIndex));
    }

    /// <summary>Stores a detached graph that references an already stored and validated package.</summary>
    public static async ValueTask<PartitionState> CaptureFileGraphAsync(PartitionState source,
        PartitionGraphSnapshot graph, ulong knowledgeSequence, ContentHandle package,
        IContentStore content, CancellationToken cancellationToken = default)
    {
        if (source.Kind != 0) throw new ArgumentException("A file partition is required.", nameof(source));
        var elements = await CaptureElementsAsync(graph, source, content, cancellationToken);
        return source with { Knowledge = knowledgeSequence, Content = package,
            StorageIndex = StorageIds.Capture(graph.StorageIndex), Elements = elements, InlineContent = [] };
    }
    /// <summary>Appends newly admitted immutable payloads while preserving every retained descriptor.</summary>
    internal static async ValueTask<PartitionState> CaptureFileDeltaAsync(PartitionState source, ExGuid storageIndex,
        IReadOnlyCollection<DataElement> additions, ulong knowledgeSequence, ContentHandle package,
        IContentStore content, CancellationToken cancellationToken = default)
    {
        if (source.Kind != 0) throw new ArgumentException("A file partition is required.", nameof(source));
        var mappings = additions.Where(e => e.DataElementType == DataElementType.StorageIndexDataElementData)
            .ToDictionary(e => e.DataElementExtendedGuid, e => StorageIndexMappingSerials.Read(e.Data ?? []));
        async ValueTask VisitAsync(Func<DataElement, Stream, ValueTask> visit)
        {
            foreach (var element in additions)
            {
                using var payload = new MemoryStream(element.Data ?? [], writable: false);
                await visit(element, payload);
            }
        }
        var added = await CaptureElementsAsync(VisitAsync, mappings, null, content, cancellationToken);
        return source with { Knowledge = knowledgeSequence, Content = package,
            StorageIndex = StorageIds.Capture(storageIndex), Elements = source.Elements.Concat(added), InlineContent = [] };
    }

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
        // Coordination and legacy application metadata updates must not erase
        // separately captured opaque graph elements or their BLOB references.
        if (Kind != DocumentPartitionKind.FileContents && _sourceState?.StorageIndex is not null)
            return _sourceState with { Knowledge = KnowledgeSequence, InlineContent = Content.ToImmutableArray() };
        var payload = Kind == DocumentPartitionKind.FileContents
            ? await StoredDocument.WriteAsync(content, _content, cancellationToken, _sourceState?.Content)
            : _sourceState?.Content ?? await StoredDocument.WriteAsync(content, [], cancellationToken);
        var graph = Kind == DocumentPartitionKind.FileContents ? FileGraph : null;
        var elements = graph is null ? [] : await CaptureElementsAsync(graph, _sourceState, content, cancellationToken);
        return new((int)Kind, ProtocolIdentity.Capture(), KnowledgeSequence, payload,
            graph is null ? null : StorageIds.Capture(graph.StorageIndex), elements,
            graph is null ? Content.ToImmutableArray() : []);
    }

    private static async ValueTask<GraphElementCollection> CaptureElementsAsync(
        PartitionGraphSnapshot graph, PartitionState? source, IContentStore content, CancellationToken cancellationToken)
        => await CaptureElementsAsync(graph.VisitElementsAsync, graph.MappingSerials, source, content, cancellationToken);

    private static async ValueTask<GraphElementCollection> CaptureElementsAsync(
        Func<Func<DataElement, Stream, ValueTask>, ValueTask> visit,
        IReadOnlyDictionary<ExGuid, IReadOnlyList<SerialNumber>> mappingSerials, PartitionState? source,
        IContentStore content, CancellationToken cancellationToken, long maxObjectBytes = long.MaxValue,
        bool rejectIdentityReuse = false)
    {
        var elements = ImmutableArray.CreateBuilder<GraphElementState>();
        var previous = source?.Elements.ToDictionary(e => e.Id);
        var pending = new List<ReadOnlyMemory<byte>>();
        var pendingIndexes = new List<int>();
        long pendingBytes = 0;
        async ValueTask FlushAsync()
        {
            if (pending.Count == 0) return;
            var handles = await content.WriteManyAsync(pending, cancellationToken);
            for (var i = 0; i < handles.Count; i++)
            {
                var index = pendingIndexes[i];
                elements[index] = elements[index] with { Payload = handles[i] };
            }
            pending.Clear();
            pendingIndexes.Clear();
            pendingBytes = 0;
        }
        await visit(async (element, stream) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                StorageLimits.Check("graph object bytes", stream.Length, maxObjectBytes);
                var id = StorageIds.Capture(element.DataElementExtendedGuid);
                var old = previous?.GetValueOrDefault(id);
                var serial = new SerialId(element.SerialNumber.Guid, element.SerialNumber.Value);
                if (rejectIdentityReuse && old is not null &&
                    (old.Type != (uint)element.DataElementType ||
                     old.Serial.Guid != Guid.Empty && !element.SerialNumber.IsNull && old.Serial != serial))
                    throw new InvalidDataException("Conflicting immutable data-element identity.");
                if (rejectIdentityReuse && old is not null && element.SerialNumber.IsNull)
                    serial = old.Serial;
                var retained = old?.Payload;
                ContentHandle handle;
                if (retained is not null && retained.Length == stream.Length &&
                    retained.Sha256 == Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)))
                    handle = retained;
                else
                {
                    if (rejectIdentityReuse && retained is not null)
                        throw new InvalidDataException("Conflicting immutable data-element payload.");
                    stream.Position = 0;
                    // The visitor owns the stream. Retain only a bounded batch of
                    // new payloads, then publish their durable handles together.
                    if (pendingBytes + stream.Length > 8L * 1024 * 1024) await FlushAsync();
                    if (stream.Length > 8L * 1024 * 1024)
                        handle = await content.WriteAsync(stream, cancellationToken);
                    else
                    {
                        using var buffer = new MemoryStream();
                        await stream.CopyToAsync(buffer, cancellationToken);
                        var bytes = buffer.ToArray();
                        pending.Add(bytes);
                        pendingIndexes.Add(elements.Count);
                        pendingBytes += bytes.Length;
                        handle = null!; // Replaced by FlushAsync before returning the graph.
                    }
                }
                var mappings = mappingSerials.TryGetValue(element.DataElementExtendedGuid, out var serials)
                    ? serials.Select(s => new SerialId(s.Guid, s.Value)).ToImmutableArray() : [];
                // Historical revisions share these immutable descriptors with the current graph.
                elements.Add(old is not null && old.Type == (uint)element.DataElementType &&
                    old.Serial == serial && old.Payload == handle && old.MappingSerials.AsSpan().SequenceEqual(mappings.AsSpan())
                    ? old : new GraphElementState(id, (uint)element.DataElementType, serial, handle) { MappingSerials = mappings });
                if (pending.Count == 64 || pendingBytes >= 8L * 1024 * 1024) await FlushAsync();
            });
        await FlushAsync();
        var captured = elements.ToImmutable();
        if (source is not null && captured.Length >= source.Elements.Length)
        {
            var index = 0;
            foreach (var old in source.Elements)
            {
                if (!ReferenceEquals(old, captured[index])) break;
                index++;
            }
            if (index == source.Elements.Length)
                return source.Elements.Concat((GraphElementCollection)ImmutableArray.Create(captured.AsSpan()[index..]));
        }
        return captured;
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
