using CellBridge.AspNetCore;
using CellBridge.Lists;
using CellBridge.Lists.AspNetCore;
using CellBridge.Lists.PostgreSql;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var content = new InMemoryContentStore();
var documents = new StorageProvider(new InMemoryDocumentRepository(content), content);

builder.Services.AddCellBridge(documents, requireDurability: false);
builder.Services.AddAuthentication("CellBridge");
builder.Services.AddCellBridgeCookieLogin<ExampleLoginAuthenticator>("CellBridge");

var listConnection = builder.Configuration.GetConnectionString("lists")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:lists.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(listConnection));
builder.Services.AddSingleton<IListAuthorizationProvider, ExampleListAuthorization>();
builder.Services.AddSingleton<IListEngine>(services => new PostgreSqlListEngine(
    services.GetRequiredService<NpgsqlDataSource>(),
    null,
    services.GetRequiredService<IListAuthorizationProvider>()));
builder.Services.AddCellBridgeLists(options => options.ActorResolver = context =>
    CellBridgeActor.FromPrincipal(context.User) is { } actor
        ? new ListActor(actor.Identity.Subject, "example-tenant")
        : null);

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapCellBridge();
app.MapCellBridgeLists();
app.MapGet("/", () => "CellBridge Documents and Lists example").AllowAnonymous();

var service = app.Services.GetRequiredService<CellBridgeDocumentService>();
await service.CreateAsync("/shared/example.txt", "Example document\n"u8.ToArray(),
    new(new("example:owner", "owner", "Example owner"), CanCreate: true));

await app.RunAsync();

public partial class Program;
