using System.Globalization;
using CellBridge.Authorization.Internal;

namespace CellBridge.AspNetCore;

/// <summary>Configures the opt-in process-local document authorization cache.</summary>
public sealed class DocumentAuthorizationCacheOptions
{
    /// <summary>Gets the maximum number of cached or pending keys.</summary>
    public int Capacity { get; init; } = 1024;
    /// <summary>Gets the maximum combined character count of a cache key.</summary>
    public int MaximumKeyLength { get; init; } = 2048;
    /// <summary>
    /// Gets the local decision lifetime. Source evaluation age and source freshness can shorten it.
    /// Provider callback time consumes this lifetime.
    /// </summary>
    public TimeSpan DecisionTtl { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// Gets the number of retries after invalidation supersedes a pending evaluation. Exhaustion
    /// returns a denied decision.
    /// </summary>
    public int MaximumInvalidationRetries { get; init; } = 2;
}

/// <summary>
/// Adds a bounded process-local cache to an external document authorization provider.
/// Construct this decorator explicitly when a host accepts the configured decision lifetime.
/// A successful return from <see cref="AuthorizeAsync"/> is the local admission boundary. Work
/// admitted before a later invalidation may finish. Invalidation affects only this decorator
/// instance and does not publish revocation to another process.
/// </summary>
public sealed class CachedDocumentAuthorizationProvider : IDocumentAuthorizationProvider
{
    private readonly IDocumentAuthorizationProvider _provider;
    private readonly string _policyDomain;
    private readonly int _maximumKeyLength;
    private readonly BoundedAuthorizationCache<DocumentAuthorizationDecision> _cache;

    public CachedDocumentAuthorizationProvider(
        IDocumentAuthorizationProvider provider,
        DocumentAuthorizationCacheOptions options,
        TimeProvider? timeProvider = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(provider.PolicyDomain))
            throw new ArgumentException("The authorization policy domain is required.", nameof(provider));

        _policyDomain = provider.PolicyDomain;
        _maximumKeyLength = options.MaximumKeyLength;
        _cache = new(
            new(options.Capacity, options.MaximumKeyLength, options.DecisionTtl, options.MaximumInvalidationRetries),
            static () => new(false),
            timeProvider);
    }

    public string PolicyDomain => _policyDomain;

    public ValueTask<DocumentAuthorizationDecision> AuthorizeAsync(
        DocumentAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Scope);
        ArgumentNullException.ThrowIfNull(request.Binding);
        if (string.IsNullOrEmpty(request.Subject) || request.ResourceId == Guid.Empty ||
            request.LifecycleGeneration <= 0 || string.IsNullOrEmpty(request.Binding.PolicyDomain) ||
            request.Binding.PolicyDomain.Length > _maximumKeyLength || request.Binding.ContractVersion <= 0 ||
            request.Binding.Revision <= 0 || !Enum.IsDefined(request.Action))
            return ValueTask.FromResult(new DocumentAuthorizationDecision(false));

        var key = new AuthorizationCacheKey(
            PolicyDomain,
            request.Subject,
            request.Scope.TenantId,
            request.Scope.WorkspaceId,
            request.ResourceId.ToString("D"),
            Lifecycle(request),
            request.Action.ToString());

        return _cache.AuthorizeAsync(key, EvaluateAsync, cancellationToken);

        async ValueTask<AuthorizationCacheDecision<DocumentAuthorizationDecision>> EvaluateAsync(
            CancellationToken token)
        {
            var decision = await _provider.AuthorizeAsync(request, token).ConfigureAwait(false);
            if (decision is null) return new(new(false), false);
            return new(decision, decision.Allowed, decision.EvaluatedAtUtc, decision.FreshUntilUtc);
        }
    }

    /// <summary>Removes local entries for a subject and supersedes matching pending evaluations.</summary>
    public void InvalidateSubject(string subject) => _cache.InvalidateSubject(subject);

    /// <summary>Removes local entries for a document and supersedes matching pending evaluations.</summary>
    public void InvalidateResource(Guid resourceId) => _cache.InvalidateResource(resourceId.ToString("D"));

    /// <summary>Removes local entries for a workspace and supersedes matching pending evaluations.</summary>
    public void InvalidateWorkspace(string workspaceId) => _cache.InvalidateWorkspace(workspaceId);

    private static string Lifecycle(DocumentAuthorizationRequest request) => string.Create(
        CultureInfo.InvariantCulture,
        $"{request.LifecycleGeneration}:{request.Binding.PolicyDomain.Length}:{request.Binding.PolicyDomain}:{request.Binding.ContractVersion}:{request.Binding.Revision}");
}
