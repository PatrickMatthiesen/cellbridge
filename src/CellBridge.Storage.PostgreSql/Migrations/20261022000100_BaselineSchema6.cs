using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CellBridge.Storage.PostgreSql.Migrations
{
    /// <inheritdoc />
    internal sealed partial class BaselineSchema6 : Migration
    {
        internal const string MigrationId = "20261022000100_BaselineSchema6";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cellbridge_documents",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    path_key = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    state_version = table.Column<long>(type: "bigint", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("cellbridge_documents_pkey", x => x.resource_id);
                });

            migrationBuilder.CreateTable(
                name: "cellbridge_objects",
                columns: table => new
                {
                    object_key = table.Column<string>(type: "text", nullable: false),
                    length = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("cellbridge_objects_pkey", x => x.object_key);
                    table.CheckConstraint("cellbridge_objects_length_check", "length >= 0");
                });

            migrationBuilder.CreateTable(
                name: "cellbridge_read_heads",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state_version = table.Column<long>(type: "bigint", nullable: false),
                    lifecycle_generation = table.Column<long>(type: "bigint", nullable: false),
                    revision_count = table.Column<int>(type: "integer", nullable: false),
                    metadata_json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("cellbridge_read_heads_pkey", x => x.resource_id);
                    table.CheckConstraint("cellbridge_read_heads_revision_count_check", "revision_count > 0");
                });

            migrationBuilder.CreateTable(
                name: "cellbridge_recovery_receipts",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("cellbridge_recovery_receipts_pkey", x => x.operation_id);
                });

            migrationBuilder.CreateTable(
                name: "cellbridge_recovery_schema",
                columns: table => new
                {
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("cellbridge_recovery_schema_pkey", x => x.version);
                });

            migrationBuilder.CreateTable(
                name: "cellbridge_revision_summaries",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lifecycle_generation = table.Column<long>(type: "bigint", nullable: false),
                    revision_number = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    summary_json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("cellbridge_revision_summaries_pkey", x => new { x.resource_id, x.lifecycle_generation, x.revision_number });
                    table.CheckConstraint("cellbridge_revision_summaries_revision_number_check", "revision_number BETWEEN 1 AND 18446744073709551615");
                });

            migrationBuilder.CreateTable(
                name: "cellbridge_schema",
                columns: table => new
                {
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("cellbridge_schema_pkey", x => x.version);
                });

            migrationBuilder.CreateTable(
                name: "cellbridge_states",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state_version = table.Column<long>(type: "bigint", nullable: false),
                    state_json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("cellbridge_states_pkey", x => new { x.resource_id, x.state_version });
                });

            migrationBuilder.CreateTable(
                name: "cellbridge_usage",
                columns: table => new
                {
                    singleton = table.Column<bool>(type: "boolean", nullable: false),
                    stored_bytes = table.Column<long>(type: "bigint", nullable: false),
                    document_count = table.Column<long>(type: "bigint", nullable: false),
                    max_stored_bytes = table.Column<long>(type: "bigint", nullable: false),
                    max_documents = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("cellbridge_usage_pkey", x => x.singleton);
                    table.CheckConstraint("cellbridge_usage_document_count_check", "document_count >= 0");
                    table.CheckConstraint("cellbridge_usage_max_documents_check", "max_documents > 0");
                    table.CheckConstraint("cellbridge_usage_max_stored_bytes_check", "max_stored_bytes > 0");
                    table.CheckConstraint("cellbridge_usage_singleton_check", "singleton");
                    table.CheckConstraint("cellbridge_usage_stored_bytes_check", "stored_bytes >= 0");
                });

            migrationBuilder.CreateTable(
                name: "cellbridge_chunks",
                columns: table => new
                {
                    object_key = table.Column<string>(type: "text", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    bytes = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("cellbridge_chunks_pkey", x => new { x.object_key, x.ordinal });
                    table.ForeignKey(
                        name: "cellbridge_chunks_object_key_fkey",
                        column: x => x.object_key,
                        principalTable: "cellbridge_objects",
                        principalColumn: "object_key");
                });

            migrationBuilder.CreateIndex(
                name: "cellbridge_live_paths",
                table: "cellbridge_documents",
                column: "path_key",
                unique: true,
                filter: "NOT is_deleted");

            migrationBuilder.InsertData("cellbridge_schema", "version", 6);
            migrationBuilder.InsertData("cellbridge_recovery_schema", "version", 1);
            migrationBuilder.InsertData("cellbridge_usage",
                new[] { "singleton", "stored_bytes", "document_count", "max_stored_bytes", "max_documents" },
                new object[] { true, 0L, 0L, 10737418240L, 10000L });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
            "CellBridge storage migrations do not delete persisted document data.");
    }
}
