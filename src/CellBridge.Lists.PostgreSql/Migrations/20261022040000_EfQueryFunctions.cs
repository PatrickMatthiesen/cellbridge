using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CellBridge.Lists.PostgreSql.Migrations;

[DbContext(typeof(PostgreSqlListDbContext))]
[Migration("20261022040000_EfQueryFunctions")]
public sealed class EfQueryFunctions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(PostgreSqlListMigrationSql.ReadQueryFunctionsV4());

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("The Lists schema migration does not support destructive downgrade.");
}
