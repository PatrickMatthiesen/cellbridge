using System.Reflection;

namespace CellBridge.Lists.PostgreSql;

internal static class PostgreSqlListMigrationSql
{
    internal const string CurrentMigrationId = "20261022033144_ListsSchemaV3";
    internal const string QueryFunctionsMigrationId = "20261022040000_EfQueryFunctions";
    internal const string ProductVersion = "10.0.4";

    // The SQL-created schemas need their original data transforms. Fresh databases
    // use generated EF table operations followed by PostgreSQL security objects.
    internal static string ReadSchemaV3()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("CellBridge.Lists.PostgreSql.Migrations.20261022033144_ListsSchemaV3.sql")
            ?? throw new InvalidOperationException("The Lists schema migration resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static string ReadWorkspaceSecurityV3()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("CellBridge.Lists.PostgreSql.Migrations.20261022033144_WorkspaceSecurity.sql")
            ?? throw new InvalidOperationException("The Lists workspace security migration resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static string ReadQueryFunctionsV4()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("CellBridge.Lists.PostgreSql.Migrations.20261022040000_EfQueryFunctions.sql")
            ?? throw new InvalidOperationException("The Lists EF query migration resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
