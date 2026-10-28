using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CellBridge.Storage.PostgreSql.Migrations;

[DbContext(typeof(CellBridgeDbContext))]
[Migration(MigrationId)]
internal sealed class UsageAllowanceMigration : Migration
{
    internal const string MigrationId = "20261028000100_UsageAllowances";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE cellbridge_usage_buckets (
            bucket_id integer PRIMARY KEY CONSTRAINT cellbridge_usage_bucket_id_check CHECK (bucket_id BETWEEN 0 AND 63),
            available_bytes bigint NOT NULL CONSTRAINT cellbridge_usage_bucket_bytes_check CHECK (available_bytes >= 0)
        );
        INSERT INTO cellbridge_usage_buckets SELECT generate_series(0,63),0;
        UPDATE cellbridge_schema SET version=7 WHERE version=6;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        UPDATE cellbridge_usage SET stored_bytes=stored_bytes-(SELECT SUM(available_bytes) FROM cellbridge_usage_buckets);
        DROP TABLE cellbridge_usage_buckets;
        UPDATE cellbridge_schema SET version=6 WHERE version=7;
        """);

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => CellBridgeDbContext.ConfigureModel(modelBuilder);
}
