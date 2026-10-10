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
using Microsoft.Extensions.Options;

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
        { ["login"] = "user", ["password"] = "correct-test-password-29" }));
        Assert.Equal(HttpStatusCode.BadRequest, invalidCsrf.StatusCode);
        Assert.Equal(0, app.Services.GetRequiredService<LoginStats>().Calls);
        using var loggedIn = await client.PostAsync("/mount/sign-in", Form(html, "user", "correct-test-password-29", "/mount/_cellbridge/auth/complete"));
        Assert.Equal(HttpStatusCode.Redirect, loggedIn.StatusCode);
        Assert.Equal("/mount/_cellbridge/auth/complete", loggedIn.Headers.Location!.ToString());
        var cookie = loggedIn.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("Office.Login="));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(!custom, cookie.Contains("expires=", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(!custom, Ticket(app, cookie).Properties.IsPersistent);
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        using var complete = await client.GetAsync("/mount/_cellbridge/auth/complete");
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        using var office = await client.SendAsync(new(HttpMethod.Options, "/mount/shared/"));
        Assert.Equal(HttpStatusCode.OK, office.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentOfficeCookieUsesConfiguredLifetimeAndHostSigningInOverride(bool overrideExpiry)
    {
        var lifetime = TimeSpan.FromMinutes(23);
        var hostLifetime = overrideExpiry ? TimeSpan.FromMinutes(7) : (TimeSpan?)null;
        await using var app = await Start(persistOfficeSession: true, cookieLifetime: lifetime, signInLifetime: hostLifetime);
        using var client = app.GetTestClient(); client.BaseAddress = new("https://localhost");
        using var page = await client.GetAsync("/mount/_cellbridge/auth/login");
        var html = await page.Content.ReadAsStringAsync();
        client.DefaultRequestHeaders.Add("Cookie", page.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        using var login = await client.PostAsync("/mount/sign-in", Form(html, "user", "correct-test-password-29", "/mount/forged"));
        Assert.Equal("/mount/_cellbridge/auth/complete", login.Headers.Location!.ToString());
        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("Office.Login="));
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        var ticket = Ticket(app, cookie);
        Assert.True(ticket.Properties.IsPersistent);
        Assert.Equal(hostLifetime ?? lifetime, ticket.Properties.ExpiresUtc - ticket.Properties.IssuedUtc);
        var clock = app.Services.GetRequiredService<TestClock>();
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        clock.Now = ticket.Properties.ExpiresUtc!.Value.AddSeconds(-1);
        using var valid = await client.GetAsync("/mount/_cellbridge/auth/complete");
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        clock.Now = ticket.Properties.ExpiresUtc.Value.AddSeconds(1);
        using var expired = await client.GetAsync("/mount/_cellbridge/auth/complete");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Theory]
    [InlineData("browser")]
    [InlineData("legacy")]
    [InlineData("opt-out")]
    public async Task PersistenceRequiresEnabledOptionAndProtectedOfficeStateEvenWithForgedOfficeFields(string flow)
    {
        var legacyRenderer = flow == "legacy";
        await using var app = await Start(custom: legacyRenderer, persistOfficeSession: flow != "opt-out");
        using var client = app.GetTestClient(); client.BaseAddress = new("https://localhost");
        using var page = await client.GetAsync(flow == "browser" ? "/mount/sign-in" : "/mount/_cellbridge/auth/login");
        var html = await page.Content.ReadAsStringAsync();
        client.DefaultRequestHeaders.Add("Cookie", page.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        var fields = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Field(html, "__RequestVerificationToken"),
            ["login"] = "user", ["password"] = "correct-test-password-29",
            ["next"] = "/mount/_cellbridge/auth/complete", ["_cellbridgePresentation"] = "office"
        };
        if (!legacyRenderer) fields["_cellbridgeState"] = Field(html, "_cellbridgeState");
        using var login = await client.PostAsync("/mount/sign-in", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal(flow == "browser" ? "/mount/" : "/mount/_cellbridge/auth/complete", login.Headers.Location!.ToString());
        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("Office.Login="));
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.False(Ticket(app, cookie).Properties.IsPersistent);
    }

    [Theory]
    [InlineData("user", "secret-invalid")]
    [InlineData("unmapped", "correct-test-password-29")]
    [InlineData("unauthenticated", "correct-test-password-29")]
    public async Task FailedLoginNeverIssuesACookieOrEchoesCredentials(string username, string password)
    {
        await using var app = await Start();
        using var client = app.GetTestClient(); client.BaseAddress = new("https://localhost");
        using var page = await client.GetAsync("/mount/sign-in?next=//evil.example");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("name=\"next\" value=\"/mount/\"", html);
        client.DefaultRequestHeaders.Add("Cookie", page.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        using var failed = await client.PostAsync("/mount/sign-in",
            Form(html, username, password, "//evil.example"));
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
    [InlineData("_cellbridgeState", "__RequestVerificationToken")]
    [InlineData("_cellbridgePresentation", "__RequestVerificationToken")]
    [InlineData("code", "__RequestVerificationToken")]
    [InlineData("method", "__RequestVerificationToken")]
    [InlineData("cancel", "__RequestVerificationToken")]
    [InlineData("next", "_cellbridgeState")]
    [InlineData("next", "_cellbridgePresentation")]
    [InlineData("next", "code")]
    [InlineData("next", "method")]
    [InlineData("next", "cancel")]
    public async Task ConflictingReturnFieldsFailBeforeCredentialsCanBeSubmitted(string returnField, string tokenField)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        { await using var app = await Start(returnField: returnField, tokenField: tokenField); });
        Assert.Contains("must not collide", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    [InlineData("/bad\npath")]
    [InlineData("/mount-other/library")]
    [InlineData("/mount/../outside")]
    [InlineData("/mount/%2e%2e/outside")]
    public async Task BrowserLoginUsesMountedHomeForMissingOrUnsafeReturns(string? suppliedReturn)
    {
        await using var app = await Start();
        using var client = app.GetTestClient(); client.BaseAddress = new("https://localhost");
        var query = suppliedReturn is null ? "" : "?next=" + Uri.EscapeDataString(suppliedReturn);
        using var page = await client.GetAsync("/mount/sign-in" + query);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal("/mount/", Field(html, "next"));
        client.DefaultRequestHeaders.Add("Cookie", page.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        var fields = new Dictionary<string, string>
        { ["__RequestVerificationToken"] = Field(html, "__RequestVerificationToken"), ["_cellbridgeState"] = Field(html, "_cellbridgeState"), ["login"] = "user", ["password"] = "correct-test-password-29" };
        if (suppliedReturn is not null) fields["next"] = suppliedReturn;
        using var login = await client.PostAsync("/mount/sign-in", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/mount/", login.Headers.Location!.ToString());
    }

    [Fact]
    public async Task HostCanChooseItsBrowserHomeWithoutChangingOfficeCompletion()
    {
        await using var app = await Start(defaultReturnPath: "/library");
        using var client = app.GetTestClient(); client.BaseAddress = new("https://localhost");
        using var page = await client.GetAsync("/mount/sign-in");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal("/mount/library", Field(html, "next"));
        client.DefaultRequestHeaders.Add("Cookie", page.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        using var login = await client.PostAsync("/mount/sign-in", Form(html, "user", "correct-test-password-29", "//evil.example"));
        Assert.Equal("/mount/library", login.Headers.Location!.ToString());
        using var office = await client.SendAsync(new(HttpMethod.Options, "/mount/shared/"));
        Assert.EndsWith("next=%2Fmount%2F_cellbridge%2Fauth%2Fcomplete",
            office.Headers.GetValues("X-FORMS_BASED_AUTH_REQUIRED").Single());
    }

    [Theory]
    [InlineData("", "correct-test-password-29")]
    [InlineData("user", "")]
    public async Task EmptyCredentialsUseTheInlineErrorAndPreserveOfficeReturn(string user, string password)
    {
        await using var app = await Start();
        using var client = app.GetTestClient(); client.BaseAddress = new("https://localhost");
        const string completion = "/mount/_cellbridge/auth/complete";
        using var page = await client.GetAsync("/mount/sign-in?next=" + Uri.EscapeDataString(completion));
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("novalidate", html);
        client.DefaultRequestHeaders.Add("Cookie", page.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        using var failed = await client.PostAsync("/mount/sign-in", Form(html, user, password, completion));
        Assert.Equal(HttpStatusCode.Redirect, failed.StatusCode);
        Assert.False(failed.Headers.TryGetValues("Set-Cookie", out _));
        Assert.Equal(0, app.Services.GetRequiredService<LoginStats>().Calls);
        using var retry = await client.GetAsync(failed.Headers.Location);
        var retryHtml = await retry.Content.ReadAsStringAsync();
        Assert.Contains("role=\"alert\"", retryHtml);
        Assert.Equal(completion, Field(retryHtml, "next"));
        using var success = await client.PostAsync("/mount/sign-in", Form(retryHtml, "user", "correct-test-password-29", Field(retryHtml, "next")));
        Assert.Equal(completion, success.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("//evil.example")]
    [InlineData("/bad?query")]
    [InlineData("/bad#fragment")]
    [InlineData("/\\bad")]
    [InlineData("/{route}")]
    public async Task InvalidDefaultReturnPathsAreRejected(string path)
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        { await using var app = await Start(defaultReturnPath: path); });
    }

    private static FormUrlEncodedContent Form(string html, string user, string password, string returnUrl) => new(
        new Dictionary<string, string> { ["__RequestVerificationToken"] = Field(html, "__RequestVerificationToken"), ["_cellbridgeState"] = Field(html, "_cellbridgeState"), ["login"] = user,
            ["password"] = password, ["next"] = returnUrl });
    private static string Field(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html,
        "name=\"" + name + "\" value=\"([^\"]+)\"").Groups[1].Value);

    private static Microsoft.AspNetCore.Authentication.AuthenticationTicket Ticket(WebApplication app, string cookie) =>
        app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("office")
            .TicketDataFormat.Unprotect(cookie.Split(';')[0].Split('=', 2)[1])!;

    private static async Task<WebApplication> Start(bool custom = false, bool blocked = false, bool missingAuthenticator = false, string returnField = "next", string tokenField = "__RequestVerificationToken", string? defaultReturnPath = null,
        bool? persistOfficeSession = null, TimeSpan? cookieLifetime = null, TimeSpan? signInLifetime = null)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        var clock = new TestClock();
        builder.Services.AddSingleton(clock);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddAuthentication("browser").AddCookie("browser").AddCookie("office", options =>
        {
            options.LoginPath = "/sign-in"; options.ReturnUrlParameter = returnField; options.Cookie.Name = "Office.Login";
            options.TimeProvider = clock;
            if (cookieLifetime.HasValue) options.ExpireTimeSpan = cookieLifetime.Value;
            if (signInLifetime.HasValue) options.Events.OnSigningIn = context =>
            {
                context.Properties.ExpiresUtc = context.Properties.IssuedUtc!.Value + signInLifetime.Value;
                return Task.CompletedTask;
            };
        });
        builder.Services.AddCellBridge(new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore()), requireDurability: false);
        builder.Services.AddAntiforgery(options => options.FormFieldName = tokenField);
        builder.Services.AddSingleton<LoginStats>();
        if (!missingAuthenticator) builder.Services.AddScoped<ICellBridgeLoginAuthenticator, LoginAuthenticator>();
        builder.Services.AddCellBridgeLogin("office", options =>
        {
            options.ApplicationName = "Portal <Team>";
            if (persistOfficeSession.HasValue) options.PersistOfficeSession = persistOfficeSession.Value;
            if (defaultReturnPath is not null) options.DefaultReturnPath = new(defaultReturnPath);
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
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class LoginAuthenticator(LoginStats stats) : ICellBridgeLoginAuthenticator
    {
        public Task<ClaimsPrincipal?> AuthenticateAsync(HttpContext context, string username, string password, CancellationToken cancellationToken)
        {
            stats.Calls++;
            if (password != "correct-test-password-29") return Task.FromResult<ClaimsPrincipal?>(null);
            var claims = username == "unmapped" ? Array.Empty<Claim>() : [new Claim(CellBridgeActor.SubjectClaim, "test:user")];
            return Task.FromResult<ClaimsPrincipal?>(new(new ClaimsIdentity(claims, username == "unauthenticated" ? null : "checked")));
        }
    }
}
