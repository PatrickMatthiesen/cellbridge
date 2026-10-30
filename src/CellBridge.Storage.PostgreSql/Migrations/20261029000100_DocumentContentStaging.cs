using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CellBridge.Storage.PostgreSql.Migrations;

[DbContext(typeof(CellBridgeDbContext))]
[Migration(MigrationId)]
internal sealed class DocumentContentStaging : Migration
{
    internal const string MigrationId = "20261029000100_DocumentContentStaging";
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE cellbridge_repository_references
            ADD COLUMN "IsPending" boolean NOT NULL DEFAULT false,
            ADD COLUMN "AdmissionBytes" bigint NOT NULL DEFAULT 0;
        UPDATE cellbridge_schema SET version=10 WHERE version=9;
        """);
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => CellBridgeDbContext.ConfigureModel(modelBuilder);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Removing charged document content reservations is not supported.");
}
