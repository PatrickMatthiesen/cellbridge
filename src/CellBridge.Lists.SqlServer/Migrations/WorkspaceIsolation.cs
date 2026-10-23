using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CellBridge.Lists.SqlServer.Migrations;

public sealed partial class WorkspaceIsolation : Migration
{
    private static readonly string[] WorkspaceTables = ["workspaces", "memberships", "lists", "list_schemas",
        "list_counters", "list_grants", "records", "record_tombstones", "create_receipts",
        "record_references", "saved_views", "audit_events"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // CONTEXT_INFO avoids SESSION_CONTEXT's documented parallel-plan/reset issue.
        // SQL Server pads this session value to 128 bytes. The first sixteen hold
        // the workspace; an empty/reset value must never grant an empty workspace.
        migrationBuilder.Sql("""
            CREATE FUNCTION cellbridge_lists.workspace_visible(@workspace_id uniqueidentifier)
            RETURNS TABLE WITH SCHEMABINDING AS RETURN
                SELECT 1 AS visible WHERE DATALENGTH(CONTEXT_INFO()) = 128
                    AND @workspace_id <> CONVERT(uniqueidentifier, '00000000-0000-0000-0000-000000000000')
                    AND @workspace_id = CONVERT(uniqueidentifier, SUBSTRING(CONTEXT_INFO(),1,16));
            """);
        foreach (var table in WorkspaceTables)
            migrationBuilder.Sql($"""
                CREATE SECURITY POLICY cellbridge_lists.[{table}_isolation]
                    ADD FILTER PREDICATE cellbridge_lists.workspace_visible(workspace_id)
                        ON cellbridge_lists.[{table}],
                    ADD BLOCK PREDICATE cellbridge_lists.workspace_visible(workspace_id)
                        ON cellbridge_lists.[{table}] AFTER INSERT,
                    ADD BLOCK PREDICATE cellbridge_lists.workspace_visible(workspace_id)
                        ON cellbridge_lists.[{table}] AFTER UPDATE
                    WITH (STATE = ON, SCHEMABINDING = ON);
                """);
        migrationBuilder.Sql("""
            CREATE TRIGGER cellbridge_lists.immutable_workspace_authorization_anchor
                ON cellbridge_lists.workspaces AFTER UPDATE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT workspace_id, CONVERT(varbinary(max), authorization_mode),
                        CONVERT(varbinary(max), authorization_domain), CONVERT(varbinary(max), authorization_scope)
                    FROM inserted
                    EXCEPT
                    SELECT workspace_id, CONVERT(varbinary(max), authorization_mode),
                        CONVERT(varbinary(max), authorization_domain), CONVERT(varbinary(max), authorization_scope)
                    FROM deleted)
                    THROW 51001, 'A workspace authorization anchor is immutable.', 1;
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER cellbridge_lists.immutable_workspace_authorization_anchor;");
        foreach (var table in WorkspaceTables)
            migrationBuilder.Sql($"DROP SECURITY POLICY cellbridge_lists.[{table}_isolation];");
        migrationBuilder.Sql("DROP FUNCTION cellbridge_lists.workspace_visible;");
    }
}
