using System.Collections.Immutable;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public sealed record AcceptedSave(Guid ResourceId, uint ContentVersion, DocumentPackage Package,
    string OperationKey, bool IsReplay)
{
    public long LifecycleGeneration { get; init; } = 1;
}

public sealed record CellExecution(FsshttpbResponse Response, DocumentHead State, string? LockError = null)
{
    public ImmutableArray<AcceptedSave> AcceptedSaves { get; init; } = [];
    public bool IsCancelled { get; init; }
    internal bool HasAcceptedSave => !AcceptedSaves.IsEmpty ||
        Response.SubResponses.Any(operation => operation.RequestType == RequestTypes.PutChanges && !operation.Status);
    internal CellBridgeActor? ResponseActor { get; init; }
}

/// <summary>Executes protocol operations through bounded reads and typed document publications.</summary>
public sealed partial class CellBridgeDocumentService(StorageProvider provider, ICellBridgeAccessEvaluator? accessEvaluator = null,
    ICellBridgeAuthorizationPolicy? authorizationPolicy = null, ProtocolHashingOptions? hashing = null,
    IDocumentAuthorizationProvider? documentAuthorizationProvider = null, TimeProvider? authorizationTimeProvider = null)
{
    private readonly ICellBridgeAccessEvaluator? _access = accessEvaluator;
    private readonly ICellBridgeAuthorizationPolicy _policy = authorizationPolicy ?? new StoredGrantAuthorizationPolicy();
    private readonly IDocumentAuthorizationProvider? _externalAuthorization = ValidateExternalProvider(documentAuthorizationProvider, authorizationPolicy, accessEvaluator);
    private readonly TimeProvider _authorizationTime = authorizationTimeProvider ?? TimeProvider.System;
    public StorageProvider Provider => provider;

    public DocumentAccess Access(CellBridgeActor actor, DocumentHead state)
    {
        if (state.IsDeleted || actor.AccessLimit is { } limit && limit.ResourceId != state.ResourceId) return DocumentAccess.None;
        var access = _externalAuthorization is null ? SubjectAccess(actor, state) : AdmittedAccess(actor, state);
        return actor.AccessLimit is { } ceiling ? access & ceiling.Access : access;
    }

    internal DocumentActorAdmission Admission(CellBridgeActor actor, DocumentHead head) =>
        new(actor.Identity, head.ResourceId, head.LifecycleGeneration, head.Security.AuthorizationPolicy, Access(actor, head))
        { AuthorityGeneration = head.AuthorityGeneration, StateVersion = head.StateVersion };

    public async ValueTask<DocumentHead?> ResolveAsync(FssHttpRequest request, CellBridgeActor actor,
        CancellationToken cancellationToken = default)
    {
        var state = await LookupAsync(request, cancellationToken);
        if (state is null) return null;
        actor = await AuthorizeActorAsync(actor, state, DocumentAccess.Read, cancellationToken);
        state = await RevalidateReadStateAsync(state, cancellationToken);
        if (state is not null) Demand(actor, state, DocumentAccess.Read);
        return state;
    }

    internal ValueTask<DocumentHead?> LookupAsync(FssHttpRequest request, CancellationToken cancellationToken = default,
        string applicationPathBase = "")
    {
        if (request.UseResourceId && request.ResourceId is not null)
            return Guid.TryParse(request.ResourceId, out var id)
                ? provider.Documents.FindAsync(new DocumentIdentity.Resource(id), cancellationToken)
                : ValueTask.FromResult<DocumentHead?>(null);
        return DocumentUrlPaths.TryDecode(request.Url, out var path) && DocumentUrlPaths.StripMount(ref path, applicationPathBase)
            ? provider.Documents.FindAsync(new DocumentIdentity.Path(StorageIds.PathKey(path)), cancellationToken)
            : ValueTask.FromResult<DocumentHead?>(null);
    }

    public ValueTask<DocumentHead?> CreateAsync(string escapedPath, byte[] bytes, CellBridgeActor actor, CancellationToken cancellationToken = default) =>
        CreateAsync(Guid.NewGuid(), escapedPath, bytes, actor, cancellationToken);

    public ValueTask<DocumentHead?> CreateAsync(Guid resourceId, string escapedPath, byte[] bytes,
        CellBridgeActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.CanCreate) throw new UnauthorizedAccessException("Document creation denied.");
        return CreateOwnedAsync(escapedPath, bytes, DocumentSecurity.Create(actor.Identity), cancellationToken, resourceId);
    }

    public ValueTask<DocumentHead?> ImportAsync(string escapedPath, byte[] bytes, SubjectIdentity owner,
        CellBridgeActor importer, CancellationToken cancellationToken = default) =>
        ImportAsync(Guid.NewGuid(), escapedPath, bytes, owner, importer, cancellationToken);

    public ValueTask<DocumentHead?> ImportAsync(Guid resourceId, string escapedPath, byte[] bytes, SubjectIdentity owner,
        CellBridgeActor importer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(importer);
        if (!importer.CanCreate) throw new UnauthorizedAccessException("Document import denied.");
        return CreateOwnedAsync(escapedPath, bytes, DocumentSecurity.Create(owner, importer.Identity), cancellationToken, resourceId);
    }

    private async ValueTask<DocumentHead?> CreateOwnedAsync(string escapedPath, byte[] bytes, DocumentSecurity security,
        CancellationToken cancellationToken, Guid? resourceId = null, DocumentAuthorizationBinding? externalBinding = null)
    {
        var id = resourceId ?? Guid.NewGuid();
        if (id == Guid.Empty) throw new ArgumentException("A resource identifier is required.", nameof(resourceId));
        var path = DocumentStore.NormalizeUrl(escapedPath);
        if (await provider.Documents.FindAsync(new DocumentIdentity.Resource(id, IncludeDeleted: true), cancellationToken) is not null ||
            await provider.Documents.FindAsync(new DocumentIdentity.Path(StorageIds.PathKey(path)), cancellationToken) is not null) return null;
        var binding = externalBinding ?? (_externalAuthorization is null ? _policy.BindNewDocument(id) :
            throw new InvalidOperationException("External documents require an explicit trusted scope."));
        security = security with { AuthorizationPolicy = binding };
        var prepared = await IndexedDocumentPublicationBuilder.CreateAsync(id, path, bytes, security, provider.Content, provider.Limits, cancellationToken);
        if (externalBinding is null && ResolvePolicy(prepared.Head) is null)
            throw new UnauthorizedAccessException("Document authorization could not be initialized.");
        var result = await provider.Documents.CreateAsync(prepared, cancellationToken);
        return result.Created ? result.Current : null;
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
                state = await CurrentAsync(id, cancellationToken);
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
                    try { saved = await SaveAsync(state, selectedKind, operation, request.DataElementPackage, attributes, operationActor, queries.CanAppend, cancellationToken); }
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
                if (operation.RequestType == RequestTypes.QueryChanges)
                {
                    var query = await QueryPartitionAsync(id, selectedKind, operation, operationActor, cancellationToken);
                    state = query.State;
                    queries.Append(query.Response, operation.Data as QueryChangesSubRequestData);
                    continue;
                }
                switch (operation.RequestType)
                {
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


    private ValueTask<DocumentHead> CurrentAsync(Guid id, CancellationToken cancellationToken) => GetCurrentAsync(id, cancellationToken);
    private async ValueTask<DocumentHead> GetCurrentAsync(Guid id, CancellationToken cancellationToken) =>
        await provider.Documents.FindAsync(new DocumentIdentity.Resource(id), cancellationToken) ?? throw MissingDocument();
    private static CellExecution Wrap(FsshttpbSubResponse response, DocumentHead state)
    {
        var result = new FsshttpbResponse(); result.SubResponses.Add(response); return new(result, state);
    }
    private static FsshttpbSubResponse Failure(ulong requestId, CellErrorCode error, string message, RequestTypes requestType = RequestTypes.PutChanges) => new()
    {
        RequestId = requestId, RequestType = requestType, Status = true,
        Error = new ResponseError(ErrorType.Cell, (ulong)error, message),
    };
    private void Demand(CellBridgeActor actor, DocumentHead head, DocumentAccess required)
    {
        if (!Access(actor, head).HasFlag(required)) throw new UnauthorizedAccessException("Document access denied.");
    }
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

    private static bool MatchesEtag(IReadOnlyDictionary<string, string> attributes, DocumentHead state) =>
        !attributes.TryGetValue("Etag", out string? supplied) || string.Equals(supplied, state.Etag, StringComparison.Ordinal);
}
