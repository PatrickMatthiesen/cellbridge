using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CellBridge.Lists.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class ListsSchemaV3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "cellbridge_lists");

            migrationBuilder.CreateTable(
                name: "schema_version",
                schema: "cellbridge_lists",
                columns: table => new
                {
                    singleton = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("schema_version_pkey", x => x.singleton);
                    table.CheckConstraint("schema_version_singleton_check", "singleton");
                });

            migrationBuilder.CreateTable(
                name: "templates",
                schema: "cellbridge_lists",
                columns: table => new
                {
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    definition = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("templates_pkey", x => new { x.template_id, x.version });
                    table.CheckConstraint("templates_version_check", "version > 0");
                });

            migrationBuilder.CreateTable(
                name: "workspaces",
                schema: "cellbridge_lists",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    authorization_mode = table.Column<string>(type: "text", nullable: false, defaultValue: "builtin"),
                    authorization_domain = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    authorization_scope = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    stored_bytes = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    audit_count = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    security_audit_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("workspaces_pkey", x => x.workspace_id);
                    table.CheckConstraint("workspaces_audit_count_check", "audit_count >= 0");
                    table.CheckConstraint("workspaces_authorization_anchor_check", "(authorization_mode='builtin' AND authorization_domain IS NULL AND authorization_scope IS NULL) OR (authorization_mode='host' AND authorization_domain IS NOT NULL AND authorization_scope IS NOT NULL AND char_length(authorization_domain) BETWEEN 1 AND 200 AND btrim(authorization_domain)<>'' AND char_length(authorization_scope) BETWEEN 1 AND 1024 AND btrim(authorization_scope)<>'')");
                    table.CheckConstraint("workspaces_security_audit_count_check", "security_audit_count >= 0");
                    table.CheckConstraint("workspaces_stored_bytes_check", "stored_bytes >= 0");
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "cellbridge_lists",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: true),
                    record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: true),
                    field_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]"),
                    target_subject = table.Column<string>(type: "text", nullable: true),
                    security = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("audit_events_pkey", x => new { x.workspace_id, x.id });
                    table.ForeignKey(
                        name: "audit_events_workspace_id_fkey",
                        column: x => x.workspace_id,
                        principalSchema: "cellbridge_lists",
                        principalTable: "workspaces",
                        principalColumn: "workspace_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lists",
                schema: "cellbridge_lists",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    schema_version = table.Column<long>(type: "bigint", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: true),
                    template_version = table.Column<long>(type: "bigint", nullable: true),
                    number_prefix = table.Column<string>(type: "text", nullable: true),
                    next_number = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("lists_pkey", x => new { x.workspace_id, x.list_id });
                    table.CheckConstraint("lists_next_number_check", "next_number > 0");
                    table.CheckConstraint("lists_schema_version_check", "schema_version > 0");
                    table.ForeignKey(
                        name: "lists_template_id_template_version_fkey",
                        columns: x => new { x.template_id, x.template_version },
                        principalSchema: "cellbridge_lists",
                        principalTable: "templates",
                        principalColumns: new[] { "template_id", "version" });
                    table.ForeignKey(
                        name: "lists_workspace_id_fkey",
                        column: x => x.workspace_id,
                        principalSchema: "cellbridge_lists",
                        principalTable: "workspaces",
                        principalColumn: "workspace_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "memberships",
                schema: "cellbridge_lists",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    can_create_lists = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    can_manage_grants = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("memberships_pkey", x => new { x.workspace_id, x.subject });
                    table.ForeignKey(
                        name: "memberships_workspace_id_fkey",
                        column: x => x.workspace_id,
                        principalSchema: "cellbridge_lists",
                        principalTable: "workspaces",
                        principalColumn: "workspace_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "create_receipts",
                schema: "cellbridge_lists",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    schema_version = table.Column<long>(type: "bigint", nullable: false),
                    request_digest = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    number = table.Column<long>(type: "bigint", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("create_receipts_pkey", x => new { x.workspace_id, x.list_id, x.record_id });
                    table.ForeignKey(
                        name: "create_receipts_workspace_id_list_id_fkey",
                        columns: x => new { x.workspace_id, x.list_id },
                        principalSchema: "cellbridge_lists",
                        principalTable: "lists",
                        principalColumns: new[] { "workspace_id", "list_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "list_counters",
                schema: "cellbridge_lists",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    identity_count = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    next_number = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("list_counters_pkey", x => new { x.workspace_id, x.list_id });
                    table.CheckConstraint("list_counters_identity_count_check", "identity_count >= 0");
                    table.CheckConstraint("list_counters_next_number_check", "next_number > 0");
                    table.ForeignKey(
                        name: "list_counters_workspace_id_list_id_fkey",
                        columns: x => new { x.workspace_id, x.list_id },
                        principalSchema: "cellbridge_lists",
                        principalTable: "lists",
                        principalColumns: new[] { "workspace_id", "list_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "list_schemas",
                schema: "cellbridge_lists",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    schema_version = table.Column<long>(type: "bigint", nullable: false),
                    definition = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("list_schemas_pkey", x => new { x.workspace_id, x.list_id, x.schema_version });
                    table.CheckConstraint("list_schemas_schema_version_check", "schema_version > 0");
                    table.ForeignKey(
                        name: "list_schemas_workspace_id_list_id_fkey",
                        columns: x => new { x.workspace_id, x.list_id },
                        principalSchema: "cellbridge_lists",
                        principalTable: "lists",
                        principalColumns: new[] { "workspace_id", "list_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "record_tombstones",
                schema: "cellbridge_lists",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("record_tombstones_pkey", x => new { x.workspace_id, x.list_id, x.record_id });
                    table.ForeignKey(
                        name: "record_tombstones_workspace_id_list_id_fkey",
                        columns: x => new { x.workspace_id, x.list_id },
                        principalSchema: "cellbridge_lists",
                        principalTable: "lists",
                        principalColumns: new[] { "workspace_id", "list_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "saved_views",
                schema: "cellbridge_lists",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    view_id = table.Column<Guid>(type: "uuid", nullable: false),
                    definition = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("saved_views_pkey", x => new { x.workspace_id, x.list_id, x.view_id });
                    table.ForeignKey(
                        name: "saved_views_workspace_id_list_id_fkey",
                        columns: x => new { x.workspace_id, x.list_id },
                        principalSchema: "cellbridge_lists",
                        principalTable: "lists",
                        principalColumns: new[] { "workspace_id", "list_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "list_grants",
                schema: "cellbridge_lists",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    access = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("list_grants_pkey", x => new { x.workspace_id, x.list_id, x.subject });
                    table.CheckConstraint("list_grants_access_check", "access >= 0 AND access <= 7");
                    table.ForeignKey(
                        name: "list_grants_workspace_id_list_id_fkey",
                        columns: x => new { x.workspace_id, x.list_id },
                        principalSchema: "cellbridge_lists",
                        principalTable: "lists",
                        principalColumns: new[] { "workspace_id", "list_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "list_grants_workspace_id_subject_fkey",
                        columns: x => new { x.workspace_id, x.subject },
                        principalSchema: "cellbridge_lists",
                        principalTable: "memberships",
                        principalColumns: new[] { "workspace_id", "subject" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "records",
                schema: "cellbridge_lists",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    schema_version = table.Column<long>(type: "bigint", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    number = table.Column<long>(type: "bigint", nullable: false),
                    values_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    modified_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("records_pkey", x => new { x.workspace_id, x.list_id, x.record_id });
                    table.UniqueConstraint("records_workspace_id_list_id_number_key", x => new { x.workspace_id, x.list_id, x.number });
                    table.CheckConstraint("records_number_check", "number > 0");
                    table.CheckConstraint("records_revision_check", "revision > 0");
                    table.ForeignKey(
                        name: "records_workspace_id_list_id_schema_version_fkey",
                        columns: x => new { x.workspace_id, x.list_id, x.schema_version },
                        principalSchema: "cellbridge_lists",
                        principalTable: "list_schemas",
                        principalColumns: new[] { "workspace_id", "list_id", "schema_version" });
                });

            migrationBuilder.CreateTable(
                name: "record_references",
                schema: "cellbridge_lists",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_record_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("record_references_pkey", x => new { x.workspace_id, x.source_list_id, x.source_record_id, x.field_id });
                    table.ForeignKey(
                        name: "record_references_source_fk",
                        columns: x => new { x.workspace_id, x.source_list_id, x.source_record_id },
                        principalSchema: "cellbridge_lists",
                        principalTable: "records",
                        principalColumns: new[] { "workspace_id", "list_id", "record_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "record_references_target_fk",
                        columns: x => new { x.workspace_id, x.target_list_id, x.target_record_id },
                        principalSchema: "cellbridge_lists",
                        principalTable: "records",
                        principalColumns: new[] { "workspace_id", "list_id", "record_id" });
                });

            migrationBuilder.CreateIndex(
                name: "record_references_by_target",
                schema: "cellbridge_lists",
                table: "record_references",
                columns: new[] { "workspace_id", "target_list_id", "target_record_id" });

            migrationBuilder.InsertData("schema_version",
                new[] { "singleton", "version" }, new object[] { true, 3 }, "cellbridge_lists");
            migrationBuilder.Sql(PostgreSqlListMigrationSql.ReadWorkspaceSecurityV3());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
            "The Lists schema migration does not support destructive downgrade.");
    }
}
