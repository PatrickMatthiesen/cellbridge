using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.AspNetCore;

internal static class CellBridgeHostingRegistration
{
    internal static void RequireSingleton<T>(IServiceCollection services)
    {
        var existing = services.LastOrDefault(x => x.ServiceType == typeof(T) && !x.IsKeyedService);
        if (existing is not null && existing.Lifetime != ServiceLifetime.Singleton)
            throw new InvalidOperationException($"{typeof(T).Name} must be registered as a singleton.");
    }
}
