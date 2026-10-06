using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CellBridge.FssHttp.Tests;

public sealed class OfficeFormsAuthenticationTests
{
    [Fact]
    public async Task NamedOfficeCookieWorksAlongsideBearerDefaultAndPreservesCookieEvents()
    {
        await using var fixture = await Fixture.Start();
        using var client = fixture.Client;
        using var challenge = await client.SendAsync(new(HttpMethod.Options, "/shared/"));
        Assert.Equal(HttpStatusCode.Forbidden, challenge.StatusCode);
        Assert.Equal("https://localhost/login?next=%2F_cellbridge%2Fauth%2Fcomplete",
            challenge.Headers.GetValues("X-FORMS_BASED_AUTH_REQUIRED").Single());
        Assert.Null(challenge.Headers.Location);
        Assert.Equal("bearer", await client.GetStringAsync("/api"));
        using var completeWithoutCookie = await client.GetAsync("/_cellbridge/auth/complete");
        Assert.Equal(HttpStatusCode.Unauthorized, completeWithoutCookie.StatusCode);
        using var unrelated = await client.GetAsync("/shared/unrelated/protected");
        Assert.Equal((HttpStatusCode)418, unrelated.StatusCode);
        Assert.False(unrelated.Headers.Contains("X-FORMS_BASED_AUTH_REQUIRED"));
        Assert.Equal(1, fixture.Events.LoginRedirects);

        using var login = await client.GetAsync("/sign-in");
        client.DefaultRequestHeaders.Add("Cookie", login.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        Assert.Equal(1, fixture.Events.SigningIns);
        Assert.Equal(1, fixture.Events.SignedIns);
        using var complete = await client.GetAsync("/_cellbridge/auth/complete");
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        using var discovery = await client.SendAsync(new(HttpMethod.Options, "/shared/"));
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        Assert.True(fixture.Events.Validations >= 2);
        using var denied = await client.GetAsync("/shared/unrelated/denied");
        Assert.Equal((HttpStatusCode)409, denied.StatusCode);
        Assert.Equal(1, fixture.Events.Denials);
        fixture.Events.Reject = true;
        using var rejected = await client.GetAsync("/_cellbridge/auth/complete");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [Fact]
    public async Task MountedAppUsesLocalReturnPathAndConfiguredExternalOrigin()
    {
        await using var fixture = await Fixture.Start(pathBase: "/mount", publicOrigin: "https://office.example:8446");
        using var challenge = await fixture.Client.SendAsync(new(HttpMethod.Head, "/mount/shared/file.docx"));
        Assert.Equal(HttpStatusCode.Forbidden, challenge.StatusCode);
        Assert.Equal("https://office.example:8446/mount/login?next=%2Fmount%2F_cellbridge%2Fauth%2Fcomplete",
            challenge.Headers.GetValues("X-FORMS_BASED_AUTH_REQUIRED").Single());
        Assert.Equal("https://office.example:8446/mount/_cellbridge/auth/complete",
            challenge.Headers.GetValues("X-FORMS_BASED_AUTH_RETURN_URL").Single());
        using var login = await fixture.Client.GetAsync("/mount/sign-in");
        fixture.Client.DefaultRequestHeaders.Add("Cookie", login.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        using var complete = await fixture.Client.SendAsync(new(HttpMethod.Head, "/mount/_cellbridge/auth/complete"));
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.True(complete.Headers.CacheControl!.NoStore);
        Assert.Empty(await complete.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task CustomCompletionPathIsEscapedAndCanBeReached()
    {
        await using var fixture = await Fixture.Start(completionPath: "/auth/complete 東京");
        using var challenge = await fixture.Client.SendAsync(new(HttpMethod.Options, "/shared/"));
        const string completion = "/auth/complete%20%E6%9D%B1%E4%BA%AC";
        Assert.Equal("https://localhost" + completion,
            challenge.Headers.GetValues("X-FORMS_BASED_AUTH_RETURN_URL").Single());
        var login = new Uri(challenge.Headers.GetValues("X-FORMS_BASED_AUTH_REQUIRED").Single());
        Assert.Equal(completion, Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(login.Query)["next"].ToString());
        using var signedIn = await fixture.Client.GetAsync("/sign-in");
        fixture.Client.DefaultRequestHeaders.Add("Cookie", signedIn.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        using var complete = await fixture.Client.GetAsync(completion);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
    }

    [Fact]
    public async Task CompletionRequiresMappedActorAndDocumentDenialDoesNotRestartLogin()
    {
        await using var fixture = await Fixture.Start();
        var documents = fixture.App.Services.GetRequiredService<CellBridgeDocumentService>();
        var owner = new CellBridgeActor(new("owner", "owner", "Owner"), true);
        await documents.CreateAsync("/shared/secret.docx", [1, 2, 3], owner);
        using var login = await fixture.Client.GetAsync("/sign-in?actor=false");
        fixture.Client.DefaultRequestHeaders.Add("Cookie", login.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        using var unmapped = await fixture.Client.GetAsync("/_cellbridge/auth/complete");
        Assert.Equal(HttpStatusCode.Unauthorized, unmapped.StatusCode);
        fixture.Client.DefaultRequestHeaders.Remove("Cookie");
        using var mappedLogin = await fixture.Client.GetAsync("/sign-in");
        fixture.Client.DefaultRequestHeaders.Add("Cookie", mappedLogin.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        using var denied = await fixture.Client.GetAsync("/shared/secret.docx");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Null(denied.Headers.Location);
        Assert.False(denied.Headers.Contains("X-FORMS_BASED_AUTH_REQUIRED"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("bearer")]
    [InlineData("events-type")]
    public async Task InvalidCookieConfigurationFailsBeforeServingRequests(string configuration)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        { await using var fixture = await Fixture.Start(configuration: configuration); });
        Assert.Contains(configuration == "events-type" ? "EventsType" : "registered cookie scheme", error.Message);
    }

    private sealed class Fixture(WebApplication app, HttpClient client, HostCookieEvents events) : IAsyncDisposable
    {
        public WebApplication App => app;
        public HttpClient Client => client;
        public HostCookieEvents Events => events;
        public static async Task<Fixture> Start(string pathBase = "", string? publicOrigin = null, string configuration = "valid", string? completionPath = null)
        {
            var events = new HostCookieEvents();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddAuthentication("bearer").AddScheme<AuthenticationSchemeOptions, BearerHandler>("bearer", _ => { })
                .AddCookie("office", o =>
                {
                    o.LoginPath = "/login";
                    o.ReturnUrlParameter = "next";
                    o.Events = events;
                    if (configuration == "events-type") o.EventsType = typeof(HostCookieEvents);
                });
            builder.Services.AddAuthorization();
            builder.Services.AddCellBridge(new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore()), requireDurability: false);
            builder.Services.AddCellBridgeOfficeFormsAuthentication(configuration is "missing" or "bearer" ? configuration : "office",
                o =>
                {
                    o.PublicOrigin = publicOrigin;
                    if (completionPath is not null) o.CompletionPath = completionPath;
                });
            var app = builder.Build();
            if (pathBase.Length > 0) app.UsePathBase(pathBase);
            app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
            app.MapCellBridge();
            app.MapGet("/api", (ClaimsPrincipal user) => user.Identity!.Name).RequireAuthorization();
            var cookiePolicy = new AuthorizeAttribute { AuthenticationSchemes = "office" };
            app.MapGet("/shared/unrelated/protected", () => "host").RequireAuthorization(cookiePolicy);
            app.MapGet("/shared/unrelated/denied", () => "host").RequireAuthorization(cookiePolicy)
                .RequireAuthorization(p => p.RequireClaim("admin"));
            app.MapGet("/sign-in", async (HttpContext context) =>
            {
                var claims = new List<Claim> { new(ClaimTypes.Name, "Cookie user") };
                if (context.Request.Query["actor"] != "false") claims.Add(new(CellBridgeActor.SubjectClaim, "reader"));
                await context.SignInAsync("office", new ClaimsPrincipal(new ClaimsIdentity(claims, "office")));
                return Results.Ok();
            }).AllowAnonymous();
            try { await app.StartAsync(); }
            catch { await app.DisposeAsync(); throw; }
            var client = app.GetTestClient(); client.BaseAddress = new("https://localhost");
            return new(app, client, events);
        }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }
    }

    private sealed class HostCookieEvents : CookieAuthenticationEvents
    {
        public int LoginRedirects, SigningIns, SignedIns, Validations, Denials;
        public bool Reject;
        public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
        { LoginRedirects++; context.Response.StatusCode = 418; return Task.CompletedTask; }
        public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
        { Denials++; context.Response.StatusCode = 409; return Task.CompletedTask; }
        public override Task SigningIn(CookieSigningInContext context) { SigningIns++; return Task.CompletedTask; }
        public override Task SignedIn(CookieSignedInContext context) { SignedIns++; return Task.CompletedTask; }
        public override Task ValidatePrincipal(CookieValidatePrincipalContext context)
        { Validations++; if (Reject) context.RejectPrincipal(); return Task.CompletedTask; }
    }

    private sealed class BearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.Name, "bearer"),
                new(CellBridgeActor.SubjectClaim, "bearer-subject")], Scheme.Name)), Scheme.Name)));
    }
}
