using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CellBridge.Storage.SqlServer.Migrations;

/// <inheritdoc />
public partial class DurableUsageAllowances : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "usage_buckets",
            schema: "cellbridge_documents",
            columns: table => new
            {
                BucketId = table.Column<int>(type: "int", nullable: false),
                AvailableBytes = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_usage_buckets", x => x.BucketId);
                table.CheckConstraint("usage_buckets_available", "[AvailableBytes] >= 0");
                table.CheckConstraint("usage_buckets_id", "[BucketId] BETWEEN 0 AND 63");
            });

        // SQL Server keeps escalated locks across a savepoint rollback. Claims
        // use ROWLOCK and touch one key, and disabling escalation also covers
        // memory-pressure escalation before an unsuccessful claim rolls back.
        migrationBuilder.Sql("ALTER TABLE cellbridge_documents.usage_buckets SET (LOCK_ESCALATION = DISABLE);");

        var buckets = new object[64, 2];
        for (var bucket = 0; bucket < 64; bucket++)
        {
            buckets[bucket, 0] = bucket;
            buckets[bucket, 1] = 0L;
        }
        migrationBuilder.InsertData(
            schema: "cellbridge_documents",
            table: "usage_buckets",
            columns: new[] { "BucketId", "AvailableBytes" },
            values: buckets);
        migrationBuilder.UpdateData(
            schema: "cellbridge_documents",
            table: "schema_version",
            keyColumn: "Id",
            keyValue: 1,
            column: "Version",
            value: 2);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Restore the exact ledger before removing durable unused allowances.
        migrationBuilder.Sql("""
            UPDATE cellbridge_documents.usage WITH (UPDLOCK,HOLDLOCK)
            SET StoredBytes = StoredBytes -
                (SELECT SUM(AvailableBytes) FROM cellbridge_documents.usage_buckets WITH (UPDLOCK,HOLDLOCK))
            WHERE Id=1;
            """);
        migrationBuilder.DropTable(name: "usage_buckets", schema: "cellbridge_documents");
        migrationBuilder.UpdateData(
            schema: "cellbridge_documents",
            table: "schema_version",
            keyColumn: "Id",
            keyValue: 1,
            column: "Version",
            value: 1);
    }
}
