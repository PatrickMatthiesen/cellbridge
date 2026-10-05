using System.Globalization;
using System.Runtime.CompilerServices;
using System.Collections.Immutable;
using CellBridge.Storage.Abstractions;
using CellBridge.FssHttp;
using CellBridge.Storage;

namespace CellBridge.AspNetCore;

/// <summary>Stateful MS-FSSHTTP schema and exclusive lock coordinator.</summary>
public sealed class FssHttpLockCoordinator
{
    private static readonly ConditionalWeakTable<StoredDocument, FssHttpLockCoordinator> States = new();
    private readonly object _gate = new();
    private readonly StoredDocument _document;
    private readonly HashSet<string> _coauthors = new(StringComparer.OrdinalIgnoreCase);
    private bool _transitionPending;

    private FssHttpLockCoordinator(StoredDocument document) => _document = document;
    private string? _schemaLockId;
    private readonly Dictionary<string, Lease> _schemaOwners = new(StringComparer.OrdinalIgnoreCase);
    private Lease? _exclusive;
    private long _generation;
    private HostLease? _hostLock;
    private DateTime? _authoritativeNow;
    private SubjectIdentity? _actor;

    public static FssHttpLockCoordinator Restore(StoredDocument document, CoordinationState state, DateTime? now = null, SubjectIdentity? actor = null)
    {
        var coordinator = For(document, actor);
        coordinator._schemaLockId = state.SchemaId;
        coordinator._generation = state.Generation;
        coordinator._hostLock = state.HostLock;
        coordinator._authoritativeNow = now;
        coordinator._schemaOwners.Clear();
        foreach (var owner in state.SchemaOwners)
        {
            var lease = RestoreLease(owner);
            coordinator._schemaOwners.Add(lease.Client!, lease);
        }
        coordinator._exclusive = state.Exclusive is null ? null : RestoreLease(state.Exclusive);
        coordinator._coauthors.Clear();
        foreach (var client in state.CoauthorClients.IsDefault ? [] : state.CoauthorClients)
            if (coordinator._schemaOwners.ContainsKey(CanonicalizeId(client)))
                coordinator._coauthors.Add(CanonicalizeId(client));
        coordinator._transitionPending = state.CoauthorTransitionPending && coordinator._coauthors.Count != 0;
        return coordinator;
    }

    public CoordinationState Capture()
    {
        lock (_gate)
            return new(_schemaLockId, _schemaOwners.Values.Select(CaptureLease).ToImmutableArray(),
                _exclusive is null ? null : CaptureLease(_exclusive), _generation)
            {
                HostLock = _hostLock,
                CoauthorClients = _coauthors.Order(StringComparer.Ordinal).ToImmutableArray(),
                CoauthorTransitionPending = _transitionPending,
            };
    }

    private static Lease RestoreLease(LeaseState state) => new(state.Id, state.Client, state.ExpiresUtc, (LockKind)state.Kind, state.SchemaId, state.OwnerSubject);
    private static LeaseState CaptureLease(Lease state) => new(state.Id, state.Client, state.ExpiresUtc, (int)state.Kind, state.SchemaId, state.OwnerSubject);

    public static FssHttpLockCoordinator For(StoredDocument document, SubjectIdentity? actor = null)
    {
        var coordinator = States.GetValue(document, static doc => new FssHttpLockCoordinator(doc));
        if (actor is not null) coordinator._actor = actor;
        return coordinator;
    }

    private LockOperationResult Denied(FssHttpSubResponse response)
    {
        response.ErrorCode = "FileUnauthorizedAccess";
        response.HResult = CellBridge.AspNetCore.CellBridgeAuthorization.AccessDeniedHResult.ToString(CultureInfo.InvariantCulture);
        return LockOperationResult.AccessDenied;
    }

    /// <summary>Processes SchemaLockRequestType (GetLock, ReleaseLock, RefreshLock, ConvertToExclusive, CheckLockAvailability).</summary>
    public LockOperationResult ApplySchemaLock(FssHttpSubRequest request, FssHttpSubResponse response, DateTime? now = null)
    {
        var attrs = request.SubRequestDataAttributes;
        if (!TryRead(attrs, "SchemaLockRequestType", out var operation))
            return Fail(response, LockOperationResult.InvalidArgument, "SchemaLockRequestType is required");
        if (!TryRead(attrs, "SchemaLockID", out var schemaId))
            return Fail(response, LockOperationResult.InvalidArgument, "SchemaLockID is required");

        var instant = now ?? _authoritativeNow ?? DateTime.UtcNow;
        lock (_gate)
        {
            var client = ReadClient(attrs);
            if (_actor is null || client is not null && _schemaOwners.TryGetValue(client, out var existingOwner)
                && existingOwner.ExpiresUtc > instant && existingOwner.OwnerSubject != _actor.Subject)
                return Denied(response);
            ExpireLocked(instant);
            switch (operation)
            {
                case "GetLock":
                    if (client is null)
                        return Fail(response, LockOperationResult.InvalidArgument, "ClientID is required");
                    if (_exclusive is not null)
                        return Fail(response, LockOperationResult.Conflict, "FileAlreadyLockedOnServer");
                    if (_schemaLockId is not null && !Same(_schemaLockId, schemaId))
                        return Fail(response, LockOperationResult.Conflict, "FileAlreadyLockedOnServer");
                    _schemaLockId ??= schemaId;
                    var refreshed = _schemaOwners.ContainsKey(client);
                    _schemaOwners[client] = new Lease(schemaId, client,
                        instant.AddSeconds(ReadTimeout(attrs)), LockKind.Schema, OwnerSubject: _actor.Subject);
                    response.SubResponseDataAttributes["LockType"] = "SchemaLock";
                    return refreshed ? LockOperationResult.Refreshed : LockOperationResult.Granted;

                case "RefreshLock":
                    if (client is null || !Same(_schemaLockId, schemaId) || !_schemaOwners.ContainsKey(client))
                        return Fail(response, LockOperationResult.Conflict, "InvalidCoauthSession");
                    _schemaOwners[client] = new Lease(schemaId, client,
                        instant.AddSeconds(ReadTimeout(attrs)), LockKind.Schema, OwnerSubject: _actor.Subject);
                    response.SubResponseDataAttributes["LockType"] = "SchemaLock";
                    return LockOperationResult.Refreshed;

                case "ReleaseLock":
                    if (client is null || !Same(_schemaLockId, schemaId) || !_schemaOwners.Remove(client))
                        return Fail(response, LockOperationResult.Conflict, "FileAlreadyLockedOnServer");
                    RemoveCoauthor(client);
                    if (_schemaOwners.Count == 0)
                        _schemaLockId = null;
                    response.SubResponseDataAttributes["LockType"] = "SchemaLock";
                    return LockOperationResult.Released;

                case "CheckLockAvailability":
                    if (_exclusive is not null || (_schemaLockId is not null && !Same(_schemaLockId, schemaId)))
                        return Fail(response, LockOperationResult.Conflict, "FileAlreadyLockedOnServer");
                    response.SubResponseDataAttributes["LockType"] = "SchemaLock";
                    return LockOperationResult.Observed;

                case "ConvertToExclusive":
                    return ConvertSchemaToExclusive(attrs, schemaId, instant, response);

                default:
                    return Fail(response, LockOperationResult.InvalidArgument,
                        $"Unsupported SchemaLockRequestType '{operation}'");
            }
        }
    }

    /// <summary>Processes ExclusiveLockRequestType using the exact request-type attribute.</summary>
    public LockOperationResult ApplyExclusiveLock(FssHttpSubRequest request, FssHttpSubResponse response, DateTime? now = null)
    {
        var attrs = request.SubRequestDataAttributes;
        if (!TryRead(attrs, "ExclusiveLockRequestType", out var operation))
            return Fail(response, LockOperationResult.InvalidArgument, "ExclusiveLockRequestType is required");
        if (!TryRead(attrs, "ExclusiveLockID", out var lockId))
            return Fail(response, LockOperationResult.InvalidArgument, "ExclusiveLockID is required");

        var instant = now ?? _authoritativeNow ?? DateTime.UtcNow;
        lock (_gate)
        {
            if (_actor is null || _exclusive is { } active && active.ExpiresUtc > instant &&
                Same(active.Id, lockId) && active.OwnerSubject != _actor.Subject)
                return Denied(response);
            ExpireLocked(instant);
            switch (operation)
            {
                case "GetLock":
                    if (_exclusive is not null && !Same(_exclusive.Id, lockId))
                        return Fail(response, LockOperationResult.Conflict, "FileAlreadyLockedOnServer");
                    if (_schemaOwners.Count != 0)
                        return Fail(response, LockOperationResult.Conflict, "FileAlreadyLockedOnServer");
                    var refreshed = _exclusive is not null;
                    _exclusive = new Lease(lockId, ReadClient(attrs),
                        instant.AddSeconds(ReadTimeout(attrs)), LockKind.Exclusive, OwnerSubject: _actor.Subject);
                    return refreshed ? LockOperationResult.Refreshed : LockOperationResult.Granted;

                case "RefreshLock":
                    if (_exclusive is null || !Same(_exclusive.Id, lockId))
                        return Fail(response, LockOperationResult.Conflict, "InvalidCoauthSession");
                    _exclusive = _exclusive with { ExpiresUtc = instant.AddSeconds(ReadTimeout(attrs)) };
                    return LockOperationResult.Refreshed;

                case "ReleaseLock":
                    if (_exclusive is null || !Same(_exclusive.Id, lockId))
                        return Fail(response, LockOperationResult.Conflict, "FileAlreadyLockedOnServer");
                    _exclusive = null;
                    return LockOperationResult.Released;

                case "CheckLockAvailability":
                    if ((_exclusive is not null && !Same(_exclusive.Id, lockId)) || _schemaOwners.Count != 0)
                        return Fail(response, LockOperationResult.Conflict, "FileAlreadyLockedOnServer");
                    return LockOperationResult.Observed;

                case "ConvertToSchema":
                case "ConvertToSchemaJoinCoauth":
                    if (!TryRead(attrs, "SchemaLockID", out var schemaId) || ReadClient(attrs) is not { } client ||
                        !TryTransitionTimeout(attrs, coauth: false, out var timeout))
                        return Fail(response, LockOperationResult.InvalidArgument, "SchemaLockID, ClientID and valid Timeout are required");
                    if (_schemaOwners.Count != 0)
                        return Fail(response, LockOperationResult.Conflict, "FileAlreadyLockedOnServer");
                    if (_exclusive is null)
                        return Fail(response, LockOperationResult.Conflict, "FileNotLockedOnServer");
                    if (!Same(_exclusive.Id, lockId))
                        return Fail(response, LockOperationResult.Conflict, "FileAlreadyLockedOnServer");
                    if (operation == "ConvertToSchemaJoinCoauth" && Guid.TryParse(client, out var editorClient) &&
                        _document.GetSession(editorClient) is { } editor && editor.Owner?.Subject != _actor.Subject)
                        return Denied(response);
                    _exclusive = null;
                    _schemaLockId = schemaId;
                    _schemaOwners[client] = new Lease(schemaId, client, instant.AddSeconds(timeout),
                        LockKind.Schema, OwnerSubject: _actor.Subject);
                    if (operation == "ConvertToSchemaJoinCoauth")
                    {
                        JoinCoauthor(client, timeout);
                        SetCoauthStatus(response, includeTransition: true);
                    }
                    return LockOperationResult.Completed;

                default:
                    return Fail(response, LockOperationResult.InvalidArgument,
                        $"Unsupported ExclusiveLockRequestType '{operation}'");
            }
        }
    }

    /// <summary>LockStatus uses the protocol's numeric LockTypes: 1 shared, 2 exclusive.</summary>
    public LockOperationResult ApplyLockStatus(FssHttpSubRequest request, FssHttpSubResponse response, DateTime? now = null)
    {
        lock (_gate)
        {
            ExpireLocked(now ?? _authoritativeNow ?? DateTime.UtcNow);
            if (_exclusive is not null)
            {
                response.SubResponseDataAttributes["LockType"] = "2";
                response.SubResponseDataAttributes["LockID"] = _exclusive.Id;
                if (_exclusive.Client is not null)
                    response.SubResponseDataAttributes["LockedBy"] = _exclusive.Client;
            }
            else if (_schemaOwners.Count != 0)
            {
                response.SubResponseDataAttributes["LockType"] = "1";
                response.SubResponseDataAttributes["LockID"] = _schemaLockId!;
                response.SubResponseDataAttributes["LockedBy"] = _schemaOwners.Keys.First();
            }
            else
            {
                response.SubResponseDataAttributes["LockType"] = "0";
            }
            return LockOperationResult.Observed;
        }
    }

    /// <summary>AmIAlone takes the JoinCoauthoring TransitionID, not a ClientID.</summary>
    public LockOperationResult ApplyAmIAlone(StoredDocument document, FssHttpSubRequest request, FssHttpSubResponse response)
    {
        if (!TryRead(request.SubRequestDataAttributes, "TransitionID", out var transition) ||
            !Guid.TryParse(transition, out var id) || id != document.TransitionId)
            return Fail(response, LockOperationResult.InvalidArgument, "TransitionID is invalid");
        lock (_gate)
        {
            ExpireLocked(_authoritativeNow ?? DateTime.UtcNow);
            response.SubResponseDataAttributes["AmIAlone"] = !_transitionPending && _coauthors.Count == 1 ? "True" : "False";
            return LockOperationResult.Observed;
        }
    }

    /// <summary>Applies conversion and acknowledgement using explicit coauthor membership.</summary>
    public LockOperationResult ApplyCoauthTransition(FssHttpSubRequest request, FssHttpSubResponse response, DateTime? now = null)
    {
        var attrs = request.SubRequestDataAttributes;
        if (!TryRead(attrs, "CoauthRequestType", out var operation) ||
            !TryRead(attrs, "SchemaLockID", out var schemaId) || ReadClient(attrs) is not { } client)
            return Fail(response, LockOperationResult.InvalidArgument, "CoauthRequestType, SchemaLockID and ClientID are required");
        var instant = now ?? _authoritativeNow ?? DateTime.UtcNow;
        lock (_gate)
        {
            if (_actor is null || _schemaOwners.TryGetValue(client, out var owner) &&
                owner.ExpiresUtc > instant && owner.OwnerSubject != _actor.Subject)
                return Denied(response);
            ExpireLocked(instant);
            if (!Same(_schemaLockId, schemaId) || !_schemaOwners.ContainsKey(client) || !_coauthors.Contains(client))
                return Fail(response, LockOperationResult.Conflict, "InvalidCoauthSession");
            if (operation == "MarkTransitionComplete")
            {
                _transitionPending = false;
                return LockOperationResult.Completed;
            }
            if (operation != "ConvertToExclusive")
                return Fail(response, LockOperationResult.InvalidArgument, "Unsupported CoauthRequestType");
            if (!TryTransitionTimeout(attrs, coauth: true, out var timeout) ||
                !TryBoolean(attrs, "ReleaseLockOnConversionToExclusiveFailure", required: true, out _) ||
                !TryRead(attrs, "ExclusiveLockID", out _))
                return Fail(response, LockOperationResult.InvalidArgument, "Conversion requires Timeout, ExclusiveLockID and a valid release flag");
            return ConvertSchemaToExclusive(attrs, schemaId, instant, response, timeout);
        }
    }

    /// <summary>Synchronizes Join, Refresh, Exit, and status operations with the shared schema lock.</summary>
    public LockOperationResult ApplyCoauthSession(StoredDocument document,
        FssHttpSubRequest request, FssHttpSubResponse response, DateTime? now = null)
    {
        var attrs = request.SubRequestDataAttributes;
        if (!TryRead(attrs, "CoauthRequestType", out var operation) ||
            ReadClient(attrs) is not { } clientText ||
            !TryRead(attrs, "SchemaLockID", out _))
            return Fail(response, LockOperationResult.InvalidArgument,
                "CoauthRequestType, ClientID, and SchemaLockID are required");

        var instant = now ?? _authoritativeNow ?? DateTime.UtcNow;
        lock (_gate)
        {
            if (_actor is null || Guid.TryParse(clientText, out var client) &&
                document.GetSession(client) is { } existing && existing.Owner?.Subject != _actor.Subject)
                return Denied(response);
            ExpireLocked(instant);
            switch (operation)
            {
                case "JoinCoauthoring":
                {
                    var result = ApplySchemaLock(CopyRequest(request, "SchemaLockRequestType", "GetLock"), response, instant);
                    if (result is not (LockOperationResult.Granted or LockOperationResult.Refreshed))
                        return result;
                    JoinCoauthor(clientText, ReadTimeout(attrs));
                    response.SubResponseDataAttributes["LockType"] = "SchemaLock";
                    SetCoauthStatus(response, includeTransition: true);
                    return result;
                }
                case "RefreshCoauthoring":
                {
                    // An expired tracker entry refreshes through Join. Legacy states
                    // acquire explicit membership only on this coauthoring request.
                    _schemaOwners.TryGetValue(clientText, out var previous);
                    var result = ApplySchemaLock(CopyRequest(request, "SchemaLockRequestType", "GetLock"), response, instant);
                    if (result is not (LockOperationResult.Granted or LockOperationResult.Refreshed)) return result;
                    if (previous is not null && previous.ExpiresUtc > _schemaOwners[clientText].ExpiresUtc)
                        _schemaOwners[clientText] = _schemaOwners[clientText] with { ExpiresUtc = previous.ExpiresUtc };
                    JoinCoauthor(clientText, Math.Max(ReadTimeout(attrs),
                        (int)Math.Ceiling((_schemaOwners[clientText].ExpiresUtc - instant).TotalSeconds)));
                    response.SubResponseDataAttributes["LockType"] = "SchemaLock";
                    SetCoauthStatus(response, includeTransition: false);
                    return result;
                }
                case "ExitCoauthoring":
                {
                    var result = ApplySchemaLock(CopyRequest(request, "SchemaLockRequestType", "ReleaseLock"), response, instant);
                    if (result != LockOperationResult.Released)
                        return result;
                    return result;
                }
                case "GetCoauthoringStatus":
                    response.SubResponseDataAttributes["LockType"] = "SchemaLock";
                    SetCoauthStatus(response, includeTransition: true);
                    return LockOperationResult.Observed;
                case "CheckLockAvailability":
                    return ApplySchemaLock(CopyRequest(request, "SchemaLockRequestType", "CheckLockAvailability"), response, instant);
                case "ConvertToExclusive":
                case "MarkTransitionComplete":
                    return ApplyCoauthTransition(request, response, instant);
                default:
                    return Fail(response, LockOperationResult.InvalidArgument, $"Unsupported CoauthRequestType '{operation}'");
            }
        }
    }

    /// <summary>
    /// Executes one cell write while holding this document's lock state gate.
    ///
    /// A write is allowed when no protocol lock is active. Once a schema or
    /// exclusive lock exists, the caller must present the corresponding lock
    /// ID (or a matching BypassLockID); a schema lock may also present its
    /// owning ClientID. The callback is
    /// invoked under the same gate as lock transitions so a release/refresh
    /// cannot race the write decision or the write itself.
    /// </summary>
    /// <returns><see langword="true"/> when the callback ran.</returns>
    public bool ExecuteCellWrite<T>(
        IReadOnlyDictionary<string, string> attrs,
        Func<T> operation,
        out T? result,
        out string? errorCode,
        DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(attrs);
        ArgumentNullException.ThrowIfNull(operation);

        lock (_gate)
        {
            var instant = now ?? _authoritativeNow ?? DateTime.UtcNow;
            if (_actor is null || _exclusive is { } owned && owned.ExpiresUtc > instant && owned.OwnerSubject != _actor.Subject)
                return RejectCellWrite(out result, out errorCode, "FileUnauthorizedAccess");
            if (_schemaOwners.Values.Any(l => l.ExpiresUtc > instant))
            {
                var client = ReadClient(attrs);
                if (!_schemaOwners.Values.Any(l => l.ExpiresUtc > instant && l.OwnerSubject == _actor.Subject &&
                    (client is null || Same(l.Client, client))))
                    return RejectCellWrite(out result, out errorCode, "FileUnauthorizedAccess");
            }
            ExpireLocked(instant);
            if (_hostLock is not null) return RejectCellWrite(out result, out errorCode, "FileAlreadyLockedOnServer");
            if (_exclusive is not null)
            {
                if (!HasMatchingLockId(attrs, _exclusive.Id, "ExclusiveLockID"))
                    return RejectCellWrite(out result, out errorCode,
                        "FileAlreadyLockedOnServer");

                if (_exclusive.Client is not null &&
                    TryRead(attrs, "ClientID", out var exclusiveClient) &&
                    !EquivalentId(_exclusive.Client, exclusiveClient))
                    return RejectCellWrite(out result, out errorCode,
                        "InvalidCoauthSession");
            }
            else if (_schemaLockId is not null)
            {
                if (!HasMatchingLockId(attrs, _schemaLockId, "SchemaLockID"))
                    return RejectCellWrite(out result, out errorCode,
                        "FileAlreadyLockedOnServer");

                // Word's binary PutChanges identity may be carried by the
                // schema ID alone. When ClientID is present, enforce it too.
                if (TryRead(attrs, "ClientID", out var clientId) &&
                    !_schemaOwners.Keys.Any(owner => EquivalentId(owner, clientId)))
                    return RejectCellWrite(out result, out errorCode,
                        "InvalidCoauthSession");
            }

            result = operation();
            errorCode = null;
            return true;
        }
    }

    private LockOperationResult ConvertSchemaToExclusive(IReadOnlyDictionary<string, string> attrs,
        string schemaId, DateTime instant, FssHttpSubResponse response, int? timeout = null)
    {
        if (!TryBoolean(attrs, "ReleaseLockOnConversionToExclusiveFailure", required: false, out var release))
            return Fail(response, LockOperationResult.InvalidArgument, "Invalid release flag");
        if (ReadClient(attrs) is not { } client || !TryRead(attrs, "ExclusiveLockID", out var exclusiveId))
            return Fail(response, LockOperationResult.InvalidArgument, "ClientID and ExclusiveLockID are required");
        if (_exclusive is not null && !Same(_exclusive.Id, exclusiveId))
            return Fail(response, LockOperationResult.Conflict, "InvalidCoauthSession");
        if (!Same(_schemaLockId, schemaId) || !_schemaOwners.ContainsKey(client))
            return Fail(response, LockOperationResult.Conflict, "InvalidCoauthSession");
        if (_schemaOwners.Keys.Any(x => !Same(x, client)))
        {
            if (release)
            {
                _schemaOwners.Remove(client);
                RemoveCoauthor(client);
            }
            return Fail(response, LockOperationResult.Conflict,
                release ? "ExitCoauthSessionAsConvertToExclusiveFailed" : "MultipleClientsInCoauthSession");
        }

        _schemaOwners.Clear();
        _schemaLockId = null;
        RemoveCoauthor(client);
        _exclusive = new Lease(exclusiveId, client,
            instant.AddSeconds(timeout ?? ReadTimeout(attrs)), LockKind.Exclusive, schemaId, _actor!.Subject);
        response.SubResponseDataAttributes["LockType"] = "ExclusiveLock";
        return LockOperationResult.Completed;
    }

    private void ExpireLocked(DateTime now)
    {
        if (_hostLock is not null && _hostLock.ExpiresUtc <= now) _hostLock = null;
        foreach (var key in _schemaOwners.Where(pair => pair.Value.ExpiresUtc <= now).Select(pair => pair.Key).ToArray())
        {
            _schemaOwners.Remove(key);
            _coauthors.Remove(key);
        }
        if (_coauthors.Count == 0) _transitionPending = false;
        if (_schemaOwners.Count == 0)
            _schemaLockId = null;
        if (_exclusive is not null && _exclusive.ExpiresUtc <= now)
            _exclusive = null;
    }

    private static LockOperationResult Fail(FssHttpSubResponse response, LockOperationResult result, string message)
    {
        response.ErrorCode = message switch
        {
            "FileAlreadyLockedOnServer" => "FileAlreadyLockedOnServer",
            "FileNotLockedOnServer" => "FileNotLockedOnServer",
            "MultipleClientsInCoauthSession" => "MultipleClientsInCoauthSession",
            "InvalidCoauthSession" => "InvalidCoauthSession",
            "ExitCoauthSessionAsConvertToExclusiveFailed" => "ExitCoauthSessionAsConvertToExclusiveFailed",
            _ when result == LockOperationResult.NotSupported => "NotSupported",
            _ => "InvalidArgument",
        };
        return result;
    }

    private static bool TryRead(IReadOnlyDictionary<string, string> attrs, string key, out string value) =>
        attrs.TryGetValue(key, out value!) && !string.IsNullOrWhiteSpace(value);

    private static bool RejectCellWrite<T>(out T? result, out string? errorCode, string code)
    {
        result = default;
        errorCode = code;
        return false;
    }

    private static bool HasMatchingLockId(
        IReadOnlyDictionary<string, string> attrs,
        string activeId,
        string primaryKey)
    {
        bool supplied = false;
        foreach (var key in new[] { primaryKey, "BypassLockID" })
        {
            if (!TryRead(attrs, key, out var value))
                continue;
            supplied = true;
            if (!EquivalentId(activeId, value))
                return false;
        }

        return supplied;
    }

    private static bool EquivalentId(string left, string right)
    {
        if (Guid.TryParse(left, out var leftGuid) && Guid.TryParse(right, out var rightGuid))
            return leftGuid == rightGuid;
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadClient(IReadOnlyDictionary<string, string> attrs) =>
        TryRead(attrs, "ClientID", out var id) ? CanonicalizeId(id) : null;

    private static string CanonicalizeId(string value) =>
        Guid.TryParse(value, out var id) ? id.ToString("D") : value;

    private static bool TryBoolean(IReadOnlyDictionary<string, string> attrs, string key, bool required, out bool result)
    {
        result = false;
        if (!TryRead(attrs, key, out var value)) return !required;
        if (value == "1" || value == "true") { result = true; return true; }
        return value is "0" or "false";
    }

    private static bool TryTransitionTimeout(IReadOnlyDictionary<string, string> attrs, bool coauth, out int timeout)
    {
        timeout = 0;
        if (!TryRead(attrs, "Timeout", out var value) || !int.TryParse(value, NumberStyles.None,
            CultureInfo.InvariantCulture, out timeout) || timeout < 60 || timeout > 120000) return false;
        if (coauth && timeout < 3600) timeout = CoauthSession.DefaultTimeoutSeconds;
        return true;
    }

    private void JoinCoauthor(string client, int timeout)
    {
        int previous = _coauthors.Count;
        if (_coauthors.Add(client) && previous == 1) _transitionPending = true;
        if (Guid.TryParse(client, out var id))
            _document.JoinEditingSession(id, timeout, asEditor: true, userName: _actor!.Login, owner: _actor);
    }

    private void RemoveCoauthor(string client)
    {
        if (_coauthors.Remove(client) && Guid.TryParse(client, out var id)) _document.LeaveSession(id);
        if (_coauthors.Count == 0) _transitionPending = false;
    }

    private void SetCoauthStatus(FssHttpSubResponse response, bool includeTransition)
    {
        response.SubResponseDataAttributes["CoauthStatus"] = _coauthors.Count <= 1 ? "Alone" : "Coauthoring";
        if (includeTransition) response.SubResponseDataAttributes["TransitionID"] = _document.TransitionId.ToString("D");
    }

    private static int ReadTimeout(IReadOnlyDictionary<string, string> attrs) =>
        TryRead(attrs, "Timeout", out var value) && int.TryParse(value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var timeout)
            ? Math.Clamp(timeout, 1, 24 * 60 * 60)
            : CoauthSession.DefaultTimeoutSeconds;

    private static FssHttpSubRequest CopyRequest(FssHttpSubRequest source, string key, string value)
    {
        var copy = new FssHttpSubRequest();
        foreach (var pair in source.SubRequestDataAttributes)
            copy.SubRequestDataAttributes[pair.Key] = pair.Value;
        copy.SubRequestDataAttributes[key] = value;
        return copy;
    }

    // FSSHTTP lock identifiers are GUID-valued tokens. SOAP serializers are
    // allowed to vary the textual representation (case, braces and hyphens)
    // between requests, so compare parsed GUIDs before falling back to an
    // ordinal token comparison for non-GUID identifiers.
    private static bool Same(string? left, string? right) =>
        left is not null && right is not null && EquivalentId(left, right);

    private sealed record Lease(string Id, string? Client, DateTime ExpiresUtc, LockKind Kind, string? SchemaId = null, string? OwnerSubject = null);
    private enum LockKind { Schema, Exclusive }
}

public enum LockOperationResult
{
    Granted, Refreshed, Released, Completed, Observed, Conflict, NotFound, InvalidArgument, NotSupported, AccessDenied,
}
