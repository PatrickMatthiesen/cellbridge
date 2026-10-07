using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using Microsoft.AspNetCore.Authentication.Cookies;
using Npgsql;

namespace CellBridge.DocumentLibrary;

// These helpers configure this example; they are not CellBridge package APIs.
internal static class DocumentLibraryHostingExtensions
{
    public static StorageProvider AddDocumentLibrary(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
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

        services.AddSingleton(_ => new DocumentLibraryDestination(libraryOptions.DestinationRoot));
        services.AddSingleton<DocumentLibraryPermissionPolicy>();
        // Register before AddCellBridge to use this example's permission revisions.
        services.AddSingleton<ICellBridgeAuthorizationPolicy>(sp =>
            sp.GetRequiredService<DocumentLibraryPermissionPolicy>());
        services.AddSingleton(sp => new ExternalRevisionPublisher(provider,
            sp.GetRequiredService<DocumentLibraryDestination>()));
        services.AddSingleton<PublicationWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<PublicationWorker>());
        services.AddSingleton<DocumentLibraryService>();
        services.AddAntiforgery();
        services.AddAuthorization(options =>
            options.AddPolicy("permission-admin", policy => policy.RequireClaim("document-library:permission-admin", "true")));
        return provider;
    }

    public static IServiceCollection AddDocumentLibraryTestLogin(this IServiceCollection services,
        IHostEnvironment environment)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "CellBridge.DocumentLibrary.Local";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = false;
                options.LoginPath = "/auth/login";
                options.ReturnUrlParameter = "returnUrl";
                options.AccessDeniedPath = "/auth/login";
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
        services.AddCellBridgeLogin<LocalLoginAuthenticator>(configure: options =>
            options.IsRequestAllowed = context => DocumentLibraryAuthentication.IsRequestAllowed(context, environment));
        return services;
    }

    public static WebApplication UseDocumentLibrarySecurityHeaders(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; style-src 'self' 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
            await next();
        });
        return app;
    }

    public static async Task InitializeDocumentLibraryAsync(this WebApplication app)
    {
        await app.Services.GetRequiredService<StorageProvider>().CheckHealthAsync();
        await app.Services.GetRequiredService<DocumentLibraryService>().InitializeAsync();
    }
}
