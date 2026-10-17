using Npgsql;

namespace CellBridge.Lists.PostgreSql;

public sealed partial class PostgreSqlListEngine
{
    public async Task SetMembershipAsync(ListActor actor, Guid workspaceId, string subject, bool member,
        bool canCreateLists, bool canManageGrants, CancellationToken ct = default)
    {
        ValidateActor(actor);
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 1024 || !ListValidation.IsWellFormedText(subject)) throw Validation("The membership subject is invalid.");
        await InTransactionAsync(workspaceId, true, async (connection, transaction) =>
        {
            var administrator = await AuthorizeMemberAsync(connection, transaction, workspaceId, actor.Subject, ct);
            if (!administrator.CanManageGrants) throw Forbidden();
            await LockQuotaAsync(connection, transaction, workspaceId, ct);
            await using var existingCommand = Command(connection, transaction,
                "SELECT can_create_lists,can_manage_grants FROM cellbridge_lists.memberships WHERE workspace_id=$1 AND subject=$2",
                workspaceId, subject);
            await using var reader = await existingCommand.ExecuteReaderAsync(ct);
            bool? oldCreate = null;
            bool? oldManage = null;
            if (await reader.ReadAsync(ct))
            {
                oldCreate = reader.GetBoolean(0);
                oldManage = reader.GetBoolean(1);
            }
            await reader.DisposeAsync();
            var reduction = oldCreate is not null && (!member || oldCreate.Value && !canCreateLists || oldManage!.Value && !canManageGrants);
            if (member)
            {
                if (oldCreate is null) await ReserveStorageAsync(connection, transaction, workspaceId, 128 + System.Text.Encoding.UTF8.GetByteCount(subject), ct);
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO cellbridge_lists.memberships(workspace_id,subject,can_create_lists,can_manage_grants)
                    VALUES ($1,$2,$3,$4) ON CONFLICT (workspace_id,subject) DO UPDATE
                    SET can_create_lists=EXCLUDED.can_create_lists,can_manage_grants=EXCLUDED.can_manage_grants
                    """, ct, workspaceId, subject, canCreateLists, canManageGrants);
            }
            else if (oldCreate is not null)
            {
                var grantCount = await ScalarAsync<long>(connection, transaction,
                    "SELECT count(*) FROM cellbridge_lists.list_grants WHERE workspace_id=$1 AND subject=$2", ct,
                    workspaceId, subject);
                await ExecuteAsync(connection, transaction,
                    "DELETE FROM cellbridge_lists.memberships WHERE workspace_id=$1 AND subject=$2", ct, workspaceId, subject);
                var subjectBytes = System.Text.Encoding.UTF8.GetByteCount(subject);
                await ReserveStorageAsync(connection, transaction, workspaceId,
                    -(128 + subjectBytes + grantCount * (80 + subjectBytes)), ct);
            }
            await AuditAsync(connection, transaction, workspaceId, actor.Subject,
                member ? "membership.set" : "membership.remove", null, null, null, Array.Empty<Guid>(), reduction, ct, subject);
            return true;
        }, ct);
    }

    public async Task SetListGrantAsync(ListActor actor, Guid workspaceId, Guid listId, string subject,
        ListAccess access, CancellationToken ct = default)
    {
        ValidateActor(actor);
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 1024 || !ListValidation.IsWellFormedText(subject)) throw Validation("The grant subject is invalid.");
        if ((access & ~(ListAccess.Read | ListAccess.Write | ListAccess.Design)) != 0)
            throw Validation("The list access value is invalid.");
        await InTransactionAsync(workspaceId, true, async (connection, transaction) =>
        {
            var administrator = await AuthorizeMemberAsync(connection, transaction, workspaceId, actor.Subject, ct);
            if (!administrator.CanManageGrants) throw Forbidden();
            await LockQuotaAsync(connection, transaction, workspaceId, ct);
            if (await ScalarAsync<long>(connection, transaction,
                "SELECT count(*) FROM cellbridge_lists.lists WHERE workspace_id=$1 AND list_id=$2", ct, workspaceId, listId) == 0)
                throw NotFound();
            if (access != ListAccess.None && await ScalarAsync<long>(connection, transaction,
                "SELECT count(*) FROM cellbridge_lists.memberships WHERE workspace_id=$1 AND subject=$2", ct, workspaceId, subject) == 0)
                throw Validation("The grant subject is not a workspace member.");
            var old = (ListAccess)(await ScalarAsync<int?>(connection, transaction,
                "SELECT access FROM cellbridge_lists.list_grants WHERE workspace_id=$1 AND list_id=$2 AND subject=$3", ct,
                workspaceId, listId, subject) ?? 0);
            var reduction = (old & access) != old;
            if (access == ListAccess.None)
            {
                await ExecuteAsync(connection, transaction,
                    "DELETE FROM cellbridge_lists.list_grants WHERE workspace_id=$1 AND list_id=$2 AND subject=$3", ct,
                    workspaceId, listId, subject);
                if (old != ListAccess.None)
                    await ReserveStorageAsync(connection, transaction, workspaceId,
                        -(80 + System.Text.Encoding.UTF8.GetByteCount(subject)), ct);
            }
            else
            {
                if (old == ListAccess.None) await ReserveStorageAsync(connection, transaction, workspaceId, 80 + System.Text.Encoding.UTF8.GetByteCount(subject), ct);
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO cellbridge_lists.list_grants(workspace_id,list_id,subject,access) VALUES ($1,$2,$3,$4)
                    ON CONFLICT (workspace_id,list_id,subject) DO UPDATE SET access=EXCLUDED.access
                    """, ct, workspaceId, listId, subject, (int)access);
            }
            await AuditAsync(connection, transaction, workspaceId, actor.Subject, "grant.set", listId, null, null,
                Array.Empty<Guid>(), reduction, ct, subject);
            return true;
        }, ct);
    }

    public Task<IReadOnlyList<AuditEvent>> GetAuditAsync(ListActor actor, Guid workspaceId, int limit = 100,
        CancellationToken ct = default)
    {
        if (limit is <= 0 or > 1000) throw Validation("The audit page size is invalid.");
        return ReadAsync(actor, workspaceId, async (connection, transaction, member) =>
        {
            if (!member.CanManageGrants) throw Forbidden();
            await using var command = Command(connection, transaction, """
                SELECT id,utc,actor,action,list_id,record_id,revision,field_ids,target_subject
                FROM cellbridge_lists.audit_events WHERE workspace_id=$1 ORDER BY id DESC LIMIT $2
                """, workspaceId, limit);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var events = new List<AuditEvent>();
            while (await reader.ReadAsync(ct))
                events.Add(new AuditEvent(reader.GetInt64(0), new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero),
                    reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.GetFieldValue<Guid[]>(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
            return (IReadOnlyList<AuditEvent>)events;
        }, ct);
    }
}
