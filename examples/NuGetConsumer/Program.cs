using System.Security.Claims;
using System.Text;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Authentication.JwtBearer;

var builder = WebApplication.CreateBuilder(args);
var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
builder.Services.AddCellBridge(provider, requireDurability: false);
// The consuming application supplies a real token authority. This sample is a
// bearer-token API host; desktop Office needs its own compatible sign-in flow.
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
