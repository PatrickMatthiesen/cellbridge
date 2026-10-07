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
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CellBridge.Packages.Tests;

public sealed class HostingRegistrationTests
{
    private static StorageProvider Memory() => new(new InMemoryStateStore(), new InMemoryContentStore());

    [Fact]
    public async Task TypedPolicyReusesHostSingletonAndActuallyControlsDocumentAccess()
    {
        var services = new ServiceCollection();
        var policy = new HostPolicy();
        services.AddSingleton(_ => policy);
        services.AddCellBridge<HostPolicy>(Memory(), requireDurability: false);
        using var sp = services.BuildServiceProvider();
        Assert.Same(policy, sp.GetRequiredService<ICellBridgeAuthorizationPolicy>());
        var documents = sp.GetRequiredService<CellBridgeDocumentService>();
        var state = (await documents.CreateAsync("/shared/policy.bin", [1, 2], TestActor.Value))!;
        Assert.True(documents.Access(TestActor.Value, state).HasFlag(DocumentAccess.Write));
        policy.Deny = true;
        Assert.Equal(DocumentAccess.None, documents.Access(TestActor.Value, state));
    }

    [Fact]
    public void DefaultPolicyStillUsesStoredGrantsAndCannotBeSilentlyReplaced()
    {
        var services = new ServiceCollection();
        services.AddCellBridge(Memory(), requireDurability: false);
        using var sp = services.BuildServiceProvider();
        Assert.IsType<StoredGrantAuthorizationPolicy>(sp.GetRequiredService<ICellBridgeAuthorizationPolicy>());
        Assert.Throws<InvalidOperationException>(() => services.AddCellBridge<HostPolicy>(Memory(), requireDurability: false));
    }

    [Theory]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    public void TypedPolicyRejectsCaptiveDependencies(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection();
        ((IServiceCollection)services).Add(new ServiceDescriptor(typeof(HostPolicy), typeof(HostPolicy), lifetime));
        Assert.Throws<InvalidOperationException>(() => services.AddCellBridge<HostPolicy>(Memory(), requireDurability: false));
    }

    [Fact]
    public void TypedPolicyRejectsCompetingAuthority()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICellBridgeAuthorizationPolicy, StoredGrantAuthorizationPolicy>();
        Assert.Throws<InvalidOperationException>(() => services.AddCellBridge<HostPolicy>(Memory(), requireDurability: false));
    }

    [Fact]
    public async Task CookieHelperServesProtectedOfficeLoginAndUsesSecureDefaults()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCellBridge(Memory(), requireDurability: false);
        builder.Services.AddCellBridgeCookieLogin<AccountChecker>();
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); app.MapCellBridge();
        await app.StartAsync();
        using var client = app.GetTestClient(); client.BaseAddress = new("https://localhost");
        using var challenge = await client.SendAsync(new(HttpMethod.Options, "/shared"));
        Assert.Equal(HttpStatusCode.Forbidden, challenge.StatusCode);
        var location = new Uri(challenge.Headers.GetValues("X-FORMS_BASED_AUTH_REQUIRED").Single());
        using var page = await client.GetAsync(location.PathAndQuery);
        var html = await page.Content.ReadAsStringAsync();
        client.DefaultRequestHeaders.Add("Cookie", page.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        using var badCsrf = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["login"] = "user", ["password"] = "test" }));
        Assert.Equal(HttpStatusCode.BadRequest, badCsrf.StatusCode);
        using var loggedIn = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = "user", ["password"] = "test", ["returnUrl"] = Field(html, "returnUrl"),
            ["__RequestVerificationToken"] = Field(html, "__RequestVerificationToken"),
            ["_cellbridgeState"] = Field(html, "_cellbridgeState"),
        }));
        Assert.Equal(HttpStatusCode.Redirect, loggedIn.StatusCode);
        Assert.Equal("/_cellbridge/auth/complete", loggedIn.Headers.Location!.ToString());
        var cookie = loggedIn.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith(".CellBridge.CellBridge="));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        using var complete = await client.GetAsync(loggedIn.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        using var discovery = await client.SendAsync(new(HttpMethod.Options, "/shared"));
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("CellBridge");
        Assert.False(options.SlidingExpiration);
        Assert.Equal(TimeSpan.FromHours(8), options.ExpireTimeSpan);
        await app.StopAsync();
    }

    [Fact]
    public async Task CookieHelperPreservesExplicitHostDefaultAndDoesNotSelectAnImplicitMixedDefault()
    {
        foreach (var explicitDefault in new[] { true, false })
        {
            var services = new ServiceCollection(); services.AddLogging();
            if (explicitDefault) services.AddAuthentication("api").AddCookie("api");
            else services.AddAuthentication().AddCookie("api");
            services.AddCellBridgeCookieLogin<AccountChecker>();
            using var sp = services.BuildServiceProvider();
            var schemes = sp.GetRequiredService<IAuthenticationSchemeProvider>();
            Assert.Equal(explicitDefault ? "api" : null, (await schemes.GetDefaultAuthenticateSchemeAsync())?.Name);
            Assert.Equal(explicitDefault ? "api" : null, (await schemes.GetDefaultChallengeSchemeAsync())?.Name);
        }
    }

    [Fact]
    public void CookieHelperRejectsDuplicateLoginAndInvalidPathsEarly()
    {
        var services = new ServiceCollection(); services.AddCellBridgeCookieLogin<AccountChecker>();
        Assert.Throws<InvalidOperationException>(() => services.AddCellBridgeCookieLogin<AccountChecker>("another"));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddCellBridgeCookieLogin<AccountChecker>(
            configure: o => o.OfficeLoginPath = "//untrusted"));
    }

    [Fact]
    public void PublishingRegistrationSharesFactoryDestinationPublisherAndHostedWorker()
    {
        var services = new ServiceCollection(); services.AddLogging();
        var destination = new Destination();
        services.AddSingleton(_ => destination);
        services.AddCellBridge(Memory(), requireDurability: false);
        services.AddCellBridgeExternalPublishing<Destination>();
        using var sp = services.BuildServiceProvider();
        Assert.Same(destination, sp.GetRequiredService<IExternalRevisionDestination>());
        Assert.Same(sp.GetRequiredService<ExternalRevisionPublicationWorker>(),
            Assert.Single(sp.GetServices<IHostedService>(), service => service is ExternalRevisionPublicationWorker));
        Assert.Same(sp.GetRequiredService<ExternalRevisionPublisher>(), sp.GetRequiredService<ExternalRevisionPublisher>());
        Assert.Throws<InvalidOperationException>(() => services.AddCellBridgeExternalPublishing<Destination>());
    }

    [Theory]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    public void PublishingRegistrationRejectsCaptiveDestination(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection();
        ((IServiceCollection)services).Add(new ServiceDescriptor(typeof(Destination), typeof(Destination), lifetime));
        Assert.Throws<InvalidOperationException>(() => services.AddCellBridgeExternalPublishing<Destination>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2147483648L)]
    public void PublishingRegistrationRejectsInvalidPollingIntervals(long milliseconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddCellBridgeExternalPublishing<Destination>(
            o => o.PollingInterval = TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void PublishingRegistrationRejectsCompetingDestinationOrPublisher()
    {
        var services = new ServiceCollection(); services.AddSingleton<IExternalRevisionDestination>(new Destination());
        Assert.Throws<InvalidOperationException>(() => services.AddCellBridgeExternalPublishing<Destination>());
        services = new(); services.AddSingleton<ExternalRevisionPublisher>();
        Assert.Throws<InvalidOperationException>(() => services.AddCellBridgeExternalPublishing<Destination>());
        services = new(); services.AddHostedService<ExternalRevisionPublicationWorker>();
        Assert.Throws<InvalidOperationException>(() => services.AddCellBridgeExternalPublishing<Destination>());
    }

    [Fact]
    public async Task WorkerBoundsEachPassAndRetriesSameOperationAfterTransientFailure()
    {
        var provider = Memory(); var destination = new Destination { FailOnce = true };
        using var worker = Worker(provider, destination);
        var id = await Queue(provider, 2);
        var first = (await provider.State.FindByResourceIdAsync(id))!.Publication!.Pending[0];
        Assert.Equal(0, await worker.PublishPendingOnceAsync());
        Assert.Equal(2, (await provider.State.FindByResourceIdAsync(id))!.Publication!.Pending.Length);
        Assert.Equal(1, await worker.PublishPendingOnceAsync());
        Assert.Single((await provider.State.FindByResourceIdAsync(id))!.Publication!.Pending);
        Assert.Equal(first.OperationId, destination.Operations[0]);
        Assert.Equal(first.OperationId, destination.Operations[1]);
        Assert.Equal(1, await worker.PublishPendingOnceAsync());
        Assert.Empty((await provider.State.FindByResourceIdAsync(id))!.Publication!.Pending);
    }

    [Fact]
    public async Task CancelledPassWaiterDoesNotReleaseAnotherPassAndConcurrentWakesAreSafe()
    {
        var provider = Memory(); var destination = new Destination { Pause = true };
        using var worker = Worker(provider, destination);
        await Queue(provider, 2);
        var active = worker.PublishPendingOnceAsync();
        await destination.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource();
        var waiter = worker.PublishPendingOnceAsync(cancel.Token); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        var next = worker.PublishPendingOnceAsync();
        await Task.WhenAll(Enumerable.Range(0, 1000).Select(_ => Task.Run(worker.Wake)));
        Assert.False(next.IsCompleted);
        Assert.Equal(1, destination.Calls);
        destination.Continue.TrySetResult();
        Assert.Equal(1, await active.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, await next.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task StopCancelsActiveAndWaitingPassesWithoutAcknowledgingPendingRevision()
    {
        var provider = Memory(); var destination = new Destination { Pause = true };
        using var worker = Worker(provider, destination);
        var id = await Queue(provider, 1);
        var active = worker.PublishPendingOnceAsync();
        await destination.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiter = worker.PublishPendingOnceAsync();
        await worker.StopAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.Single((await provider.State.FindByResourceIdAsync(id))!.Publication!.Pending);
        worker.Dispose();
        await Task.WhenAll(Enumerable.Range(0, 1000).Select(_ => Task.Run(worker.Wake)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.PublishPendingOnceAsync());
    }

    [Fact]
    public async Task HostedWorkerResumesPendingOperationAfterRestart()
    {
        var provider = Memory(); var destination = new Destination { FailOnce = true };
        var id = await Queue(provider, 1);
        using (var first = Worker(provider, destination))
            Assert.Equal(0, await first.PublishPendingOnceAsync());
        using var restarted = Worker(provider, destination);
        await restarted.StartAsync(CancellationToken.None);
        await destination.Applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await restarted.StopAsync(CancellationToken.None);
        Assert.Empty((await provider.State.FindByResourceIdAsync(id))!.Publication!.Pending);
        Assert.Equal(destination.Operations[0], destination.Operations[1]);
    }

    [Fact]
    public void SubMillisecondPollingCannotSpinTheWorker()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddCellBridgeExternalPublishing<Destination>(
            o => o.PollingInterval = TimeSpan.FromTicks(1)));
        var services = new ServiceCollection();
        services.AddCellBridgeExternalPublishing<Destination>(o => o.PollingInterval = TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task StopWaitsForManualPassCancellationCleanup()
    {
        var provider = Memory(); var destination = new Destination { Pause = true, DelayCancellationCleanup = true };
        using var worker = Worker(provider, destination);
        await Queue(provider, 1);
        var active = worker.PublishPendingOnceAsync();
        await destination.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = worker.StopAsync(CancellationToken.None);
        await destination.Cleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stopped.IsCompleted);
        destination.CleanupContinue.TrySetResult();
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
    }

    [Fact]
    public async Task ManualPassCleanupHonorsTheShutdownTimeout()
    {
        var provider = Memory(); var destination = new Destination { Pause = true, DelayCancellationCleanup = true };
        using var worker = Worker(provider, destination);
        await Queue(provider, 1);
        var active = worker.PublishPendingOnceAsync();
        await destination.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var deadline = new CancellationTokenSource();
        var stopped = worker.StopAsync(deadline.Token);
        await destination.Cleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped);
        destination.CleanupContinue.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
    }

    [Fact]
    public async Task TransientFailureWaitsEvenWhenAnotherDocumentHasABacklogAndWakesArrive()
    {
        var provider = Memory(); var destination = new Destination();
        destination.PersistentFailureId = await Queue(provider, 1, "/shared/a-failing.bin");
        var goodId = await Queue(provider, 2, "/shared/b-working.bin");
        using var worker = new ExternalRevisionPublicationWorker(provider, new(provider, destination),
            new() { PollingInterval = TimeSpan.FromSeconds(30) }, NullLogger<ExternalRevisionPublicationWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await destination.Applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(worker.Wake)));
        await Assert.ThrowsAsync<TimeoutException>(() => destination.ExtraAttempt.Task.WaitAsync(TimeSpan.FromMilliseconds(200)));
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(2, destination.Calls);
        Assert.Single((await provider.State.FindByResourceIdAsync(goodId))!.Publication!.Pending);
        Assert.Single((await provider.State.FindByResourceIdAsync(destination.PersistentFailureId))!.Publication!.Pending);
    }

    private static ExternalRevisionPublicationWorker Worker(StorageProvider provider, Destination destination) =>
        new(provider, new(provider, destination), new(), NullLogger<ExternalRevisionPublicationWorker>.Instance);

    private static async Task<Guid> Queue(StorageProvider provider, int count, string path = "/shared/queued.bin")
    {
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync(path, [1, 2, 3], TestActor.Value))!;
        var publisher = new ExternalRevisionPublisher(provider, new Destination());
        Assert.True(await publisher.BindAsync(state.ResourceId, state.LifecycleGeneration, state.StateVersion,
            Guid.NewGuid(), "host-file", "r0"));
        for (var i = 0; i < count; i++)
            await provider.State.TransitionAsync(state.ResourceId, (current, _) => new StateTransition<bool>(
                ExternalPublication.Append(current, current with { ContentVersion = current.ContentVersion + 1 },
                    Guid.NewGuid(), provider.Limits), true));
        return state.ResourceId;
    }

    private static string Field(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html,
        "name=\"" + name + "\" value=\"([^\"]+)\"").Groups[1].Value);

    public sealed class AccountChecker : ICellBridgeLoginAuthenticator
    {
        public Task<ClaimsPrincipal?> AuthenticateAsync(HttpContext context, string username, string password,
            CancellationToken cancellationToken) => Task.FromResult<ClaimsPrincipal?>(username == "user" && password == "test"
                ? new(new ClaimsIdentity([new Claim(CellBridgeActor.SubjectClaim, "tests:user")], "account")) : null);
    }

    public sealed class HostPolicy : ICellBridgeAuthorizationPolicy
    {
        public bool Deny { get; set; }
        public string PolicyDomain => "test:host-permissions";
        public DocumentAuthorizationBinding? BindNewDocument(Guid id) => new(PolicyDomain, 1, 1);
        public ICellBridgeAuthorizationSnapshot? Resolve(DocumentState state) =>
            Deny ? null : new Snapshot(state.ResourceId, state.Security.AuthorizationPolicy!);
        private sealed record Snapshot(Guid ResourceId, DocumentAuthorizationBinding Binding) : ICellBridgeAuthorizationSnapshot
        {
            public DocumentAccess Evaluate(string subject) => subject == TestActor.Value.Identity.Subject
                ? DocumentAccess.Write : DocumentAccess.None;
        }
    }

    public sealed class Destination : IExternalRevisionDestination
    {
        public bool FailOnce { get; set; }
        public bool Pause { get; set; }
        public bool DelayCancellationCleanup { get; set; }
        public Guid PersistentFailureId { get; set; }
        public TaskCompletionSource Cleaning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupContinue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ExtraAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public List<Guid> Operations { get; } = [];
        public TaskCompletionSource Applied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ExternalDeliveryResult> CompareExchangeAsync(ExternalDeliveryRequest request,
            Stream content, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref Calls) > 2) ExtraAttempt.TrySetResult();
            Operations.Add(request.Revision.OperationId); Entered.TrySetResult();
            if (Pause)
            {
                try { await Continue.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) when (DelayCancellationCleanup)
                {
                    Cleaning.TrySetResult();
                    await CleanupContinue.Task;
                    throw;
                }
            }
            if (request.Revision.ResourceId == PersistentFailureId) throw new IOException("Destination remains unavailable");
            if (FailOnce) { FailOnce = false; throw new IOException("Temporary destination failure"); }
            await content.CopyToAsync(Stream.Null, cancellationToken);
            Applied.TrySetResult();
            return new(ExternalDeliveryStatus.Applied, "r" + request.Revision.Sequence);
        }
    }
}
