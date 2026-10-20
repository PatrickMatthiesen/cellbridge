var builder = DistributedApplication.CreateBuilder(args);
var marker = builder.Configuration["RecoveryRun"]
    ?? throw new InvalidOperationException("The recovery fixture must supply its run identifier.");
if (!Guid.TryParseExact(marker, "N", out _)) throw new InvalidOperationException("Invalid recovery run identifier.");
// The fault tests restart this exact container. The fixture verifies ownership
// and removes it after Aspire disposal. PGDATA never uses a developer volume.
builder.AddPostgres("recovery-storage")
    .WithImageTag("18.3")
    .WithContainerName($"cellbridge-recovery-{marker}")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithEnvironment("PGDATA", $"/cellbridge-recovery-data/{marker}")
    .WithEnvironment("CELLBRIDGE_RECOVERY_RUN", marker)
    .WithContainerRuntimeArgs("--label", $"cellbridge.recovery={marker}",
        "--tmpfs", "/var/lib/postgresql:rw")
    .AddDatabase("cellbridge");
builder.Build().Run();
