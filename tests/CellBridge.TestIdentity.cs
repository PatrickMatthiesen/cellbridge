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

    public static HttpClient CreateClient(TestServer server, string user) => new(server.CreateHandler(context =>
    {
        var claims = new List<Claim>
        {
            new(CellBridgeActor.SubjectClaim, "tests:" + user), new(ClaimTypes.Name, "test-" + user),
            new(CellBridgeActor.DisplayNameClaim, "Test " + user),
        };
        if (user == "writer") claims.Add(new(CellBridgeActor.CreateClaim, "true"));
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    })) { BaseAddress = server.BaseAddress };
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
