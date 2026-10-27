using BenchmarkDotNet.Running;
using CellBridge.TestInfrastructure;

if (args.Length > 0 && args[0] == "--load-test")
    return await CellBridgeLoadTests.RunAsync(args[1..]);

var inspect = args.Any(argument => argument is "--list" or "--help" or "-h");
if (inspect) return RunBenchmarks(args);

// Keep Aspire's health checks and telemetry out of measured child processes.
await using var environment = await PostgreSqlEnvironment.StartAsync(initializeListsRuntime: true);
var childEnvironment = new Dictionary<string, string?>
{
    [BenchmarkConnections.Documents] = environment.ConnectionString,
    [BenchmarkConnections.ListsAdmin] = environment.ListsConnectionString,
    [BenchmarkConnections.ListsRuntime] = environment.ListsRuntimeConnectionString,
};
var previous = childEnvironment.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
try
{
    foreach (var (name, value) in childEnvironment) Environment.SetEnvironmentVariable(name, value);
    return RunBenchmarks(args);
}
finally
{
    foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
}

static int RunBenchmarks(string[] args)
{
    var summaries = BenchmarkSwitcher.FromAssemblies([
        typeof(AllocationBenchmarks).Assembly,
        typeof(PostgreSqlPagingBenchmarks).Assembly,
        typeof(PostgreSqlContentReadBenchmarks).Assembly,
    ]).Run(args.Length == 0 ? ["--filter", "*"] : args).ToArray();

    return args.Any(argument => argument is "--list" or "--help" or "-h") ? 0 :
        summaries.Length == 0 || summaries.Any(summary => summary.HasCriticalValidationErrors ||
            summary.Reports.Length == 0 || summary.Reports.Any(report => !report.Success)) ? 1 : 0;
}
