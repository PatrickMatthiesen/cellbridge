using System.Text;
using Microsoft.EntityFrameworkCore;

#if CELLBRIDGE_SQLSERVER
namespace CellBridge.Lists.SqlServer;
#else
namespace CellBridge.Lists.PostgreSql;
#endif

internal sealed partial class EfListEngine
{
    internal static Task RegisterWorkspaceAsync(DbContextOptions<ListDbContext> options, Guid workspaceId,
        string name, string administratorSubject, CancellationToken ct)
    {
        ValidateWorkspace(workspaceId, name);
        ListAuthorizationValidation.ValidateActor(new ListActor(administratorSubject));
        var engine = new EfListEngine(options, null, null);
        return engine.RegisterAsync(workspaceId, false, async db =>
        {
            var workspace = await db.Workspaces.SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId, ct);
            if (workspace is null)
            {
                workspace = new WorkspaceRow { WorkspaceId = workspaceId, Name = name };
                db.Workspaces.Add(workspace);
                await SaveRequiredAsync(db, ct);
            }
            else if (workspace.AuthorizationMode != "builtin")
                throw Conflict("The workspace has a different immutable authorization anchor.");
            else workspace.Name = name;
            var membership = await db.Memberships.SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId && row.Subject == administratorSubject, ct);
            if (membership is null)
            {
                membership = new MembershipRow { WorkspaceId = workspaceId, Subject = administratorSubject };
                db.Memberships.Add(membership);
                workspace.StoredBytes = checked(workspace.StoredBytes + 128 + Encoding.UTF8.GetByteCount(administratorSubject));
            }
            membership.CanCreateLists = true;
            membership.CanManageGrants = true;
            await SaveRequiredAsync(db, ct);
        }, ct);
    }

    internal static Task RegisterHostWorkspaceAsync(DbContextOptions<ListDbContext> options, Guid workspaceId,
        string name, string policyDomain, string scope, CancellationToken ct)
    {
        ValidateWorkspace(workspaceId, name);
        ListAuthorizationValidation.ValidateDomain(policyDomain, nameof(policyDomain));
        ListAuthorizationValidation.ValidateActor(new ListActor(scope));
        var engine = new EfListEngine(options, null, null);
        return engine.RegisterAsync(workspaceId, false, async db =>
        {
            var workspace = await db.Workspaces.SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId, ct);
            if (workspace is null)
                db.Workspaces.Add(new WorkspaceRow { WorkspaceId = workspaceId, Name = name,
                    AuthorizationMode = "host", AuthorizationDomain = policyDomain, AuthorizationScope = scope });
            else if (workspace.AuthorizationMode != "host" || !StringComparer.Ordinal.Equals(workspace.AuthorizationDomain, policyDomain) ||
                !StringComparer.Ordinal.Equals(workspace.AuthorizationScope, scope))
                throw Conflict("The workspace has a different immutable authorization anchor.");
            else workspace.Name = name;
            await SaveRequiredAsync(db, ct);
        }, ct);
    }

    internal static Task RegisterTemplateAsync(DbContextOptions<ListDbContext> options, ListTemplate template, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(template);
        ListTemplates.Validate(template, new ListLimits());
        var engine = new EfListEngine(options, null, null);
        return engine.RegisterAsync(Guid.NewGuid(), true, async db =>
        {
            var json = ListJson.Serialize(template);
            var existing = await db.Templates.SingleOrDefaultAsync(row => row.TemplateId == template.Id && row.Version == template.Version, ct);
            if (existing is null) db.Templates.Add(new TemplateRow { TemplateId = template.Id, Version = template.Version, Definition = json });
            else if (!await TemplateDefinitionsEqualAsync(db, existing.Definition, json, ct))
                throw Conflict("That template version has a different definition.");
            await SaveRequiredAsync(db, ct);
        }, ct);
    }

    private Task RegisterAsync(Guid workspaceId, bool template, Func<ListDbContext, Task> operation, CancellationToken ct) =>
        MapStoreFailuresAsync(async () =>
        {
            await using var session = await OpenSessionAsync(workspaceId, ct);
            await LockRegistrationAsync(session.Context, workspaceId, template, ct);
            await operation(session.Context);
            ct.ThrowIfCancellationRequested();
            await session.CommitAsync(ct);
            return true;
        }, ct);

    private static void ValidateWorkspace(Guid workspaceId, string name)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("A workspace ID is required.", nameof(workspaceId));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || !ListValidation.IsWellFormedText(name))
            throw new ArgumentException("A valid workspace name is required.", nameof(name));
    }
}
