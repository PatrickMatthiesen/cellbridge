using System.Net;
using CellBridge.Client;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public sealed partial class CellBridgeInProcessHost
{
    /// <summary>Releases an owned Excel client on the indicated document lifecycle, even after its grant is revoked.</summary>
    /// <remarks>The trusted host must supply the subject and client from its original participation record.</remarks>
    public async Task ReleaseParticipationAsync(Guid resourceId, long lifecycleGeneration, string subject, Guid clientId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lifecycleGeneration);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        if (resourceId == Guid.Empty || clientId == Guid.Empty)
            throw new ArgumentException("Resource and client IDs are required.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RequestTimeout);
        var token = deadline.Token;
        var admitted = await admission.AcquireAsync(subject, token).ConfigureAwait(false);
        using var lease = admitted.Lease;
        if (lease is null)
            throw Rejected((HttpStatusCode)admitted.RejectionStatus, "CellBridge is busy. Retry the release.");

        var repository = documents.Provider.Documents;
        var client = clientId.ToString("B");
        var identity = new SubjectIdentity(subject, subject, subject);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var current = await repository.ReadControlAsync(resourceId, token).ConfigureAwait(false);
            if (current is null || current.Head.IsDeleted || current.Head.LifecycleGeneration != lifecycleGeneration) return;

            var schemaOwner = current.Coordination.SchemaOwners.Any(owner => owner.OwnerSubject == subject &&
                Guid.TryParse(owner.Client, out var id) && id == clientId &&
                StringComparer.OrdinalIgnoreCase.Equals(owner.Id, CellBridgeDocumentSession.ExcelSchemaLockId));
            var editor = current.Editors.Any(entry => entry.ClientId == clientId && entry.Owner?.Subject == subject);
            if (!schemaOwner && !editor) return;

            var head = current.Head;
            var release = schemaOwner ? new FssHttpSubRequest
            {
                Type = SubRequestType.Coauth,
                SubRequestDataAttributes =
                {
                    ["ClientID"] = client,
                    ["CoauthRequestType"] = "ExitCoauthoring",
                    ["SchemaLockID"] = CellBridgeDocumentSession.ExcelSchemaLockId,
                },
            } : new FssHttpSubRequest
            {
                Type = SubRequestType.EditorsTable,
                SubRequestDataAttributes =
                {
                    ["ClientID"] = client,
                    ["EditorsTableRequestType"] = "LeaveEditingSession",
                },
            };
            // This admission is confined to an ownership-checked release and binds to the exact control state.
            var cleanup = new DocumentActorAdmission(identity, resourceId, lifecycleGeneration,
                head.Security.AuthorizationPolicy, DocumentAccess.Read)
            {
                AuthorityGeneration = head.AuthorityGeneration,
                StateVersion = head.StateVersion,
            };
            var result = await repository.ExecuteControlAsync(new DocumentControlCommand.Office(resourceId, cleanup, release),
                token).ConfigureAwait(false);
            if (result.Current.Head.LifecycleGeneration != lifecycleGeneration) return;
            if (!result.Changed && result.Current.Head.StateVersion == cleanup.StateVersion && result.ErrorCode is not null)
                throw new InvalidOperationException("CellBridge could not release the Excel client.");
        }
        throw Rejected(HttpStatusCode.ServiceUnavailable, "CellBridge participation changed during release.");
    }
}
