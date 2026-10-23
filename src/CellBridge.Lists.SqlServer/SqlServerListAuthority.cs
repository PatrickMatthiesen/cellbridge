using System.Text;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Lists.SqlServer;

public sealed partial class SqlServerListEngine
{
    public Task SetMembershipAsync(ListActor actor, Guid workspaceId, string subject, bool member,
        bool canCreateLists, bool canManageGrants, CancellationToken ct = default)
    {
        if (authorizationProvider is not null) return Task.FromException(Unsupported());
        ListAuthorizationValidation.ValidateActor(new ListActor(subject));
        return OperateAsync(actor, workspaceId, true, async (db, workspace, administrator, authorization) =>
        {
            if (!administrator.CanManageGrants) throw Forbidden();
            var existing = await FindMemberAsync(db, workspaceId, subject, ct, tracked: true);
            var reduction = existing is not null && (!member || existing.CanCreateLists && !canCreateLists ||
                existing.CanManageGrants && !canManageGrants);
            long delta = 0;
            if (member)
            {
                if (existing is null)
                {
                    existing = new MembershipRow { WorkspaceId = workspaceId, Subject = subject };
                    db.Memberships.Add(existing);
                    delta = 128 + Encoding.UTF8.GetByteCount(subject);
                }
                existing.CanCreateLists = canCreateLists;
                existing.CanManageGrants = canManageGrants;
            }
            else if (existing is not null)
            {
                var key = SqlServerListSetup.SubjectKey(subject);
                var grants = await db.ListGrants.Where(row => row.WorkspaceId == workspaceId &&
                    EF.Property<byte[]>(row, "SubjectKey") == key).ToListAsync(ct);
                if (grants.Any(grant => !StringComparer.Ordinal.Equals(grant.Subject, subject))) throw Forbidden();
                // SQL Server relationships use NoAction. Delete grants explicitly,
                // before deleting the membership that owns their binary subject key.
                db.ListGrants.RemoveRange(grants);
                await SqlServerListDatabase.SaveRequiredAsync(db, ct);
                db.Memberships.Remove(existing);
                delta = -(128L + Encoding.UTF8.GetByteCount(subject) + grants.Count * (80L + Encoding.UTF8.GetByteCount(subject)));
            }
            AddAuthorityAudit(db, workspace, actor.Subject, member ? "membership.set" : "membership.remove",
                subject, null, delta, reduction, await SqlServerListDatabase.ClockAsync(db, ct));
            await SqlServerListDatabase.SaveRequiredAsync(db, ct);
            return true;
        }, ct);
    }

    public Task SetListGrantAsync(ListActor actor, Guid workspaceId, Guid listId, string subject,
        ListAccess access, CancellationToken ct = default)
    {
        if (authorizationProvider is not null) return Task.FromException(Unsupported());
        ListAuthorizationValidation.ValidateActor(new ListActor(subject));
        if ((access & ~(ListAccess.Read | ListAccess.Write | ListAccess.Design)) != 0) throw Validation("The list access value is invalid.");
        return OperateAsync(actor, workspaceId, true, async (db, workspace, member, authorization) =>
        {
            if (!member.CanManageGrants) throw Forbidden();
            if (!await db.Lists.AnyAsync(row => row.WorkspaceId == workspaceId && row.ListId == listId, ct)) throw NotFound();
            if (access != ListAccess.None && await FindMemberAsync(db, workspaceId, subject, ct) is null)
                throw Validation("The grant subject is not a workspace member.");
            var key = SqlServerListSetup.SubjectKey(subject);
            var grant = await db.ListGrants.SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId && row.ListId == listId &&
                EF.Property<byte[]>(row, "SubjectKey") == key, ct);
            if (grant is not null && !StringComparer.Ordinal.Equals(grant.Subject, subject)) throw Forbidden();
            var old = (ListAccess)(grant?.Access ?? 0);
            var reduction = (old & access) != old;
            long delta = 0;
            if (access == ListAccess.None)
            {
                if (grant is not null)
                {
                    db.ListGrants.Remove(grant);
                    delta = -(80L + Encoding.UTF8.GetByteCount(subject));
                }
            }
            else if (grant is null)
            {
                db.ListGrants.Add(new ListGrantRow { WorkspaceId = workspaceId, ListId = listId, Subject = subject, Access = (int)access });
                delta = 80L + Encoding.UTF8.GetByteCount(subject);
            }
            else grant.Access = (int)access;
            AddAuthorityAudit(db, workspace, actor.Subject, "grant.set", subject, listId, delta, reduction,
                await SqlServerListDatabase.ClockAsync(db, ct));
            await SqlServerListDatabase.SaveRequiredAsync(db, ct);
            return true;
        }, ct);
    }

    private void AddAuthorityAudit(SqlServerListDbContext db, WorkspaceRow workspace, string actor,
        string action, string target, Guid? listId, long bytes, bool security, DateTimeOffset now)
    {
        if (security)
        {
            if (workspace.SecurityAuditCount >= limits.MaxSecurityAuditEvents) throw Limit("The security audit reserve is exhausted.");
            workspace.SecurityAuditCount = checked(workspace.SecurityAuditCount + 1);
            workspace.StoredBytes = Math.Max(0, checked(workspace.StoredBytes + bytes));
        }
        else Reserve(workspace, checked(bytes + Encoding.UTF8.GetByteCount(target)), actor, action, 0);
        var audit = Audit(workspace.WorkspaceId, now, actor, action, listId, null, null, []);
        audit.TargetSubject = target;
        audit.Security = security;
        db.AuditEvents.Add(audit);
    }
}
