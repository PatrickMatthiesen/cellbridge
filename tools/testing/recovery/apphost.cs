#:package Aspire.Hosting.PostgreSQL@17.0.0-preview.1.26502.1
#:sdk Aspire.AppHost.Sdk@17.0.0-preview.1.26479.11
#:property AspireUseCliBundle=true

var builder = DistributedApplication.CreateBuilder(args);
var marker = Environment.GetEnvironmentVariable("CELLBRIDGE_RECOVERY_RUN")
    ?? throw new InvalidOperationException("Start through tools/testing/recovery.py.");
if (!Guid.TryParseExact(marker, "N", out _)) throw new InvalidOperationException("Invalid recovery run marker.");

// PGDATA lives in this disposable container's writable layer, so stop/start
// retains it. Mask the image's unused VOLUME with tmpfs; no named volume or
// developer bind mount is used. Removing the container discards the database.
var storage = builder.AddPostgres("recovery-storage")
    .WithImageTag("18.3")
    .WithContainerName($"cellbridge-recovery-{marker}")
    // Reuse this exact container after a fault. The runner removes it in finally;
    // persistent lifetime here does not mean persistent development storage.
    .WithLifetime(ContainerLifetime.Persistent)
    .WithEnvironment("PGDATA", $"/cellbridge-recovery-data/{marker}")
    .WithEnvironment("CELLBRIDGE_RECOVERY_RUN", marker)
    .WithContainerRuntimeArgs("--label", $"cellbridge.recovery={marker}",
        "--tmpfs", "/var/lib/postgresql:rw");
// Provides Aspire's generated connection environment to the runner. Tests run
// outside the AppHost, after container ownership has been checked.
builder.AddExecutable("connection", "dotnet", "../../..", "--version")
    .WithReference(storage)
    .WaitFor(storage);
builder.Build().Run();
