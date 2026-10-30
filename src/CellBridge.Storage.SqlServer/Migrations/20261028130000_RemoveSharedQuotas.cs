using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CellBridge.Storage.SqlServer.Migrations;

[DbContext(typeof(SqlServerDocumentDbContext))]
[Migration("20261028130000_RemoveSharedQuotas")]
public sealed class RemoveSharedQuotas : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "usage_buckets", schema: "cellbridge_documents");
        migrationBuilder.DropTable(name: "usage", schema: "cellbridge_documents");
        migrationBuilder.Sql("UPDATE cellbridge_documents.schema_version SET Version=3 WHERE Id=1 AND Version=2;");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => SqlServerDocumentDbContext.ConfigureModel(modelBuilder);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Restoring provider-wide storage quotas is not supported.");
}
