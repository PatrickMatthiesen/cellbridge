using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CellBridge.Lists.PostgreSql.Migrations;

/// <summary>Adopts or upgrades legacy Lists schemas through schema version 3.</summary>
public partial class ListsSchemaV3 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(PostgreSqlListMigrationSql.ReadSchemaV3());

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("The Lists schema migration does not support destructive downgrade.");
}
