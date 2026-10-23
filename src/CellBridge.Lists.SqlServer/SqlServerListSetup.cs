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
                await db.SaveChangesAsync(ct);
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
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return true;
        }, ct);
    }

    internal static byte[] SubjectKey(string subject) => SHA256.HashData(Encoding.Unicode.GetBytes(subject));
}
