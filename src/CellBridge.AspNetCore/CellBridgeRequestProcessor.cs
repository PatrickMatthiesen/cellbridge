using System.Collections.Immutable;
using System.Text;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CellBridge.AspNetCore;

/// <summary>Protocol response and accepted save receipts produced while executing a parsed SOAP request.</summary>
public sealed record CellStorageExecution(CellStorageResponse Response, ImmutableArray<AcceptedSave> AcceptedSaves);

/// <summary>Executes parsed MS-FSSHTTP requests without an HTTP endpoint or transport dependency.</summary>
public sealed class CellBridgeRequestProcessor(CellBridgeDocumentService service,
    ILogger<CellBridgeRequestProcessor>? logger = null)
{
    /// <summary>Executes against existing documents, preserving authorization, dependencies and durable publication.</summary>
    public async Task<CellStorageExecution> ExecuteAsync(CellStorageRequest request, string publicOrigin,
        CellBridgeActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        if (!Uri.TryCreate(publicOrigin, UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https") || origin.UserInfo.Length != 0 ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.AbsolutePath != "/" ||
            publicOrigin != publicOrigin.Trim() || publicOrigin.Contains('\\') ||
            (publicOrigin.IndexOf('/', publicOrigin.IndexOf("://", StringComparison.Ordinal) + 3) is var slash &&
                slash >= 0 && slash != publicOrigin.Length - 1))
            throw new ArgumentException("An HTTP(S) origin without credentials, path, query or fragment is required.", nameof(publicOrigin));
        var log = logger ?? NullLogger<CellBridgeRequestProcessor>.Instance;
        var accepted = ImmutableArray.CreateBuilder<AcceptedSave>();
        // WebUrl must be same-origin with the request ([MS-FSSHTTP] 2.2.3.6).
        var response = new CellStorageResponse
        {
            // SharePoint answers Word's 2.2 request with the highest response
            // revision it supports. The captured editable session uses 2.3.
            Version = request.Version,
            MinorVersion = request.Version == 2 ? 3u : request.MinorVersion,
            UsesDirectBody = request.UsesDirectBody,
            WebUrl = publicOrigin,
        };

        if (request.Version != 2 || request.MinorVersion > ushort.MaxValue)
        {
            response.Version = 2;
            response.MinorVersion = 0;
            response.VersionErrorCode = "IncompatibleVersion";
            response.VersionErrorMessage = "The requested MS-FSSHTTP version is not supported.";
            return new(response, []);
        }

        if (Preflight(request) is { } structuralError)
        {
            response.Responses.Add(structuralError);
            return new(response, []);
        }

        try
        {
            foreach (var fileRequest in request.Requests)
            {
                var initial = await service.LookupAsync(fileRequest, cancellationToken);
                var doc = initial is null ? null : StoredDocument.RestoreMetadata(initial, DateTime.UtcNow);
                if (doc is null)
                {
                    response.Responses.Add(new FssHttpResponse
                    {
                        Url = fileRequest.Url,
                        RequestToken = fileRequest.RequestToken,
                        ErrorCode = "FileNotExistsOrCannotBeCreated",
                        ErrorMessage = "The requested file does not exist and cannot be created by this operation.",
                    });
                    continue;
                }

                // Every subrequest, including lock release, targets the resolved file.
                var canRead = service.Access(actor, initial!).HasFlag(DocumentAccess.Read);
                var fileResponse = new FssHttpResponse
                {
                    Url = canRead ? DocumentRequestResolver.CanonicalUrl(doc, publicOrigin) : fileRequest.Url,
                    RequestToken = fileRequest.RequestToken,
                    IntervalOverride = 0,
                    ResourceId = canRead ? doc.TransitionId : null,
                };
                if (!canRead)
                {
                    fileResponse.ErrorCode = "FileUnauthorizedAccess";
                    fileResponse.ErrorMessage = "The current user is not authorized to read this file.";
                }

                foreach (var subRequest in fileRequest.SubRequests)
                {
                    var subResponse = new FssHttpSubResponse
                    {
                        Type = subRequest.Type,
                        SubRequestToken = subRequest.SubRequestToken,
                        ErrorCode = "Success",
                        HResult = "0",
                    };

                    try
                    {
                        if (FssHttpDependencies.Error(subRequest, fileResponse.SubResponses) is { } dependencyError)
                        {
                            subResponse.ErrorCode = dependencyError;
                            subResponse.HResult = "2147500037";
                            subResponse.EmitEmptySubResponseData = true;
                        }
                        else if (initial!.LifecycleGeneration > 1 &&
                            !(fileRequest.UseResourceId && Guid.TryParse(fileRequest.ResourceId, out var requestedId) && requestedId == initial.ResourceId) &&
                            (subRequest.Type == SubRequestType.Cell || CellBridgeAuthorization.RequiredAccess(subRequest).HasFlag(DocumentAccess.Write)))
                        {
                            subResponse.ErrorCode = "InvalidCoauthSession";
                            subResponse.HResult = "2147500037";
                        }
                        else if (subRequest.Type == SubRequestType.Cell)
                        {
                            await HandleCellSubRequest(service, doc.TransitionId, subRequest, subResponse, log, actor, accepted, cancellationToken);
                        }
                        else if (OuterDocumentOperations.Handles(subRequest.Type))
                            await OuterDocumentOperations.ExecuteAsync(service, doc.TransitionId, subRequest, subResponse,
                                publicOrigin, actor, cancellationToken);
                        else
                            await service.Provider.State.TransitionAsync(doc.TransitionId, (current, now) =>
                            {
                                var access = service.Access(actor, current);
                                if (!access.HasFlag(CellBridgeAuthorization.RequiredAccess(subRequest)) || !OwnsSession(current, subRequest, actor, now))
                                {
                                    subResponse.ErrorCode = "FileUnauthorizedAccess";
                                    subResponse.HResult = CellBridgeAuthorization.AccessDeniedHResult.ToString();
                                    return new StateTransition<bool>(null, false);
                                }
                                var document = StoredDocument.RestoreMetadata(current, now);
                                var coordinator = FssHttpLockCoordinator.Restore(document, current.Coordination, now, actor.Identity);
                                var store = new DocumentStore();
                                store.Attach(document);
                                ApplyMetadata(document, store, new FssHttpRequest { Url = document.Url }, subRequest, subResponse, log, publicOrigin, actor);
                                if (subResponse.ErrorCode == "FileUnauthorizedAccess") return new StateTransition<bool>(null, false);
                                var next = document.CaptureCoordination(current, coordinator.Capture());
                                if (System.Text.Json.JsonSerializer.Serialize(next) == System.Text.Json.JsonSerializer.Serialize(current))
                                    return new StateTransition<bool>(null, true);
                                next = next with { Coordination = CoordinationFencing.Capture(current.Coordination, next.Coordination) };
                                return new StateTransition<bool>(next, true);
                            }, cancellationToken);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or KeyNotFoundException)
                    {
                        if (ex is AcceptedSaveException partial) accepted.AddRange(partial.AcceptedSaves);
                        log.LogError(ex, "Storage operation failed for resource {ResourceId}", doc.TransitionId);
                        subResponse = new FssHttpSubResponse
                        {
                            Type = subRequest.Type,
                            SubRequestToken = subRequest.SubRequestToken,
                            ErrorCode = "CellRequestFail",
                            ErrorMessage = "The server could not complete the Cell request.",
                            HResult = "2147500037",
                        };
                    }

                    fileResponse.SubResponses.Add(subResponse);
                }

                if (await service.Provider.State.FindByResourceIdAsync(doc.TransitionId, cancellationToken) is { } latest &&
                    service.Access(actor, latest).HasFlag(DocumentAccess.Read))
                    fileResponse.Url = DocumentRequestResolver.CanonicalUrl(StoredDocument.RestoreMetadata(latest, DateTime.UtcNow), publicOrigin);
                response.Responses.Add(fileResponse);
            }

            return new(response, accepted.ToImmutable());
        }
        catch (Exception ex) when (accepted.Count != 0 && AcceptedSaveException.CanWrap(ex))
        {
            throw AcceptedSaveException.Wrap(accepted.ToImmutable(), ex);
        }
    }

    private static FssHttpResponse RequestError(FssHttpRequest request, string code, string message)
    {
        var response = new FssHttpResponse
        {
            Url = request.Url,
            RequestToken = request.RequestToken,
            ErrorCode = code,
            ErrorMessage = message,
        };
        foreach (var subRequest in request.SubRequests)
            response.SubResponses.Add(new()
            {
                Type = subRequest.Type,
                SubRequestToken = subRequest.SubRequestToken,
                ErrorCode = code,
                HResult = "2147942487",
            });
        return response;
    }

    private static FssHttpResponse? Preflight(CellStorageRequest request)
    {
        var requestTokens = new HashSet<ulong>();
        foreach (var fileRequest in request.Requests)
        {
            if (string.IsNullOrEmpty(fileRequest.Url) || fileRequest.RequestToken > uint.MaxValue ||
                !requestTokens.Add(fileRequest.RequestToken) || fileRequest.SubRequests.Count == 0)
                return RequestError(fileRequest, "InvalidArgument",
                    "The request URL, token, or subrequest collection is invalid.");
            var subRequestTokens = new HashSet<ulong>();
            foreach (var subRequest in fileRequest.SubRequests)
            {
                if (ValidateSubRequest(subRequest, subRequestTokens) is { } error)
                    return RequestError(fileRequest, error, "The subrequest structure or scalar attributes are invalid.");
            }
        }
        return null;
    }

    private static string? ValidateSubRequest(FssHttpSubRequest request, HashSet<ulong> seenTokens)
    {
        if (request.SubRequestToken is not { } token || token > uint.MaxValue)
            return "InvalidArgument";
        if (!seenTokens.Add(token)) return "InvalidRequestDependencyType";
        if ((request.DependsOn is null) != (request.DependencyType is null) || request.DependsOn > uint.MaxValue ||
            request.DependsOn == token || request.DependencyType is not null and not
                ("OnExecute" or "OnSuccess" or "OnFail" or "OnNotSupported" or "OnSuccessOrNotSupported"))
            return "InvalidRequestDependencyType";
        if (!Enum.IsDefined(request.Type)) return "InvalidSubRequest";

        bool hasData = request.SubRequestDataXml is not null || request.SubRequestDataBinaryMemory is not null ||
            request.SubRequestDataAttributes.Count != 0;
        if (request.Type is SubRequestType.WhoAmI or SubRequestType.ServerTime or SubRequestType.GetDocMetaInfo or
            SubRequestType.GetVersions or SubRequestType.LockStatus)
            return hasData ? "InvalidArgument" : null;
        if (request.Type != SubRequestType.Cell && !hasData) return "InvalidArgument";
        if (request.Type == SubRequestType.Cell && hasData &&
            !CellSubRequestDataValidation.TryValidate(request.SubRequestDataAttributes,
                requireBinaryDataSize: true, out _)) return "InvalidArgument";
        return null;
    }

    static bool OwnsSession(DocumentState state, FssHttpSubRequest request, CellBridgeActor actor, DateTime now)
    {
        if (request.Type is not (SubRequestType.EditorsTable or SubRequestType.Coauth)) return true;
        if (!Guid.TryParse(request.SubRequestDataAttributes.GetValueOrDefault("ClientID"), out var client)) return true;
        var session = state.Editors.FirstOrDefault(e => e.ClientId == client && e.ExpiresUtc > now);
        return session is null || session.Owner?.Subject == actor.Identity.Subject;
    }

    /// <summary>
    /// Handles a Cell subrequest: decodes the FSSHTTPB binary payload from the
    /// SubRequestData and produces the matching binary response.
    ///
    /// Word's Cell subrequest flow is: QueryAccess → QueryChanges (download) →
    /// PutChanges (upload). We answer QueryAccess with access granted and
    /// QueryChanges with a full data element graph carrying the file content.
    /// </summary>
    static async Task<CellExecution?> HandleCellSubRequest(
        CellBridgeDocumentService service,
        Guid resourceId,
        FssHttpSubRequest subRequest,
        FssHttpSubResponse subResponse,
        ILogger log, CellBridgeActor actor, ImmutableArray<AcceptedSave>.Builder accepted, CancellationToken cancellationToken)
    {

        if (!TryResolvePartition(subRequest, out var partitionKind))
        {
            subResponse.ErrorCode = "InvalidArgument";
            subResponse.HResult = "2147942487";
            log.LogWarning("Unsupported Cell partition: token={Token} partitionId={PartitionId}",
                subRequest.SubRequestToken,
                subRequest.SubRequestDataAttributes.GetValueOrDefault("PartitionID", "<default>"));
            return null;
        }

        bool hasPayload = subRequest.SubRequestDataBinaryMemory is not null || subRequest.SubRequestDataXml is not null ||
            subRequest.SubRequestDataAttributes.ContainsKey("IncludeHref");
        if (!hasPayload)
        {
            subResponse.EmitEmptySubResponseData = true;
            return null;
        }

        if (!subRequest.SubRequestDataAttributes.TryGetValue("BinaryDataSize", out var binarySizeText) ||
            !CellSubRequestDataValidation.TryParseXmlInt64(binarySizeText, out long declaredSize) || declaredSize < 1)
        {
            subResponse.ErrorCode = "InvalidArgument";
            subResponse.HResult = "2147942487";
            return null;
        }


        // Try to decode the FSSHTTPB request payload if one was provided.
        FsshttpbCellRequest? fsshttpbRequest = null;
        if (subRequest.SubRequestDataAttributes.TryGetValue("IncludeHref", out var href) &&
            href.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
        {
            // MTOM part reference; the raw binary would arrive as a MIME part.
            // For non-MTOM requests the payload is inline base64 in SubRequestData.
        }

        if (subRequest.SubRequestDataBinaryMemory is not null)
        {
            if (subRequest.SubRequestDataBinaryMemory.Value.Length != declaredSize)
            {
                subResponse.ErrorCode = "InvalidArgument";
                subResponse.HResult = "2147942487";
                return null;
            }
            fsshttpbRequest = TryDecodeFsshttpbBinary(subRequest.SubRequestDataBinaryMemory.Value, log);
        }

        if (fsshttpbRequest is null && subRequest.SubRequestDataXml is not null)
        {
            fsshttpbRequest = TryDecodeFsshttpbPayload(subRequest.SubRequestDataXml, declaredSize, log);
        }

        if (fsshttpbRequest is null)
        {
            subResponse.ErrorCode = "CellRequestFail";
            subResponse.HResult = "2147500037";
            var invalid = new FsshttpbResponse
            {
                Status = true,
                Error = new ResponseError(ErrorType.Cell, (ulong)CellErrorCode.RequestStreamSchemaError,
                    "The binary Cell request is malformed."),
            };
            subResponse.SubResponseDataBase64 = invalid.ToByteArray(FsshttpbSerializationProfile.SharePoint13_11);
            return null;
        }
        var execution = await service.ExecuteAsync(resourceId, partitionKind, fsshttpbRequest,
            subRequest.SubRequestDataAttributes, actor, cancellationToken);
        accepted.AddRange(execution.AcceptedSaves);
        if (execution.LockError is not null)
        {
            subResponse.ErrorCode = execution.LockError;
            return execution;
        }
        var fsshttpbResponse = execution.Response;
        if (service.Access(actor, execution.State).HasFlag(DocumentAccess.Read) &&
            CellSubRequestDataValidation.TryGetBoolean(subRequest.SubRequestDataAttributes, "GetFileProps", out bool getFileProps) && getFileProps)
        {
            subResponse.SubResponseDataAttributes["Etag"] = execution.State.Etag;
            subResponse.SubResponseDataAttributes["CreateTime"] = execution.State.CreatedUtc.ToFileTimeUtc().ToString();
            subResponse.SubResponseDataAttributes["LastModifiedTime"] = execution.State.ModifiedUtc.ToFileTimeUtc().ToString();
            subResponse.SubResponseDataAttributes["ModifiedBy"] = execution.State.Security.ModifiedBy?.Login ?? "unknown";
        }
        subResponse.SubResponseDataBase64 = fsshttpbResponse.ToByteArray(
            FsshttpbSerializationProfile.SharePoint13_11);
        if (log.IsEnabled(LogLevel.Debug))
        {
            var inspection = FsshttpbResponseInspector.Inspect(subResponse.SubResponseDataBase64);
            log.LogDebug(
                "Cell response dump token={Token} partition={Partition}:{NewLine}{Dump}",
                subRequest.SubRequestToken,
                partitionKind,
                Environment.NewLine,
                inspection.ToCanonicalText());
        }
        log.LogInformation("Cell response token={Token} partition={Partition} bytes={Bytes}",
            subRequest.SubRequestToken, partitionKind, subResponse.SubResponseDataBase64.Length);
        return execution;
    }

    /// <summary>Maps a SOAP Cell selector to the corresponding FSSHTTPB partition.</summary>
    static bool TryResolvePartition(FssHttpSubRequest subRequest, out DocumentPartitionKind partitionKind)
    {
        return CellPartitionSelector.TryResolve(subRequest.SubRequestDataAttributes, out partitionKind);
    }

    /// <summary>
    /// Handles the SOAP EditorsTable operations that maintain the state returned
    /// by the binary editors-table partition. [MS-FSSHTTP] section 3.1.4.8.
    /// Successful responses contain an empty SubResponseData element; all input
    /// validation failures are returned as protocol errors without that element.
    /// </summary>
    static void HandleEditorsTableSubRequest(
        CellBridgeActor actor,
        DocumentStore store,
        FssHttpRequest fileRequest,
        FssHttpSubRequest subRequest,
        FssHttpSubResponse subResponse,
        ILogger log)
    {
        var attrs = subRequest.SubRequestDataAttributes;
        string requestType = attrs.GetValueOrDefault("EditorsTableRequestType", string.Empty);
        if (requestType.Length == 0 ||
            !attrs.TryGetValue("ClientID", out var clientIdText) ||
            !Guid.TryParse(clientIdText, out var clientId))
        {
            subResponse.ErrorCode = "InvalidArgument";
            subResponse.EmitEmptySubResponseData = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(fileRequest.Url))
        {
            subResponse.ErrorCode = "FileNotExistsOrCannotBeCreated";
            return;
        }

        var doc = store.Get(fileRequest.Url)!;

        switch (requestType)
        {
            case "JoinEditingSession":
                if (!TryParseEditorsTimeout(attrs, out var joinTimeout) ||
                    !TryParseXmlBoolean(attrs, "AsEditor", out var joinAsEditor))
                {
                    subResponse.ErrorCode = "InvalidArgument";
                    return;
                }

                doc.JoinEditingSession(clientId, joinTimeout, joinAsEditor, actor.Identity.Login, actor.Identity);
                log.LogInformation("EditorsTable join: client={Client} asEditor={AsEditor} timeout={Timeout} editors={Count}",
                    clientId, joinAsEditor, joinTimeout, doc.Sessions.Count);
                break;

            case "LeaveEditingSession":
                // Leave is intentionally idempotent. SharePoint treats a stale
                // leave as successful, which also makes retries safe for Word.
                doc.LeaveSession(clientId);
                log.LogInformation("EditorsTable leave: client={Client} editors={Count}",
                    clientId, doc.Sessions.Count);
                break;

            case "RefreshEditingSession":
                if (!TryParseEditorsTimeout(attrs, out var refreshTimeout) ||
                    !TryParseXmlBoolean(attrs, "AsEditor", out var refreshAsEditor))
                {
                    subResponse.ErrorCode = "InvalidArgument";
                    return;
                }

                if (!doc.RefreshEditingSession(clientId, refreshTimeout, refreshAsEditor))
                {
                    subResponse.ErrorCode = "EditorClientIdNotFound";
                    return;
                }

                log.LogInformation("EditorsTable refresh: client={Client} timeout={Timeout} editors={Count}",
                    clientId, refreshTimeout, doc.Sessions.Count);
                break;

            case "UpdateEditorMetadata":
                if (!TryGetEditorMetadataKey(attrs, out var updateKey) ||
                    !TryGetEditorMetadataValue(subRequest, out var updateValue))
                {
                    subResponse.ErrorCode = "InvalidArgument";
                    return;
                }

                lock (doc)
                {
                    var updateSession = doc.GetSession(clientId);
                    if (updateSession is null)
                    {
                        subResponse.ErrorCode = "EditorClientIdNotFound";
                        return;
                    }

                    // Check and update atomically when clients send parallel requests.
                    if (!updateSession.Metadata.ContainsKey(updateKey) && updateSession.Metadata.Count >= 4)
                    {
                        subResponse.ErrorCode = "EditorMetadataQuotaReached";
                        return;
                    }

                    doc.UpdateEditorMetadata(clientId, updateKey, updateValue);
                }
                log.LogInformation("EditorsTable metadata update: client={Client} key={Key} bytes={Bytes}",
                    clientId, updateKey, updateValue.Length);
                break;

            case "RemoveEditorMetadata":
                if (!TryGetEditorMetadataKey(attrs, out var removeKey))
                {
                    subResponse.ErrorCode = "InvalidArgument";
                    return;
                }

                if (!doc.RemoveEditorMetadata(clientId, removeKey))
                {
                    subResponse.ErrorCode = "EditorClientIdNotFound";
                    return;
                }

                log.LogInformation("EditorsTable metadata remove: client={Client} key={Key}",
                    clientId, removeKey);
                break;

            default:
                subResponse.ErrorCode = "InvalidSubRequest";
                return;
        }

        subResponse.ErrorCode = "Success";
        subResponse.EmitEmptySubResponseData = true;
    }

    static bool TryParseEditorsTimeout(
        IReadOnlyDictionary<string, string> attrs,
        out int timeoutSeconds)
    {
        timeoutSeconds = 0;
        if (!attrs.TryGetValue("Timeout", out var timeoutText) ||
            !CellSubRequestDataValidation.TryParseXmlInt64(timeoutText, out long requested) ||
            requested < 60 || requested > 120000)
        {
            return false;
        }

        // The specification permits 60..3600 as an input range but requires the
        // server to replace it with its implementation default. Use 3600.
        timeoutSeconds = requested < 3600 ? 3600 : (int)requested;
        return true;
    }

    static bool TryParseXmlBoolean(
        IReadOnlyDictionary<string, string> attrs,
        string name,
        out bool value)
    {
        value = false;
        if (!attrs.TryGetValue(name, out var text))
        {
            return false;
        }

        return CellSubRequestDataValidation.TryParseXmlBoolean(text, out value);
    }

    static bool TryGetEditorMetadataKey(
        IReadOnlyDictionary<string, string> attrs,
        out string key)
    {
        key = string.Empty;
        if (!attrs.TryGetValue("Key", out var supplied) || string.IsNullOrEmpty(supplied))
        {
            return false;
        }

        if (Encoding.UTF8.GetByteCount(supplied) > 64)
        {
            return false;
        }

        try
        {
            if (System.Xml.XmlConvert.VerifyName(supplied) != supplied)
            {
                return false;
            }
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }

        key = supplied;
        return true;
    }

    static bool TryGetEditorMetadataValue(FssHttpSubRequest subRequest, out byte[] value)
    {
        value = Array.Empty<byte>();
        string? text = null;
        if (subRequest.SubRequestDataXml is not null)
        {
            try
            {
                var element = System.Xml.Linq.XElement.Parse(subRequest.SubRequestDataXml);
                text = element.Value;
            }
            catch (System.Xml.XmlException)
            {
                return false;
            }
        }

        // Value is represented as base64 text by the SOAP schema. Accepting the
        // Value attribute as well keeps the handler compatible with clients that
        // materialize the optional attribute instead of element text.
        if (subRequest.SubRequestDataAttributes.TryGetValue("Value", out var valueAttribute))
        {
            text = valueAttribute;
        }

        if (text is null)
        {
            return false;
        }

        try
        {
            value = Convert.FromBase64String(text.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        return value.Length <= 1024;
    }

    /// <summary>
    /// Attempts to decode a base64 FSSHTTPB payload from the SubRequestData XML.
    /// Returns null when no payload is present or it cannot be decoded.
    /// </summary>
    static FsshttpbCellRequest? TryDecodeFsshttpbPayload(string subRequestDataXml, long declaredSize, ILogger log)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(subRequestDataXml);
            string? text = doc.Root?.Value;
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            byte[] bytes = Convert.FromBase64String(text.Trim());
            if (bytes.LongLength != declaredSize) return null;
            return TryDecodeFsshttpbBinary(bytes, log);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or EndOfStreamException or System.Xml.XmlException)
        {
            log.LogWarning(ex, "Inline FSSHTTPB payload decode failed");
            return null;
        }
    }

    static FsshttpbCellRequest? TryDecodeFsshttpbBinary(ReadOnlyMemory<byte> payload, ILogger log)
    {
        try
        {
            var reader = new BinaryReaderEx(payload);
            var request = FsshttpbCellRequest.Deserialize(reader);
            if (reader.Remaining != 0)
                throw new InvalidDataException($"FSSHTTPB request has {reader.Remaining} trailing bytes.");
            return request;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException)
        {
            log.LogWarning(ex, "Binary FSSHTTPB payload decode failed ({Length} bytes)", payload.Length);
            return null;
        }
    }

    /// <summary>
    /// Handles a Coauth subrequest (join/exit/refresh coauthoring session, etc.).
    /// [MS-FSSHTTP] sections 2.3.1.5, 2.3.1.6, 2.3.3.3.
    /// </summary>
    static void HandleCoauthSubRequest(
        DocumentStore store,
        FssHttpRequest fileRequest,
        FssHttpSubRequest subRequest,
        FssHttpSubResponse subResponse,
        ILogger log)
    {
        var doc = store.Get(fileRequest.Url)!;
        var result = FssHttpLockCoordinator.For(doc)
            .ApplyCoauthSession(doc, subRequest, subResponse);
        log.LogInformation("Coauth operation {Operation} for {Url}: result={Result} editors={Count}",
            subRequest.SubRequestDataAttributes.GetValueOrDefault("CoauthRequestType", "<missing>"),
            fileRequest.Url, result, doc.Sessions.Count);
    }

    /// <summary>
    /// Handles a SchemaLock subrequest: grants a shared schema lock.
    /// [MS-FSSHTTP] section 2.3.1.13.
    /// </summary>
    static void HandleSchemaLockSubRequest(
        DocumentStore store,
        FssHttpRequest fileRequest,
        FssHttpSubRequest subRequest,
        FssHttpSubResponse subResponse,
        ILogger log)
    {
        var doc = store.Get(fileRequest.Url)!;
        var result = FssHttpLockCoordinator.For(doc)
            .ApplySchemaLock(subRequest, subResponse);
        log.LogInformation("SchemaLock operation {Operation} for {Url}: result={Result}",
            subRequest.SubRequestDataAttributes.GetValueOrDefault("SchemaLockRequestType", "<missing>"),
            fileRequest.Url, result);
    }

    static void ApplyMetadata(StoredDocument doc, DocumentStore store, FssHttpRequest fileRequest,
        FssHttpSubRequest subRequest, FssHttpSubResponse subResponse, ILogger log, string publicOrigin, CellBridgeActor actor)
    {
        switch (subRequest.Type)
        {
            case SubRequestType.Cell:
                throw new InvalidOperationException("Cell execution is handled asynchronously.");

            case SubRequestType.Coauth:
                HandleCoauthSubRequest(store, fileRequest, subRequest, subResponse, log);
                break;

            case SubRequestType.SchemaLock:
                HandleSchemaLockSubRequest(store, fileRequest, subRequest, subResponse, log);
                break;

            case SubRequestType.WhoAmI:
                subResponse.SubResponseDataAttributes["UserName"] = actor.Identity.DisplayName;
                subResponse.SubResponseDataAttributes["UserLogin"] = actor.Identity.Login;
                break;

            case SubRequestType.ServerTime:
                subResponse.SubResponseDataAttributes["ServerTime"] =
                    DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;

            case SubRequestType.GetDocMetaInfo:
                subResponse.SubResponseDataXml = MetadataVersioningResponseBuilder
                    .BuildGetDocMetaInfo(doc).SubResponseDataXml;
                break;

            case SubRequestType.GetVersions:
                subResponse.SubResponseXml = MetadataVersioningResponseBuilder
                    .BuildGetVersions(doc, publicOrigin).SubResponseXml;
                break;

            case SubRequestType.ExclusiveLock:
                FssHttpLockCoordinator.For(doc).ApplyExclusiveLock(subRequest, subResponse);
                break;

            case SubRequestType.LockStatus:
                FssHttpLockCoordinator.For(doc).ApplyLockStatus(subRequest, subResponse);
                break;

            case SubRequestType.AmIAlone:
                FssHttpLockCoordinator.For(doc).ApplyAmIAlone(doc, subRequest, subResponse);
                break;

            case SubRequestType.EditorsTable:
                HandleEditorsTableSubRequest(actor, store, fileRequest, subRequest, subResponse, log);
                break;

            default:
                // Other subrequest types are not yet implemented; report
                // a protocol error rather than a malformed success.
                subResponse.ErrorCode = "RequestNotSupported";
                break;
        }


    }

}
