using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Storage.FileSystem;
using CellBridge.Web;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var storageKind = builder.Configuration["Storage:Provider"] ?? "InMemory";
var limits = builder.Configuration.GetSection("Storage").Get<StorageLimits>() ?? new StorageLimits();
limits.Validate();
StorageProvider provider;
if (storageKind.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = builder.Configuration.GetConnectionString("cellbridge")
        ?? throw new InvalidOperationException("PostgreSql storage requires ConnectionStrings:cellbridge.");
    var dataSource = NpgsqlDataSource.Create(connectionString);
    builder.Services.AddSingleton(dataSource);
    var state = new PostgreSqlStateStore(dataSource, limits);
    if (args.Contains("--migrate-storage", StringComparer.Ordinal))
    {
        await state.InitializeAsync();
        if (builder.Configuration["Storage:ContentProvider"]?.Equals("FileSystem", StringComparison.OrdinalIgnoreCase) == true)
            await new StorageMaintenance(dataSource).RegisterExistingFileSystemAsync(
                builder.Configuration["Storage:ContentRoot"] ?? throw new InvalidOperationException("Filesystem migration requires Storage:ContentRoot."));
        await dataSource.DisposeAsync();
        return;
    }
    var maxBytes = limits.MaxObjectBytes;
    IContentStore content = builder.Configuration["Storage:ContentProvider"]?.ToLowerInvariant() switch
    {
        null or "postgresql" => new PostgreSqlContentStore(dataSource, maxBytes),
        "filesystem" => new PostgreSqlFileSystemContentStore(dataSource, builder.Configuration["Storage:ContentRoot"]
            ?? throw new InvalidOperationException("Filesystem content requires Storage:ContentRoot."), maxBytes,
            builder.Configuration.GetValue("Storage:SharedContent", false)),
        _ => throw new InvalidOperationException("Unknown binary content provider."),
    };
    provider = new StorageProvider(state, content, limits);
}
else if (storageKind.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
    provider = new(new InMemoryStateStore(), new InMemoryContentStore(), limits);
else throw new InvalidOperationException("Unknown document storage provider.");

builder.Services.AddCellBridge(provider, requireDurability: storageKind.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase),
    multipleInstances: builder.Configuration.GetValue("Storage:MultipleInstances", false), configure: options =>
    {
        options.MaxRequestBytes = builder.Configuration.GetValue("Protocol:MaxRequestBytes", options.MaxRequestBytes);
        options.MaxConcurrentRequests = builder.Configuration.GetValue("Protocol:MaxConcurrentRequests", options.MaxConcurrentRequests);
        options.MaxMtomParts = builder.Configuration.GetValue("Protocol:MaxMtomParts", options.MaxMtomParts);
        options.MaxMtomHeaderBytes = builder.Configuration.GetValue("Protocol:MaxMtomHeaderBytes", options.MaxMtomHeaderBytes);
        options.CaptureDirectory = builder.Configuration["Protocol:CaptureDirectory"];
    });
var app = builder.Build();
// Tailscale Serve terminates HTTPS and forwards over loopback HTTP. The framework
// default trusts loopback proxies only; keep that restriction for forwarded origins.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
});
await provider.CheckHealthAsync();
if (!provider.Capabilities.Durable)
    app.Logger.LogWarning("InMemory storage is volatile. A restart discards documents and protocol state.");

// Imports only create missing documents. Persisted saves always take precedence.
var imports = new DocumentStore();
DocumentLibrary.Seed(imports, app.Configuration, app.Environment.ContentRootPath);
var service = app.Services.GetRequiredService<CellBridgeDocumentService>();
foreach (var document in imports.List())
{
    if (await provider.State.FindByPathKeyAsync(StorageIds.PathKey(document.Url)) is not null) continue;
    var escaped = string.Join('/', document.Url.Split('/').Select(Uri.EscapeDataString));
    await service.CreateAsync(escaped, document.Content);
}

app.MapCellBridge();
app.MapDefaultEndpoints();
app.MapGet("/api/documents", async (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var offset = int.TryParse(context.Request.Query["offset"], out var parsedOffset) ? Math.Max(0, parsedOffset) : 0;
    var limit = int.TryParse(context.Request.Query["limit"], out var parsedLimit) ? Math.Clamp(parsedLimit, 1, 1000) : 1000;
    var documents = await provider.State.ListAsync(offset, limit, context.RequestAborted);
    return Results.Ok(documents.Where(d => d.Path.StartsWith("/shared/", StringComparison.OrdinalIgnoreCase))
        .Select(d => new DocumentListing(d.Path, Path.GetFileName(d.Path),
        DocumentLibrary.ContentType(d.Path), d.Length, d.ContentVersion, d.ModifiedUtc, d.ActiveEditors)));
});
app.MapPost("/api/documents", async (CreateDocumentRequest request, HttpContext context) =>
{
    var detached = new DocumentStore();
    var result = DocumentCreation.TryCreate(detached, request.Name, request.Type);
    if (!result.Created) return Results.BadRequest(new { error = result.Error });
    var document = detached.List().Single();
    var escaped = string.Join('/', document.Url.Split('/').Select(Uri.EscapeDataString));
    CellBridge.Storage.Abstractions.DocumentState? created;
    try { created = await service.CreateAsync(escaped, document.Content, context.RequestAborted); }
    catch (StorageQuotaExceededException ex) { return Results.Json(new { error = ex.Message, budget = ex.Budget }, statusCode: 507); }
    return created is null ? Results.Conflict(new { error = "A document with that name already exists." })
        : Results.Created(escaped, result.Document);
});
await app.RunAsync();
