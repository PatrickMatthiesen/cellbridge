using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using Npgsql;

namespace CellBridge.DocumentLibrary;

// Storage choices and the directory belong to this application.
internal static class DocumentLibraryStorage
{
    public static StorageProvider Create(WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;
        var environment = builder.Environment;
        var libraryOptions = configuration.GetSection("DocumentLibrary").Get<DocumentLibraryOptions>() ?? new();
        if (libraryOptions.PublicationInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("DocumentLibrary:PublicationInterval must be positive.");
        if (libraryOptions.MaxUploadBytes <= 0)
            throw new InvalidOperationException("DocumentLibrary:MaxUploadBytes must be positive.");
        libraryOptions.DestinationRoot = Path.GetFullPath(libraryOptions.DestinationRoot ??
            Path.Combine(environment.ContentRootPath, ".document-library-data"));
        services.AddSingleton(libraryOptions);

        StorageProvider provider;
        if (libraryOptions.StorageProvider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            if (!environment.IsEnvironment("Testing"))
                throw new InvalidOperationException("The in-memory provider is allowed only in the Testing environment.");
            provider = new(new InMemoryStateStore(), new InMemoryContentStore());
        }
        else if (libraryOptions.StorageProvider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = configuration.GetConnectionString("cellbridge")
                ?? throw new InvalidOperationException("ConnectionStrings:cellbridge is required.");
            var dataSource = NpgsqlDataSource.Create(connectionString);
            services.AddSingleton(dataSource);
            provider = new(new PostgreSqlStateStore(dataSource), new PostgreSqlContentStore(dataSource));
        }
        else
        {
            throw new InvalidOperationException("DocumentLibrary:StorageProvider must be PostgreSql, or InMemory under Testing.");
        }

        return provider;
    }
}
