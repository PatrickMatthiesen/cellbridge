using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CellBridge.AspNetCore;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.FssHttp.Tests;

public sealed class EntraAudienceDiscoveryTests
{
    private static readonly Guid HomeTenant = new("69f9a418-9852-49a6-8ce1-56fd1f454191");
    private static readonly Guid Application = new("14f5e08e-838a-4efb-a792-f5aef8093443");
    private static readonly Guid OtherTenant = new("e3430db3-ec36-4980-bbf1-f0cc46f1e16a");
    private static EntraDiscoverySettings Settings => new(HomeTenant, Application, "test-only-secret",
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));

    [Theory]
    [InlineData("AzureADMyOrg", CellBridgeEntraAccountAudience.SingleTenant, true, false, false)]
    [InlineData("AzureADMultipleOrgs", CellBridgeEntraAccountAudience.Organizations, true, true, false)]
    [InlineData("AzureADandPersonalMicrosoftAccount", CellBridgeEntraAccountAudience.OrganizationsAndPersonal, true, true, true)]
    [InlineData("PersonalMicrosoftAccount", CellBridgeEntraAccountAudience.Personal, false, false, true)]
    public async Task AzureAudiencesSelectAuthorityAndValidatedTenantClass(string azureAudience,
        CellBridgeEntraAccountAudience expected, bool home, bool other, bool personal)
    {
        var clock = new ManualClock();
        var source = new ScriptedSource(() => Task.FromResult(Metadata(azureAudience)));
        var snapshot = await new CachedEntraAudienceProvider(source, clock, Settings).GetAsync();
        Assert.Equal(expected, snapshot.Audience);
        Assert.Equal(home, snapshot.Allows(HomeTenant));
        Assert.Equal(other, snapshot.Allows(OtherTenant));
        Assert.Equal(personal, snapshot.Allows(CellBridgeEntraAudienceSnapshot.PersonalTenantId));
        Assert.False(snapshot.Allows(Guid.Empty));
        Assert.Equal(expected switch
        {
            CellBridgeEntraAccountAudience.SingleTenant => HomeTenant.ToString("D"),
            CellBridgeEntraAccountAudience.Organizations => "organizations",
            CellBridgeEntraAccountAudience.OrganizationsAndPersonal => "common",
            _ => "consumers",
        }, snapshot.AuthorityTenant);
    }

    [Fact]
    public async Task OneProviderFollowsWideningAndNarrowingWithoutRebuildingHost()
    {
        var clock = new ManualClock();
        var audience = "AzureADMyOrg";
        var source = new ScriptedSource(() => Task.FromResult(Metadata(audience)));
        var provider = new CachedEntraAudienceProvider(source, clock, Settings);
        var first = await provider.GetAsync();
        Assert.False(first.Allows(OtherTenant));
        audience = "AzureADandPersonalMicrosoftAccount";
        Assert.Same(first, await provider.GetAsync());
        clock.Advance(TimeSpan.FromSeconds(30));
        var widened = await provider.GetAsync();
        Assert.True(widened.Allows(OtherTenant));
        Assert.True(widened.Allows(CellBridgeEntraAudienceSnapshot.PersonalTenantId));
        audience = "PersonalMicrosoftAccount";
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False((await provider.GetAsync()).Allows(HomeTenant));
        audience = "AzureADMultipleOrgs";
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False((await provider.GetAsync()).Allows(CellBridgeEntraAudienceSnapshot.PersonalTenantId));
        audience = "AzureADMyOrg";
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False((await provider.GetAsync()).Allows(OtherTenant));
        Assert.Equal(5, source.Calls);
    }

    [Fact]
    public async Task ConcurrentCallersShareRefreshAndOneCancellationDoesNotCancelOtherCallers()
    {
        var clock = new ManualClock();
        var response = new TaskCompletionSource<CellBridgeEntraRegistrationMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ScriptedSource(() => response.Task);
        var provider = new CachedEntraAudienceProvider(source, clock, Settings);
        using var cancellation = new CancellationTokenSource();
        var cancelled = provider.GetAsync(cancellation.Token).AsTask();
        var survivors = Enumerable.Range(0, 8).Select(_ => provider.GetAsync().AsTask()).ToArray();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(source.LastToken.IsCancellationRequested);
        response.SetResult(Metadata("AzureADMyOrg"));
        var values = await Task.WhenAll(survivors);
        Assert.All(values, value => Assert.Same(values[0], value));
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task TimeoutOfSourceIgnoringCancellationDoesNotPermanentlyBlockRecovery()
    {
        var clock = new ManualClock();
        var never = new TaskCompletionSource<CellBridgeEntraRegistrationMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ScriptedSource(() => never.Task);
        var provider = new CachedEntraAudienceProvider(source, clock, Settings);
        var waiting = provider.GetAsync().AsTask();
        clock.Advance(TimeSpan.FromSeconds(11));
        var failure = await Assert.ThrowsAsync<EntraAudienceUnavailableException>(() => waiting);
        Assert.Equal("discovery-timeout", failure.ErrorCode);
        Assert.True(source.LastToken.IsCancellationRequested);
        source.Read = () => Task.FromResult(Metadata("AzureADMyOrg"));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True((await provider.GetAsync()).Allows(HomeTenant));
    }

    [Theory]
    [InlineData("wrong-application")]
    [InlineData("unknown-audience")]
    [InlineData("unsupported-token-version")]
    public async Task UntrustedRegistrationResponsesCannotBecomePolicy(string error)
    {
        var clock = new ManualClock();
        var metadata = error switch
        {
            "wrong-application" => Metadata("AzureADMyOrg") with { ClientId = Guid.NewGuid() },
            "unknown-audience" => Metadata("unknown-value"),
            _ => Metadata("AzureADMyOrg") with { RequestedAccessTokenVersion = 1 },
        };
        var provider = new CachedEntraAudienceProvider(new ScriptedSource(() => Task.FromResult(metadata)), clock, Settings);
        Assert.Equal(error, (await Assert.ThrowsAsync<EntraAudienceUnavailableException>(() => provider.GetAsync().AsTask())).ErrorCode);
    }

    [Fact]
    public async Task DiscoveryFailureNeverServesExpiredPolicyAndHonorsBackoffThenRecovers()
    {
        var clock = new ManualClock();
        var source = new ScriptedSource(() => Task.FromResult(Metadata("AzureADandPersonalMicrosoftAccount")));
        var provider = new CachedEntraAudienceProvider(source, clock, Settings);
        Assert.True((await provider.GetAsync()).Allows(OtherTenant));
        clock.Advance(TimeSpan.FromSeconds(30));
        source.Read = () => throw new EntraAudienceUnavailableException("graph-forbidden");
        Assert.Equal("graph-forbidden", (await Assert.ThrowsAsync<EntraAudienceUnavailableException>(() => provider.GetAsync().AsTask())).ErrorCode);
        Assert.Equal(2, source.Calls);
        source.Read = () => Task.FromResult(Metadata("AzureADMyOrg"));
        await Assert.ThrowsAsync<EntraAudienceUnavailableException>(() => provider.GetAsync().AsTask());
        Assert.Equal(2, source.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False((await provider.GetAsync()).Allows(OtherTenant));
        Assert.Equal(3, source.Calls);
    }

    [Fact]
    public async Task ThrottlingBackoffDoesNotAuthorizeWithOldPolicy()
    {
        var clock = new ManualClock();
        var source = new ScriptedSource(() => throw new EntraAudienceUnavailableException("discovery-throttled", TimeSpan.FromSeconds(90)));
        var provider = new CachedEntraAudienceProvider(source, clock, Settings);
        await Assert.ThrowsAsync<EntraAudienceUnavailableException>(() => provider.GetAsync().AsTask());
        source.Read = () => Task.FromResult(Metadata("AzureADMyOrg"));
        clock.Advance(TimeSpan.FromSeconds(89));
        await Assert.ThrowsAsync<EntraAudienceUnavailableException>(() => provider.GetAsync().AsTask());
        Assert.Equal(1, source.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await provider.GetAsync()).Allows(HomeTenant));
    }

    [Fact]
    public async Task DefaultGraphSourceReadsOnlyExpectedApplicationAndCachesCredentialSeparately()
    {
        var clock = new ManualClock();
        var requests = new List<string>();
        var tokens = 0;
        var graph = 0;
        var handler = new DelegateHandler(async request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            if (request.Method == HttpMethod.Post)
            {
                tokens++;
                var body = await request.Content!.ReadAsStringAsync();
                Assert.Contains("grant_type=client_credentials", body);
                Assert.Contains("scope=https%3A%2F%2Fgraph.microsoft.com%2F.default", body);
                Assert.Contains("client_secret=test-only-secret", body);
                return Json("{\"access_token\":\"mock-token\",\"expires_in\":120}");
            }
            graph++;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("mock-token", request.Headers.Authorization?.Parameter);
            return Json(RegistrationJson());
        });
        var source = new GraphEntraRegistrationSource(new TestClientFactory(handler), clock, Settings);
        Assert.Equal(Application, (await source.ReadAsync(default)).ClientId);
        await source.ReadAsync(default);
        Assert.Equal(1, tokens);
        Assert.Equal(2, graph);
        clock.Advance(TimeSpan.FromSeconds(61));
        await source.ReadAsync(default);
        Assert.Equal(2, tokens);
        Assert.All(requests.Where(r => r.Contains("graph.microsoft.com/v1.0", StringComparison.Ordinal)), uri =>
        {
            Assert.Contains($"applications(appId='{Application:D}')", uri);
            Assert.Contains("$select=appId,signInAudience,api", uri);
        });
        Assert.Equal($"https://login.microsoftonline.com/{HomeTenant:D}/oauth2/v2.0/token", requests[0]);
    }

    [Theory]
    [InlineData(401, "graph-unauthorized")]
    [InlineData(403, "graph-forbidden")]
    [InlineData(429, "discovery-throttled")]
    [InlineData(302, "discovery-http-failure")]
    public async Task GraphErrorsAreSanitizedAndThrottlingIsBounded(int status, string expected)
    {
        var handler = new DelegateHandler(request => Task.FromResult(request.Method == HttpMethod.Post
            ? Json("{\"access_token\":\"mock-token\",\"expires_in\":3600}")
            : Error((HttpStatusCode)status)));
        var source = new GraphEntraRegistrationSource(new TestClientFactory(handler), new ManualClock(), Settings);
        var failure = await Assert.ThrowsAsync<EntraAudienceUnavailableException>(() => source.ReadAsync(default));
        Assert.Equal(expected, failure.ErrorCode);
        Assert.DoesNotContain("provider-secret", failure.ToString(), StringComparison.Ordinal);
        if (status == 429) Assert.Equal(TimeSpan.FromMinutes(5), failure.RetryAfter);
    }

    [Theory]
    [InlineData(400, "credentials-rejected")]
    [InlineData(401, "credentials-rejected")]
    [InlineData(429, "discovery-throttled")]
    public async Task CredentialEndpointErrorsNeverExposeTheProviderBody(int status, string expected)
    {
        var handler = new DelegateHandler(_ => Task.FromResult(Error((HttpStatusCode)status)));
        var source = new GraphEntraRegistrationSource(new TestClientFactory(handler), new ManualClock(), Settings);
        var failure = await Assert.ThrowsAsync<EntraAudienceUnavailableException>(() => source.ReadAsync(default));
        Assert.Equal(expected, failure.ErrorCode);
        Assert.DoesNotContain("provider-secret", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GraphUnauthorizedInvalidatesCachedCredentialForNextAttempt()
    {
        var tokens = 0;
        var graph = 0;
        var handler = new DelegateHandler(request => Task.FromResult(request.Method == HttpMethod.Post
            ? Token(++tokens)
            : ++graph == 1 ? Error(HttpStatusCode.Unauthorized) : Json(RegistrationJson())));
        var source = new GraphEntraRegistrationSource(new TestClientFactory(handler), new ManualClock(), Settings);
        await Assert.ThrowsAsync<EntraAudienceUnavailableException>(() => source.ReadAsync(default));
        Assert.Equal(Application, (await source.ReadAsync(default)).ClientId);
        Assert.Equal(2, tokens);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"appId\":\"bad\",\"signInAudience\":\"AzureADMyOrg\",\"api\":{\"requestedAccessTokenVersion\":2}}")]
    [InlineData("{\"appId\":\"14f5e08e-838a-4efb-a792-f5aef8093443\",\"signInAudience\":\"AzureADMyOrg\",\"signInAudience\":\"PersonalMicrosoftAccount\",\"api\":{\"requestedAccessTokenVersion\":2}}")]
    public async Task MalformedGraphPayloadsDoNotReachThePolicyCache(string response)
    {
        var handler = new DelegateHandler(request => Task.FromResult(request.Method == HttpMethod.Post ? Token(1) : Json(response)));
        var source = new GraphEntraRegistrationSource(new TestClientFactory(handler), new ManualClock(), Settings);
        Assert.Equal("invalid-response", (await Assert.ThrowsAsync<EntraAudienceUnavailableException>(() => source.ReadAsync(default))).ErrorCode);
    }

    [Fact]
    public async Task OversizedGraphResponseIsRejectedBeforeParsing()
    {
        var handler = new DelegateHandler(request => Task.FromResult(request.Method == HttpMethod.Post ? Token(1) : Json(new string('x', 65537))));
        var source = new GraphEntraRegistrationSource(new TestClientFactory(handler), new ManualClock(), Settings);
        Assert.Equal("response-too-large", (await Assert.ThrowsAsync<EntraAudienceUnavailableException>(() => source.ReadAsync(default))).ErrorCode);
    }

    [Fact]
    public async Task CallerCanInjectSourceWithoutLiveGraphAccessAndLocalConfigurationFailsEarly()
    {
        var services = new ServiceCollection();
        var clock = new ManualClock();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<ICellBridgeEntraRegistrationSource>(new ScriptedSource(() => Task.FromResult(Metadata("AzureADMyOrg"))));
        CellBridgeEntraAudienceRegistration.Add(services, new CellBridgeEntraOptions
        {
            DiscoverAccountTypes = true,
            ClientSecret = "test-only-secret",
        }, HomeTenant, Application);
        using var provider = services.BuildServiceProvider();
        Assert.True((await provider.GetRequiredService<ICellBridgeEntraAudienceProvider>().GetAsync()).Allows(HomeTenant));
        Assert.Throws<ArgumentException>(() => CellBridgeEntraAudienceRegistration.Add(new ServiceCollection(),
            new CellBridgeEntraOptions { DiscoverAccountTypes = true }, HomeTenant, Application));
    }

    private static CellBridgeEntraRegistrationMetadata Metadata(string audience) => new(Application, audience, 2);
    private static string RegistrationJson() => $"{{\"appId\":\"{Application:D}\",\"signInAudience\":\"AzureADMyOrg\",\"api\":{{\"requestedAccessTokenVersion\":2}}}}";
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Token(int number) => Json($"{{\"access_token\":\"mock-token-{number}\",\"expires_in\":3600}}");
    private static HttpResponseMessage Error(HttpStatusCode status)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("provider-secret") };
        if (status == HttpStatusCode.TooManyRequests) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(1));
        if (status == HttpStatusCode.Found) response.Headers.Location = new("https://untrusted.example/");
        return response;
    }

    private sealed class ScriptedSource(Func<Task<CellBridgeEntraRegistrationMetadata>> read) : ICellBridgeEntraRegistrationSource
    {
        public Func<Task<CellBridgeEntraRegistrationMetadata>> Read { get; set; } = read;
        public int Calls { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public Task<CellBridgeEntraRegistrationMetadata> ReadAsync(CancellationToken cancellationToken)
        {
            Calls++;
            LastToken = cancellationToken;
            return Read();
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class TestClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        private readonly List<ManualTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount)
        {
            _now += amount;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _due;
            private TimeSpan _period;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                _period = period;
                return true;
            }
            public void FireIfDue()
            {
                if (_disposed || _due is null || _due > clock._now) return;
                _due = _period == Timeout.InfiniteTimeSpan ? null : clock._now + _period;
                callback(state);
            }
            public void Dispose() { _disposed = true; _due = null; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
