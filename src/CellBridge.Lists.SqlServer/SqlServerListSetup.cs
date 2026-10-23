using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Lists.SqlServer;

/// <summary>Explicit administrator registration, separate from the restricted runtime engine.</summary>
public static class SqlServerListSetup
{
    public static Task RegisterWorkspaceAsync(DbContextOptions<SqlServerListDbContext> options,
        Guid workspaceId, string name, string administratorSubject, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("A workspace ID is required.", nameof(workspaceId));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || !ListValidation.IsWellFormedText(name))
            throw new ArgumentException("A valid workspace name is required.", nameof(name));
        ListAuthorizationValidation.ValidateActor(new ListActor(administratorSubject));
        var normalized = SqlServerListDatabase.Normalize(options);
        return SqlServerListDatabase.MapFailuresAsync(async () =>
        {
            var limits = new ListLimits();
            await using var scope = await SqlServerListWorkspaceTransaction.OpenAsync(normalized, workspaceId, ct,
                limits.StatementTimeout);
            var db = scope.Context;
            await SqlServerListDatabase.LockAsync(db, SqlServerListDatabase.WorkspaceLock(workspaceId), true, limits, ct);
            var workspace = await db.Workspaces.SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId, ct);
            if (workspace is null)
            {
                workspace = new WorkspaceRow { WorkspaceId = workspaceId, Name = name };
                db.Workspaces.Add(workspace);
                await SqlServerListDatabase.SaveRequiredAsync(db, ct);
            }
            else if (!StringComparer.Ordinal.Equals(workspace.AuthorizationMode, "builtin"))
                throw new ListException(ListFailure.Conflict, "The workspace has a different authorization anchor.");
            else workspace.Name = name;

            var key = SubjectKey(administratorSubject);
            var member = await db.Memberships.SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId &&
                EF.Property<byte[]>(row, "SubjectKey") == key, ct);
            if (member is null)
            {
                member = new MembershipRow { WorkspaceId = workspaceId, Subject = administratorSubject };
                db.Memberships.Add(member);
                workspace.StoredBytes = checked(workspace.StoredBytes + 128 + Encoding.UTF8.GetByteCount(administratorSubject));
            }
            else if (!StringComparer.Ordinal.Equals(member.Subject, administratorSubject))
                throw new ListException(ListFailure.Conflict, "A different subject occupies the membership lookup key.");
            member.CanCreateLists = true;
            member.CanManageGrants = true;
            await SqlServerListDatabase.SaveRequiredAsync(db, ct);
            await scope.CommitAsync(ct);
            return true;
        }, ct);
    }

    public static Task RegisterHostWorkspaceAsync(DbContextOptions<SqlServerListDbContext> options,
        Guid workspaceId, string name, string policyDomain, string authorizationScope, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("A workspace ID is required.", nameof(workspaceId));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || !ListValidation.IsWellFormedText(name))
            throw new ArgumentException("A valid workspace name is required.", nameof(name));
        ListAuthorizationValidation.ValidateDomain(policyDomain, nameof(policyDomain));
        ListAuthorizationValidation.ValidateActor(new ListActor(authorizationScope));
        var normalized = SqlServerListDatabase.Normalize(options);
        return SqlServerListDatabase.MapFailuresAsync(async () =>
        {
            var limits = new ListLimits();
            await using var scope = await SqlServerListWorkspaceTransaction.OpenAsync(normalized, workspaceId, ct, limits.StatementTimeout);
            var db = scope.Context;
            await SqlServerListDatabase.LockAsync(db, SqlServerListDatabase.WorkspaceLock(workspaceId), true, limits, ct);
            var workspace = await db.Workspaces.SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId, ct);
            if (workspace is null)
                db.Workspaces.Add(new WorkspaceRow { WorkspaceId = workspaceId, Name = name,
                    AuthorizationMode = "host", AuthorizationDomain = policyDomain, AuthorizationScope = authorizationScope });
            else if (workspace.AuthorizationMode != "host" || !StringComparer.Ordinal.Equals(workspace.AuthorizationDomain, policyDomain) ||
                !StringComparer.Ordinal.Equals(workspace.AuthorizationScope, authorizationScope))
                throw new ListException(ListFailure.Conflict, "The workspace has a different immutable authorization anchor.");
            else workspace.Name = name;
            await SqlServerListDatabase.SaveRequiredAsync(db, ct);
            await scope.CommitAsync(ct);
            return true;
        }, ct);
    }

    public static Task RegisterTemplateAsync(DbContextOptions<SqlServerListDbContext> options,
        ListTemplate template, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ListTemplates.Validate(template, new ListLimits());
        var normalized = SqlServerListDatabase.Normalize(options);
        return SqlServerListDatabase.MapFailuresAsync(async () =>
        {
            var limits = new ListLimits();
            await using var scope = await SqlServerListWorkspaceTransaction.OpenAsync(normalized, Guid.NewGuid(), ct, limits.StatementTimeout);
            var db = scope.Context;
            await SqlServerListDatabase.LockAsync(db, "CellBridge.Lists.Templates", true, limits, ct);
            var json = System.Text.Json.JsonSerializer.Serialize(template, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            var existing = await db.Templates.SingleOrDefaultAsync(row => row.TemplateId == template.Id && row.Version == template.Version, ct);
            if (existing is null) db.Templates.Add(new TemplateRow { TemplateId = template.Id, Version = template.Version, Definition = json });
            else if (!System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(existing.Definition),
                System.Text.Json.Nodes.JsonNode.Parse(json)))
                throw new ListException(ListFailure.Conflict, "That template version has a different definition.");
            await SqlServerListDatabase.SaveRequiredAsync(db, ct);
            await scope.CommitAsync(ct);
            return true;
        }, ct);
    }

    internal static byte[] SubjectKey(string subject) => SHA256.HashData(Encoding.Unicode.GetBytes(subject));
}
