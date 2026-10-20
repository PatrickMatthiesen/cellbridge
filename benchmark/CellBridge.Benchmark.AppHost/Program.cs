var builder = DistributedApplication.CreateBuilder(args);

// This workflow owns an ephemeral PostgreSQL container. Each executable also
// owns its generated database and never resets a developer database.
var postgres = builder.AddPostgres("benchmark-postgres").WithImageTag("18.3");
var documents = postgres.AddDatabase("cellbridge");
var lists = postgres.AddDatabase("lists");

builder.AddProject<Projects.CellBridge_Storage_Benchmark>("storage-benchmark")
    .WithReference(documents)
    .WaitFor(documents)
    .WithEnvironment("StorageBenchmark__Arguments", builder.Configuration["StorageBenchmark:Arguments"] ?? """["--sizes","1","--clients","1,8","--iterations","64","--workload","delta-chain"]""")
    .WithExplicitStart();
builder.AddProject<Projects.CellBridge_Lists_Benchmark>("lists-benchmark")
    .WithReference(lists)
    .WaitFor(lists)
    .WithEnvironment("ListsBenchmark__Arguments", builder.Configuration["ListsBenchmark:Arguments"] ?? "")
    .WithExplicitStart();

builder.Build().Run();
