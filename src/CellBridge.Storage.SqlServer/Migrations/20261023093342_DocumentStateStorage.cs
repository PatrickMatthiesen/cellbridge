using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CellBridge.Storage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class DocumentStateStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DocumentCount",
                schema: "cellbridge_documents",
                table: "usage",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "MaxDocuments",
                schema: "cellbridge_documents",
                table: "usage",
                type: "bigint",
                nullable: false,
                defaultValue: 10000L);

            migrationBuilder.CreateTable(
                name: "documents",
                schema: "cellbridge_documents",
                columns: table => new
                {
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PathKey = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    PathHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    StateVersion = table.Column<long>(type: "bigint", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    RetiredPathCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documents", x => x.ResourceId);
                    table.CheckConstraint("documents_version", "[StateVersion] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "metadata",
                schema: "cellbridge_documents",
                columns: table => new
                {
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StateVersion = table.Column<long>(type: "bigint", nullable: false),
                    LifecycleGeneration = table.Column<long>(type: "bigint", nullable: false),
                    RevisionCount = table.Column<int>(type: "int", nullable: false),
                    Data = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata", x => x.ResourceId);
                    table.CheckConstraint("metadata_revisions", "[RevisionCount] > 0 AND [LifecycleGeneration] > 0");
                });

            migrationBuilder.CreateTable(
                name: "recovery_receipts",
                schema: "cellbridge_documents",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Data = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recovery_receipts", x => x.OperationId);
                });

            migrationBuilder.CreateTable(
                name: "reservations",
                schema: "cellbridge_documents",
                columns: table => new
                {
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    PathKey = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    PathHash = table.Column<byte[]>(type: "binary(32)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reservations", x => new { x.ResourceId, x.Ordinal });
                });

            migrationBuilder.CreateTable(
                name: "revisions",
                schema: "cellbridge_documents",
                columns: table => new
                {
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LifecycleGeneration = table.Column<long>(type: "bigint", nullable: false),
                    RevisionNumber = table.Column<decimal>(type: "decimal(20,0)", precision: 20, scale: 0, nullable: false),
                    Data = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_revisions", x => new { x.ResourceId, x.LifecycleGeneration, x.RevisionNumber });
                    table.CheckConstraint("revisions_number", "[RevisionNumber] BETWEEN 1 AND 18446744073709551615 AND [LifecycleGeneration] > 0");
                });

            migrationBuilder.CreateTable(
                name: "schema_version",
                schema: "cellbridge_documents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schema_version", x => x.Id);
                    table.CheckConstraint("schema_singleton", "[Id] = 1");
                });

            migrationBuilder.CreateTable(
                name: "snapshots",
                schema: "cellbridge_documents",
                columns: table => new
                {
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StateVersion = table.Column<long>(type: "bigint", nullable: false),
                    Data = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_snapshots", x => new { x.ResourceId, x.StateVersion });
                    table.CheckConstraint("snapshots_version", "[StateVersion] >= 0");
                });

            migrationBuilder.InsertData(
                schema: "cellbridge_documents",
                table: "schema_version",
                columns: new[] { "Id", "Version" },
                values: new object[] { 1, 1 });

            migrationBuilder.UpdateData(
                schema: "cellbridge_documents",
                table: "usage",
                keyColumn: "Id",
                keyValue: 1,
                column: "MaxDocuments",
                value: 10000L);

            migrationBuilder.AddCheckConstraint(
                name: "usage_documents",
                schema: "cellbridge_documents",
                table: "usage",
                sql: "[DocumentCount] >= 0 AND [MaxDocuments] > 0");

            migrationBuilder.CreateIndex(
                name: "IX_documents_PathHash",
                schema: "cellbridge_documents",
                table: "documents",
                column: "PathHash");

            migrationBuilder.CreateIndex(
                name: "IX_reservations_PathHash",
                schema: "cellbridge_documents",
                table: "reservations",
                column: "PathHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "documents",
                schema: "cellbridge_documents");

            migrationBuilder.DropTable(
                name: "metadata",
                schema: "cellbridge_documents");

            migrationBuilder.DropTable(
                name: "recovery_receipts",
                schema: "cellbridge_documents");

            migrationBuilder.DropTable(
                name: "reservations",
                schema: "cellbridge_documents");

            migrationBuilder.DropTable(
                name: "revisions",
                schema: "cellbridge_documents");

            migrationBuilder.DropTable(
                name: "schema_version",
                schema: "cellbridge_documents");

            migrationBuilder.DropTable(
                name: "snapshots",
                schema: "cellbridge_documents");

            migrationBuilder.DropCheckConstraint(
                name: "usage_documents",
                schema: "cellbridge_documents",
                table: "usage");

            migrationBuilder.DropColumn(
                name: "DocumentCount",
                schema: "cellbridge_documents",
                table: "usage");

            migrationBuilder.DropColumn(
                name: "MaxDocuments",
                schema: "cellbridge_documents",
                table: "usage");
        }
    }
}
