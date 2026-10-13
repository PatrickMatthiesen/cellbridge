using System.Net;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using CellBridge.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CellBridge.Storage.Tests;

public sealed class AuthenticationPageTests
{
    [Fact]
    public async Task ApplicationNameAndReturnUrlAreEncodedInTheDefaultPage()
    {
        const string name = "Research <Portal> & \"Team\"";
        const string returnUrl = "/library?name=\"<document>\"";
        await using var app = await Start(name);
        using var client = app.GetTestClient();
        client.BaseAddress = new("https://localhost");
        using var response = await client.GetAsync("/auth/login?returnUrl=" + Uri.EscapeDataString(returnUrl));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<h1>Sign in to " + HtmlEncoder.Default.Encode(name) + "</h1>", html);
        Assert.DoesNotContain(name, html);
        Assert.Contains("name=\"returnUrl\" value=\"" + HtmlEncoder.Default.Encode(returnUrl) + "\"", html);
        Assert.NotEmpty(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
    }

    [Fact]
    public async Task CustomPageReceivesAntiforgeryAndValidatedReturnUrl()
    {
        LoginPageContext? page = null;
        await using var app = await Start("Example", context =>
        {
            page = context;
            return "<!doctype html><title>Custom sign in</title>";
        });
        using var client = app.GetTestClient();
        client.BaseAddress = new("https://localhost");
        using var response = await client.GetAsync("/auth/login?failed=1&returnUrl=//evil.test");
        Assert.Equal("<!doctype html><title>Custom sign in</title>", await response.Content.ReadAsStringAsync());
        Assert.NotNull(page);
        Assert.Equal("Example", page.ApplicationName);
        Assert.True(page.SignInFailed);
        Assert.Equal("/auth/complete", page.ReturnUrl);
        Assert.Equal("__RequestVerificationToken", page.Antiforgery.FormFieldName);
        Assert.NotEmpty(page.Antiforgery.RequestToken!);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(AuthenticationServices.AntiforgeryCookie + "="));
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task BlankApplicationNameIsRejectedAtStartup()
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() => Start(" "));
    }

    [Fact]
    public async Task OversizedLoginFormIsRejectedBeforeAccountLookup()
    {
        await using var app = await Start("Example");
        using var client = app.GetTestClient();
        client.BaseAddress = new("https://localhost");
        using var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = new('a', 17 * 1024) }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LoginAttemptsAreRateLimitedByRemoteAddress()
    {
        await using var app = await Start("Example");
        using var client = app.GetTestClient();
        client.BaseAddress = new("https://localhost");
        for (int i = 0; i < 20; i++)
        {
            using var rejectedForm = await client.PostAsync("/auth/login", new StringContent(""));
            Assert.Equal(HttpStatusCode.BadRequest, rejectedForm.StatusCode);
        }

        using var limited = await client.PostAsync("/auth/login", new StringContent(""));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostGlobalRateLimiterIsPreserved(bool configureAfterAuthentication)
    {
        using var hostLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
            _ => RateLimitPartition.GetNoLimiter("host"));
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:cellbridge"] = "Host=localhost;Database=unused",
                ["Authentication:PublicOrigin"] = "https://localhost",
                ["Authentication:ApplicationName"] = "Example",
            }).Build();

        if (!configureAfterAuthentication)
            services.AddRateLimiter(options => options.GlobalLimiter = hostLimiter);
        services.AddCellBridgeAuthentication(configuration);
        if (configureAfterAuthentication)
            services.AddRateLimiter(options => options.GlobalLimiter = hostLimiter);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        Assert.Same(hostLimiter, options.GlobalLimiter);
    }

    [Fact]
    public void AggregateLoginBudgetIsSharedAcrossRemoteAddresses()
    {
        using var policy = new AuthenticationLoginRateLimitPolicy();
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(policy.GetPartition);

        for (int address = 1; address <= 5; address++)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                using var lease = limiter.AttemptAcquire(ContextFor($"192.0.2.{address}"), 1);
                Assert.True(lease.IsAcquired);
            }
        }

        using var rejected = limiter.AttemptAcquire(ContextFor("192.0.2.6"), 1);
        Assert.False(rejected.IsAcquired);
    }

    [Fact]
    public void ExhaustedRemoteAddressDoesNotConsumeAggregateBudget()
    {
        using var policy = new AuthenticationLoginRateLimitPolicy();
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(policy.GetPartition);

        for (int attempt = 0; attempt < 20; attempt++)
        {
            using var lease = limiter.AttemptAcquire(ContextFor("192.0.2.1"), 1);
            Assert.True(lease.IsAcquired);
        }
        using (var rejectedAddress = limiter.AttemptAcquire(ContextFor("192.0.2.1"), 1))
            Assert.False(rejectedAddress.IsAcquired);

        for (int address = 2; address <= 5; address++)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                using var lease = limiter.AttemptAcquire(ContextFor($"192.0.2.{address}"), 1);
                Assert.True(lease.IsAcquired);
            }
        }

        using var aggregateRejected = limiter.AttemptAcquire(ContextFor("192.0.2.6"), 1);
        Assert.False(aggregateRejected.IsAcquired);
    }

    [Fact]
    public void DisposingRemoteAddressPartitionLeavesAggregateLimiterAvailable()
    {
        using var policy = new AuthenticationLoginRateLimitPolicy();
        var firstPartition = policy.GetPartition(ContextFor("192.0.2.1"));
        var firstLimiter = firstPartition.Factory(firstPartition.PartitionKey);
        using (var lease = firstLimiter.AttemptAcquire(1))
            Assert.True(lease.IsAcquired);
        firstLimiter.Dispose();

        var secondPartition = policy.GetPartition(ContextFor("192.0.2.2"));
        using var secondLimiter = secondPartition.Factory(secondPartition.PartitionKey);
        using var secondLease = secondLimiter.AttemptAcquire(1);

        Assert.True(secondLease.IsAcquired);
    }

    [Fact]
    public void DisposingLoginPolicyClosesSharedAggregateLimiter()
    {
        var policy = new AuthenticationLoginRateLimitPolicy();
        var partition = policy.GetPartition(ContextFor("192.0.2.1"));
        using var remoteAddressLimiter = partition.Factory(partition.PartitionKey);

        policy.Dispose();

        Assert.Throws<ObjectDisposedException>(() => remoteAddressLimiter.AttemptAcquire(1));
    }

    [Fact]
    public void DisposingOwnedChainClosesTimerBackedLimiters()
    {
        var window = new FixedWindowRateLimiter(new()
        {
            PermitLimit = 1,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
        var concurrency = new ConcurrencyLimiter(new()
        {
            PermitLimit = 1,
            QueueLimit = 0,
        });
        var chain = new OwnedChainedRateLimiter([window, concurrency], [window, concurrency]);

        chain.Dispose();

        Assert.Throws<ObjectDisposedException>(() => chain.AttemptAcquire(1));
        Assert.Throws<ObjectDisposedException>(() => window.AttemptAcquire(1));
        Assert.Throws<ObjectDisposedException>(() => concurrency.AttemptAcquire(1));
    }

    [Fact]
    public void PerIpIdleDurationIgnoresBorrowedAggregateActivity()
    {
        var aggregate = new IdleRateLimiter(TimeSpan.Zero);
        var perIp = new IdleRateLimiter(TimeSpan.FromMinutes(15));
        using var chain = new OwnedChainedRateLimiter([aggregate, perIp], [perIp]);

        Assert.Equal(TimeSpan.FromMinutes(15), chain.IdleDuration);
    }

    [Fact]
    public void PerIpPartitionIsNotIdleWhileAnyLocalLimiterIsActive()
    {
        var aggregate = new IdleRateLimiter(TimeSpan.FromMinutes(15));
        var activePerIpLimiter = new IdleRateLimiter(null);
        var idlePerIpLimiter = new IdleRateLimiter(TimeSpan.FromMinutes(15));
        using var chain = new OwnedChainedRateLimiter(
            [activePerIpLimiter, idlePerIpLimiter, aggregate],
            [activePerIpLimiter, idlePerIpLimiter]);

        Assert.Null(chain.IdleDuration);
    }

    private static async Task<WebApplication> Start(string applicationName,
        Func<LoginPageContext, string>? renderer = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Page rendering requires no account database access.
            ["ConnectionStrings:cellbridge"] = "Host=localhost;Database=unused",
            ["Authentication:PublicOrigin"] = "https://localhost",
            ["Authentication:ApplicationName"] = applicationName,
        });
        builder.Services.AddCellBridgeAuthentication(builder.Configuration);
        var keyValidationService = builder.Services.Single(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(AuthenticationKeyValidationService));
        builder.Services.Remove(keyValidationService);
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseRequestTimeouts();
        app.UseAntiforgery();
        app.MapCellBridgeAuthentication(renderer);
        try
        {
            await app.StartAsync();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static DefaultHttpContext ContextFor(string address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return context;
    }

    private sealed class IdleRateLimiter(TimeSpan? idleDuration) : RateLimiter
    {
        public override TimeSpan? IdleDuration => idleDuration;
        public override RateLimiterStatistics? GetStatistics() => null;

        protected override RateLimitLease AttemptAcquireCore(int permitCount) =>
            throw new NotSupportedException();

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(
            int permitCount, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
