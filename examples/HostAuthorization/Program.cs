using System.Security.Claims;
using System.Text;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var signingKey = builder.Configuration["Authentication:SigningKey"]
    ?? throw new InvalidOperationException("Configure Authentication:SigningKey with a disposable key of at least 32 bytes.");
if (Encoding.UTF8.GetByteCount(signingKey) < 32) throw new InvalidOperationException("Signing key is too short.");
var permissions = new HostPermissionService();
builder.Services.AddSingleton(permissions);
builder.Services.AddCellBridge<HostPermissionService>(new(new InMemoryStateStore(), new InMemoryContentStore()), requireDurability: false);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new()
    {
        ValidateIssuer = true, ValidIssuer = "cellbridge-host-example",
        ValidateAudience = true, ValidAudience = "cellbridge",
        ValidateLifetime = true, ClockSkew = TimeSpan.Zero,
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
    };
    options.Events.OnTokenValidated = context =>
    {
        var identity = (ClaimsIdentity)context.Principal!.Identity!;
        var subject = identity.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject)) { context.Fail("A subject is required."); return Task.CompletedTask; }
        foreach (var claim in identity.Claims.Where(c => c.Type.StartsWith("cellbridge:", StringComparison.Ordinal)).ToArray())
            identity.RemoveClaim(claim);
        identity.AddClaim(new(CellBridgeActor.SubjectClaim, "example:" + subject));
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization(options => options.AddPolicy("permission-operator",
    policy => policy.RequireAuthenticatedUser().RequireClaim("sub", "permission-operator")));
var app = builder.Build();
app.UseAuthentication(); app.UseAuthorization(); app.MapCellBridge();
app.MapGet("/health", () => Results.Ok());
app.MapGet("/example/catalog", async (HttpContext context, CellBridgeDocumentService service, int offset = 0, int limit = 10) =>
{
    var actor = CellBridgeActor.FromPrincipal(context.User);
    if (actor is null) return Results.Unauthorized();
    if (offset < 0 || limit is < 1 or > 100) return Results.BadRequest();
    var page = await AuthorizedDocumentCatalog.ReadAsync(service, actor, offset, limit, context.RequestAborted);
    return Results.Ok(new { Documents = page.Documents.Select(d => new { d.ResourceId, d.Path, d.Length }), page.NextOffset });
}).RequireAuthorization();
// This is a separate trusted permission-operator privilege, never document Write.
app.MapPost("/example/permissions/{resourceId:guid}", async (Guid resourceId, PermissionUpdate update,
    CellBridgeDocumentService service, HttpContext context) =>
{
    var access = update.Access switch { "none" => DocumentAccess.None, "read" => DocumentAccess.Read,
        "write" => DocumentAccess.Write, _ => (DocumentAccess)(-1) };
    if (update.ExpectedRevision <= 0 || update.NextRevision <= update.ExpectedRevision || access == (DocumentAccess)(-1))
        return Results.BadRequest();
    try
    {
        // A real host fetches/installs its immutable permission revision outside the transaction here.
        permissions.Install(resourceId, update.NextRevision, access);
        bool changed = await service.UpdateAuthorizationAsync(resourceId, permissions.Binding(update.ExpectedRevision),
            permissions.Binding(update.NextRevision), context.RequestAborted);
        return changed ? Results.Ok(new { Revision = update.NextRevision }) : Results.Conflict();
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (InvalidOperationException) { return Results.Conflict(); }
}).RequireAuthorization("permission-operator");
var documents = app.Services.GetRequiredService<CellBridgeDocumentService>();
await documents.ImportAsync(Guid.Parse("0a0a19bc-9b47-4183-8764-41c117bb1f14"), "/shared/example.txt",
    Encoding.UTF8.GetBytes("Host-owned authorization\n"), new("system:owner", "owner", "Attribution only"),
    new(new("system:import", "import", "Import"), true));
await app.RunAsync();

public sealed record PermissionUpdate(long ExpectedRevision, long NextRevision, string Access);
