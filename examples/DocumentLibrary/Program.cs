using System.Net;
using System.Security.Claims;
using CellBridge.AspNetCore;
using CellBridge.DocumentLibrary;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using Microsoft.AspNetCore.Authentication.Cookies;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var libraryOptions = builder.Configuration.GetSection("DocumentLibrary").Get<DocumentLibraryOptions>() ?? new();
if (libraryOptions.PublicationInterval <= TimeSpan.Zero)
    throw new InvalidOperationException("DocumentLibrary:PublicationInterval must be positive.");
if (libraryOptions.MaxUploadBytes <= 0)
    throw new InvalidOperationException("DocumentLibrary:MaxUploadBytes must be positive.");
libraryOptions.DestinationRoot = Path.GetFullPath(libraryOptions.DestinationRoot ??
    Path.Combine(builder.Environment.ContentRootPath, ".document-library-data"));
builder.Services.AddSingleton(libraryOptions);

StorageProvider provider;
if (libraryOptions.StorageProvider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
{
    if (!builder.Environment.IsEnvironment("Testing"))
        throw new InvalidOperationException("The in-memory provider is allowed only in the Testing environment.");
    provider = new(new InMemoryStateStore(), new InMemoryContentStore());
}
else if (libraryOptions.StorageProvider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = builder.Configuration.GetConnectionString("cellbridge")
        ?? throw new InvalidOperationException("ConnectionStrings:cellbridge is required.");
    var dataSource = NpgsqlDataSource.Create(connectionString);
    builder.Services.AddSingleton(dataSource);
    provider = new(new PostgreSqlStateStore(dataSource), new PostgreSqlContentStore(dataSource));
}
else
{
    throw new InvalidOperationException("DocumentLibrary:StorageProvider must be PostgreSql, or InMemory under Testing.");
}

builder.Services.AddSingleton(_ => new DocumentLibraryDestination(libraryOptions.DestinationRoot));
builder.Services.AddSingleton<DocumentLibraryPermissionPolicy>();
builder.Services.AddSingleton<ICellBridgeAuthorizationPolicy>(sp =>
    sp.GetRequiredService<DocumentLibraryPermissionPolicy>());
builder.Services.AddCellBridge(provider, requireDurability: !builder.Environment.IsEnvironment("Testing"));
builder.Services.AddSingleton(sp => new ExternalRevisionPublisher(provider,
    sp.GetRequiredService<DocumentLibraryDestination>()));
builder.Services.AddSingleton<PublicationWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PublicationWorker>());
builder.Services.AddSingleton<DocumentLibraryService>();
builder.Services.AddAntiforgery();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "CellBridge.DocumentLibrary.Local";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
        options.LoginPath = "/local-login";
        options.AccessDeniedPath = "/local-login";
    });
builder.Services.AddCellBridgeOfficeFormsAuthentication();
builder.Services.AddAuthorization(options =>
    options.AddPolicy("permission-admin", policy => policy.RequireClaim("document-library:permission-admin", "true")));

var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
    await next();
});
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

DocumentLibraryEndpoints.Map(app, builder.Environment);
app.MapCellBridge();
app.MapHealthChecks("/health").AllowAnonymous();

await provider.CheckHealthAsync();
await app.Services.GetRequiredService<DocumentLibraryService>().InitializeAsync();
await app.RunAsync();

public partial class Program;
