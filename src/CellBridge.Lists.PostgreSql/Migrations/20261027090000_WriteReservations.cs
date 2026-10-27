using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CellBridge.Lists.PostgreSql.Migrations;

[DbContext(typeof(PostgreSqlListDbContext))]
[Migration("20261027090000_WriteReservations")]
public sealed class WriteReservations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(WriteReservations).Assembly.GetManifestResourceStream(
            "CellBridge.Lists.PostgreSql.Migrations.20261027090000_WriteReservations.sql")
            ?? throw new InvalidOperationException("The Lists reservation migration resource is missing.");
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("The Lists reservation migration does not support destructive downgrade.");
}
