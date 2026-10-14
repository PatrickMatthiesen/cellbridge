#:package Aspire.Hosting.PostgreSQL@17.0.0-preview.1.26502.1
#:sdk Aspire.AppHost.Sdk@17.0.0-preview.1.26479.11
#:property AspireUseCliBundle=true

var builder = DistributedApplication.CreateBuilder(args);
var toolDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, ".."));
var repository = Path.GetFullPath(Path.Combine(toolDirectory, "..", ".."));
var cpuSet = builder.Configuration["Benchmark:CpuSet"];
var postgresMemory = builder.Configuration["Benchmark:PostgresMemory"];
var postgres = builder.AddPostgres("lists-benchmark-postgres");
if (!string.IsNullOrWhiteSpace(cpuSet))
    postgres.WithContainerRuntimeArgs(context => context.Args.Add("--cpuset-cpus=" + cpuSet));
if (!string.IsNullOrWhiteSpace(postgresMemory))
    postgres.WithContainerRuntimeArgs(context => context.Args.Add("--memory=" + postgresMemory));
var database = postgres.AddDatabase("lists");
var benchmarkArgs = new List<string>
{
    Path.Combine(toolDirectory, "bin", "Release", "net10.0", "CellBridge.Lists.Benchmark.dll"),
    "--clients", builder.Configuration["Benchmark:Clients"] ?? "1,8,32",
    "--workspaces", builder.Configuration["Benchmark:Workspaces"] ?? "1,16",
    "--iterations", builder.Configuration["Benchmark:Iterations"] ?? "200",
    "--target-per-minute", builder.Configuration["Benchmark:TargetPerMinute"] ?? "0",
    "--records", builder.Configuration["Benchmark:Records"] ?? "1000",
    "--modes", builder.Configuration["Benchmark:Modes"] ?? "provider,http",
    "--output", builder.Configuration["Benchmark:Output"] ?? "artifacts/lists-performance/report.json"
};
if (bool.TryParse(builder.Configuration["Benchmark:SkipRecords"], out var skip) && skip)
    benchmarkArgs.Add("--records-skip");
var command = "dotnet";
if (!string.IsNullOrWhiteSpace(cpuSet))
{
    if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Benchmark:CpuSet requires Linux taskset.");
    command = "taskset";
    benchmarkArgs.InsertRange(0, ["-c", cpuSet, "dotnet"]);
}
builder.AddExecutable("lists-benchmark", command, repository, benchmarkArgs.ToArray())
    .WithEnvironment("CELLBRIDGE_BENCHMARK_CPU_SET", cpuSet ?? "")
    .WithEnvironment("CELLBRIDGE_BENCHMARK_POSTGRES_MEMORY", postgresMemory ?? "")
    .WithReference(database)
    .WaitFor(database)
    .WithExplicitStart();
builder.Build().Run();
