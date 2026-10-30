using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace CellBridge.Storage.Abstractions;

/// <summary>Current-document decisions executed under the affected document's guard. Performs no I/O.</summary>
public static class DocumentControlReducer
{
    /// <summary>Captures time-driven cleanup in the same transaction as an accepted publication.</summary>
    public static ControlResult ExpireForPublication(DocumentControlRead current)
    {
        var now = current.UtcNow;
        var state = current.Coordination;
        if (!current.Editors.Any(editor => editor.ExpiresUtc <= now) &&
            !state.SchemaOwners.Any(lease => lease.ExpiresUtc <= now) &&
            (state.Exclusive is null || state.Exclusive.ExpiresUtc > now) &&
            (state.HostLock is null || state.HostLock.ExpiresUtc > now)) return new(current, false);
        var document = StoredDocument.RestoreControl(current);
        var owners = state.SchemaOwners.Where(lease => lease.ExpiresUtc > now).ToImmutableArray();
        var clients = state.CoauthorClients.Where(client => owners.Any(lease => SameClient(lease.Client, client))).ToImmutableArray();
        var coordination = state with
        {
            SchemaOwners = owners, SchemaId = owners.IsEmpty ? null : state.SchemaId,
            Exclusive = state.Exclusive is { } exclusive && exclusive.ExpiresUtc > now ? exclusive : null,
            HostLock = state.HostLock is { } host && host.ExpiresUtc > now ? host : null,
            CoauthorClients = clients, CoauthorTransitionPending = state.CoauthorTransitionPending && !clients.IsEmpty,
        };
        coordination = CoordinationFencing.Capture(state, coordination);
        return Finish(current, document.CaptureControl(current, coordination));
    }

    public static DocumentAccess Access(DocumentControlRead current, DocumentActorAdmission admission) => Access(current.Head, admission);

    public static DocumentAccess Access(DocumentHead head, DocumentActorAdmission admission)
    {
        if (head.IsDeleted || admission.ResourceId != head.ResourceId ||
            admission.LifecycleGeneration != head.LifecycleGeneration ||
            admission.Binding != head.Security.AuthorizationPolicy ||
            admission.AuthorityGeneration != head.AuthorityGeneration || admission.StateVersion != head.StateVersion ||
            string.IsNullOrWhiteSpace(admission.Actor.Subject)) return DocumentAccess.None;
        // Repository callers are trusted engine/host code. Request claims alone
        // never construct admissions. All policy types bind their result to the
        // exact current metadata, including custom unbound evaluators.
        var access = admission.Access;
        return (access & ~(DocumentAccess.Read | DocumentAccess.Write)) != 0 ? DocumentAccess.None
            : access.HasFlag(DocumentAccess.Write) ? access | DocumentAccess.Read : access;
    }

    public static string? ValidateWrite(DocumentControlRead current, DocumentActorAdmission admission,
        IReadOnlyDictionary<string, string> lockAttributes)
    {
        if (!Access(current, admission).HasFlag(DocumentAccess.Write)) return "FileUnauthorizedAccess";
        var document = StoredDocument.RestoreControl(current);
        var coordinator = FssHttpLockCoordinator.Restore(document, current.Coordination, current.UtcNow, admission.Actor);
        return coordinator.ExecuteCellWrite(lockAttributes, static () => true, out _, out var error, current.UtcNow)
            ? null : error;
    }

    public static string? ValidateWholeFile(DocumentControlRead current, DocumentActorAdmission admission, string? token)
    {
        if (!Access(current, admission).HasFlag(DocumentAccess.Write)) return "FileUnauthorizedAccess";
        var coordination = current.Coordination;
        if (coordination.Exclusive is { } exclusive && exclusive.ExpiresUtc > current.UtcNow ||
            coordination.SchemaOwners.Any(lease => lease.ExpiresUtc > current.UtcNow)) return "FileAlreadyLockedOnServer";
        if (token is not null)
            return coordination.HostLock is { } host && host.ExpiresUtc > current.UtcNow &&
                host.Token == token && host.OwnerSubject == admission.Actor.Subject ? null : "InvalidCoauthSession";
        return current.Head.Package.Length == 0 && !(coordination.HostLock is { } active && active.ExpiresUtc > current.UtcNow)
            ? null : "FileAlreadyLockedOnServer";
    }

    public static ControlResult Apply(DocumentControlRead current, DocumentControlCommand command, StorageLimits limits)
    {
        if (command.ResourceId != current.Head.ResourceId) throw new ArgumentException("The command targets another document.", nameof(command));
        if (current.Head.IsDeleted) return new(current, false) { ErrorCode = "FileNotFound" };
        if (command is DocumentControlCommand.BindAuthorization bind)
        {
            if (current.Head.Security.AuthorizationPolicy != bind.Expected ||
                bind.ExpectedStateVersion is { } version && version != current.Head.StateVersion)
                return new(current, false) { ErrorCode = "CoherencyFailure" };
            if (bind.Expected is { } old && (old.PolicyDomain != bind.Next.PolicyDomain ||
                old.ContractVersion != bind.Next.ContractVersion || old.Revision >= bind.Next.Revision))
                throw new ArgumentException("The next binding must advance within the same policy domain.", nameof(command));
            return Permissions(current, current.Head.Security with { AuthorizationPolicy = bind.Next },
                subject => bind.ParticipantAccess.GetValueOrDefault(subject));
        }
        var admission = command.Admission ?? throw new ArgumentException("An actor admission is required.", nameof(command));
        var access = Access(current, admission);
        if (!access.HasFlag(command is DocumentControlCommand.Office requestedOffice
            ? CellBridgeAuthorization.RequiredAccess(requestedOffice.Request) :
            command is DocumentControlCommand.ExpireEditors ? DocumentAccess.Read : DocumentAccess.Write))
            return new(current, false) { ErrorCode = "FileUnauthorizedAccess" };

        switch (command)
        {
            case DocumentControlCommand.SetPermissions permissions:
                if (current.Head.AuthorityGeneration != permissions.ExpectedAuthorityGeneration)
                    return new(current, false) { ErrorCode = "CoherencyFailure" };
                if (current.Head.Security.AuthorizationPolicy is not null || permissions.Security.AuthorizationPolicy is not null)
                    throw new InvalidOperationException("Bound documents require a host policy update.");
                return Permissions(current, permissions.Security, permissions.Security.AccessFor);
            case DocumentControlCommand.SetHostLease host:
                return SetHostLease(current, host);
            case DocumentControlCommand.ExpireEditors:
            {
                var document = StoredDocument.RestoreControl(current);
                return Finish(current, document.CaptureControl(current, current.Coordination));
            }
            case DocumentControlCommand.Office office:
                return Office(current, office, limits);
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    private static ControlResult Office(DocumentControlRead current, DocumentControlCommand.Office command, StorageLimits limits)
    {
        var request = command.Request;
        var response = new FssHttpSubResponse { Type = request.Type, SubRequestToken = request.SubRequestToken,
            ErrorCode = "Success", HResult = "0" };
        if (request.Type is SubRequestType.Cell or SubRequestType.GetVersions)
            throw new ArgumentException("Graph and history operations use repository reads and publications.", nameof(command));
        if (request.Type is SubRequestType.EditorsTable or SubRequestType.Coauth &&
            Guid.TryParse(request.SubRequestDataAttributes.GetValueOrDefault("ClientID"), out var client) &&
            current.Editors.Any(e => e.ClientId == client && e.ExpiresUtc > current.UtcNow &&
                e.Owner?.Subject != command.Actor.Actor.Subject))
            return new(current, false) { ErrorCode = "FileUnauthorizedAccess" };
        var document = StoredDocument.RestoreControl(current);
        var coordinator = FssHttpLockCoordinator.Restore(document, current.Coordination, current.UtcNow, command.Actor.Actor);
        if (request.Type == SubRequestType.ServerTime)
            response.SubResponseDataAttributes["ServerTime"] = current.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        else
            CellBridgeRequestProcessor.ApplyMetadata(document, request, response, NullLogger.Instance, "",
                new CellBridgeActor(command.Actor.Actor));
        if (response.ErrorCode == "FileUnauthorizedAccess" || response.ErrorCode == "InvalidArgument" &&
            request.Type is SubRequestType.SchemaLock or SubRequestType.ExclusiveLock or SubRequestType.Coauth)
            return new(current, false) { ErrorCode = response.ErrorCode, Response = response };
        var next = document.CaptureControl(current, coordinator.Capture());
        next = next with { Coordination = CoordinationFencing.Capture(current.Coordination, next.Coordination) };
        // Inline editor data is bounded independently of retained file history.
        StorageLimits.Check("editors partition bytes", next.Partitions.Single(p => p.Kind == 2).InlineContent.Length, limits.MaxObjectBytes);
        return Finish(current, next) with { Response = response, ErrorCode = response.ErrorCode == "Success" ? null : response.ErrorCode };
    }

    private static ControlResult Permissions(DocumentControlRead current, DocumentSecurity security, Func<string, DocumentAccess> accessFor)
    {
        var document = StoredDocument.RestoreControl(current);
        foreach (var editor in current.Editors)
        {
            var access = editor.Owner is null ? DocumentAccess.None : accessFor(editor.Owner.Subject);
            if (!access.HasFlag(DocumentAccess.Read)) document.LeaveSession(editor.ClientId);
            else if (editor.AsEditor && !access.HasFlag(DocumentAccess.Write)) document.SetEditorPermission(editor.ClientId, false);
        }
        var coordination = current.Coordination with
        {
            SchemaOwners = current.Coordination.SchemaOwners.Where(l => l.OwnerSubject is not null &&
                accessFor(l.OwnerSubject).HasFlag(DocumentAccess.Write)).ToImmutableArray(),
            Exclusive = current.Coordination.Exclusive is { OwnerSubject: { } subject } exclusive &&
                accessFor(subject).HasFlag(DocumentAccess.Write) ? exclusive : null,
            HostLock = current.Coordination.HostLock is { } host && accessFor(host.OwnerSubject).HasFlag(DocumentAccess.Write) ? host : null,
            Generation = checked(current.Coordination.Generation + 1),
        };
        coordination = coordination with { CoauthorClients = coordination.CoauthorClients.Where(client =>
            coordination.SchemaOwners.Any(lease => SameClient(lease.Client, client))).ToImmutableArray() };
        if (coordination.CoauthorClients.IsEmpty) coordination = coordination with { CoauthorTransitionPending = false };
        if (coordination.SchemaOwners.IsEmpty) coordination = coordination with { SchemaId = null };
        var next = document.CaptureControl(current, coordination);
        next = next with { Head = next.Head with { Security = security, AuthorityGeneration = checked(current.Head.AuthorityGeneration + 1) } };
        return Finish(current, next);
    }

    private static ControlResult SetHostLease(DocumentControlRead current, DocumentControlCommand.SetHostLease command)
    {
        var coordination = current.Coordination;
        var active = coordination.HostLock is { } host && host.ExpiresUtc > current.UtcNow ? host : null;
        if (coordination.Exclusive is { } exclusive && exclusive.ExpiresUtc > current.UtcNow ||
            coordination.SchemaOwners.Any(l => l.ExpiresUtc > current.UtcNow))
            return new(current, false) { ErrorCode = "FileAlreadyLockedOnServer" };
        if (active is not null && (active.Token != command.ExpectedToken || active.OwnerSubject != command.Actor.Actor.Subject) ||
            active is null && command.ExpectedToken is not null)
            return new(current, false) { ErrorCode = "InvalidCoauthSession" };
        if (command.Lease is { } lease && (lease.OwnerSubject != command.Actor.Actor.Subject ||
            string.IsNullOrWhiteSpace(lease.Token) || lease.Token.Length > 1024 || lease.ExpiresUtc <= current.UtcNow))
            throw new ArgumentException("A host lease must belong to the admitted actor and have a valid token and expiry.", nameof(command));
        return Finish(current, current with { Coordination = coordination with { HostLock = command.Lease,
            Exclusive = null, SchemaOwners = [], SchemaId = null, CoauthorClients = [], CoauthorTransitionPending = false,
            Generation = checked(coordination.Generation + 1) } });
    }

    private static ControlResult Finish(DocumentControlRead current, DocumentControlRead next)
    {
        next = next with { Head = next.Head with
        {
            NextEditorExpiryUtc = next.Editors.IsEmpty ? null : next.Editors.Min(e => e.ExpiresUtc),
            ActiveEditors = next.Editors.Count(e => e.ExpiresUtc > next.UtcNow),
            CoordinationGeneration = next.Coordination.Generation,
        } };
        // All fields here describe current participants and controls. No graph,
        // receipt or history collection participates in this comparison.
        var changed = JsonSerializer.Serialize(current) != JsonSerializer.Serialize(next);
        return new(changed ? next : current, changed);
    }

    private static bool SameClient(string? left, string right) =>
        Guid.TryParse(left, out var a) && Guid.TryParse(right, out var b) ? a == b
            : string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
