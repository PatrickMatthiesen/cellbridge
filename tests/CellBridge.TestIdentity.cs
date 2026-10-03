using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
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
}

internal sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Test-User"].ToString();
        if (user.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
        var subject = "tests:" + user;
        var claims = new List<Claim>
        {
            new(CellBridgeActor.SubjectClaim, subject), new(ClaimTypes.Name, "test-" + user),
            new(CellBridgeActor.DisplayNameClaim, "Test " + user),
        };
        if (user == "writer") claims.Add(new(CellBridgeActor.CreateClaim, "true"));
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
