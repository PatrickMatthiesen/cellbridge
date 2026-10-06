using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using CellBridge.AspNetCore;
using CellBridge.Authentication;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.Storage.Tests;

public sealed class AuthenticationTests
{
    [PostgreSqlFact]
    public async Task CookiesShareIdentityAcrossHostsAndStampRevocationRejectsBoth()
    {
        var clock = new TestClock();
        await using var first = await Start(clock);
        await using var peer = await Start(clock, renew: false);
        var username = "auth-" + Guid.NewGuid().ToString("N");
        const string password = "Test-password-7292!";
        string subject;
        using (var scope = first.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<CellBridgeUser>>();
            var user = new CellBridgeUser { UserName = username, DisplayName = "Test author", CanCreate = true };
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
            subject = user.Subject;
        }
        using var client = first.GetTestClient();
        client.BaseAddress = new("https://localhost");
        using var challenge = await client.SendAsync(new(HttpMethod.Options, "/shared/"));
        Assert.Equal(HttpStatusCode.Forbidden, challenge.StatusCode);
        Assert.Equal("https://localhost/_cellbridge/auth/complete", challenge.Headers.GetValues("X-FORMS_BASED_AUTH_RETURN_URL").Single());
        using var apiChallenge = await client.GetAsync("/api/protected");
        Assert.Equal(HttpStatusCode.Unauthorized, apiChallenge.StatusCode);
        using var library = await client.GetAsync("/library");
        Assert.Equal(HttpStatusCode.Redirect, library.StatusCode);
        Assert.StartsWith("https://localhost/auth/login", library.Headers.Location!.ToString());

        using var loginPage = await client.GetAsync("/auth/login?returnUrl=//evil.example");
        var html = await loginPage.Content.ReadAsStringAsync();
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(token);
        var csrfCookie = loginPage.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AuthenticationServices.AntiforgeryCookie + "="));
        Assert.Contains("secure", csrfCookie, StringComparison.OrdinalIgnoreCase);
        using var invalidLogin = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["login"] = username, ["password"] = password }));
        Assert.Equal(HttpStatusCode.BadRequest, invalidLogin.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", csrfCookie.Split(';')[0]);
        using var loggedIn = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["login"] = username, ["password"] = password, ["__RequestVerificationToken"] = token, ["returnUrl"] = "//evil.example" }));
        Assert.Equal(HttpStatusCode.Redirect, loggedIn.StatusCode);
        Assert.Equal("/auth/complete", loggedIn.Headers.Location!.ToString());
        var authCookie = loggedIn.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AuthenticationServices.CookieName + "="));
        Assert.Contains("secure", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", authCookie, StringComparison.OrdinalIgnoreCase);
        using var secondClient = peer.GetTestClient();
        secondClient.BaseAddress = new("https://localhost");
        secondClient.DefaultRequestHeaders.Add("Cookie", authCookie.Split(';')[0]);
        Assert.Equal(subject, await secondClient.GetStringAsync("/protected"));
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", authCookie.Split(';')[0] + "; " + csrfCookie.Split(';')[0]);
        using var missingToken = await client.PostAsync("/api/mutate", new StringContent(""));
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        // A token issued anonymously is not valid for a signed-in identity.
        client.DefaultRequestHeaders.Add(AuthenticationServices.AntiforgeryHeader, token);
        using var anonymousToken = await client.PostAsync("/api/mutate", new StringContent(""));
        Assert.Equal(HttpStatusCode.BadRequest, anonymousToken.StatusCode);
        using var signedPage = await client.GetAsync("/auth/login");
        var signedToken = WebUtility.HtmlDecode(Regex.Match(await signedPage.Content.ReadAsStringAsync(), "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        client.DefaultRequestHeaders.Remove(AuthenticationServices.AntiforgeryHeader);
        client.DefaultRequestHeaders.Add(AuthenticationServices.AntiforgeryHeader, signedToken);
        using var mutation = await client.PostAsync("/api/mutate", new StringContent(""));
        Assert.Equal(HttpStatusCode.OK, mutation.StatusCode);
        using (var scope = first.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<CellBridgeUser>>();
            var user = (await users.FindByNameAsync(username))!;
            user.Enabled = false;
            user.CanCreate = false;
            Assert.True((await users.UpdateSecurityStampAsync(user)).Succeeded);
        }
        clock.Advance(TimeSpan.FromMinutes(2));
        using var revoked = await client.GetAsync("/api/protected");
        using var peerRevoked = await secondClient.GetAsync("/protected");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, peerRevoked.StatusCode);
    }

    [Theory]
    [InlineData("//evil.test")]
    [InlineData("/\\evil.test")]
    [InlineData("https://evil.test")]
    [InlineData("/auth/complete\n")]
    public void ExternalReturnUrlsAreRejected(string value) => Assert.Equal("/auth/complete", AuthenticationEndpoints.LocalReturnUrl(value));

    [Fact]
    public void UnauthenticatedOrUnscopedSubjectsAreRejected()
    {
        Assert.Null(CellBridgeActor.FromPrincipal(new(new ClaimsIdentity([new(CellBridgeActor.SubjectClaim, "attacker")]))));
        Assert.Null(CellBridgeActor.FromPrincipal(new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "attacker")], "external"))));
    }

    private static async Task<WebApplication> Start(TestClock clock, bool renew = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:cellbridge"] = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge"),
          ["Authentication:PublicOrigin"] = "https://localhost" });
        builder.Services.AddCellBridgeAuthentication(builder.Configuration, renew, serveOffice: renew);
        builder.Services.Configure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, o => o.TimeProvider = clock);
        builder.Services.Configure<SecurityStampValidatorOptions>(o => o.TimeProvider = clock);
        builder.Services.AddCellBridge(new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore()), requireDurability: false);
        var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); app.UseAntiforgery();
        app.MapCellBridgeAuthentication();
        app.MapCellBridge();
        app.MapGet("/protected", (System.Security.Claims.ClaimsPrincipal user) => CellBridgeActor.FromPrincipal(user)!.Identity.Subject).RequireAuthorization();
        app.MapGet("/api/protected", () => "ok").RequireAuthorization();
        app.MapGet("/library", () => "ok").RequireAuthorization();
        app.MapPost("/api/mutate", async (Microsoft.AspNetCore.Http.HttpContext context, IAntiforgery antiforgery) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Microsoft.AspNetCore.Http.Results.BadRequest(); }
            return Microsoft.AspNetCore.Http.Results.Ok();
        }).RequireAuthorization();
        await app.StartAsync();
        return app;
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
