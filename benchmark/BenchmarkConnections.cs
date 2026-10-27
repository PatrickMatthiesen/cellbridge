internal static class BenchmarkConnections
{
    public const string Documents = "CELLBRIDGE_BENCHMARK_DOCUMENT_CONNECTION_STRING";
    public const string ListsAdmin = "CELLBRIDGE_BENCHMARK_LISTS_ADMIN_CONNECTION_STRING";
    public const string ListsRuntime = "CELLBRIDGE_BENCHMARK_LISTS_RUNTIME_CONNECTION_STRING";

    public static string Require(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException(
            "Run database benchmarks through benchmark/CellBridge.Benchmarks so Aspire owns their environment.");
}
