using Microsoft.EntityFrameworkCore;

#if CELLBRIDGE_SQLSERVER
namespace CellBridge.Lists.SqlServer;
#else
namespace CellBridge.Lists.PostgreSql;
#endif

internal sealed partial class EfListEngine
{
    public async Task SetMembershipAsync(ListActor actor, Guid workspaceId, string subject, bool member,
        bool canCreateLists, bool canManageGrants, CancellationToken ct = default)
    {
        ValidateActor(actor);
        if (_authorizationProvider is not null) throw HostAuthorityUnsupported();
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 1024 || !ListValidation.IsWellFormedText(subject) || subject.Any(char.IsControl)) throw Validation("The membership subject is invalid.");
        await InTransactionAsync(workspaceId, true, null, async (db, connection, transaction) =>
        {
            var administrator = await AuthorizeMemberAsync(db, workspaceId, actor.Subject, ct, null);
            if (!administrator.CanManageGrants) throw Forbidden();
            await _locks.QuotaAsync(db, workspaceId, ct);
            var existing = await db.Memberships.SingleOrDefaultAsync(
                row => row.WorkspaceId == workspaceId && row.Subject == subject, ct);
            bool? oldCreate = existing?.CanCreateLists;
            bool? oldManage = existing?.CanManageGrants;
            var reduction = oldCreate is not null && (!member || oldCreate.Value && !canCreateLists || oldManage!.Value && !canManageGrants);
            if (member)
            {
                if (oldCreate is null) await ReserveStorageAsync(db, workspaceId, 128 + System.Text.Encoding.UTF8.GetByteCount(subject), ct);
                if (existing is null)
                    db.Memberships.Add(new MembershipRow { WorkspaceId = workspaceId, Subject = subject,
                        CanCreateLists = canCreateLists, CanManageGrants = canManageGrants });
                else
                {
                    existing.CanCreateLists = canCreateLists;
                    existing.CanManageGrants = canManageGrants;
                }
                await SaveRequiredAsync(db, ct);
            }
            else if (oldCreate is not null)
            {
                var grantCount = await db.ListGrants.LongCountAsync(
                    row => row.WorkspaceId == workspaceId && row.Subject == subject, ct);
                db.ListGrants.RemoveRange(await db.ListGrants.Where(row => row.WorkspaceId == workspaceId && row.Subject == subject).ToListAsync(ct));
                await SaveRequiredAsync(db, ct);
                db.Memberships.Remove(existing!);
                await SaveRequiredAsync(db, ct);
                var subjectBytes = System.Text.Encoding.UTF8.GetByteCount(subject);
                await ReserveStorageAsync(db, workspaceId,
                    -(128 + subjectBytes + grantCount * (80 + subjectBytes)), ct);
            }
            await AuditAsync(db, connection, transaction, workspaceId, actor.Subject,
                member ? "membership.set" : "membership.remove", null, null, null, Array.Empty<Guid>(), reduction, ct, subject);
            return true;
        }, ct, mutation: true);
    }

    public async Task SetListGrantAsync(ListActor actor, Guid workspaceId, Guid listId, string subject,
        ListAccess access, CancellationToken ct = default)
    {
        ValidateActor(actor);
        if (_authorizationProvider is not null) throw HostAuthorityUnsupported();
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 1024 || !ListValidation.IsWellFormedText(subject) || subject.Any(char.IsControl)) throw Validation("The grant subject is invalid.");
        if ((access & ~(ListAccess.Read | ListAccess.Write | ListAccess.Design)) != 0)
            throw Validation("The list access value is invalid.");
        await InTransactionAsync(workspaceId, true, null, async (db, connection, transaction) =>
        {
            var administrator = await AuthorizeMemberAsync(db, workspaceId, actor.Subject, ct, null);
            if (!administrator.CanManageGrants) throw Forbidden();
            await _locks.QuotaAsync(db, workspaceId, ct);
            if (!await db.Lists.AnyAsync(row => row.WorkspaceId == workspaceId && row.ListId == listId, ct))
                throw NotFound();
            if (access != ListAccess.None && !await db.Memberships.AnyAsync(
                    row => row.WorkspaceId == workspaceId && row.Subject == subject, ct))
                throw Validation("The grant subject is not a workspace member.");
            var existing = await db.ListGrants.SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId &&
                row.ListId == listId && row.Subject == subject, ct);
            var old = (ListAccess)(existing?.Access ?? 0);
            var reduction = (old & access) != old;
            if (access == ListAccess.None)
            {
                if (existing is not null)
                {
                    db.ListGrants.Remove(existing);
                    await SaveRequiredAsync(db, ct);
                }
                if (old != ListAccess.None)
                    await ReserveStorageAsync(db, workspaceId,
                        -(80 + System.Text.Encoding.UTF8.GetByteCount(subject)), ct);
            }
            else
            {
                if (old == ListAccess.None) await ReserveStorageAsync(db, workspaceId, 80 + System.Text.Encoding.UTF8.GetByteCount(subject), ct);
                if (existing is null)
                    db.ListGrants.Add(new ListGrantRow { WorkspaceId = workspaceId, ListId = listId,
                        Subject = subject, Access = (int)access });
                else
                    existing.Access = (int)access;
                await SaveRequiredAsync(db, ct);
            }
            await AuditAsync(db, connection, transaction, workspaceId, actor.Subject, "grant.set", listId, null, null,
                Array.Empty<Guid>(), reduction, ct, subject);
            return true;
        }, ct, mutation: true);
    }

    public Task<IReadOnlyList<AuditEvent>> GetAuditAsync(ListActor actor, Guid workspaceId, int limit = 100,
        CancellationToken ct = default)
    {
        if (limit is <= 0 or > 1000) throw Validation("The audit page size is invalid.");
        return ReadAsync(actor, workspaceId, async (db, connection, transaction, member, authorization) =>
        {
            if (authorization is null && !member.CanManageGrants) throw Forbidden();
            return (IReadOnlyList<AuditEvent>)await db.AuditEvents.AsNoTracking().Where(row => row.WorkspaceId == workspaceId)
                .OrderByDescending(row => row.Id).Take(limit)
                .Select(row => new AuditEvent(row.Id, row.Utc, row.Actor, row.Action, row.ListId,
                    row.RecordId, row.Revision, row.FieldIds, row.TargetSubject)).ToListAsync(ct);
        }, [(null, ListAuthorizationAction.ReadAudit)], ct);
    }

    private static ListException HostAuthorityUnsupported() => new(ListFailure.NotSupported,
        "Host-authorized workspaces manage membership and grants in the host authority store.");
}
