using CellBridge.Storage.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CellBridge.AspNetCore;

public sealed class CellBridgeStorageHealthCheck(StorageProvider provider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await provider.CheckHealthAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) { return HealthCheckResult.Unhealthy("The configured document/content storage is unavailable.", ex); }
    }
}
