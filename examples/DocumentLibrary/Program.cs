using CellBridge.AspNetCore;
using CellBridge.DocumentLibrary;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

var provider = DocumentLibraryStorage.Create(builder);
builder.Services.AddSingleton(sp => new DocumentLibraryDestination(
    sp.GetRequiredService<DocumentLibraryOptions>().DestinationRoot!));
builder.Services.AddSingleton<DocumentLibraryService>();
builder.Services.AddAuthorization(options => options.AddPolicy("permission-admin",
    policy => policy.RequireClaim("document-library:permission-admin", "true")));

builder.Services.AddCellBridge<DocumentLibraryPermissionPolicy>(provider,
    requireDurability: !builder.Environment.IsEnvironment("Testing"));
builder.Services.AddCellBridgeExternalPublishing<DocumentLibraryDestination>(options =>
    options.PollingInterval = builder.Configuration.GetValue("DocumentLibrary:PublicationInterval", TimeSpan.FromSeconds(1)));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme);
builder.Services.AddCellBridgeCookieLogin<LocalLoginAuthenticator>(
    CookieAuthenticationDefaults.AuthenticationScheme,
    configureCookie: cookie =>
    {
        cookie.Cookie.Name = "CellBridge.DocumentLibrary.Local";
        cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    },
    configure: login => login.IsRequestAllowed = context =>
        DocumentLibraryAuthentication.IsRequestAllowed(context, builder.Environment));

var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; style-src 'self' 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapDocumentLibrary();
app.MapCellBridge();
app.MapHealthChecks("/health").AllowAnonymous();

await app.Services.GetRequiredService<StorageProvider>().CheckHealthAsync();
await app.Services.GetRequiredService<DocumentLibraryService>().InitializeAsync();
await app.RunAsync();

public partial class Program;
