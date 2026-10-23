using System.Reflection;
using System.Runtime.Loader;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using Npgsql;

internal static class Providers
{
    private static readonly OriginalProviderContext Context = new();
    private static readonly Lazy<Assembly> sqlLists = new(() => Load("CellBridge.Lists.PostgreSql"));
    private static readonly Lazy<Assembly> sqlStorage = new(() => Load("CellBridge.Storage.PostgreSql"));
    public static Assembly SqlLists => sqlLists.Value;
    public static Assembly SqlStorage => sqlStorage.Value;
    public static Assembly[] SqlAssemblies => Settings.Variants.Contains("sql") ? [SqlLists, SqlStorage] : [];
    private static Assembly Load(string name)
    {
        var directory = Path.GetFullPath(Environment.GetEnvironmentVariable("CB_SQL_PROVIDER_DIRECTORY")
            ?? throw new InvalidOperationException("Set CB_SQL_PROVIDER_DIRECTORY for SQL comparisons. See docs/postgresql-comparison.md."));
        Settings.Require(File.ReadAllText(Path.Combine(directory, "SOURCE_HEAD")).Trim() == Settings.SqlHead,
            "SQL providers must be built from the recorded SQL source commit.");
        var file = name + ".dll";
        var expected = File.ReadAllLines(Path.Combine(directory, "SHA256SUMS"))
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Single(parts => parts.Length == 2 && parts[1] == file)[0];
        var path = Path.Combine(directory, file);
        Settings.Require(Settings.Hash(File.ReadAllBytes(path)) == expected, "SQL provider DLL hash mismatch.");
        var assembly = Context.LoadFromAssemblyPath(path);
        Settings.Require(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Contains(Settings.SqlHead, StringComparison.Ordinal) == true, "SQL provider assembly source identity mismatch.");
        return assembly;
    }
    public static IListEngine Lists(string variant, NpgsqlDataSource source) => variant switch
    {
        "ef" => new PostgreSqlListEngine(source),
        "sql" => (IListEngine)Activator.CreateInstance(SqlLists.GetType("CellBridge.Lists.PostgreSql.PostgreSqlListEngine")!, source, null)!,
        _ => throw new ArgumentException("Unknown variant")
    };
    public static IDocumentStateStore State(string variant, NpgsqlDataSource source, StorageLimits limits) => variant switch
    {
        "ef" => new PostgreSqlStateStore(source, limits),
        "sql" => (IDocumentStateStore)Activator.CreateInstance(SqlStorage.GetType("CellBridge.Storage.PostgreSql.PostgreSqlStateStore")!, source, limits)!,
        _ => throw new ArgumentException("Unknown variant")
    };
    public static IContentStore Content(string variant, NpgsqlDataSource source, StorageLimits limits) => variant switch
    {
        "ef" => new PostgreSqlContentStore(source, limits.MaxObjectBytes),
        "sql" => (IContentStore)Activator.CreateInstance(SqlStorage.GetType("CellBridge.Storage.PostgreSql.PostgreSqlContentStore")!, source, limits.MaxObjectBytes)!,
        _ => throw new ArgumentException("Unknown variant")
    };
    // Only these two original assemblies enter the custom context. All contracts, core,
    // Npgsql and TestInfrastructure resolve in Default from this worktree's project references.
    private sealed class OriginalProviderContext() : AssemblyLoadContext("immutable-original-sql-providers", isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName name) =>
            AssemblyLoadContext.Default.LoadFromAssemblyName(name);
    }
}
