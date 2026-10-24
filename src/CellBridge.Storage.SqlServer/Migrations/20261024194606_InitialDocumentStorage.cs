using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CellBridge.Storage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class InitialDocumentStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "cellbridge_documents");

            migrationBuilder.CreateTable(
                name: "documents",
                schema: "cellbridge_documents",
                columns: table => new
                {
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PathKey = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    PathHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    RetiredPathCount = table.Column<int>(type: "int", nullable: false),
                    StateVersion = table.Column<long>(type: "bigint", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
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
                name: "objects",
                schema: "cellbridge_documents",
                columns: table => new
                {
                    Key = table.Column<string>(type: "char(64)", nullable: false, collation: "Latin1_General_100_BIN2"),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "char(64)", nullable: false, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_objects", x => x.Key);
                    table.CheckConstraint("objects_length", "[Length] >= 0");
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

            migrationBuilder.CreateTable(
                name: "usage",
                schema: "cellbridge_documents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    StoredBytes = table.Column<long>(type: "bigint", nullable: false),
                    DocumentCount = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    MaxStoredBytes = table.Column<long>(type: "bigint", nullable: false),
                    MaxDocuments = table.Column<long>(type: "bigint", nullable: false, defaultValue: 10000L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage", x => x.Id);
                    table.CheckConstraint("usage_bytes", "[StoredBytes] >= 0 AND [MaxStoredBytes] > 0");
                    table.CheckConstraint("usage_documents", "[DocumentCount] >= 0 AND [MaxDocuments] > 0");
                    table.CheckConstraint("usage_singleton", "[Id] = 1");
                });

            migrationBuilder.CreateTable(
                name: "chunks",
                schema: "cellbridge_documents",
                columns: table => new
                {
                    Key = table.Column<string>(type: "char(64)", nullable: false, collation: "Latin1_General_100_BIN2"),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Bytes = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chunks", x => new { x.Key, x.Ordinal });
                    table.CheckConstraint("chunks_length", "DATALENGTH([Bytes]) BETWEEN 1 AND 65536");
                    table.CheckConstraint("chunks_ordinal", "[Ordinal] >= 0");
                    table.ForeignKey(
                        name: "FK_chunks_objects_Key",
                        column: x => x.Key,
                        principalSchema: "cellbridge_documents",
                        principalTable: "objects",
                        principalColumn: "Key");
                });

            migrationBuilder.InsertData(
                schema: "cellbridge_documents",
                table: "schema_version",
                columns: new[] { "Id", "Version" },
                values: new object[] { 1, 1 });

            migrationBuilder.InsertData(
                schema: "cellbridge_documents",
                table: "usage",
                columns: new[] { "Id", "MaxDocuments", "MaxStoredBytes", "StoredBytes" },
                values: new object[] { 1, 10000L, 10737418240L, 0L });

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
                name: "chunks",
                schema: "cellbridge_documents");

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

            migrationBuilder.DropTable(
                name: "usage",
                schema: "cellbridge_documents");

            migrationBuilder.DropTable(
                name: "objects",
                schema: "cellbridge_documents");
        }
    }
}
