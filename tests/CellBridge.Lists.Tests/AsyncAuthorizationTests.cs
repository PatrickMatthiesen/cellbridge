using CellBridge.Lists;

namespace CellBridge.Lists.Tests;

public sealed class AsyncAuthorizationTests
{
    [Fact]
    public async Task Cache_is_opt_in_bounded_and_subject_invalidation_rechecks_provider()
    {
        var provider = new CountingProvider();
        var cached = new CachedListAuthorizationProvider(provider,
            new ListAuthorizationCacheOptions { Capacity = 2, DecisionTtl = TimeSpan.FromMinutes(1) });
        var request = Request("subject-a", Guid.NewGuid());

        Assert.True((await cached.AuthorizeAsync(request)).Allowed);
        Assert.True((await cached.AuthorizeAsync(request)).Allowed);
        Assert.Equal(1, provider.Calls);

        cached.InvalidateSubject(request.Subject);
        Assert.True((await cached.AuthorizeAsync(request)).Allowed);
        Assert.Equal(2, provider.Calls);

        await cached.AuthorizeAsync(Request("subject-b", Guid.NewGuid()));
        await cached.AuthorizeAsync(Request("subject-c", Guid.NewGuid()));
        await cached.AuthorizeAsync(request);
        Assert.Equal(5, provider.Calls);
    }

    [Fact]
    public async Task Cache_fails_closed_when_provider_freshness_is_inconsistent()
    {
        var provider = new CountingProvider
        {
            Decision = new(true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(-1))
        };
        var cached = new CachedListAuthorizationProvider(provider,
            new ListAuthorizationCacheOptions { Capacity = 4, DecisionTtl = TimeSpan.FromMinutes(1) });

        Assert.False((await cached.AuthorizeAsync(Request("subject", Guid.NewGuid()))).Allowed);
    }

    [Fact]
    public async Task Omitted_timestamps_are_anchored_to_the_current_provider_call()
    {
        var provider = new CountingProvider { Decision = new(true) };
        var cached = new CachedListAuthorizationProvider(provider,
            new ListAuthorizationCacheOptions { Capacity = 4, DecisionTtl = TimeSpan.FromMinutes(1) });
        var request = Request("subject", Guid.NewGuid());

        Assert.True((await cached.AuthorizeAsync(request)).Allowed);
        Assert.True((await cached.AuthorizeAsync(request)).Allowed);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task List_invalidation_also_discards_workspace_wide_query_scope()
    {
        var provider = new QueryProvider();
        var cached = new CachedListAuthorizationProvider(provider,
            new ListAuthorizationCacheOptions { Capacity = 4, DecisionTtl = TimeSpan.FromMinutes(1) });
        var workspace = Guid.NewGuid();
        var request = new ListAuthorizationRequest("test-domain", "subject", "tenant", workspace,
            null, ListAuthorizationAction.Read);

        Assert.Equal(ListQueryAuthorizationScope.All, (await cached.AuthorizeQueryAsync(request)).Scope);
        Assert.Equal(ListQueryAuthorizationScope.All, (await cached.AuthorizeQueryAsync(request)).Scope);
        Assert.Equal(1, provider.QueryCalls);

        cached.InvalidateList(Guid.NewGuid());
        Assert.Equal(ListQueryAuthorizationScope.All, (await cached.AuthorizeQueryAsync(request)).Scope);
        Assert.Equal(2, provider.QueryCalls);
    }

    private static ListAuthorizationRequest Request(string subject, Guid listId) =>
        new("test-domain", subject, "tenant", Guid.NewGuid(), listId, ListAuthorizationAction.Read);

    private sealed class CountingProvider : IListAuthorizationProvider
    {
        public string PolicyDomain => "test-domain";
        public int Calls { get; private set; }
        public ListAuthorizationDecision? Decision { get; init; }

        public ValueTask<ListAuthorizationDecision> AuthorizeAsync(ListAuthorizationRequest request,
            CancellationToken ct = default)
        {
            Calls++;
            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(Decision ?? new ListAuthorizationDecision(true, now, now.AddMinutes(5)));
        }
    }

    private sealed class QueryProvider : IListQueryAuthorizationProvider
    {
        public string PolicyDomain => "test-domain";
        public int QueryCalls { get; private set; }

        public ValueTask<ListAuthorizationDecision> AuthorizeAsync(ListAuthorizationRequest request,
            CancellationToken ct = default) => ValueTask.FromResult(new ListAuthorizationDecision(true));

        public ValueTask<ListQueryAuthorizationDecision> AuthorizeQueryAsync(
            ListAuthorizationRequest request, CancellationToken ct = default)
        {
            QueryCalls++;
            return ValueTask.FromResult(new ListQueryAuthorizationDecision(ListQueryAuthorizationScope.All,
                request.PolicyDomain, request.Subject, request.TenantId, request.WorkspaceId));
        }
    }
}
