namespace CellBridge.Lists.SqlServer;

internal sealed partial class EfListEngine
{
    private static void ValidateActor(ListActor actor) => ListAuthorizationValidation.ValidateActor(actor);
    private int ListEnumerationLimit => _limits.MaxLists;
    private static ListException MissingContext() => NotFound();
    private async Task CheckContextAdmissionAsync(ListDbContext db, Guid workspaceId, Guid listId, string subject,
        ListAccess requiredAccess, OperationAuthorization? authorization, CancellationToken ct)
    {
        // SQL Server promises source admission before it inspects receipts or schemas.
        // PostgreSQL keeps its guarded compiled projection for the same decision.
        await AuthorizeMemberAsync(db, workspaceId, subject, ct, authorization);
        await RequireAccessAsync(db, workspaceId, listId, subject, requiredAccess, ct, authorization);
    }
    private static ListException MissingWorkspace(OperationAuthorization? authorization) => authorization is null ? Forbidden() : NotFound();

}
