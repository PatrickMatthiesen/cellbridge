using CellBridge.Storage.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CellBridge.AspNetCore;

/// <summary>Settings for polling the durable queue of accepted file revisions.</summary>
public sealed class CellBridgeExternalPublicationOptions
{
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    internal void Validate()
    {
        if (PollingInterval < TimeSpan.FromMilliseconds(1) || PollingInterval > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(PollingInterval), "PollingInterval must be at least one millisecond and fit a timer.");
    }
}

/// <summary>Registers delivery of accepted revisions to the host's external file store.</summary>
public static class CellBridgeExternalPublishing
{
    /// <summary>Registers one shared destination, publisher and hosted queue worker.</summary>
    /// <remarks>Requires AddCellBridge. Existing singleton registrations of TDestination are reused.
    /// Documents must be explicitly bound through ExternalRevisionPublisher.BindAsync.
    /// The destination must implement durable idempotent receipts, including across host instances.
    /// Do not register another hosted worker, including through an opaque host factory.</remarks>
    public static IServiceCollection AddCellBridgeExternalPublishing<TDestination>(this IServiceCollection services,
        Action<CellBridgeExternalPublicationOptions>? configure = null)
        where TDestination : class, IExternalRevisionDestination
    {
        if (services.Any(x => !x.IsKeyedService && (x.ServiceType == typeof(IExternalRevisionDestination) ||
                              x.ServiceType == typeof(ExternalRevisionPublisher) ||
                              x.ServiceType == typeof(ExternalRevisionPublicationWorker) ||
                              x.ServiceType == typeof(CellBridgeExternalPublicationOptions) ||
                              x.ServiceType == typeof(IHostedService) &&
                              (x.ImplementationType == typeof(ExternalRevisionPublicationWorker) ||
                               x.ImplementationInstance is ExternalRevisionPublicationWorker))))
            throw new InvalidOperationException("Configure one CellBridge external destination and publisher per host.");
        CellBridgeHostingRegistration.RequireSingleton<TDestination>(services);
        var options = new CellBridgeExternalPublicationOptions();
        configure?.Invoke(options);
        options.Validate();
        services.AddSingleton(options);
        services.TryAddSingleton<TDestination>();
        services.AddSingleton<IExternalRevisionDestination>(sp => sp.GetRequiredService<TDestination>());
        services.AddSingleton<ExternalRevisionPublisher>();
        services.AddSingleton<ExternalRevisionPublicationWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<ExternalRevisionPublicationWorker>());
        return services;
    }
}
