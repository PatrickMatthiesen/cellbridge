using CellBridge.AspNetCore;
using CellBridge.DocumentLibrary;

var builder = WebApplication.CreateBuilder(args);

var provider = builder.Services.AddDocumentLibrary(builder.Configuration, builder.Environment);
builder.Services.AddCellBridge(provider, requireDurability: !builder.Environment.IsEnvironment("Testing"));
builder.Services.AddDocumentLibraryTestLogin(builder.Environment);

var app = builder.Build();
app.UseDocumentLibrarySecurityHeaders();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapDocumentLibrary();
app.MapCellBridge();
app.MapHealthChecks("/health").AllowAnonymous();

await app.InitializeDocumentLibraryAsync();
await app.RunAsync();

public partial class Program;
