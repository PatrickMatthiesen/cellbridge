using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.FssHttp.Tests;

public sealed class IdentityLoginTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PasswordSignInUsesExistingIdentityCookieAndOfficeGetsFullPage(bool office)
    {
        await using var f = await Fixture.Start();
        using var page = await f.Get(office ? f.Office : f.Login);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(office, html.Contains("max-width: none"));
        using var success = await f.Post(f.Login, Fields(html, ("login", "alice"), ("password", Fixture.Password)));
        Assert.Equal(office ? f.Completion : "/mount/", success.Headers.Location!.ToString());
        Assert.Single(success.Headers.GetValues("Set-Cookie"), c => c.StartsWith("Identity.Application="));
        using var subject = await f.Get("/mount/who");
        Assert.Contains("identity:", await subject.Content.ReadAsStringAsync());
        Assert.Contains("host:claim", await subject.Content.ReadAsStringAsync());
        Assert.Contains("create=False", await subject.Content.ReadAsStringAsync());
        using var complete = await f.Get(f.Completion);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        await f.Users(async users =>
        {
            var user = (await users.FindByNameAsync("alice"))!;
            Assert.True((await users.UpdateSecurityStampAsync(user)).Succeeded);
        });
        using var revoked = await f.Get(f.Completion);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostIssuedCookiesAreMappedWithoutReplacingFactoryAndExplicitClaimsSurvive(bool explicitSubject)
    {
        await using var f = await Fixture.Start(explicitSubject: explicitSubject);
        using var hostLogin = await f.Get("/mount/host-login");
        using var before = await f.Get("/mount/who");
        var original = await before.Content.ReadAsStringAsync();
        Assert.Contains(explicitSubject ? "custom:alice" : "identity:", original);
        Assert.Contains(explicitSubject ? "create=True" : "create=False", original);
        await f.Users(async users =>
        {
            var user = (await users.FindByNameAsync("alice"))!;
            Assert.True((await users.SetUserNameAsync(user, "renamed")).Succeeded);
        });
        using var renamedLogin = await f.Get("/mount/host-login?name=renamed");
        using var after = await f.Get("/mount/who");
        Assert.Equal(original, await after.Content.ReadAsStringAsync());
        using var other = await f.Get("/mount/other-login");
        f.Jar = new();
        f.Jar.SetCookies(new("https://localhost"), other.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(".AspNetCore.other=")));
        using var unrelated = await f.Get(f.Completion);
        Assert.Equal(HttpStatusCode.Unauthorized, unrelated.StatusCode);
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("locked")]
    [InlineData("unconfirmed")]
    public async Task PasswordFailuresDoNotIssueAnApplicationCookie(string reason)
    {
        await using var f = await Fixture.Start();
        await f.Users(async users =>
        {
            var user = (await users.FindByNameAsync("alice"))!;
            if (reason == "locked") Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddHours(1))).Succeeded);
            if (reason == "unconfirmed") { user.EmailConfirmed = false; Assert.True((await users.UpdateAsync(user)).Succeeded); }
        });
        var html = await f.Html(f.Login);
        using var response = await f.Post(f.Login, Fields(html, ("login", "alice"), ("password", reason == "wrong" ? "bad-secret" : Fixture.Password)));
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("Identity.Application="));
        var error = await f.Html(response.Headers.Location!.ToString());
        Assert.Contains("Sign-in failed", error);
        Assert.DoesNotContain("bad-secret", error);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MfaUsesPendingCookieThenAuthenticatesWithProtectedDestination(bool office, bool recovery)
    {
        await using var f = await Fixture.Start();
        await f.EnableMfa();
        var html = await f.BeginMfa(office);
        using var blocked = await f.Get(f.Completion);
        Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);
        var code = recovery ? f.RecoveryCode : await f.Code();
        using var response = await f.Post(f.Mfa, Fields(html, ("code", code), ("method", recovery ? "recovery" : "authenticator"),
            ("next", "//evil.example"), ("_cellbridgePresentation", office ? "browser" : "office")));
        Assert.Equal(office ? f.Completion : "/mount/library", response.Headers.Location!.ToString());
        Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("Identity.Application="));
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Identity.TwoFactorRememberMe="));
        using var complete = await f.Get(f.Completion);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        // Identity's stamp validator may renew an existing cookie on any request.
        // Test replay without that already authenticated session.
        f.Jar.GetCookies(new("https://localhost/mount"))["Identity.Application"]!.Expired = true;
        using var replay = await f.Post(f.Mfa, Fields(html, ("code", code), ("method", recovery ? "recovery" : "authenticator")));
        Assert.False(replay.Headers.TryGetValues("Set-Cookie", out var replayCookies) && replayCookies.Any(c => c.StartsWith("Identity.Application=")));
        if (recovery)
        {
            f.Jar = new();
            var retry = await f.BeginMfa(office);
            using var reuse = await f.Post(f.Mfa, Fields(retry, ("code", code), ("method", "recovery")));
            Assert.Contains("failed=1", reuse.Headers.Location!.ToString());
            Assert.False(reuse.Headers.TryGetValues("Set-Cookie", out var reused) && reused.Any(c => c.StartsWith("Identity.Application=")));
        }
    }

    [Theory]
    [InlineData("authenticator")]
    [InlineData("recovery")]
    public async Task BadMfaCodesRetainOfficeLayoutAndEventuallyLockOut(string method)
    {
        await using var f = await Fixture.Start();
        await f.EnableMfa();
        var html = await f.BeginMfa(true);
        using var bad = await f.Post(f.Mfa, Fields(html, ("code", "invalid-code"), ("method", method)));
        html = await f.Html(bad.Headers.Location!.ToString());
        Assert.Contains("max-width: none", html);
        Assert.Contains("Sign-in failed", html);
        using var locked = await f.Post(f.Mfa, Fields(html, ("code", "invalid-code"), ("method", method)));
        Assert.StartsWith(f.Office, locked.Headers.Location!.ToString());
        await f.Users(async users => Assert.True(await users.IsLockedOutAsync((await users.FindByNameAsync("alice"))!)));
        using var complete = await f.Get(f.Completion);
        Assert.Equal(HttpStatusCode.Unauthorized, complete.StatusCode);
    }

    [Theory]
    [InlineData("locked", false)]
    [InlineData("unconfirmed", false)]
    [InlineData("stamp", false)]
    [InlineData("disabled-mfa", false)]
    [InlineData("locked", true)]
    [InlineData("unconfirmed", true)]
    [InlineData("stamp", true)]
    [InlineData("disabled-mfa", true)]
    public async Task ChangedAccountPolicyCannotBeBypassedDuringTwoFactorSignIn(string change, bool recovery)
    {
        await using var f = await Fixture.Start();
        await f.EnableMfa();
        var html = await f.BeginMfa(true);
        var code = recovery ? f.RecoveryCode : await f.Code();
        await f.Users(async users =>
        {
            var user = (await users.FindByNameAsync("alice"))!;
            if (change == "locked") await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddHours(1));
            if (change == "unconfirmed") { user.EmailConfirmed = false; await users.UpdateAsync(user); }
            if (change == "stamp") await users.UpdateSecurityStampAsync(user);
            if (change == "disabled-mfa") await users.SetTwoFactorEnabledAsync(user, false);
        });
        using var response = await f.Post(f.Mfa, Fields(html, ("code", code), ("method", recovery ? "recovery" : "authenticator")));
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("Identity.Application="));
        using var complete = await f.Get(f.Completion);
        Assert.Equal(HttpStatusCode.Unauthorized, complete.StatusCode);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("tampered")]
    [InlineData("new-attempt")]
    [InlineData("other-user")]
    [InlineData("missing")]
    public async Task InvalidContinuationCannotIssueAnApplicationCookie(string change)
    {
        await using var f = await Fixture.Start();
        await f.EnableMfa();
        var html = await f.BeginMfa(true);
        var fields = Fields(html, ("code", await f.Code()), ("method", "authenticator"));
        if (change == "expired") f.Clock.Now += TimeSpan.FromMinutes(6);
        if (change == "tampered") fields["_cellbridgeState"] += "x";
        if (change == "missing") fields.Remove("_cellbridgeState");
        if (change == "new-attempt") await f.BeginMfa(false);
        if (change == "other-user")
        {
            await f.EnableMfa("bob");
            await f.BeginMfa(false, "bob");
        }
        using var response = await f.Post(f.Mfa, fields);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var values) && values.Any(c => c.StartsWith("Identity.Application=")));
        using var complete = await f.Get(f.Completion);
        Assert.Equal(HttpStatusCode.Unauthorized, complete.StatusCode);
    }

    [Fact]
    public async Task CancellationRequiresCsrfAndANormalGetDoesNotCancelTheAttempt()
    {
        await using var f = await Fixture.Start();
        await f.EnableMfa();
        var html = await f.BeginMfa(true);
        using var primary = await f.Get(f.Login);
        using var invalidCancel = await f.Post(f.Mfa, new() { ["cancel"] = "1" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidCancel.StatusCode);
        using var cancel = await f.Post(f.Mfa, Fields(html, ("cancel", "1")));
        Assert.StartsWith(f.Office, cancel.Headers.Location!.ToString());
        using var stale = await f.Post(f.Mfa, Fields(html, ("code", await f.Code()), ("method", "authenticator")));
        Assert.False(stale.Headers.TryGetValues("Set-Cookie", out var values) && values.Any(c => c.StartsWith("Identity.Application=")));
    }

    [Fact]
    public async Task MfaPostChecksCsrfAndBoundsBeforeCheckingCodes()
    {
        await using var f = await Fixture.Start();
        await f.EnableMfa();
        var html = await f.BeginMfa(false);
        using var csrf = await f.Post(f.Mfa, new() { ["code"] = await f.Code(), ["method"] = "authenticator" });
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
        using var oversized = await f.Post(f.Mfa, Fields(html, ("code", new string('x', 5000)), ("method", "authenticator")));
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        using var success = await f.Post(f.Mfa, Fields(html, ("code", await f.Code()), ("method", "authenticator")));
        Assert.Equal("/mount/library", success.Headers.Location!.ToString());
    }

    [Fact]
    public async Task GateAppliesToOfficeAndBothMfaMethodsWithoutIssuingCookies()
    {
        await using var f = await Fixture.Start();
        await f.EnableMfa();
        var html = await f.BeginMfa(true);
        f.Allowed = false;
        foreach (var path in new[] { f.Office, f.Mfa })
        {
            using var get = await f.Get(path);
            using var post = await f.Post(path == f.Office ? f.Login : path, Fields(html, ("cancel", "1")));
            Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
            Assert.False(get.Headers.TryGetValues("Set-Cookie", out _));
        }
    }

    [Theory]
    [InlineData("/auth/login")]
    [InlineData("/_cellbridge/auth/login")]
    [InlineData("/_cellbridge/auth/complete")]
    [InlineData("/auth/login/")]
    public async Task ConflictingMfaRoutesFailAtStartup(string path)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await using var _ = await Fixture.Start(twoFactorPath: path); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyRendererRemainsPrimaryOnlyAndNewRenderersHandleBothStages(bool custom)
    {
        await using var f = await Fixture.Start(customRenderer: custom);
        await f.EnableMfa();
        var primary = await f.Html(f.Office);
        Assert.Contains(custom ? "Office custom" : "Legacy custom", primary);
        var mfa = await f.BeginMfa(true);
        if (custom) Assert.Contains("Office custom", mfa);
        else Assert.DoesNotContain("Legacy custom", mfa);
        Assert.Contains("name=\"_cellbridgeState\"", mfa);
        using var signedIn = await f.Post(f.Mfa, Fields(mfa, ("code", await f.Code()), ("method", "authenticator")));
        Assert.Equal(f.Completion, signedIn.Headers.Location!.ToString());
    }

    [Fact]
    public async Task IdentityUsesConfiguredUserIdClaimTypeAndRejectsAmbiguousIds()
    {
        await using var f = await Fixture.Start(userIdClaim: "host:user-id");
        using var host = await f.Get("/mount/host-login");
        using var mapped = await f.Get(f.Completion);
        Assert.Equal(HttpStatusCode.OK, mapped.StatusCode);
        await f.Users(async users =>
        {
            var user = (await users.FindByNameAsync("alice"))!;
            await users.AddClaimAsync(user, new("host:user-id", "competing-id"));
        });
        using var ambiguous = await f.Get(f.Completion);
        Assert.Equal(HttpStatusCode.Unauthorized, ambiguous.StatusCode);
    }

    [Fact]
    public async Task EmailOnlyTwoFactorAccountsNeverFallBackToPasswordOnlySignIn()
    {
        await using var f = await Fixture.Start();
        await f.Users(async users => await users.SetTwoFactorEnabledAsync((await users.FindByNameAsync("alice"))!, true));
        var mfa = await f.BeginMfa(true);
        Assert.Contains("Recovery code", mfa);
        using var complete = await f.Get(f.Completion);
        Assert.Equal(HttpStatusCode.Unauthorized, complete.StatusCode);
    }

    [Fact]
    public async Task LongUnicodeReturnFallsBackBeforeIssuingOversizedState()
    {
        await using var f = await Fixture.Start();
        await f.EnableMfa();
        var html = await f.Html(f.Login + "?next=" + Uri.EscapeDataString("/mount/library?name=" + new string('界', 1800)));
        var fields = Fields(html, ("login", "alice"), ("password", Fixture.Password));
        Assert.Equal("/mount/", fields["next"]);
        Assert.True(fields["_cellbridgeState"].Length <= 3000);
        using var primary = await f.Post(f.Login, fields);
        Assert.All(primary.Headers.GetValues("Set-Cookie"), cookie => Assert.True(cookie.Length < 4096));
        var mfa = await f.Html(f.Mfa);
        using var success = await f.Post(f.Mfa, Fields(mfa, ("code", await f.Code()), ("method", "authenticator")));
        Assert.Equal("/mount/", success.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("flow-cookie")]
    [InlineData("missing-temporary")]
    [InlineData("replaced-temporary")]
    [InlineData("cross-browser")]
    public async Task PendingCookiesAndFormCannotBeMixedOrAltered(string change)
    {
        await using var f = await Fixture.Start();
        await f.EnableMfa();
        var browserA = new CookieContainer();
        f.Jar = browserA;
        var old = await f.BeginMfa(true);
        var state = Fields(old)["_cellbridgeState"];
        var temporary = browserA.GetCookies(new("https://localhost/mount"))["Identity.Pending"]!.Value;
        string html = old;
        if (change == "flow-cookie") browserA.GetCookies(new("https://localhost/mount"))["CellBridge.IdentityLogin.Pending"]!.Value += "x";
        if (change == "missing-temporary") browserA.GetCookies(new("https://localhost/mount"))["Identity.Pending"]!.Expired = true;
        if (change is "replaced-temporary" or "cross-browser")
        {
            f.Jar = new();
            html = await f.BeginMfa(false);
            if (change == "replaced-temporary")
                f.Jar.GetCookies(new("https://localhost/mount"))["Identity.Pending"]!.Value = temporary;
        }
        var fields = Fields(html, ("code", await f.Code()), ("method", "authenticator"));
        if (change == "cross-browser") fields["_cellbridgeState"] = state;
        using var response = await f.Post(f.Mfa, fields);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var values) && values.Any(c => c.StartsWith("Identity.Application=")));
        using var complete = await f.Get(f.Completion);
        Assert.Equal(HttpStatusCode.Unauthorized, complete.StatusCode);
    }

    [Fact]
    public async Task EmptyCodeUsesInlineErrorAndOfficePrimaryFieldsCannotChangeItsDestination()
    {
        await using var f = await Fixture.Start();
        await f.EnableMfa();
        var primary = await f.Html(f.Office);
        using var started = await f.Post(f.Login, Fields(primary, ("login", "alice"), ("password", Fixture.Password),
            ("next", "/mount/other"), ("_cellbridgePresentation", "browser")));
        var mfa = await f.Html(f.Mfa);
        using var empty = await f.Post(f.Mfa, Fields(mfa, ("code", ""), ("method", "authenticator")));
        mfa = await f.Html(empty.Headers.Location!.ToString());
        Assert.Contains("role=\"alert\"", mfa);
        Assert.Contains("max-width: none", mfa);
        using var success = await f.Post(f.Mfa, Fields(mfa, ("code", await f.Code()), ("method", "authenticator")));
        Assert.Equal(f.Completion, success.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("missing-temporary")]
    [InlineData("wrong-sign-in-scheme")]
    public async Task IncompleteIdentitySetupFailsAtStartup(string problem)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await using var _ = await Fixture.Start(configuration: problem); });
    }

    [Fact]
    public async Task HostCookieRejectionIsPreservedAfterIdentityMapping()
    {
        await using var f = await Fixture.Start();
        using var host = await f.Get("/mount/host-login");
        using var before = await f.Get(f.Completion);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        f.RejectCookie = true;
        using var rejected = await f.Get(f.Completion);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    internal static Dictionary<string, string> Fields(string html, params (string Key, string Value)[] supplied)
    {
        var form = Regex.Match(html, "<form .*?</form>", RegexOptions.Singleline).Value;
        var result = Regex.Matches(form, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\"")
            .ToDictionary(m => WebUtility.HtmlDecode(m.Groups[1].Value), m => WebUtility.HtmlDecode(m.Groups[2].Value));
        foreach (var (key, value) in supplied) result[key] = value;
        return result;
    }

    private sealed class Fixture(WebApplication app) : IAsyncDisposable
    {
        internal const string Password = "Test-password-identity-2026!";
        internal readonly string Login = "/mount/auth/login", Office = "/mount/_cellbridge/auth/login",
            Mfa = "/mount/_cellbridge/auth/two-factor", Completion = "/mount/_cellbridge/auth/complete";
        internal CookieContainer Jar = new();
        internal readonly TestClock Clock = app.Services.GetRequiredService<TestClock>();
        internal bool Allowed { get => app.Services.GetRequiredService<Gate>().Allowed; set => app.Services.GetRequiredService<Gate>().Allowed = value; }
        internal bool RejectCookie { set => app.Services.GetRequiredService<Gate>().RejectCookie = value; }
        internal string RecoveryCode = "";
        private readonly HttpClient _client = app.GetTestClient();
        internal async Task<HttpResponseMessage> Get(string path) => await Send(new(HttpMethod.Get, path));
        internal async Task<HttpResponseMessage> Post(string path, Dictionary<string, string> fields) =>
            await Send(new(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) });
        private async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            var uri = new Uri(new Uri("https://localhost"), request.RequestUri!);
            request.RequestUri = uri;
            request.Headers.TryAddWithoutValidation("Cookie", Jar.GetCookieHeader(uri));
            var response = await _client.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies)) foreach (var cookie in cookies) Jar.SetCookies(uri, cookie);
            return response;
        }
        internal async Task<string> Html(string path)
        {
            using var response = await Get(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            return await response.Content.ReadAsStringAsync();
        }
        internal async Task Users(Func<UserManager<IdentityUser>, Task> action)
        {
            using var scope = app.Services.CreateScope();
            await action(scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>());
        }
        internal async Task EnableMfa(string name = "alice") => await Users(async users =>
        {
            var user = (await users.FindByNameAsync(name))!;
            await users.ResetAuthenticatorKeyAsync(user);
            await users.SetTwoFactorEnabledAsync(user, true);
            RecoveryCode = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 2))!.First();
        });
        internal async Task<string> Code()
        {
            string code = "";
            await Users(async users =>
            {
                var key = (await users.GetAuthenticatorKeyAsync((await users.FindByNameAsync("alice"))!))!;
                const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
                var bytes = new List<byte>(); var bits = 0; var buffer = 0;
                foreach (var c in key)
                {
                    buffer = (buffer << 5) | alphabet.IndexOf(c); bits += 5;
                    if (bits >= 8) { bits -= 8; bytes.Add((byte)(buffer >> bits)); }
                }
                var time = new byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(time, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
                var hash = System.Security.Cryptography.HMACSHA1.HashData(bytes.ToArray(), time);
                var offset = hash[^1] & 15;
                var value = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset, 4)) & int.MaxValue;
                code = (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            });
            return code;
        }
        internal async Task<string> BeginMfa(bool office, string name = "alice")
        {
            var html = await Html((office ? Office : Login) + "?next=" + Uri.EscapeDataString("/mount/library"));
            using var first = await Post(Login, Fields(html, ("login", name), ("password", Password)));
            Assert.Equal(Mfa, first.Headers.Location!.ToString());
            Assert.DoesNotContain(first.Headers.GetValues("Set-Cookie"), c => c.StartsWith("Identity.Application="));
            return await Html(Mfa);
        }
        internal static async Task<Fixture> Start(bool explicitSubject = false, string? twoFactorPath = null,
            bool? customRenderer = null, string? userIdClaim = null, string? configuration = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<TestClock>();
            builder.Services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<TestClock>());
            builder.Services.AddSingleton<Gate>();
            // One database per host, shared by scoped Identity contexts.
            var database = Guid.NewGuid().ToString();
            builder.Services.AddDbContext<IdentityDbContext<IdentityUser>>(o => o.UseInMemoryDatabase(database));
            builder.Services.AddIdentityCore<IdentityUser>(o =>
            {
                o.SignIn.RequireConfirmedEmail = true;
                o.Lockout.MaxFailedAccessAttempts = 2;
                if (userIdClaim is not null) o.ClaimsIdentity.UserIdClaimType = userIdClaim;
            }).AddEntityFrameworkStores<IdentityDbContext<IdentityUser>>().AddSignInManager().AddDefaultTokenProviders();
            builder.Services.AddScoped<IUserClaimsPrincipalFactory<IdentityUser>, HostClaimsFactory>();
            if (configuration == "missing-temporary")
                builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddCookie(IdentityConstants.ApplicationScheme);
            else builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
            if (configuration == "wrong-sign-in-scheme") builder.Services.AddScoped<SignInManager<IdentityUser>>(sp =>
            {
                var manager = ActivatorUtilities.CreateInstance<SignInManager<IdentityUser>>(sp);
                manager.AuthenticationScheme = "other";
                return manager;
            });
            builder.Services.AddAuthentication().AddCookie("other");
            builder.Services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, o => o.Cookie.Name = "Identity.Pending");
            builder.Services.Configure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, o =>
            {
                o.LoginPath = "/auth/login"; o.ReturnUrlParameter = "next"; o.Cookie.Name = "Identity.Application";
                var validate = o.Events.OnValidatePrincipal;
                o.Events.OnValidatePrincipal = async context =>
                {
                    await validate(context);
                    if (context.HttpContext.RequestServices.GetRequiredService<Gate>().RejectCookie) context.RejectPrincipal();
                };
            });
            builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);
            builder.Services.AddCellBridge(new(new InMemoryStateStore(), new InMemoryContentStore()), requireDurability: false);
            builder.Services.AddCellBridgeIdentityLogin<IdentityUser>(o =>
            {
                o.IdentityAuthority = "test:accounts";
                o.IsRequestAllowed = c => c.RequestServices.GetRequiredService<Gate>().Allowed;
                if (twoFactorPath is not null) o.TwoFactorPath = new(twoFactorPath);
                if (customRenderer == false) o.RenderPage = page => "<!-- Legacy custom -->" + CellBridgeLoginPage.Render(page);
                if (customRenderer == true)
                {
                    o.RenderBrowserPage = page => "<!-- Browser custom -->" + CellBridgeLoginPage.Render(page);
                    o.RenderOfficePage = page => "<!-- Office custom -->" + CellBridgeLoginPage.Render(page);
                }
            });
            var app = builder.Build();
            app.UsePathBase("/mount"); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
            app.MapCellBridge();
            app.MapGet("/who", (ClaimsPrincipal principal) =>
                CellBridgeActor.FromPrincipal(principal)!.Identity.Subject + " host:claim=" + principal.FindFirst("host:claim")?.Value +
                " create=" + CellBridgeActor.FromPrincipal(principal)!.CanCreate).RequireAuthorization();
            app.MapGet("/host-login", async (HttpContext context, SignInManager<IdentityUser> manager) =>
            {
                await manager.SignInAsync((await manager.UserManager.FindByNameAsync(context.Request.Query["name"].FirstOrDefault() ?? "alice"))!, false);
                return Results.Ok();
            });
            app.MapGet("/other-login", async (HttpContext context) =>
            {
                await context.SignInAsync("other", new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "alice")], "other")));
                return Results.Ok();
            });
            try
            {
                await app.StartAsync();
                var fixture = new Fixture(app);
                await fixture.Users(async users =>
                {
                    foreach (var name in new[] { "alice", "bob" })
                    {
                        var user = new IdentityUser { UserName = name, Email = name + "@example.test", EmailConfirmed = true };
                        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
                        if (explicitSubject && name == "alice")
                            Assert.True((await users.AddClaimsAsync(user, [new(CellBridgeActor.SubjectClaim, "custom:alice"), new(CellBridgeActor.CreateClaim, "true")])).Succeeded);
                    }
                });
                return fixture;
            }
            catch { await app.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync() { _client.Dispose(); await app.DisposeAsync(); }
    }
    private sealed class Gate { public bool Allowed = true; public bool RejectCookie; }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class HostClaimsFactory(UserManager<IdentityUser> users, Microsoft.Extensions.Options.IOptions<IdentityOptions> options)
        : UserClaimsPrincipalFactory<IdentityUser>(users, options)
    {
        protected override async Task<ClaimsIdentity> GenerateClaimsAsync(IdentityUser user)
        {
            var claims = await base.GenerateClaimsAsync(user);
            claims.AddClaim(new("host:claim", "preserved"));
            return claims;
        }
    }
}
