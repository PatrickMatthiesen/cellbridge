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
    private string? _schemaLockId;
    private readonly Dictionary<string, Lease> _schemaOwners = new(StringComparer.OrdinalIgnoreCase);
    private Lease? _exclusive;
    private long _generation;
    private DateTime? _authoritativeNow;
    private SubjectIdentity? _actor;

    public static FssHttpLockCoordinator Restore(StoredDocument document, CoordinationState state, DateTime? now = null, SubjectIdentity? actor = null)
    {
        var coordinator = For(document, actor);
        coordinator._schemaLockId = state.SchemaId;
        coordinator._generation = state.Generation;
        coordinator._authoritativeNow = now;
        coordinator._schemaOwners.Clear();
        foreach (var owner in state.SchemaOwners)
        {
            var lease = RestoreLease(owner);
            coordinator._schemaOwners.Add(lease.Client!, lease);
        }
        coordinator._exclusive = state.Exclusive is null ? null : RestoreLease(state.Exclusive);
        return coordinator;
    }

    public CoordinationState Capture() => new(_schemaLockId,
        _schemaOwners.Values.Select(CaptureLease).ToImmutableArray(),
        _exclusive is null ? null : CaptureLease(_exclusive), _generation);

    private static Lease RestoreLease(LeaseState state) => new(state.Id, state.Client, state.ExpiresUtc, (LockKind)state.Kind, state.SchemaId, state.OwnerSubject);
    private static LeaseState CaptureLease(Lease state) => new(state.Id, state.Client, state.ExpiresUtc, (int)state.Kind, state.SchemaId, state.OwnerSubject);

    public static FssHttpLockCoordinator For(StoredDocument document, SubjectIdentity? actor = null)
    {
        var coordinator = States.GetValue(document, static _ => new FssHttpLockCoordinator());
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
                    response.SubResponseDataAttributes["CoauthStatus"] = "Alone";
                    response.SubResponseDataAttributes["TransitionID"] = Guid.NewGuid().ToString("D");
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
                    if (_exclusive is null || !Same(_exclusive.Id, lockId) ||
                        !TryRead(attrs, "SchemaLockID", out var schemaId) || ReadClient(attrs) is not { } client)
                        return Fail(response, LockOperationResult.Conflict, "InvalidCoauthSession");
                    return Fail(response, LockOperationResult.NotSupported,
                        "Exclusive lock conversion to schema is not implemented");

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
        response.SubResponseDataAttributes["AmIAlone"] = document.Sessions.Count == 1 ? "True" : "False";
        return LockOperationResult.Observed;
    }

    /// <summary>Applies only the stateful ConvertToExclusive and MarkTransitionComplete Coauth operations.</summary>
    public LockOperationResult ApplyCoauthTransition(FssHttpSubRequest request, FssHttpSubResponse response, DateTime? now = null)
    {
        var attrs = request.SubRequestDataAttributes;
        if (!TryRead(attrs, "CoauthRequestType", out var operation))
            return Fail(response, LockOperationResult.InvalidArgument, "CoauthRequestType is required");
        if (operation.Equals("ConvertToExclusive", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("MarkTransitionComplete", StringComparison.OrdinalIgnoreCase))
            return Fail(response, LockOperationResult.NotSupported,
                $"Coauth transition '{operation}' is not implemented");

        return Fail(response, LockOperationResult.InvalidArgument,
            $"Unsupported CoauthRequestType '{operation}'");
    }

    /// <summary>Synchronizes Join, Refresh, Exit, and status operations with the shared schema lock.</summary>
    public LockOperationResult ApplyCoauthSession(StoredDocument document,
        FssHttpSubRequest request, FssHttpSubResponse response, DateTime? now = null)
    {
        var attrs = request.SubRequestDataAttributes;
        if (!TryRead(attrs, "CoauthRequestType", out var operation) ||
            ReadClient(attrs) is not { } clientText || !Guid.TryParse(clientText, out var client) ||
            !TryRead(attrs, "SchemaLockID", out _))
            return Fail(response, LockOperationResult.InvalidArgument,
                "CoauthRequestType, ClientID, and SchemaLockID are required");

        var instant = now ?? _authoritativeNow ?? DateTime.UtcNow;
        lock (_gate)
        {
        if (_actor is null || document.GetSession(client) is { } existing && existing.Owner?.Subject != _actor.Subject)
            return Denied(response);
        switch (operation)
        {
            case "JoinCoauthoring":
            {
                var result = ApplySchemaLock(CopyRequest(request, "SchemaLockRequestType", "GetLock"), response, instant);
                if (result is not (LockOperationResult.Granted or LockOperationResult.Refreshed))
                    return result;
                document.JoinEditingSession(client, ReadTimeout(attrs), asEditor: true, userName: _actor.Login, owner: _actor);
                response.SubResponseDataAttributes["LockType"] = "SchemaLock";
                response.SubResponseDataAttributes["CoauthStatus"] = document.Sessions.Count == 1 ? "Alone" : "Coauthoring";
                response.SubResponseDataAttributes["TransitionID"] = document.TransitionId.ToString("D");
                return result;
            }
            case "RefreshCoauthoring":
            {
                var result = ApplySchemaLock(CopyRequest(request, "SchemaLockRequestType", "RefreshLock"), response, instant);
                if (result != LockOperationResult.Refreshed ||
                    !document.RefreshEditingSession(client, ReadTimeout(attrs), asEditor: true))
                    return Fail(response, LockOperationResult.Conflict, "InvalidCoauthSession");
                response.SubResponseDataAttributes["LockType"] = "SchemaLock";
                response.SubResponseDataAttributes["CoauthStatus"] = document.Sessions.Count == 1 ? "Alone" : "Coauthoring";
                return result;
            }
            case "ExitCoauthoring":
            {
                var result = ApplySchemaLock(CopyRequest(request, "SchemaLockRequestType", "ReleaseLock"), response, instant);
                if (result != LockOperationResult.Released)
                    return result;
                document.LeaveSession(client);
                return result;
            }
            case "GetCoauthoringStatus":
                response.SubResponseDataAttributes["LockType"] = "SchemaLock";
                response.SubResponseDataAttributes["CoauthStatus"] = document.Sessions.Count <= 1 ? "Alone" : "Coauthoring";
                response.SubResponseDataAttributes["TransitionID"] = document.TransitionId.ToString("D");
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
        string schemaId, DateTime instant, FssHttpSubResponse response)
    {
        if (ReadClient(attrs) is not { } client || !TryRead(attrs, "ExclusiveLockID", out var exclusiveId))
            return Fail(response, LockOperationResult.InvalidArgument, "ClientID and ExclusiveLockID are required");
        if (_exclusive is not null && !Same(_exclusive.Id, exclusiveId))
            return Fail(response, LockOperationResult.Conflict, "InvalidCoauthSession");
        if (!Same(_schemaLockId, schemaId) || !_schemaOwners.ContainsKey(client))
            return Fail(response, LockOperationResult.Conflict, "InvalidCoauthSession");
        if (_schemaOwners.Keys.Any(x => !Same(x, client)))
        {
            if (ReadBoolean(attrs, "ReleaseLockOnConversionToExclusiveFailure"))
                _schemaOwners.Remove(client);
            return Fail(response, LockOperationResult.Conflict,
                ReadBoolean(attrs, "ReleaseLockOnConversionToExclusiveFailure")
                    ? "ExitCoauthSessionAsConvertToExclusiveFailed" : "FileAlreadyLockedOnServer");
        }

        _schemaOwners.Clear();
        _schemaLockId = null;
        _exclusive = new Lease(exclusiveId, client,
            instant.AddSeconds(ReadTimeout(attrs)), LockKind.Exclusive, schemaId, _actor!.Subject);
        response.SubResponseDataAttributes["LockType"] = "ExclusiveLock";
        return LockOperationResult.Completed;
    }

    private void ExpireLocked(DateTime now)
    {
        foreach (var key in _schemaOwners.Where(pair => pair.Value.ExpiresUtc <= now).Select(pair => pair.Key).ToArray())
            _schemaOwners.Remove(key);
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

    private static bool ReadBoolean(IReadOnlyDictionary<string, string> attrs, string key) =>
        TryRead(attrs, key, out var value) && bool.TryParse(value, out var parsed) && parsed;

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
