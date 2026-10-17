using CellBridge.Authorization.Internal;

namespace CellBridge.Lists;

public sealed class ListAuthorizationCacheOptions
{
    public int Capacity { get; init; } = 1024;
    public int MaximumKeyLength { get; init; } = 4096;
    public TimeSpan DecisionTtl { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumInvalidationRetries { get; init; } = 2;
}

/// <summary>
/// Adds an opt-in, bounded process-local cache to a list authorization provider.
/// Construct this decorator only when the host accepts the configured decision lifetime.
/// </summary>
public sealed class CachedListAuthorizationProvider : IListBatchAuthorizationProvider, IListQueryAuthorizationProvider
{
    private readonly IListAuthorizationProvider _provider;
    private readonly string _policyDomain;
    private readonly BoundedAuthorizationCache<ListAuthorizationDecision> _decisions;
    private readonly BoundedAuthorizationCache<ListQueryAuthorizationDecision> _queries;

    public CachedListAuthorizationProvider(IListAuthorizationProvider provider,
        ListAuthorizationCacheOptions options, TimeProvider? timeProvider = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        ArgumentNullException.ThrowIfNull(options);
        _policyDomain = ListAuthorizationValidation.ValidateDomain(provider.PolicyDomain, nameof(provider));
        var cacheOptions = new BoundedAuthorizationCacheOptions(options.Capacity,
            options.MaximumKeyLength, options.DecisionTtl, options.MaximumInvalidationRetries);
        _decisions = new(cacheOptions, static () => new(false, default, default), timeProvider);
        _queries = new(cacheOptions, () => new(ListQueryAuthorizationScope.None, PolicyDomain,
            string.Empty, null, Guid.Empty, default, default), timeProvider);
    }

    public string PolicyDomain => _policyDomain;

    public ValueTask<ListAuthorizationDecision> AuthorizeAsync(ListAuthorizationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!StringComparer.Ordinal.Equals(request.PolicyDomain, PolicyDomain))
            return ValueTask.FromResult(new ListAuthorizationDecision(false));
        return _decisions.AuthorizeAsync(Key(request, request.ListId?.ToString("D") ?? "workspace"),
            async token =>
            {
                var decision = await _provider.AuthorizeAsync(request, token).ConfigureAwait(false);
                return decision is null
                    ? new(new(false, default, default), false)
                    : new(decision, decision.Allowed, decision.EvaluatedAtUtc, decision.FreshUntilUtc);
            }, ct);
    }

    public async ValueTask<IReadOnlyList<ListAuthorizationDecision>> AuthorizeBatchAsync(
        IReadOnlyList<ListAuthorizationRequest> requests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var result = new List<ListAuthorizationDecision>(requests.Count);
        foreach (var request in requests) result.Add(await AuthorizeAsync(request, ct));
        return result;
    }

    public ValueTask<ListQueryAuthorizationDecision> AuthorizeQueryAsync(ListAuthorizationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!StringComparer.Ordinal.Equals(request.PolicyDomain, PolicyDomain))
            return ValueTask.FromResult(new ListQueryAuthorizationDecision(ListQueryAuthorizationScope.None,
                request.PolicyDomain, request.Subject, request.TenantId, request.WorkspaceId));
        if (_provider is not IListQueryAuthorizationProvider queryProvider)
            return ValueTask.FromResult(new ListQueryAuthorizationDecision(ListQueryAuthorizationScope.CheckCandidates,
                request.PolicyDomain, request.Subject, request.TenantId, request.WorkspaceId));
        return _queries.AuthorizeAsync(Key(request, "query"), async token =>
        {
            var decision = await queryProvider.AuthorizeQueryAsync(request, token).ConfigureAwait(false);
            var allowed = decision is not null && decision.Scope != ListQueryAuthorizationScope.None;
            return decision is null
                ? new(new(ListQueryAuthorizationScope.None, request.PolicyDomain, request.Subject,
                    request.TenantId, request.WorkspaceId, default, default), false)
                : new(decision, allowed, decision.EvaluatedAtUtc, decision.FreshUntilUtc);
        }, ct);
    }

    public void InvalidateSubject(string subject)
    {
        _decisions.InvalidateSubject(subject);
        _queries.InvalidateSubject(subject);
    }

    public void InvalidateWorkspace(Guid workspaceId)
    {
        var value = workspaceId.ToString("D");
        _decisions.InvalidateWorkspace(value);
        _queries.InvalidateWorkspace(value);
    }

    public void InvalidateList(Guid listId)
    {
        _decisions.InvalidateResource(listId.ToString("D"));
        // An All query decision can cover this resource without naming it in its cache key.
        // The query cache is bounded, so clearing it does not create invalidation metadata.
        _queries.InvalidateAll();
    }

    private AuthorizationCacheKey Key(ListAuthorizationRequest request, string resource) => new(
        PolicyDomain, request.Subject, request.TenantId, request.WorkspaceId.ToString("D"),
        resource, "lists-v1", request.Action.ToString());
}
