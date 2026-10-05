using System.Collections.Immutable;
using System.Security.Cryptography;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

/// <summary>A successful committed save receipt, including accepted no-ops. Replay does not republish content.</summary>
public sealed record AcceptedSave(Guid ResourceId, uint ContentVersion, ContentHandle Content,
    string OperationKey, bool IsReplay)
{
    public long LifecycleGeneration { get; init; } = 1;
}

public sealed record CellExecution(FsshttpbResponse Response, DocumentState State, string? LockError = null)
{
    public ImmutableArray<AcceptedSave> AcceptedSaves { get; init; } = [];
}

/// <summary>Prepares file revisions outside coordination transactions and publishes them durably.</summary>
public sealed class CellBridgeDocumentService(StorageProvider provider, ICellBridgeAccessEvaluator? accessEvaluator = null)
{
    private readonly ICellBridgeAccessEvaluator _access = accessEvaluator ?? new StoredDocumentAccessEvaluator();
    public StorageProvider Provider => provider;
    public DocumentAccess Access(CellBridgeActor actor, DocumentState state)
    {
        if (actor.AccessLimit is { } limit && limit.ResourceId != state.ResourceId) return DocumentAccess.None;
        var access = _access.Evaluate(actor, state);
        if (access.HasFlag(DocumentAccess.Write)) access |= DocumentAccess.Read;
        if (actor.AccessLimit is { } ceiling) access &= ceiling.Access;
        return access;
    }

    public async ValueTask<DocumentState?> ResolveAsync(FssHttpRequest request, CellBridgeActor actor,
        CancellationToken cancellationToken = default)
    {
        var state = await LookupAsync(request, cancellationToken);
        if (state is not null && !Access(actor, state).HasFlag(DocumentAccess.Read))
            throw new UnauthorizedAccessException("Document access denied.");
        return state;
    }

    internal ValueTask<DocumentState?> LookupAsync(FssHttpRequest request, CancellationToken cancellationToken = default)
    {
        if (request.UseResourceId && request.ResourceId is not null)
            return Guid.TryParse(request.ResourceId, out var id)
                ? provider.State.FindByResourceIdAsync(id, cancellationToken)
                : ValueTask.FromResult<DocumentState?>(null);
        return string.IsNullOrWhiteSpace(request.Url) ? ValueTask.FromResult<DocumentState?>(null)
            : provider.State.FindByPathKeyAsync(StorageIds.PathKey(DocumentStore.NormalizeUrl(request.Url)), cancellationToken);
    }

    public async ValueTask<DocumentState?> CreateAsync(string escapedPath, byte[] bytes, CellBridgeActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.CanCreate) throw new UnauthorizedAccessException("Document creation denied.");
        return await CreateOwnedAsync(escapedPath, bytes, DocumentSecurity.Create(actor.Identity), cancellationToken);
    }

    /// <summary>Creates a document with a stable host-selected protocol resource GUID.</summary>
    public ValueTask<DocumentState?> CreateAsync(Guid resourceId, string escapedPath, byte[] bytes,
        CellBridgeActor actor, CancellationToken cancellationToken = default)
    {
        if (resourceId == Guid.Empty) throw new ArgumentException("A resource identifier is required.", nameof(resourceId));
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.CanCreate) throw new UnauthorizedAccessException("Document creation denied.");
        return CreateOwnedAsync(escapedPath, bytes, DocumentSecurity.Create(actor.Identity), cancellationToken, resourceId);
    }

    /// <summary>Trusted import: an explicit owner and the importing actor are recorded atomically.</summary>
    public ValueTask<DocumentState?> ImportAsync(string escapedPath, byte[] bytes, SubjectIdentity owner,
        CellBridgeActor importer, CancellationToken cancellationToken = default)
    {
        if (!importer.CanCreate) throw new UnauthorizedAccessException("Document import denied.");
        return CreateOwnedAsync(escapedPath, bytes, DocumentSecurity.Create(owner, importer.Identity), cancellationToken);
    }

    /// <summary>Trusted import with a stable host-selected protocol resource GUID and explicit owner.</summary>
    public ValueTask<DocumentState?> ImportAsync(Guid resourceId, string escapedPath, byte[] bytes, SubjectIdentity owner,
        CellBridgeActor importer, CancellationToken cancellationToken = default)
    {
        if (resourceId == Guid.Empty) throw new ArgumentException("A resource identifier is required.", nameof(resourceId));
        ArgumentNullException.ThrowIfNull(importer);
        if (!importer.CanCreate) throw new UnauthorizedAccessException("Document import denied.");
        return CreateOwnedAsync(escapedPath, bytes, DocumentSecurity.Create(owner, importer.Identity), cancellationToken, resourceId);
    }

    private async ValueTask<DocumentState?> CreateOwnedAsync(string escapedPath, byte[] bytes, DocumentSecurity security,
        CancellationToken cancellationToken, Guid? resourceId = null)
    {
        StorageLimits.Check("document bytes", bytes.LongLength, provider.Limits.MaxDocumentBytes);
        if (resourceId is { } id && await provider.State.FindByResourceIdAsync(id, cancellationToken) is not null)
            return null;
        if (await provider.State.FindByPathKeyAsync(StorageIds.PathKey(DocumentStore.NormalizeUrl(escapedPath)), cancellationToken) is not null)
            return null;
        var documents = new DocumentStore();
        var document = documents.Put(escapedPath, bytes);
        document.Security = security;
        var state = await document.CaptureAsync(provider.Content, cancellationToken: cancellationToken);
        if (resourceId is { } selectedId) state = state with { ResourceId = selectedId };
        provider.Limits.CheckDocument(state);
        return await provider.State.TryCreateAsync(state, cancellationToken) ? state : null;
    }

    public async ValueTask<CellExecution> ExecuteAsync(Guid id, DocumentPartitionKind kind,
        FsshttpbCellRequest request, IReadOnlyDictionary<string, string> attributes,
        CellBridgeActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var response = new FsshttpbResponse();
        var accepted = ImmutableArray.CreateBuilder<AcceptedSave>();
        var state = await CurrentAsync(id, cancellationToken);
        if (request.SubRequests.Count == 0)
        {
            var emptyDocument = StoredDocument.RestoreMetadata(state, DateTime.UtcNow);
            return new(CellBinaryRequestExecutor.Execute(emptyDocument, emptyDocument.GetPartition(kind), request, Access(actor, state)), state);
        }
        try
        {
            // Each operation has its own publication boundary. Queries keep
            // their own knowledge/constraints and share a union of immutable payloads.
            var queries = new QueryChangesResponseAssembler(response, provider.Limits.MaxGraphBytes);
            foreach (var operation in request.SubRequests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CellBinaryRequestExecutor.TryResolveTarget(operation, kind, out var selectedKind))
                {
                    state = await CurrentAsync(id, cancellationToken);
                    var accessForTarget = Access(actor, state);
                    var requiredForTarget = operation.RequestType is RequestTypes.PutChanges or RequestTypes.AllocateExtendedGuidRange
                        ? DocumentAccess.Write : DocumentAccess.Read;
                    response.SubResponses.Add(operation.RequestType != RequestTypes.QueryAccess && !accessForTarget.HasFlag(requiredForTarget)
                        ? CellBridgeAuthorization.Denied(operation)
                        : CellBinaryRequestExecutor.UnsupportedSubResponse(operation.RequestId, operation.RequestType));
                    continue;
                }
                if (operation.RequestType == RequestTypes.PutChanges)
                {
                    CellExecution saved;
                    try { saved = await SaveAsync(id, selectedKind, operation, request.DataElementPackage, attributes, actor, cancellationToken); }
                    catch (StorageQuotaExceededException ex)
                    {
                        var rejected = new FsshttpbResponse();
                        rejected.SubResponses.Add(new FsshttpbSubResponse
                        {
                            RequestId = operation.RequestId,
                            RequestType = RequestTypes.PutChanges,
                            Status = true,
                            Error = new ResponseError(ErrorType.Win32, 112, ex.Message)
                        }); // ERROR_DISK_FULL.
                        saved = new(rejected, await CurrentAsync(id, cancellationToken));
                    }
                    response.SubResponses.AddRange(saved.Response.SubResponses);
                    accepted.AddRange(saved.AcceptedSaves);
                    state = saved.State;
                    if (saved.LockError is not null) return new(response, state, saved.LockError) { AcceptedSaves = accepted.ToImmutable() };
                    continue;
                }
                state = await CurrentAsync(id, cancellationToken);
                var access = Access(actor, state);
                var requiredAccess = operation.RequestType == RequestTypes.AllocateExtendedGuidRange
                    ? DocumentAccess.Write : DocumentAccess.Read;
                if (operation.RequestType != RequestTypes.QueryAccess && !access.HasFlag(requiredAccess))
                {
                    response.SubResponses.Add(CellBridgeAuthorization.Denied(operation));
                    continue;
                }
                if (selectedKind == DocumentPartitionKind.FileContents && operation.RequestType == RequestTypes.QueryChanges)
                {
                    var query = await FileQueryAsync(state, operation, cancellationToken);
                    queries.Append(query);
                    continue;
                }
                var observedNow = DateTime.UtcNow;
                if (selectedKind == DocumentPartitionKind.EditorsTable && operation.RequestType == RequestTypes.QueryChanges)
                {
                    (state, observedNow) = await ExpireEditorsAsync(id, actor, cancellationToken);
                    access = Access(actor, state);
                    if (!access.HasFlag(DocumentAccess.Read))
                    {
                        response.SubResponses.Add(CellBridgeAuthorization.Denied(operation));
                        continue;
                    }
                }
                var document = StoredDocument.RestoreMetadata(state, observedNow);
                var single = new FsshttpbCellRequest { DataElementPackage = request.DataElementPackage };
                single.SubRequests.Add(operation);
                var partition = document.GetPartition(selectedKind);
                var result = CellBinaryRequestExecutor.Execute(document, partition, single, access,
                    maxResponseBytes: provider.Limits.MaxGraphBytes,
                    partitionEditorsQueryChanges: (p, requestId) => EditorsQuery(document, p, requestId, state));
                if (operation.RequestType == RequestTypes.QueryChanges) queries.Append(result);
                else response.SubResponses.AddRange(result.SubResponses);
            }
            // MS-FSSHTTP GetFileProps describes the updated version after the Cell
            // operations. Capture its metadata here; the SOAP adapter never rereads it.
            return new(response, state) { AcceptedSaves = accepted.ToImmutable() };
        }
        catch (Exception ex) when (accepted.Count != 0 && AcceptedSaveException.CanWrap(ex))
        {
            throw AcceptedSaveException.Wrap(accepted.ToImmutable(), ex);
        }
    }

    private async ValueTask<FsshttpbResponse> FileQueryAsync(DocumentState state,
        FsshttpbCellSubRequest operation, CancellationToken cancellationToken)
    {
        var partition = state.Partitions.Single(p => p.Kind == 0);
        var request = operation.Data as QueryChangesSubRequestData;
        var metadata = partition.Elements.Select(e => new DataElement((DataElementType)e.Type,
            StorageIds.Restore(e.Id), new SerialNumber(e.Serial.Guid, e.Serial.Value))).ToArray();
        _ = ContentStoreReader.UniqueHandles(partition.Elements.Select(e => e.Payload));
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var indexId = partition.StorageIndex ?? throw new StorageCorruptionException("Missing file storage index.");
        var selectedIndex = await ReadElementAsync(indexId);
        var manifest = await ReadElementAsync(StorageIds.Capture(PartitionGraphSnapshot.ReadStorageManifestId(selectedIndex)));
        var cells = PartitionGraphSnapshot.ReadFileCells(selectedIndex, manifest);
        if (!FileQueryResponseBuilder.Supports(request, cells)) return FileQueryResponseBuilder.Unsupported(operation.RequestId);
        var cell = FileQueryResponseBuilder.IsScoped(request) ? request!.CellId! : cells.First();
        IReadOnlySet<ExGuid>? scope = null;
        if (FileQueryResponseBuilder.IsScoped(request) && cells.Count > 1)
        {
            try
            {
                var graph = await DocumentPartition.RestoreGraphAsync(partition, provider.Content, provider.Limits, cancellationToken);
                scope = graph.GetRequiredElements(cell).ToHashSet();
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            { return FileQueryResponseBuilder.Unsupported(operation.RequestId); }
        }
        var mappingSerials = new Dictionary<ExGuid, IReadOnlyList<SerialNumber>>();
        foreach (var stored in partition.Elements.Where(e => e.Type == (uint)DataElementType.StorageIndexDataElementData))
        {
            if (stored.MappingSerials.IsDefault)
                throw new StorageCorruptionException("Storage index is missing persisted mapping metadata.");
            mappingSerials.Add(StorageIds.Restore(stored.Id),
                stored.MappingSerials.Select(s => new SerialNumber(s.Guid, s.Value)).ToArray());
        }
        var selection = FileQueryResponseBuilder.Select(metadata,
            StorageIds.Restore(partition.StorageIndex ?? throw new StorageCorruptionException("Missing file storage index.")),
            cell, partition.Knowledge, request, mappingSerials, scope);
        var elements = new List<DataElement>();
        foreach (var stored in partition.Elements.Where(e => selection.PayloadIds.Contains(StorageIds.Restore(e.Id))))
        {
            var bytes = await ReadPayloadAsync(stored.Payload);
            elements.Add(new DataElement((DataElementType)stored.Type, StorageIds.Restore(stored.Id),
                new SerialNumber(stored.Serial.Guid, stored.Serial.Value))
            { Data = bytes });
        }
        return FileQueryResponseBuilder.Build(operation.RequestId, selection, elements, request);

        async ValueTask<DataElement> ReadElementAsync(ExtendedId elementId)
        {
            var stored = partition.Elements.SingleOrDefault(e => e.Id == elementId)
                ?? throw new StorageCorruptionException("Missing selected graph element.");
            return new DataElement((DataElementType)stored.Type, StorageIds.Restore(stored.Id),
                new SerialNumber(stored.Serial.Guid, stored.Serial.Value))
            { Data = await ReadPayloadAsync(stored.Payload) };
        }

        async ValueTask<byte[]> ReadPayloadAsync(ContentHandle handle)
        {
            if (payloads.TryGetValue(handle.Key, out var bytes)) return bytes;
            bytes = await provider.Content.ReadVerifiedAsync(handle, provider.Limits.MaxObjectBytes, cancellationToken);
            payloads.Add(handle.Key, bytes);
            return bytes;
        }
    }

    private async ValueTask<CellExecution> SaveAsync(Guid id, DocumentPartitionKind kind,
        FsshttpbCellSubRequest operation, DataElementPackage? package, IReadOnlyDictionary<string, string> attributes,
        CellBridgeActor actor, CancellationToken cancellationToken)
    {
        var initial = await CurrentAsync(id, cancellationToken);
        if (!Access(actor, initial).HasFlag(DocumentAccess.Write)) return Wrap(CellBridgeAuthorization.Denied(operation), initial);
        if (kind != DocumentPartitionKind.FileContents || operation.Data is not PutChangesSubRequestData put || package is null)
            return await FailedAsync(id, operation.RequestId, CellErrorCode.RequestNotSupported, "Only complete file partition writes are supported.", cancellationToken);
        if ((put.Flags & ~0x79) != 0 || (put.AdditionalFlagsBits & 0x38) != 0)
            return await FailedAsync(id, operation.RequestId, CellErrorCode.RequestNotSupported, "This upload mode is unsupported.", cancellationToken);
        var publicationOperationId = Guid.NewGuid();
        string key = $"{put.StorageIndex.Value}:{put.StorageIndex.Guid:D}";
        string digest;
        try { digest = Digest(put, package); }
        catch (InvalidDataException ex) { return await FailedAsync(id, operation.RequestId, CellErrorCode.InvalidObject, ex.Message, cancellationToken); }
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var before = await CurrentAsync(id, cancellationToken);
            if (!Access(actor, before).HasFlag(DocumentAccess.Write)) return Wrap(CellBridgeAuthorization.Denied(operation), before);
            if (before.LifecycleGeneration != initial.LifecycleGeneration)
                return Wrap(Failure(operation.RequestId, CellErrorCode.CoherencyFailure, "The document incarnation changed."), before);
            if (before.Receipts.FirstOrDefault(r => r.OperationKey == key) is { } prior)
                return await RepeatAsync(before, prior, digest, operation.RequestId, actor, cancellationToken);
            if (before.Coordination.Generation != initial.Coordination.Generation)
                return Wrap(Failure(operation.RequestId, CellErrorCode.CoherencyFailure, "Write authority changed while preparing the save."), before);
            var document = StoredDocument.RestoreMetadata(before, DateTime.UtcNow);
            var currentGraph = await DocumentPartition.RestoreFileGraphAsync(before, provider.Content, provider.Limits, cancellationToken);
            StorageLimits.Check("save receipts", before.Receipts.Length + 1L, provider.Limits.MaxSaveReceipts);
            var prepared = FilePartitionSaveHandler.Prepare(document, document.FilePartition, operation, package,
                provider.Limits, currentGraph);
            var result = prepared.Response;
            if (result.Status) return Wrap(result, before);
            DocumentState candidate;
            try
            {
                uint version = prepared.Repeat ? before.ContentVersion : checked(before.ContentVersion + 1);
                await using var staging = OpenSaveStagingStream();
                try { await prepared.Graph!.MaterializeToAsync(staging, provider.Limits.MaxDocumentBytes, cancellationToken); }
                catch (GraphMaterializationLimitException)
                { throw new StorageQuotaExceededException("document bytes", provider.Limits.MaxDocumentBytes + 1, provider.Limits.MaxDocumentBytes); }
                await FilePartitionSaveHandler.ValidateDocumentAsync(staging, document.Url,
                    provider.Limits.MaxDocumentBytes, cancellationToken);
                if (prepared.Repeat)
                    candidate = before;
                else
                {
                    staging.Position = 0;
                    var handle = await provider.Content.WriteAsync(staging, cancellationToken);
                    var file = await DocumentPartition.CaptureFileGraphAsync(before.Partitions.Single(p => p.Kind == 0),
                        prepared.Graph!, prepared.KnowledgeSequence, handle, provider.Content, cancellationToken);
                    candidate = before with
                    {
                        Content = handle,
                        ContentVersion = version,
                        Partitions = before.Partitions.Select(p => p.Kind == 0 ? file : p.Kind == 1 ? p with
                        { Knowledge = unchecked(p.Knowledge + 1) } : p).ToImmutableArray(),
                    };
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException or OverflowException)
            {
                return Wrap(Failure(operation.RequestId, CellErrorCode.InvalidObject, ex.Message), before);
            }
            var receiptResponse = new FsshttpbResponse();
            receiptResponse.SubResponses.Add(result);
            using var receiptStream = new MemoryStream(receiptResponse.ToByteArray());
            var responseHandle = await provider.Content.WriteAsync(receiptStream, cancellationToken);
            var receipt = new SaveReceipt(key, digest, candidate.ContentVersion, responseHandle, actor.Identity.Subject);
            PublishResult published;
            try
            {
                published = await provider.State.TransitionAsync(id, (current, now) =>
                {
                    if (!Access(actor, current).HasFlag(DocumentAccess.Write))
                        return new StateTransition<PublishResult>(null, new(current, null, "FileUnauthorizedAccess", false));
                    if (current.LifecycleGeneration != initial.LifecycleGeneration)
                        return new StateTransition<PublishResult>(null, new(current, null, "InvalidCoauthSession", false));
                    if (current.Receipts.FirstOrDefault(r => r.OperationKey == key) is { } accepted)
                        return new StateTransition<PublishResult>(null, new(current, accepted, null, false));
                    if (current.Coordination.Generation != initial.Coordination.Generation)
                        return new StateTransition<PublishResult>(null, new(current, null, "InvalidCoauthSession", false));
                    if (current.ContentVersion != before.ContentVersion || current.Content != before.Content)
                        return new StateTransition<PublishResult>(null, new(current, null, null, true));
                    var metadata = StoredDocument.RestoreMetadata(current, now);
                    var coordinator = FssHttpLockCoordinator.Restore(metadata, current.Coordination, now, actor.Identity);
                    if (!coordinator.ExecuteCellWrite(attributes, () => true, out _, out var error, now))
                        return new StateTransition<PublishResult>(null, new(current, null, error, false));
                    var currentMetadata = current.Partitions.Single(x => x.Kind == 1);
                    var state = metadata.CaptureCoordination(current, coordinator.Capture()) with
                    {
                        Security = current.Security with { ModifiedBy = candidate.ContentVersion == before.ContentVersion ? current.Security.ModifiedBy : actor.Identity },
                        Content = candidate.Content,
                        StateVersion = checked(current.StateVersion + 1),
                        ContentVersion = candidate.ContentVersion,
                        ModifiedUtc = candidate.ContentVersion == before.ContentVersion ? current.ModifiedUtc : now,
                        // Keep the authoritative editors partition, including updates
                        // made while file materialization/staging was in progress.
                        Partitions = candidate.Partitions.Select(p => p.Kind == 2
                            ? current.Partitions.Single(x => x.Kind == 2) with
                            {
                                Knowledge = metadata.EditorsTablePartition.KnowledgeSequence,
                                InlineContent = metadata.EditorsTablePartition.Content.ToImmutableArray()
                            }
                            : p.Kind == 1 && candidate.ContentVersion != before.ContentVersion ? current.Partitions.Single(x => x.Kind == 1) with
                            {
                                Knowledge = unchecked(currentMetadata.Knowledge + 1),
                                InlineContent = !currentMetadata.Elements.IsEmpty ? currentMetadata.InlineContent : System.Text.Encoding.UTF8.GetBytes(
                                $"<Metadata ContentVersion=\"{candidate.ContentVersion}\" Modified=\"{now.Ticks}\" />").ToImmutableArray()
                            }
                            : p).ToImmutableArray(),
                        Coordination = CoordinationFencing.Capture(current.Coordination, coordinator.Capture()),
                        // Superseded retries need their digest/version, never their response bytes.
                        Receipts = current.Receipts.Select(r => r.ContentVersion == candidate.ContentVersion
                            ? r : r with { Response = null }).Append(receipt).ToImmutableArray(),
                    };
                    state = ExternalPublication.Append(current, state, publicationOperationId, provider.Limits);
                    provider.Limits.CheckDocument(state);
                    return new StateTransition<PublishResult>(state, new(state, null, null, false));
                }, cancellationToken);
            }
            catch (StorageUnavailableException)
            {
                // A lost commit response is not a rollback. Resolve the durable
                // receipt before attempting to apply this operation again.
                var current = await CurrentAsync(id, CancellationToken.None);
                if (current.LifecycleGeneration != initial.LifecycleGeneration ||
                    current.Receipts.FirstOrDefault(r => r.OperationKey == key) is not { } accepted) throw;
                return await RepeatAsync(current, accepted, digest, operation.RequestId, actor, CancellationToken.None);
            }
            if (published.Receipt is { } duplicate)
                return await RepeatAsync(published.State, duplicate, digest, operation.RequestId, actor, cancellationToken);
            if (published.LockError == "FileUnauthorizedAccess") return Wrap(CellBridgeAuthorization.Denied(operation), published.State);
            if (published.LockError is not null) return new(new FsshttpbResponse(), published.State, published.LockError);
            if (published.Retry) continue;
            return Wrap(result, published.State) with
            {
                AcceptedSaves =
                [new(id, receipt.ContentVersion, published.State.Content, key, false) { LifecycleGeneration = published.State.LifecycleGeneration }]
            };
        }
        return await FailedAsync(id, operation.RequestId, CellErrorCode.CoherencyFailure, "The document changed while preparing this save.", cancellationToken);
    }

    private async ValueTask<CellExecution> RepeatAsync(DocumentState state, SaveReceipt receipt, string digest,
        ulong requestId, CellBridgeActor actor, CancellationToken cancellationToken)
    {
        var generation = state.LifecycleGeneration;
        state = await provider.State.TransitionAsync(state.ResourceId, (current, _) => new StateTransition<DocumentState>(null, current), cancellationToken);
        if (state.LifecycleGeneration != generation || state.Receipts.FirstOrDefault(r => r.OperationKey == receipt.OperationKey) is not { } authoritative)
            return Wrap(Failure(requestId, CellErrorCode.CoherencyFailure, "The accepted operation is no longer current."), state);
        receipt = authoritative;
        if (!Access(actor, state).HasFlag(DocumentAccess.Write) || receipt.OwnerSubject != actor.Identity.Subject)
            return Wrap(new FsshttpbSubResponse
            {
                RequestId = requestId,
                RequestType = RequestTypes.PutChanges,
                Status = true,
                Error = CellBridgeAuthorization.AccessError()
            }, state);
        if (receipt.Digest != digest)
            return Wrap(Failure(requestId, CellErrorCode.InvalidObject, "An accepted storage index was reused for a different operation."), state);
        if (state.ContentVersion != receipt.ContentVersion)
            return Wrap(Failure(requestId, CellErrorCode.CoherencyFailure, "The accepted operation has been superseded by a later revision."), state);
        var bytes = await provider.Content.ReadVerifiedAsync(receipt.Response ??
            throw new StorageCorruptionException("The current save receipt has no response."),
            provider.Limits.MaxObjectBytes, cancellationToken);
        FsshttpbResponse response;
        try
        {
            var reader = new BinaryReaderEx(bytes);
            response = FsshttpbResponse.Deserialize(reader);
            if (reader.Remaining != 0 || response.SubResponses.Count != 1 ||
                response.SubResponses[0].RequestType != RequestTypes.PutChanges || response.SubResponses[0].Status)
                throw new StorageCorruptionException("The save receipt is not a complete successful PutChanges response.");
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException or ArgumentException or OverflowException)
        {
            throw new StorageCorruptionException("The saved response receipt contains malformed binary data.", ex);
        }
        response.SubResponses[0].RequestId = requestId;
        return new(response, state)
        {
            AcceptedSaves =
            [new(state.ResourceId, receipt.ContentVersion, state.Content, receipt.OperationKey, true) { LifecycleGeneration = state.LifecycleGeneration }]
        };
    }

    private ValueTask<DocumentState> CurrentAsync(Guid id, CancellationToken cancellationToken) => GetCurrentAsync(id, cancellationToken);

    private static FileStream OpenSaveStagingStream()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = 65536,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(Path.Combine(Path.GetTempPath(), "cellbridge-save-" + Guid.NewGuid().ToString("N") + ".tmp"), options);
    }
    private ValueTask<(DocumentState State, DateTime Now)> ExpireEditorsAsync(Guid id, CellBridgeActor actor, CancellationToken cancellationToken) =>
        provider.State.TransitionAsync(id, (current, now) =>
        {
            if (!Access(actor, current).HasFlag(DocumentAccess.Read) || !current.Editors.Any(e => e.ExpiresUtc <= now))
                return new StateTransition<(DocumentState, DateTime)>(null, (current, now));
            var document = StoredDocument.RestoreMetadata(current, now);
            var next = document.CaptureCoordination(current, current.Coordination) with
            {
                StateVersion = checked(current.StateVersion + 1),
                Coordination = current.Coordination,
            };
            return new StateTransition<(DocumentState, DateTime)>(next, (next, now));
        }, cancellationToken);
    private async ValueTask<DocumentState> GetCurrentAsync(Guid id, CancellationToken cancellationToken) =>
        await provider.State.FindByResourceIdAsync(id, cancellationToken) ?? throw new KeyNotFoundException("Document does not exist.");
    private async ValueTask<CellExecution> FailedAsync(Guid id, ulong requestId, CellErrorCode error, string message, CancellationToken cancellationToken) =>
        Wrap(Failure(requestId, error, message), await CurrentAsync(id, cancellationToken));
    private static CellExecution Wrap(FsshttpbSubResponse response, DocumentState state)
    {
        var result = new FsshttpbResponse(); result.SubResponses.Add(response); return new(result, state);
    }
    private static FsshttpbSubResponse Failure(ulong requestId, CellErrorCode error, string message, RequestTypes requestType = RequestTypes.PutChanges) => new()
    {
        RequestId = requestId,
        RequestType = requestType,
        Status = true,
        Error = new ResponseError(ErrorType.Cell, (ulong)error, message),
    };
    private static string Digest(PutChangesSubRequestData put, DataElementPackage package)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var writer = new BinaryWriterEx();
        put.StorageIndex.Serialize(writer); put.ExpectedStorageIndex.Serialize(writer);
        writer.WriteByte((byte)(put.Flags & 1));
        writer.WriteUInt16((ushort)(put.AdditionalFlagsBits & 7));
        hash.AppendData(writer.ToArray());
        // Normalize identical duplicate elements and wire order. Request IDs,
        // transport tokens and ignored reserved bits are not operation identity.
        foreach (var group in package.DataElements.GroupBy(e => e.DataElementExtendedGuid)
                     .OrderBy(g => g.Key.Guid).ThenBy(g => g.Key.Value))
        {
            var element = group.First();
            if (group.Any(e => e.DataElementType != element.DataElementType ||
                !e.SerialNumber.Equals(element.SerialNumber) || !(e.Data ?? []).SequenceEqual(element.Data ?? [])))
                throw new InvalidDataException("Conflicting duplicate data element identifiers.");
            writer = new BinaryWriterEx();
            element.DataElementExtendedGuid.Serialize(writer);
            writer.WriteUInt32((uint)element.DataElementType); element.SerialNumber.Serialize(writer);
            writer.WriteUInt64((ulong)(element.Data?.LongLength ?? 0));
            hash.AppendData(writer.ToArray());
            hash.AppendData(element.Data ?? []);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private FsshttpbResponse EditorsQuery(StoredDocument doc, DocumentPartition partition, ulong requestId, DocumentState state) =>
        EditorsTablePartitionBuilder.BuildSharePointV13QueryChangesResponse(requestId,
            doc.Sessions.Select(s => new EditorsTableEditor(s.ClientId.ToString("D"), s.ExpiresUtc.Ticks,
                s.Owner?.DisplayName ?? s.UserName, s.Owner?.Login ?? s.UserName, HasEditorPermission: s.AsEditor && s.Owner is not null &&
                    Access(new(s.Owner), state).HasFlag(DocumentAccess.Write), Metadata: s.Metadata)), partition.FssHttpBIdentity.CellId,
            partition.FssHttpBIdentity.SerialGuid, partition.KnowledgeSequence);
    private sealed record PublishResult(DocumentState State, SaveReceipt? Receipt, string? LockError, bool Retry);
}
