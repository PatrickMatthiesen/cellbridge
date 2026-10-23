using Microsoft.EntityFrameworkCore.Migrations;

namespace CellBridge.Lists.SqlServer.Migrations;

[Migration("20261024000001_ExactQueries")]
public sealed partial class ExactQueries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(ExactQueries).Assembly.GetManifestResourceStream(
            "CellBridge.Lists.SqlServer.Migrations.20261024000001_ExactQueries.sql")!;
        using var reader = new StreamReader(stream);
        foreach (var batch in reader.ReadToEnd().Split("\nGO\n", StringSplitOptions.RemoveEmptyEntries))
            migrationBuilder.Sql(batch);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Destructive Lists query downgrade is not supported.");
}
