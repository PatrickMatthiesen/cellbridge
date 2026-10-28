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
    /// <summary>Cancellation stopped the request after an established save. The response is incomplete.</summary>
    public bool IsCancelled { get; init; }
    internal bool HasAcceptedSave => !AcceptedSaves.IsEmpty ||
        Response.SubResponses.Any(operation => operation.RequestType == RequestTypes.PutChanges && !operation.Status);
    internal CellBridgeActor? ResponseActor { get; init; }
}

/// <summary>Executes document operations and prepares file revisions outside coordination transactions.</summary>
public sealed partial class CellBridgeDocumentService(StorageProvider provider, ICellBridgeAccessEvaluator? accessEvaluator = null,
    ICellBridgeAuthorizationPolicy? authorizationPolicy = null, ProtocolHashingOptions? hashing = null,
    IDocumentAuthorizationProvider? documentAuthorizationProvider = null, TimeProvider? authorizationTimeProvider = null)
{
    private readonly ICellBridgeAccessEvaluator? _access = accessEvaluator;
    private readonly ICellBridgeAuthorizationPolicy _policy = authorizationPolicy ?? new StoredGrantAuthorizationPolicy();
    private readonly IDocumentAuthorizationProvider? _externalAuthorization = ValidateExternalProvider(documentAuthorizationProvider, authorizationPolicy, accessEvaluator);
    private readonly TimeProvider _authorizationTime = authorizationTimeProvider ?? TimeProvider.System;
    public StorageProvider Provider => provider;
    public DocumentAccess Access(CellBridgeActor actor, DocumentState state)
    {
        if (state.IsDeleted) return DocumentAccess.None;
        if (actor.AccessLimit is { } limit && limit.ResourceId != state.ResourceId) return DocumentAccess.None;
        var access = _externalAuthorization is null ? SubjectAccess(actor, state) : AdmittedAccess(actor, state);
        if (actor.AccessLimit is { } ceiling) access &= ceiling.Access;
        return access;
    }

    public async ValueTask<DocumentState?> ResolveAsync(FssHttpRequest request, CellBridgeActor actor,
        CancellationToken cancellationToken = default)
    {
        var state = await LookupAsync(request, cancellationToken);
        if (state is not null)
        {
            actor = await AuthorizeActorAsync(actor, state, DocumentAccess.Read, cancellationToken);
            state = await RevalidateReadStateAsync(state, cancellationToken);
        }
        if (state is not null && !Access(actor, state).HasFlag(DocumentAccess.Read))
            throw new UnauthorizedAccessException("Document access denied.");
        return state;
    }

    internal ValueTask<DocumentState?> LookupAsync(FssHttpRequest request, CancellationToken cancellationToken = default,
        string applicationPathBase = "")
    {
        // When ResourceID is absent, UseResourceID enables identity in the
        // response but the required URL still identifies the initial request.
        // Once supplied, the ID is authoritative and must never fall back.
        if (request.UseResourceId && request.ResourceId is not null)
            return Guid.TryParse(request.ResourceId, out var id)
                ? provider.State.FindByResourceIdAsync(id, cancellationToken)
                : ValueTask.FromResult<DocumentState?>(null);
        return DocumentUrlPaths.TryDecode(request.Url, out var path) && DocumentUrlPaths.StripMount(ref path, applicationPathBase)
            ? provider.State.FindByPathKeyAsync(StorageIds.PathKey(path), cancellationToken)
            : ValueTask.FromResult<DocumentState?>(null);
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
        CancellationToken cancellationToken, Guid? resourceId = null, DocumentAuthorizationBinding? externalBinding = null)
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
        state = externalBinding is null ? BindNewDocument(state) : state with
            { Security = state.Security with { AuthorizationPolicy = externalBinding } };
        state = RevisionHistory.Initialize(state);
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
        bool hasAcceptedSave = false;
        var state = await CurrentAsync(id, cancellationToken);
        var operationActor = actor;
        if (request.TryGetValidationError(out var validationCode, out var validationMessage))
        {
            response.Status = true;
            response.Error = new ResponseError(ErrorType.Cell, (ulong)validationCode, validationMessage);
            return new(response, state);
        }
        if (!CellSubRequestDataValidation.TryValidate(attributes, requireBinaryDataSize: false, out _))
            return new(response, state, "InvalidArgument");
        if (!request.SubRequests.Any(x => x.RequestType == RequestTypes.PutChanges) && !MatchesEtag(attributes, state))
            return new(response, state, "CellRequestFail");
        try
        {
            // Each operation has its own publication boundary. Queries keep
            // their own knowledge/constraints and share a union of immutable payloads.
            var queries = new QueryChangesResponseAssembler(response, provider.Limits.MaxGraphBytes, request.HashOptions, hashing);
            foreach (var operation in request.SubRequests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_externalAuthorization is not null) state = await CurrentAsync(id, cancellationToken);
                operationActor = await AuthorizeActorAsync(actor, state,
                    operation.RequestType == RequestTypes.QueryAccess ? DocumentAccess.None :
                    operation.RequestType is RequestTypes.PutChanges or RequestTypes.AllocateExtendedGuidRange
                        ? DocumentAccess.Write : DocumentAccess.Read, cancellationToken, fresh: true);
                if (!CellPartitionSelector.TryResolveTarget(operation, kind, out var selectedKind))
                {
                    state = await CurrentAsync(id, cancellationToken);
                    var accessForTarget = Access(operationActor, state);
                    var requiredForTarget = operation.RequestType is RequestTypes.PutChanges or RequestTypes.AllocateExtendedGuidRange
                        ? DocumentAccess.Write : DocumentAccess.Read;
                    response.SubResponses.Add(operation.RequestType != RequestTypes.QueryAccess && !accessForTarget.HasFlag(requiredForTarget)
                        ? CellBridgeAuthorization.Denied(operation)
                        : UnsupportedSubResponse(operation.RequestId, operation.RequestType));
                    continue;
                }
                if (operation.RequestType == RequestTypes.PutChanges)
                {
                    CellExecution saved;
                    try { saved = await SaveAsync(id, selectedKind, operation, request.DataElementPackage, attributes, operationActor, queries.CanAppend, cancellationToken); }
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
                    accepted.AddRange(saved.AcceptedSaves);
                    hasAcceptedSave |= saved.HasAcceptedSave;
                    state = saved.State;
                    queries.AppendSave(saved.Response);
                    if (saved.IsCancelled) return Cancelled();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (saved.LockError is not null) return new(response, state, saved.LockError)
                        { AcceptedSaves = accepted.ToImmutable(), ResponseActor = operationActor };
                    continue;
                }
                state = await CurrentAsync(id, cancellationToken);
                var access = Access(operationActor, state);
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
                    queries.Append(query, operation.Data as QueryChangesSubRequestData);
                    continue;
                }
                if (selectedKind == DocumentPartitionKind.Metadata && operation.RequestType == RequestTypes.QueryChanges &&
                    state.Partitions.Single(p => p.Kind == 1).StorageIndex is not null)
                {
                    queries.Append(await MetadataQueryAsync(state, operation, cancellationToken), operation.Data as QueryChangesSubRequestData);
                    continue;
                }
                var observedNow = DateTime.UtcNow;
                if (selectedKind == DocumentPartitionKind.EditorsTable && operation.RequestType == RequestTypes.QueryChanges)
                {
                    (state, observedNow) = await ExpireEditorsAsync(id, operationActor, cancellationToken);
                    access = Access(operationActor, state);
                    if (!access.HasFlag(DocumentAccess.Read))
                    {
                        response.SubResponses.Add(CellBridgeAuthorization.Denied(operation));
                        continue;
                    }
                }
                switch (operation.RequestType)
                {
                    case RequestTypes.QueryChanges:
                        var document = StoredDocument.RestoreMetadata(state, observedNow);
                        queries.Append(GeneratedPartitionQuery(document, document.GetPartition(selectedKind), operation, state),
                            operation.Data as QueryChangesSubRequestData);
                        break;
                    case RequestTypes.QueryAccess:
                        response.SubResponses.Add(QueryAccessSubResponse(operation.RequestId, access));
                        break;
                    case RequestTypes.AllocateExtendedGuidRange:
                        response.SubResponses.Add(AllocateRange(operation));
                        break;
                    default:
                        response.SubResponses.Add(UnsupportedSubResponse(operation.RequestId, operation.RequestType));
                        break;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            // MS-FSSHTTP GetFileProps describes the updated version after the Cell
            // operations. Capture its metadata here; the SOAP adapter never rereads it.
            return new(response, state) { AcceptedSaves = accepted.ToImmutable(), ResponseActor = operationActor };
        }
        catch (OperationCanceledException) when (hasAcceptedSave && cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }
        catch (Exception ex) when (accepted.Count != 0 && AcceptedSaveException.CanWrap(ex))
        {
            throw AcceptedSaveException.Wrap(accepted.ToImmutable(), ex);
        }

        CellExecution Cancelled()
        {
            response.Status = true;
            response.Error = new ResponseError(ErrorType.Win32, 995, "The request was cancelled before completion.");
            return new(response, state)
            {
                AcceptedSaves = accepted.ToImmutable(), ResponseActor = operationActor, IsCancelled = true
            };
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
        CellBridgeActor actor, Func<DataElementPackage?, bool> canAppend, CancellationToken cancellationToken)
    {
        // Capture fresh authority at save entry, after any earlier request read or
        // authorization await. This request-local view also prepares file saves;
        // final publication still rechecks it under the document lock.
        var entry = kind == DocumentPartitionKind.FileContents && provider.State is IDocumentPreparationStore reads
            ? await reads.ReadPreparationAsync(id, cancellationToken)
                ?? throw new KeyNotFoundException("Document does not exist.")
            : null;
        var initial = entry?.State ?? await CurrentAsync(id, cancellationToken);
        if (!Access(actor, initial).HasFlag(DocumentAccess.Write)) return Wrap(CellBridgeAuthorization.Denied(operation), initial);
        if (kind == DocumentPartitionKind.Metadata)
            return await SaveMetadataAsync(initial, operation, package, attributes, actor, canAppend, cancellationToken);
        if (kind != DocumentPartitionKind.FileContents || operation.Data is not PutChangesSubRequestData put || package is null)
            return await FailedAsync(id, operation.RequestId, CellErrorCode.RequestNotSupported, "Only complete file partition writes are supported.", cancellationToken);
        if ((put.Flags & ~0x79) != 0 || (put.AdditionalFlagsBits & 0x38) != 0)
            return await FailedAsync(id, operation.RequestId, CellErrorCode.RequestNotSupported, "This upload mode is unsupported.", cancellationToken);
        var publicationOperationId = Guid.NewGuid();
        var atomicSave = provider.State as IAtomicDocumentSaveStore;
        if (atomicSave?.SupportsContentStore(provider.Content) != true) atomicSave = null;
        string key = $"{put.StorageIndex.Value}:{put.StorageIndex.Guid:D}";
        string digest;
        try { digest = Digest(put, package); }
        catch (InvalidDataException ex) { return await FailedAsync(id, operation.RequestId, CellErrorCode.InvalidObject, ex.Message, cancellationToken); }
        var preparation = await PrepareSaveAsync(id, (current, now) =>
        {
            if (!Access(actor, current).HasFlag(DocumentAccess.Write))
                return new StateTransition<(DocumentState State, CoordinationState Authority, string? Error)>(null,
                    (current, current.Coordination, "FileUnauthorizedAccess"));
            if (current.Receipts.Any(r => r.PartitionKind == 0 && r.OperationKey == key))
                return new StateTransition<(DocumentState State, CoordinationState Authority, string? Error)>(null, (current, current.Coordination, null));
            if (current.LifecycleGeneration != initial.LifecycleGeneration || current.Coordination.Generation != initial.Coordination.Generation)
                return new StateTransition<(DocumentState State, CoordinationState Authority, string? Error)>(null, (current, current.Coordination, "InvalidCoauthSession"));
            var metadata = StoredDocument.RestoreMetadata(current, now);
            var coordinator = FssHttpLockCoordinator.Restore(metadata, current.Coordination, now, actor.Identity);
            coordinator.ExecuteCellWrite(attributes, () => true, out _, out var error, now);
            return new StateTransition<(DocumentState State, CoordinationState Authority, string? Error)>(null, (current, coordinator.Capture(), error));
        }, cancellationToken, entry);
        if (preparation.Error == "FileUnauthorizedAccess") return Wrap(CellBridgeAuthorization.Denied(operation), preparation.State);
        if (preparation.Error is not null) return new(new FsshttpbResponse(), preparation.State, preparation.Error);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            // Preparation observed a coherent state and provider clock. The final
            // locked publication still rechecks access, authority and content.
            var before = attempt == 0 ? preparation.State : await CurrentAsync(id, cancellationToken);
            if (!Access(actor, before).HasFlag(DocumentAccess.Write)) return Wrap(CellBridgeAuthorization.Denied(operation), before);
            if (before.LifecycleGeneration != initial.LifecycleGeneration)
                return Wrap(Failure(operation.RequestId, CellErrorCode.CoherencyFailure, "The document incarnation changed."), before);
            if (before.Receipts.FirstOrDefault(r => r.PartitionKind == 0 && r.OperationKey == key) is { } prior)
                return await RepeatAsync(before, prior, digest, operation.RequestId, actor, canAppend, cancellationToken);
            if (before.Coordination.Generation != initial.Coordination.Generation)
                return Wrap(Failure(operation.RequestId, CellErrorCode.CoherencyFailure, "Write authority changed while preparing the save."), before);
            var document = StoredDocument.RestoreMetadata(before, DateTime.UtcNow);
            StorageLimits.Check("save receipts", before.Receipts.Length + 1L, provider.Limits.MaxSaveReceipts);
            var prepared = await FilePartitionSaveHandler.PrepareAsync(before, document, operation, package,
                provider.Content, provider.Limits, cancellationToken);
            var result = prepared.Response;
            if (result.Status) return Wrap(result, before);
            if (!canAppend(prepared.ResponsePackage))
                return Wrap(Failure(operation.RequestId, CellErrorCode.RequestNotSupported,
                    "The mandatory save payload exceeds the response budget or conflicts with earlier results."), before);
            var receiptResponse = new FsshttpbResponse { DataElementPackage = prepared.ResponsePackage };
            receiptResponse.SubResponses.Add(result);
            var receiptBytes = receiptResponse.ToByteArray();
            StorageLimits.Check("save receipt bytes", receiptBytes.LongLength, provider.Limits.MaxObjectBytes);
            using var pending = atomicSave is null ? null : new PendingSaveContent(provider.Limits.MaxObjectBytes);
            IContentStore contentTarget = provider.Content;
            var useAtomicSave = false;
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
                if (pending is not null)
                {
                    var addedBytes = prepared.AddedBytes;
                    var addedObjects = prepared.AddedCount;
                    var pendingBytes = checked((prepared.Repeat ? 0 : staging.Length + addedBytes) + receiptBytes.LongLength);
                    useAtomicSave = addedBytes >= 0 && addedObjects >= 0 && pendingBytes <= PendingSaveContent.MaxBytes &&
                        (prepared.Repeat ? 1 : addedObjects + 2) <= IContentBatchWriter.MaxBatchObjects;
                    if (useAtomicSave) contentTarget = pending;
                }
                if (prepared.Repeat)
                    candidate = before;
                else
                {
                    staging.Position = 0;
                    var handle = await contentTarget.WriteAsync(staging, cancellationToken);
                    var sourceFile = before.Partitions.Single(p => p.Kind == 0);
                    var file = prepared.AddedElements is null
                        ? await DocumentPartition.CaptureFileGraphAsync(sourceFile, prepared.Graph!,
                            prepared.KnowledgeSequence, handle, contentTarget, cancellationToken)
                        : await DocumentPartition.CaptureFileDeltaAsync(sourceFile, prepared.Graph!.StorageIndex,
                            prepared.AddedElements, prepared.KnowledgeSequence, handle, contentTarget, cancellationToken);
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
            using var receiptStream = new MemoryStream(receiptBytes);
            var responseHandle = await contentTarget.WriteAsync(receiptStream, cancellationToken);
            var receipt = new SaveReceipt(key, digest, candidate.ContentVersion, responseHandle, actor.Identity.Subject)
                { LifecycleGeneration = initial.LifecycleGeneration };
            PublishResult published;
            try
            {
                StateTransition<PublishResult> Publish(DocumentState current, DateTime now)
                {
                    if (!Access(actor, current).HasFlag(DocumentAccess.Write))
                        return new StateTransition<PublishResult>(null, new(current, null, "FileUnauthorizedAccess", false));
                    if (current.LifecycleGeneration != initial.LifecycleGeneration)
                        return new StateTransition<PublishResult>(null, new(current, null, "InvalidCoauthSession", false));
                    if (current.Receipts.FirstOrDefault(r => r.PartitionKind == 0 && r.OperationKey == key) is { } accepted)
                        return new StateTransition<PublishResult>(null, new(current, accepted, null, false));
                    if (!MatchesEtag(attributes, current))
                        return new StateTransition<PublishResult>(null, new(current, null, "CellRequestFail", false));
                    if (current.Coordination.Generation != initial.Coordination.Generation)
                        return new StateTransition<PublishResult>(null, new(current, null, "InvalidCoauthSession", false));
                    if (current.ContentVersion != before.ContentVersion || current.Content != before.Content)
                        return new StateTransition<PublishResult>(null, new(current, null, null, true));
                    var metadata = StoredDocument.RestoreMetadata(current, now);
                    var coordinator = FssHttpLockCoordinator.Restore(metadata, current.Coordination, now, actor.Identity);
                    if (!coordinator.ExecuteCellWrite(attributes, () => true, out _, out var error, now))
                        return new StateTransition<PublishResult>(null, new(current, null, error, false));
                    if (CoordinationFencing.Capture(preparation.Authority, coordinator.Capture()).Generation != preparation.Authority.Generation)
                        return new StateTransition<PublishResult>(null, new(current, null, "InvalidCoauthSession", false));
                    bool isNewCommit = candidate.ContentVersion != before.ContentVersion;
                    DateTime modifiedUtc = now;
                    if (isNewCommit && !TryResolveLastModifiedTime(attributes, now, out modifiedUtc))
                        return new StateTransition<PublishResult>(null, new(current, null, "InvalidArgument", false));
                    var currentMetadata = current.Partitions.Single(x => x.Kind == 1);
                    var state = metadata.CaptureCoordination(current, coordinator.Capture()) with
                    {
                        Security = current.Security with { ModifiedBy = isNewCommit ? actor.Identity : current.Security.ModifiedBy },
                        Content = candidate.Content,
                        StateVersion = checked(current.StateVersion + 1),
                        ContentVersion = candidate.ContentVersion,
                        ModifiedUtc = isNewCommit ? modifiedUtc : current.ModifiedUtc,
                        // Keep the authoritative editors partition, including updates
                        // made while file materialization/staging was in progress.
                        Partitions = candidate.Partitions.Select(p => p.Kind == 2
                            ? current.Partitions.Single(x => x.Kind == 2) with
                            {
                                Knowledge = metadata.EditorsTablePartition.KnowledgeSequence,
                                InlineContent = metadata.EditorsTablePartition.Content.ToImmutableArray()
                            }
                            : p.Kind == 1 && (!isNewCommit ||
                                currentMetadata.StorageIndex is not null || !currentMetadata.Elements.IsEmpty) ? currentMetadata
                            : p.Kind == 1 ? currentMetadata with
                            {
                                Knowledge = checked(currentMetadata.Knowledge + 1),
                                InlineContent = System.Text.Encoding.UTF8.GetBytes(
                                $"<Metadata ContentVersion=\"{candidate.ContentVersion}\" Modified=\"{modifiedUtc.Ticks}\" />").ToImmutableArray()
                            }
                            : p).ToImmutableArray(),
                        Coordination = CoordinationFencing.Capture(current.Coordination, coordinator.Capture()),
                        // Superseded retries need their digest/version, never their response bytes.
                        Receipts = current.Receipts.Select(r => r.PartitionKind != 0 || r.ContentVersion == candidate.ContentVersion
                            ? r : r with { Response = null }).Append(receipt).ToImmutableArray(),
                    };
                    if (isNewCommit)
                        state = RevisionHistory.Append(current, state, actor.Identity, modifiedUtc);
                    state = ExternalPublication.Append(current, state, publicationOperationId, provider.Limits);
                    provider.Limits.CheckDocument(state);
                    return new StateTransition<PublishResult>(state, new(state, null, null, false));
                }
                published = useAtomicSave
                    ? await atomicSave!.TransitionWithContentAsync(id, provider.Content, pending!.Contents, Publish, cancellationToken)
                    : await provider.State.TransitionAsync(id, Publish, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancellation of the commit await does not establish whether it committed.
                // Only a matching, authorized receipt can establish acceptance.
                var current = await CurrentAsync(id, CancellationToken.None);
                if (current.LifecycleGeneration != initial.LifecycleGeneration ||
                    current.Receipts.FirstOrDefault(r => r.PartitionKind == 0 && r.OperationKey == key) is not { } accepted) throw;
                var recovered = await RepeatAsync(current, accepted, digest, operation.RequestId, actor, canAppend, CancellationToken.None);
                if (!recovered.HasAcceptedSave) throw;
                return recovered with { IsCancelled = true };
            }
            catch (StorageUnavailableException)
            {
                // A lost commit response is not a rollback. Resolve the durable
                // receipt before attempting to apply this operation again.
                var current = await CurrentAsync(id, CancellationToken.None);
                if (current.LifecycleGeneration != initial.LifecycleGeneration ||
                    current.Receipts.FirstOrDefault(r => r.PartitionKind == 0 && r.OperationKey == key) is not { } accepted) throw;
                return await RepeatAsync(current, accepted, digest, operation.RequestId, actor, canAppend, CancellationToken.None);
            }
            if (published.Receipt is { } duplicate)
                return await RepeatAsync(initial, duplicate, digest, operation.RequestId, actor, canAppend, cancellationToken);
            if (published.LockError == "FileUnauthorizedAccess") return Wrap(CellBridgeAuthorization.Denied(operation), published.State);
            if (published.LockError is not null) return new(new FsshttpbResponse(), published.State, published.LockError);
            if (published.Retry) continue;
            return new CellExecution(receiptResponse, published.State)
            {
                AcceptedSaves =
                [new(id, receipt.ContentVersion, published.State.Content, key, false) { LifecycleGeneration = published.State.LifecycleGeneration }]
            };
        }
        return await FailedAsync(id, operation.RequestId, CellErrorCode.CoherencyFailure, "The document changed while preparing this save.", cancellationToken);
    }

    private async ValueTask<CellExecution> RepeatAsync(DocumentState state, SaveReceipt receipt, string digest,
        ulong requestId, CellBridgeActor actor, Func<DataElementPackage?, bool> canAppend, CancellationToken cancellationToken)
    {
        try { return await ReadSaveReceiptAsync(state, receipt, digest, requestId, actor, canAppend, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Finish only receipt verification after cancellation, never another save.
            var recovered = await ReadSaveReceiptAsync(state, receipt, digest, requestId, actor, canAppend, CancellationToken.None);
            if (!recovered.HasAcceptedSave) throw;
            return recovered with { IsCancelled = true };
        }
    }

    private async ValueTask<CellExecution> ReadSaveReceiptAsync(DocumentState state, SaveReceipt receipt, string digest,
        ulong requestId, CellBridgeActor actor, Func<DataElementPackage?, bool> canAppend, CancellationToken cancellationToken)
    {
        var generation = state.LifecycleGeneration;
        state = await provider.State.TransitionAsync(state.ResourceId, (current, _) => new StateTransition<DocumentState>(null, current), cancellationToken);
        if (state.LifecycleGeneration != generation || state.Receipts.FirstOrDefault(r => r.PartitionKind == 0 && r.OperationKey == receipt.OperationKey) is not { } authoritative ||
            authoritative.LifecycleGeneration != generation)
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
        if (state.ContentVersion != receipt.ContentVersion || receipt.LifecycleGeneration != generation)
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
        var expectedContent = state.Content;
        state = await provider.State.TransitionAsync(state.ResourceId, (current, _) =>
            new StateTransition<DocumentState>(null, current), cancellationToken);
        if (!Access(actor, state).HasFlag(DocumentAccess.Write) || receipt.OwnerSubject != actor.Identity.Subject)
            return Wrap(CellBridgeAuthorization.Denied(new(RequestTypes.PutChanges) { RequestId = requestId }), state);
        if (state.LifecycleGeneration != generation || state.ContentVersion != receipt.ContentVersion || state.Content != expectedContent ||
            state.Receipts.FirstOrDefault(r => r.PartitionKind == 0 && r.OperationKey == receipt.OperationKey) != receipt)
            return Wrap(Failure(requestId, CellErrorCode.CoherencyFailure, "The accepted revision changed during receipt loading."), state);
        response.SubResponses[0].RequestId = requestId;
        if (!canAppend(response.DataElementPackage))
            return Wrap(Failure(requestId, CellErrorCode.RequestNotSupported,
                "The mandatory retry payload exceeds the response budget or conflicts with earlier results."), state);
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

    private static bool TryResolveLastModifiedTime(IReadOnlyDictionary<string, string> attributes, DateTime fallback,
        out DateTime modifiedUtc)
    {
        modifiedUtc = fallback;
        if (!attributes.ContainsKey("LastModifiedTime")) return true;
        return CellSubRequestDataValidation.TryGetLastModifiedTime(attributes, out modifiedUtc);
    }

    private static bool MatchesEtag(IReadOnlyDictionary<string, string> attributes, DocumentState state) =>
        !attributes.TryGetValue("Etag", out string? supplied) || string.Equals(supplied, state.Etag, StringComparison.Ordinal);
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
        // Ordinary external mode reports the recorded session flag. It does not evaluate
        // all other subjects or turn their unexpired leases into permission grants.
        EditorsTablePartitionBuilder.BuildSharePointV13QueryChangesResponse(requestId,
            doc.Sessions.Select(s => new EditorsTableEditor(s.ClientId.ToString("D"), s.ExpiresUtc.Ticks,
                s.Owner?.DisplayName ?? s.UserName, s.Owner?.Login ?? s.UserName, HasEditorPermission: s.AsEditor && s.Owner is not null &&
                    (_externalAuthorization is not null || Access(new(s.Owner), state).HasFlag(DocumentAccess.Write)), Metadata: s.Metadata)), partition.FssHttpBIdentity.CellId,
            partition.FssHttpBIdentity.SerialGuid, partition.KnowledgeSequence);
    private sealed record PublishResult(DocumentState State, SaveReceipt? Receipt, string? LockError, bool Retry);
}
