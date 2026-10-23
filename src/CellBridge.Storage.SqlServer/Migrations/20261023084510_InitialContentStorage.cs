using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CellBridge.Storage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class InitialContentStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "cellbridge_documents");

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
                name: "usage",
                schema: "cellbridge_documents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    StoredBytes = table.Column<long>(type: "bigint", nullable: false),
                    MaxStoredBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage", x => x.Id);
                    table.CheckConstraint("usage_bytes", "[StoredBytes] >= 0 AND [MaxStoredBytes] > 0");
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
                table: "usage",
                columns: new[] { "Id", "MaxStoredBytes", "StoredBytes" },
                values: new object[] { 1, 10737418240L, 0L });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chunks",
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
