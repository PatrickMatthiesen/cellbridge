using System.Security.Claims;
using System.Text;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder(args);
var content = new InMemoryContentStore();
var provider = new StorageProvider(new InMemoryDocumentRepository(content), content);
var nativeEntra = builder.Configuration.GetValue<bool>("Entra:Enabled");
if (nativeEntra)
{
    // The host explicitly configures the cookie; CellBridge does not replace
    // its defaults or own the account store.
    builder.Services.AddAuthentication("HostCookie").AddCookie("HostCookie", options =>
    {
        options.Cookie.Name = EntraAuthentication.SessionCookieName;
        options.Cookie.Path = "/";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    });
    builder.Services.AddCellBridge(provider, requireDurability: false,
        configure: options => options.PublicOrigin = builder.Configuration["PublicOrigin"],
        configureHosting: hosting => hosting.Authentication(auth =>
        {
            auth.UseMapper<ConfiguredEntraAccountMapper>("HostCookie");
            auth.UseEntra(builder.Configuration.GetSection("Entra"));
        }));
}
else
{
    builder.Services.AddCellBridge(provider, requireDurability: false);
    // A bearer-token API host can use any configured authority. This branch
    // alone does not implement native Office sign-in.
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Authentication:Authority"]
            ?? throw new InvalidOperationException("Configure Authentication:Authority.");
        options.Audience = builder.Configuration["Authentication:Audience"]
            ?? throw new InvalidOperationException("Configure Authentication:Audience.");
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "preferred_username";
        options.Events.OnTokenValidated = context =>
        {
            var identity = (ClaimsIdentity)context.Principal!.Identity!;
            var subject = identity.FindFirst("sub")?.Value;
            if (string.IsNullOrWhiteSpace(subject)) { context.Fail("A verified subject is required."); return Task.CompletedTask; }
            // Remove any provider-supplied CellBridge claims before our trusted mapping.
            foreach (var claim in identity.Claims.Where(c => c.Type.StartsWith("cellbridge:", StringComparison.Ordinal)).ToArray())
                identity.RemoveClaim(claim);
            identity.AddClaim(new(CellBridgeActor.SubjectClaim, "oidc:" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(context.SecurityToken.Issuer)) + ":" + subject));
            identity.AddClaim(new(CellBridgeActor.DisplayNameClaim, identity.FindFirst("name")?.Value ?? subject));
            return Task.CompletedTask;
        };
    });
}
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapCellBridge();
app.MapHealthChecks("/health");
var ownerSubject = builder.Configuration["Documents:OwnerSubject"]
    ?? throw new InvalidOperationException("Set Documents:OwnerSubject to the mapped subject of your test token.");
var documents = app.Services.GetRequiredService<CellBridgeDocumentService>();
await documents.ImportAsync("/shared/example.txt", Encoding.UTF8.GetBytes("Hello from CellBridge!\n"),
    new(ownerSubject, "example-owner", "Example owner"), new(new("system:example", "example", "Example import"), true));
await app.RunAsync();

// A single explicit account link keeps this example independent of a database.
// Real hosts query their existing account links and check current account status.
internal sealed class ConfiguredEntraAccountMapper(IConfiguration configuration)
    : ICellBridgeExternalIdentityMapper
{
    public ValueTask<ClaimsPrincipal?> MapAsync(ValidatedExternalIdentity identity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var matches = identity.Provider == EntraAuthentication.LoginProvider &&
            Guid.TryParse(configuration["Entra:TenantId"], out var tenant) && identity.TenantId == tenant &&
            Guid.TryParse(configuration["Entra:UserObjectId"], out var user) && identity.ObjectId == user;
        var subject = configuration["Documents:OwnerSubject"];
        if (!matches || string.IsNullOrWhiteSpace(subject))
            return ValueTask.FromResult<ClaimsPrincipal?>(null);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(CellBridgeActor.SubjectClaim, subject),
            new Claim(CellBridgeActor.DisplayNameClaim, "Example owner"),
        }, "HostAccount"));
        return ValueTask.FromResult<ClaimsPrincipal?>(principal);
    }
}
