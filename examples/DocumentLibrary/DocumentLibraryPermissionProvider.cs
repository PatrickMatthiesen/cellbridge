using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;

namespace CellBridge.DocumentLibrary;

/// <summary>Reads the host's current durable permissions for each operation.</summary>
public sealed class DocumentLibraryPermissionProvider(DocumentLibraryDestination destination)
    : IDocumentAuthorizationProvider
{
    public const string Domain = "cellbridge:document-library-permissions";
    public static DocumentAuthorizationScope Scope { get; } = new("document-library", "local");
    public string PolicyDomain => Domain;

    public async ValueTask<DocumentAuthorizationDecision> AuthorizeAsync(
        DocumentAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Scope != Scope || request.Binding.PolicyDomain != Domain ||
            request.Binding.ContractVersion != 2 || request.Binding.Revision <= 0 ||
            request.Binding.TenantId != Scope.TenantId || request.Binding.WorkspaceId != Scope.WorkspaceId)
            return new(false);

        // This host grants Create to its local owner. CellBridge separately requires CanCreate.
        // The resource need not have a manifest yet when the engine checks creation.
        if (request.Action == DocumentAction.Create)
            return new(request.Subject == DocumentLibraryService.OwnerSubject);

        var manifest = await destination.GetAsync(request.ResourceId, cancellationToken);
        if (manifest.ResourceId != request.ResourceId) return new(false);
        var access = manifest.Permissions.GetValueOrDefault(request.Subject);
        if ((access & ~(DocumentAccess.Read | DocumentAccess.Write)) != 0) return new(false);
        return new(request.Action switch
        {
            DocumentAction.Read => access.HasFlag(DocumentAccess.Read) || access.HasFlag(DocumentAccess.Write),
            DocumentAction.Write => access.HasFlag(DocumentAccess.Write),
            _ => false,
        });
    }
}
