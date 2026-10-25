using CellBridge.Lists;

public sealed class ExampleListAuthorization : IListAuthorizationProvider
{
    public const string Domain = "example:document-library";
    public const string Tenant = "example-tenant";
    public static readonly Guid Workspace = Guid.Parse("b2cb2be3-9f25-48e8-95df-92fd21cb0918");

    public string PolicyDomain => Domain;

    public ValueTask<ListAuthorizationDecision> AuthorizeAsync(ListAuthorizationRequest request,
        CancellationToken ct = default)
    {
        var allowed = request.PolicyDomain == Domain && request.TenantId == Tenant &&
            request.WorkspaceId == Workspace && (request.Subject switch
            {
                "example:owner" => true,
                "example:reader" => request.Action is ListAuthorizationAction.UseLists or ListAuthorizationAction.Read,
                _ => false
            });
        return ValueTask.FromResult(new ListAuthorizationDecision(allowed));
    }
}
