using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;

internal static class TestActor
{
    public static void Register(IServiceCollection services) => services.AddAuthentication("test")
        .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
    public static CellBridgeActor Value { get; } = new(new("tests:writer", "test-writer", "Test writer"), CanCreate: true);

    public static HttpClient CreateClient(TestServer server, string user, params Claim[] additionalClaims) => new(server.CreateHandler(context =>
    {
        var claims = new List<Claim>
        {
            new(CellBridgeActor.SubjectClaim, "tests:" + user), new(ClaimTypes.Name, "test-" + user),
            new(CellBridgeActor.DisplayNameClaim, "Test " + user),
        };
        claims.AddRange(additionalClaims);
        if (user == "writer") claims.Add(new(CellBridgeActor.CreateClaim, "true"));
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    })) { BaseAddress = server.BaseAddress };
}

internal static class TestStorage
{
    public static StorageProvider Memory(StorageLimits? limits = null)
    {
        limits ??= new();
        var content = new CellBridge.Storage.InMemory.InMemoryContentStore(limits.MaxObjectBytes);
        return new(new CellBridge.Storage.InMemory.InMemoryDocumentRepository(content, limits), content, limits);
    }

    public static DocumentActorAdmission Admit(DocumentHead head, CellBridgeActor? actor = null) => new((actor ?? TestActor.Value).Identity,
        head.ResourceId, head.LifecycleGeneration, head.Security.AuthorizationPolicy, DocumentAccess.Read | DocumentAccess.Write)
        { StateVersion = head.StateVersion, AuthorityGeneration = head.AuthorityGeneration };

    public static async Task SetPermissionsAsync(StorageProvider provider, Guid id, DocumentSecurity security)
    {
        var control = await provider.Documents.ReadControlAsync(id) ?? throw new KeyNotFoundException("Test document does not exist.");
        var result = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(id, Admit(control.Head),
            security, control.Head.AuthorityGeneration));
        if (!result.Changed) throw new InvalidOperationException("Test permission command failed: " + result.ErrorCode);
    }

    public static async Task<byte[]> ReadPackageAsync(StorageProvider provider, DocumentPackage package)
    {
        await using var stream = await provider.Packages.OpenReadAsync(package);
        using var result = new MemoryStream();
        await stream.CopyToAsync(result);
        return result.ToArray();
    }
}

internal sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        return Task.FromResult(Context.User.Identity?.IsAuthenticated == true
            ? AuthenticateResult.Success(new(Context.User, Scheme.Name))
            : AuthenticateResult.NoResult());
    }
}
