using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.FssHttp.Tests;

public sealed class BuiltInLoginTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultAndCustomHtmlUseTheSameProtectedNamedCookieHandler(bool custom)
    {
        await using var app = await Start(custom);
        using var client = app.GetTestClient(); client.BaseAddress = new("https://localhost");
        using var challenge = await client.SendAsync(new(HttpMethod.Options, "/mount/shared/"));
        Assert.Equal(HttpStatusCode.Forbidden, challenge.StatusCode);
        var loginUri = new Uri(challenge.Headers.GetValues("X-FORMS_BASED_AUTH_REQUIRED").Single());
        using var page = await client.GetAsync(loginUri.PathAndQuery);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.True(page.Headers.CacheControl!.NoStore);
        if (custom) Assert.Contains("Custom login", html);
        else
        {
            Assert.Contains("Sign in to Portal &lt;Team&gt;", html);
            Assert.Contains("type=\"password\"", html);
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("action=\"/mount/sign-in\"", html);
        Assert.Contains("name=\"next\" value=\"/mount/_cellbridge/auth/complete\"", html);
        client.DefaultRequestHeaders.Add("Cookie", page.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        var token = Field(html, "__RequestVerificationToken");
        using var invalidCsrf = await client.PostAsync("/mount/sign-in", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["login"] = "user", ["password"] = "valid" }));
        Assert.Equal(HttpStatusCode.BadRequest, invalidCsrf.StatusCode);
        Assert.Equal(0, app.Services.GetRequiredService<LoginStats>().Calls);
        using var loggedIn = await client.PostAsync("/mount/sign-in", Form(token, "user", "valid", "/mount/_cellbridge/auth/complete"));
        Assert.Equal(HttpStatusCode.Redirect, loggedIn.StatusCode);
        Assert.Equal("/mount/_cellbridge/auth/complete", loggedIn.Headers.Location!.ToString());
        var cookie = loggedIn.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("Office.Login="));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        using var complete = await client.GetAsync("/mount/_cellbridge/auth/complete");
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        using var office = await client.SendAsync(new(HttpMethod.Options, "/mount/shared/"));
        Assert.Equal(HttpStatusCode.OK, office.StatusCode);
    }

    [Theory]
    [InlineData("user", "secret-invalid")]
    [InlineData("unmapped", "valid")]
    [InlineData("unauthenticated", "valid")]
    public async Task FailedLoginNeverIssuesACookieOrEchoesCredentials(string username, string password)
    {
        await using var app = await Start();
        using var client = app.GetTestClient(); client.BaseAddress = new("https://localhost");
        using var page = await client.GetAsync("/mount/sign-in?next=//evil.example");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("name=\"next\" value=\"/mount/_cellbridge/auth/complete\"", html);
        client.DefaultRequestHeaders.Add("Cookie", page.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        using var failed = await client.PostAsync("/mount/sign-in",
            Form(Field(html, "__RequestVerificationToken"), username, password, "//evil.example"));
        Assert.Equal(HttpStatusCode.Redirect, failed.StatusCode);
        Assert.False(failed.Headers.TryGetValues("Set-Cookie", out _));
        Assert.StartsWith("/mount/sign-in?failed=1&next=", failed.Headers.Location!.ToString());
        using var retry = await client.GetAsync(failed.Headers.Location);
        var retryHtml = await retry.Content.ReadAsStringAsync();
        Assert.Contains("Sign-in failed", retryHtml);
        Assert.DoesNotContain(password, retryHtml);
        Assert.DoesNotContain("evil.example", retryHtml);
    }

    [Fact]
    public async Task HostRequestGateRejectsBothMethodsBeforeCheckingCredentials()
    {
        await using var app = await Start(blocked: true);
        using var client = app.GetTestClient();
        using var get = await client.GetAsync("/mount/sign-in");
        using var post = await client.PostAsync("/mount/sign-in", new StringContent("ignored"));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.False(get.Headers.TryGetValues("Set-Cookie", out _));
        Assert.Equal(0, app.Services.GetRequiredService<LoginStats>().Calls);
    }

    [Fact]
    public async Task MissingAuthenticatorFailsAtStartup()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        { await using var app = await Start(missingAuthenticator: true); });
        Assert.Contains("ICellBridgeLoginAuthenticator", error.Message);
    }

    [Theory]
    [InlineData("password", "__RequestVerificationToken")]
    [InlineData("PASSWORD", "__RequestVerificationToken")]
    [InlineData("login", "__RequestVerificationToken")]
    [InlineData("failed", "__RequestVerificationToken")]
    [InlineData("__RequestVerificationToken", "__RequestVerificationToken")]
    [InlineData("csrf", "csrf")]
    public async Task ConflictingReturnFieldsFailBeforeCredentialsCanBeSubmitted(string returnField, string tokenField)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        { await using var app = await Start(returnField: returnField, tokenField: tokenField); });
        Assert.Contains("return URL parameter", error.Message);
    }

    private static FormUrlEncodedContent Form(string token, string user, string password, string returnUrl) => new(
        new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["login"] = user,
            ["password"] = password, ["next"] = returnUrl });
    private static string Field(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html,
        "name=\"" + name + "\" value=\"([^\"]+)\"").Groups[1].Value);

    private static async Task<WebApplication> Start(bool custom = false, bool blocked = false, bool missingAuthenticator = false, string returnField = "next", string tokenField = "__RequestVerificationToken")
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("browser").AddCookie("browser").AddCookie("office", options =>
        { options.LoginPath = "/sign-in"; options.ReturnUrlParameter = returnField; options.Cookie.Name = "Office.Login"; });
        builder.Services.AddCellBridge(new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore()), requireDurability: false);
        builder.Services.AddAntiforgery(options => options.FormFieldName = tokenField);
        builder.Services.AddSingleton<LoginStats>();
        if (!missingAuthenticator) builder.Services.AddScoped<ICellBridgeLoginAuthenticator, LoginAuthenticator>();
        builder.Services.AddCellBridgeLogin("office", options =>
        {
            options.ApplicationName = "Portal <Team>";
            options.IsRequestAllowed = _ => !blocked;
            if (custom) options.RenderPage = page =>
            {
                var e = HtmlEncoder.Default;
                return $"<!doctype html><title>Custom login</title><form method=\"post\" action=\"{e.Encode(page.FormAction)}\">" +
                    $"<input type=\"hidden\" name=\"{e.Encode(page.Antiforgery.FormFieldName)}\" value=\"{e.Encode(page.Antiforgery.RequestToken!)}\">" +
                    $"<input type=\"hidden\" name=\"{e.Encode(page.ReturnUrlParameter)}\" value=\"{e.Encode(page.ReturnUrl)}\">" +
                    "<input name=\"login\"><input name=\"password\" type=\"password\"><button>Sign in</button></form>";
            };
        });
        var app = builder.Build(); app.UsePathBase("/mount"); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
        app.MapCellBridge();
        try { await app.StartAsync(); return app; }
        catch { await app.DisposeAsync(); throw; }
    }

    private sealed class LoginStats { public int Calls; }
    private sealed class LoginAuthenticator(LoginStats stats) : ICellBridgeLoginAuthenticator
    {
        public Task<ClaimsPrincipal?> AuthenticateAsync(HttpContext context, string username, string password, CancellationToken cancellationToken)
        {
            stats.Calls++;
            if (password != "valid") return Task.FromResult<ClaimsPrincipal?>(null);
            var claims = username == "unmapped" ? Array.Empty<Claim>() : [new Claim(CellBridgeActor.SubjectClaim, "test:user")];
            return Task.FromResult<ClaimsPrincipal?>(new(new ClaimsIdentity(claims, username == "unauthenticated" ? null : "checked")));
        }
    }
}
