using CellBridge.Authentication;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Storage.FileSystem;
using CellBridge.WebDemo;
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
        options.MaxConcurrentRequestsPerSubject = builder.Configuration.GetValue("Protocol:MaxConcurrentRequestsPerSubject", options.MaxConcurrentRequestsPerSubject);
        options.RequestTimeout = builder.Configuration.GetValue("Protocol:RequestTimeout", options.RequestTimeout);
        options.PublicOrigin = builder.Configuration["Protocol:PublicOrigin"];
        options.MaxCaptureBytes = builder.Configuration.GetValue("Protocol:MaxCaptureBytes", options.MaxCaptureBytes);
        options.MaxMtomParts = builder.Configuration.GetValue("Protocol:MaxMtomParts", options.MaxMtomParts);
        options.MaxMtomHeaderBytes = builder.Configuration.GetValue("Protocol:MaxMtomHeaderBytes", options.MaxMtomHeaderBytes);
        options.CaptureDirectory = builder.Configuration["Protocol:CaptureDirectory"];
    });
builder.Services.AddCellBridgeAuthentication(builder.Configuration);
builder.Services.AddWebDemo();
var app = builder.Build();
// Tailscale Serve terminates HTTPS and forwards over loopback HTTP. The framework
// default trusts loopback proxies only; keep that restriction for forwarded origins.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
});
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/library/error");
    app.UseHsts();
}
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseRequestTimeouts();
app.UseAntiforgery();
await AuthenticationDatabase.CheckSchemaAsync(app.Configuration.GetConnectionString("cellbridge")!);
await provider.CheckHealthAsync();
if (!provider.Capabilities.Durable)
    app.Logger.LogWarning("InMemory storage is volatile. A restart discards documents and protocol state.");

app.MapCellBridgeAuthentication();
app.MapCellBridge();
app.MapWebDemo();
app.MapDefaultEndpoints();
await app.RunAsync();

public partial class Program;
