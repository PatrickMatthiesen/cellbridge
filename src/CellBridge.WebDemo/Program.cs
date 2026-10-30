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
using CellBridge.Lists;
using CellBridge.Lists.AspNetCore;
using CellBridge.Lists.PostgreSql;
using CellBridge.Lists.SqlServer;
using CellBridge.Storage.SqlServer;
using Microsoft.EntityFrameworkCore;

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
    var maxBytes = limits.MaxObjectBytes;
    IContentStore content = builder.Configuration["Storage:ContentProvider"]?.ToLowerInvariant() switch
    {
        null or "postgresql" => new PostgreSqlContentStore(dataSource, maxBytes),
        "filesystem" => new PostgreSqlFileSystemContentStore(dataSource, builder.Configuration["Storage:ContentRoot"]
            ?? throw new InvalidOperationException("Filesystem content requires Storage:ContentRoot."), maxBytes,
            builder.Configuration.GetValue("Storage:SharedContent", false)),
        _ => throw new InvalidOperationException("Unknown binary content provider."),
    };
    var state = new PostgreSqlStateStore(dataSource, limits, content);
    provider = new StorageProvider(state, content, limits);
}
else if (storageKind.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = builder.Configuration.GetConnectionString("documents")
        ?? throw new InvalidOperationException("SqlServer storage requires ConnectionStrings:documents.");
    var options = new DbContextOptionsBuilder<SqlServerDocumentDbContext>().UseSqlServer(connectionString).Options;
    provider = new(new SqlServerStateStore(options, limits), new SqlServerContentStore(options, limits.MaxObjectBytes), limits);
}
else if (storageKind.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
{
    var content = new InMemoryContentStore(limits.MaxObjectBytes);
    provider = new(new InMemoryDocumentRepository(content, limits), content, limits);
}
else throw new InvalidOperationException("Unknown document storage provider.");

builder.Services.AddCellBridge(provider, requireDurability: !storageKind.Equals("InMemory", StringComparison.OrdinalIgnoreCase),
    multipleInstances: builder.Configuration.GetValue("Storage:MultipleInstances", false), configure: options =>
    {
        options.MaxRequestBytes = builder.Configuration.GetValue("Protocol:MaxRequestBytes", options.MaxRequestBytes);
        options.MaxConcurrentRequests = builder.Configuration.GetValue("Protocol:MaxConcurrentRequests", options.MaxConcurrentRequests);
        options.MaxConcurrentRequestsPerSubject = builder.Configuration.GetValue("Protocol:MaxConcurrentRequestsPerSubject", options.MaxConcurrentRequestsPerSubject);
        options.RequestTimeout = builder.Configuration.GetValue("Protocol:RequestTimeout", options.RequestTimeout);
        options.PublicOrigin = builder.Configuration["Protocol:PublicOrigin"];
        options.DocumentLibraryId = builder.Configuration.GetValue<Guid?>("Protocol:DocumentLibraryId");
        options.MaxCaptureBytes = builder.Configuration.GetValue("Protocol:MaxCaptureBytes", options.MaxCaptureBytes);
        options.MaxMtomParts = builder.Configuration.GetValue("Protocol:MaxMtomParts", options.MaxMtomParts);
        options.MaxMtomHeaderBytes = builder.Configuration.GetValue("Protocol:MaxMtomHeaderBytes", options.MaxMtomHeaderBytes);
        options.CaptureDirectory = builder.Configuration["Protocol:CaptureDirectory"];
    });
builder.Services.AddCellBridgeAuthentication(builder.Configuration);
var listsConnection = builder.Configuration.GetConnectionString("lists");
Func<IServiceProvider, Task>? checkListsHealth = null;
if (!string.IsNullOrWhiteSpace(listsConnection))
{
    var listsKind = builder.Configuration["Lists:Provider"] ?? "PostgreSql";
    if (listsKind.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddKeyedSingleton<NpgsqlDataSource>("lists", (_, _) => NpgsqlDataSource.Create(listsConnection));
        builder.Services.AddSingleton(services =>
            new PostgreSqlListEngine(services.GetRequiredKeyedService<NpgsqlDataSource>("lists")));
        builder.Services.AddSingleton<IListEngine>(services => services.GetRequiredService<PostgreSqlListEngine>());
        checkListsHealth = services => services.GetRequiredService<PostgreSqlListEngine>().CheckHealthAsync();
    }
    else if (listsKind.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
    {
        var options = new DbContextOptionsBuilder<SqlServerListDbContext>().UseSqlServer(listsConnection).Options;
        builder.Services.AddSingleton(new SqlServerListEngine(options));
        builder.Services.AddSingleton<IListEngine>(services => services.GetRequiredService<SqlServerListEngine>());
        checkListsHealth = services => services.GetRequiredService<SqlServerListEngine>().CheckHealthAsync();
    }
    else throw new InvalidOperationException("Unknown Lists storage provider.");
    builder.Services.AddCellBridgeLists(options => options.ActorResolver = context =>
        CellBridgeActor.FromPrincipal(context.User) is { } actor ? new ListActor(actor.Identity.Subject) : null);
}
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
if (checkListsHealth is not null) await checkListsHealth(app.Services);
if (!provider.Capabilities.Durable)
    app.Logger.LogWarning("InMemory storage is volatile. A restart discards documents and protocol state.");

app.MapCellBridgeAuthentication();
app.MapCellBridge();
if (!string.IsNullOrWhiteSpace(listsConnection)) app.MapCellBridgeLists();
app.MapWebDemo();
app.MapDefaultEndpoints();
await app.RunAsync();

public partial class Program;
