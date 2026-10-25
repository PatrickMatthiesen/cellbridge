namespace CellBridge.Lists.PostgreSql;

internal sealed partial class EfListEngine
{
    private static void ValidateActor(ListActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.Subject) || actor.Subject.Length > 1024 || !ListValidation.IsWellFormedText(actor.Subject) ||
            actor.Subject.Any(char.IsControl) || actor.TenantId is { } tenant && (string.IsNullOrWhiteSpace(tenant) || tenant.Length > 1024 || !ListValidation.IsWellFormedText(tenant) || tenant.Any(char.IsControl)))
            throw new ListException(ListFailure.Forbidden, "The actor is missing or invalid.");
    }

    private int ListEnumerationLimit => _authorizationProvider is null ? int.MaxValue : _limits.MaxLists;
    private static ListException MissingContext() => Forbidden();
    private Task CheckContextAdmissionAsync(ListDbContext db, Guid workspaceId, Guid listId, string subject,
        ListAccess requiredAccess, OperationAuthorization? authorization, CancellationToken ct) => Task.CompletedTask;
    private static ListException MissingWorkspace(OperationAuthorization? authorization) => NotFound();

}
