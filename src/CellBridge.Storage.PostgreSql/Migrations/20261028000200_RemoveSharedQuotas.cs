using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CellBridge.Storage.PostgreSql.Migrations;

[DbContext(typeof(CellBridgeDbContext))]
[Migration(MigrationId)]
internal sealed class RemoveSharedQuotas : Migration
{
    internal const string MigrationId = "20261028000200_RemoveSharedQuotas";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TABLE cellbridge_usage_buckets;
        DROP TABLE cellbridge_usage;
        UPDATE cellbridge_schema SET version=8 WHERE version=7;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Restoring provider-wide storage quotas is not supported.");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => CellBridgeDbContext.ConfigureModel(modelBuilder);
}
