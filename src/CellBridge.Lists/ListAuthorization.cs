using CellBridge.Authorization.Internal;

namespace CellBridge.Lists;

public enum ListAuthorizationAction
{
    UseLists,
    CreateList,
    ReadAudit,
    Read,
    Write,
    Design
}

public sealed record ListAuthorizationRequest(
    string PolicyDomain,
    string Subject,
    string? TenantId,
    Guid WorkspaceId,
    Guid? ListId,
    ListAuthorizationAction Action);

public sealed record ListAuthorizationDecision(
    bool Allowed,
    DateTimeOffset? EvaluatedAtUtc = null,
    DateTimeOffset? FreshUntilUtc = null);

public interface IListAuthorizationProvider
{
    string PolicyDomain { get; }
    ValueTask<ListAuthorizationDecision> AuthorizeAsync(
        ListAuthorizationRequest request, CancellationToken ct = default);
}

public interface IListBatchAuthorizationProvider : IListAuthorizationProvider
{
    ValueTask<IReadOnlyList<ListAuthorizationDecision>> AuthorizeBatchAsync(
        IReadOnlyList<ListAuthorizationRequest> requests, CancellationToken ct = default);
}

public enum ListQueryAuthorizationScope
{
    None,
    All,
    CheckCandidates
}

public sealed record ListQueryAuthorizationDecision(
    ListQueryAuthorizationScope Scope,
    string PolicyDomain,
    string Subject,
    string? TenantId,
    Guid WorkspaceId,
    DateTimeOffset? EvaluatedAtUtc = null,
    DateTimeOffset? FreshUntilUtc = null);

public interface IListQueryAuthorizationProvider : IListAuthorizationProvider
{
    ValueTask<ListQueryAuthorizationDecision> AuthorizeQueryAsync(
        ListAuthorizationRequest request, CancellationToken ct = default);
}

internal static class ListAuthorizationValidation
{
    public static string ValidateDomain(string? value, string parameterName)
    {
        ValidateText(value, parameterName, 200);
        return value!;
    }

    public static void ValidateActor(ListActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ValidateText(actor.Subject, nameof(actor), 1024);
        if (actor.TenantId is not null) ValidateText(actor.TenantId, nameof(actor), 1024);
    }

    public static void ValidateDecision(ListAuthorizationDecision decision,
        DateTimeOffset callStartedUtc, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ValidateTimes(decision.EvaluatedAtUtc, decision.FreshUntilUtc, callStartedUtc, nowUtc);
    }

    public static void ValidateQueryDecision(ListQueryAuthorizationDecision decision,
        ListAuthorizationRequest request, DateTimeOffset callStartedUtc, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (!StringComparer.Ordinal.Equals(decision.PolicyDomain, request.PolicyDomain) ||
            !StringComparer.Ordinal.Equals(decision.Subject, request.Subject) ||
            !StringComparer.Ordinal.Equals(decision.TenantId, request.TenantId) ||
            decision.WorkspaceId != request.WorkspaceId || !Enum.IsDefined(decision.Scope))
            throw Unavailable();
        ValidateTimes(decision.EvaluatedAtUtc, decision.FreshUntilUtc, callStartedUtc, nowUtc);
    }

    public static ListException Unavailable() =>
        new(ListFailure.Unavailable, "List authorization is unavailable.");

    private static void ValidateTimes(DateTimeOffset? evaluated, DateTimeOffset? freshUntil,
        DateTimeOffset callStartedUtc, DateTimeOffset nowUtc)
    {
        if (!AuthorizationDecisionFreshness.IsValid(
                evaluated, freshUntil, callStartedUtc, nowUtc))
            throw Unavailable();
    }

    private static void ValidateText(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            !ListValidation.IsWellFormedText(value) || value.Any(char.IsControl))
            throw new ArgumentException("The value must contain valid non-control text.", parameterName);
    }
}
