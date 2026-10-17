using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Npgsql;

if (args.Length != 3)
{
    Console.Error.WriteLine(
        "Usage: HostAuthorizedLists <admin-connection> <runtime-connection> <runtime-role>");
    return 2;
}

await using var adminSource = NpgsqlDataSource.Create(args[0]);
await using var runtimeSource = NpgsqlDataSource.Create(args[1]);
await PostgreSqlListSetup.InitializeAsync(adminSource, args[2]);

var workspaceId = Guid.NewGuid();
var tenantId = "customer:demo";
var subject = "user:demo";
var authority = new ExampleListAuthority("example:list-authority", subject, tenantId);
await PostgreSqlListSetup.RegisterHostWorkspaceAsync(adminSource, workspaceId,
    "Host-authorized workspace", authority.PolicyDomain, tenantId);

// Decisions are uncached by default. Wrap the authority only when a bounded stale window is acceptable:
// IListAuthorizationProvider provider = new CachedListAuthorizationProvider(authority,
//     new ListAuthorizationCacheOptions { Capacity = 2_000, DecisionTtl = TimeSpan.FromSeconds(15) });
IListAuthorizationProvider provider = authority;
var lists = new PostgreSqlListEngine(runtimeSource, limits: null, provider);
var actor = new ListActor(subject, tenantId);
var title = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text, Required: true, MaxLength: 200);
var created = await lists.CreateListAsync(actor, workspaceId,
    new CreateListRequest(Guid.NewGuid(), "Tasks", [title]));

Console.WriteLine($"Created {created.Name} ({created.Id:D})");
return 0;

file sealed class ExampleListAuthority(string policyDomain, string subject, string tenantId)
    : IListAuthorizationProvider
{
    public string PolicyDomain { get; } = policyDomain;

    public ValueTask<ListAuthorizationDecision> AuthorizeAsync(
        ListAuthorizationRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var trustedActor = StringComparer.Ordinal.Equals(request.Subject, subject) &&
            StringComparer.Ordinal.Equals(request.TenantId, tenantId);
        return ValueTask.FromResult(new ListAuthorizationDecision(trustedActor));
    }
}
