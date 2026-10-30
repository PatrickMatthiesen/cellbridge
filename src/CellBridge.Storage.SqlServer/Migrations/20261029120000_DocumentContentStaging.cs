using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CellBridge.Storage.SqlServer.Migrations;

[DbContext(typeof(SqlServerDocumentDbContext))]
[Migration(MigrationId)]
internal sealed class DocumentContentStaging : Migration
{
    internal const string MigrationId = "20261029120000_DocumentContentStaging";
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE cellbridge_documents.repository_references ADD
            [IsPending] bit NOT NULL CONSTRAINT [DF_repository_references_IsPending] DEFAULT 0,
            [AdmissionBytes] bigint NOT NULL CONSTRAINT [DF_repository_references_AdmissionBytes] DEFAULT 0;
        UPDATE cellbridge_documents.schema_version SET Version=5 WHERE Id=1 AND Version=4;
        """);
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => SqlServerDocumentDbContext.ConfigureModel(modelBuilder);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Removing charged document content reservations is not supported.");
}
