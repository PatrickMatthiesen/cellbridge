using Microsoft.EntityFrameworkCore;

namespace CellBridge.Lists.SqlServer;

public sealed partial class SqlServerListEngine
{
    private static async Task CheckConfigurationAsync(SqlServerListDbContext db, CancellationToken ct)
    {
        var versions = await db.SchemaVersions.AsNoTracking().Select(row => row.Version).ToListAsync(ct);
        if (versions.Count != 1 || versions[0] != 1) throw InvalidStored();
        var known = db.Database.GetMigrations().Order(StringComparer.Ordinal);
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).Order(StringComparer.Ordinal);
        if (!known.SequenceEqual(applied, StringComparer.Ordinal))
            throw new ListException(ListFailure.Unavailable, "Explicit SQL Server Lists migration is required.");
        var safe = await db.Database.SqlQueryRaw<int>("""
            SELECT CASE WHEN
                COALESCE(IS_SRVROLEMEMBER('sysadmin'),1)=0
                AND COALESCE(IS_MEMBER('db_owner'),1)=0
                AND COALESCE(IS_MEMBER('db_ddladmin'),1)=0
                AND HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CONTROL')=0
                AND HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','ALTER ANY SECURITY POLICY')=0
                AND HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','ALTER ANY USER')=0
                AND HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','ALTER ANY ROLE')=0
                AND HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CREATE TABLE')=0
                AND HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CREATE SCHEMA')=0
                AND HAS_PERMS_BY_NAME('cellbridge_lists','SCHEMA','ALTER')=0
                AND HAS_PERMS_BY_NAME('cellbridge_lists','SCHEMA','CONTROL')=0
                AND HAS_PERMS_BY_NAME('cellbridge_lists','SCHEMA','TAKE OWNERSHIP')=0
                AND HAS_PERMS_BY_NAME('cellbridge_lists','SCHEMA','VIEW DEFINITION')=1
                AND (SELECT delayed_durability_desc FROM sys.databases WHERE database_id=DB_ID())='DISABLED'
                THEN 1 ELSE 0 END AS Value
            """).SingleAsync(ct);
        if (safe != 1)
            throw new ListException(ListFailure.Unavailable, "The SQL Server Lists runtime requires restricted privileges, schema metadata visibility and immediate durability.");

        // Metadata visibility is required explicitly, so missing or disabled policies
        // cannot be mistaken for metadata hidden from a restricted user.
        var policies = await db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS Value FROM sys.security_policies policy
            JOIN sys.schemas ns ON ns.schema_id=policy.schema_id
            WHERE ns.name='cellbridge_lists' AND policy.is_enabled=1 AND policy.is_schema_bound=1
                AND EXISTS (SELECT 1 FROM sys.security_predicates predicate
                    JOIN sys.tables target ON target.object_id=predicate.target_object_id
                    WHERE predicate.object_id=policy.object_id
                        AND target.schema_id=ns.schema_id AND policy.name=target.name+'_isolation'
                        AND target.name IN ('workspaces','memberships','lists','list_schemas','list_counters','list_grants',
                            'records','record_tombstones','create_receipts','record_references','saved_views','audit_events'))
                AND (SELECT COUNT(*) FROM sys.security_predicates predicate WHERE predicate.object_id=policy.object_id)=3
            """).SingleAsync(ct);
        var trigger = await db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS Value FROM sys.triggers
            WHERE object_id=OBJECT_ID('cellbridge_lists.immutable_workspace_authorization_anchor')
                AND parent_id=OBJECT_ID('cellbridge_lists.workspaces') AND is_disabled=0
            """).SingleAsync(ct);
        if (policies != 12 || trigger != 1) throw InvalidStored();

        var expected = System.Text.Json.JsonSerializer.Serialize(RuntimePermissions.SelectMany(entry =>
            entry.Permissions.Select(permission => new { tableName = "cellbridge_lists." + entry.Table, permission })));
        var missing = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS Value FROM OPENJSON({expected})
                WITH (tableName nvarchar(256), permission nvarchar(32)) required
            WHERE COALESCE(HAS_PERMS_BY_NAME(required.tableName, 'OBJECT', required.permission), 0) <> 1
            """).SingleAsync(ct);
        if (missing != 0)
            throw new ListException(ListFailure.Unavailable, "Required SQL Server Lists runtime permissions are missing.");
        var excessive = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS Value
            FROM sys.tables target JOIN sys.schemas ns ON ns.schema_id=target.schema_id
            CROSS JOIN (VALUES ('SELECT'),('INSERT'),('UPDATE'),('DELETE'),('ALTER'),('CONTROL'),('TAKE OWNERSHIP')) permission(name)
            WHERE ns.name='cellbridge_lists'
                AND HAS_PERMS_BY_NAME(ns.name+'.'+target.name, 'OBJECT', permission.name)=1
                AND NOT EXISTS (SELECT 1 FROM OPENJSON({expected})
                    WITH (tableName nvarchar(256), permission nvarchar(32)) required
                    WHERE required.tableName=ns.name+'.'+target.name AND required.permission=permission.name)
            """).SingleAsync(ct);
        if (excessive != 0)
            throw new ListException(ListFailure.Unavailable, "The SQL Server Lists runtime has excess table privileges.");
    }

    // First-slice minimum permissions. Setup/migrations must use another principal.
    // No database/schema owner, DDL or security-policy authority is permitted.
    internal static readonly (string Table, string[] Permissions)[] RuntimePermissions =
    [
        ("schema_version", ["SELECT"]),
        ("templates", ["SELECT"]),
        ("record_references", ["SELECT", "INSERT", "UPDATE", "DELETE"]),
        (SqlServerListDbContext.MigrationsHistoryTable, ["SELECT"]),
        ("workspaces", ["SELECT", "UPDATE"]),
        ("memberships", ["SELECT", "INSERT", "UPDATE", "DELETE"]),
        ("lists", ["SELECT", "INSERT", "UPDATE"]),
        ("list_schemas", ["SELECT", "INSERT"]),
        ("list_counters", ["SELECT", "INSERT", "UPDATE"]),
        ("list_grants", ["SELECT", "INSERT", "UPDATE", "DELETE"]),
        ("records", ["SELECT", "INSERT", "UPDATE", "DELETE"]),
        ("record_tombstones", ["SELECT", "INSERT"]),
        ("create_receipts", ["SELECT", "INSERT"]),
        ("audit_events", ["SELECT", "INSERT"])
    ];
}
