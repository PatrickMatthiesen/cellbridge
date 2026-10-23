using Microsoft.EntityFrameworkCore;

namespace CellBridge.Lists.SqlServer;

public sealed partial class SqlServerListEngine
{
    private readonly IListAuthorizationProvider? authorizationProvider;
    private readonly string? authorizationDomain;

    public SqlServerListEngine(DbContextOptions<SqlServerListDbContext> options, ListLimits? limits,
        IListAuthorizationProvider authorizationProvider, byte[]? continuationKey = null) : this(options, limits, continuationKey)
    {
        this.authorizationProvider = authorizationProvider ?? throw new ArgumentNullException(nameof(authorizationProvider));
        authorizationDomain = ListAuthorizationValidation.ValidateDomain(authorizationProvider.PolicyDomain, nameof(authorizationProvider));
    }

    // Host callbacks run outside the owned connection/transaction and workspace
    // locks. Preflights check source authority and reserve before exposing schemas
    // or requesting target authority. Final admission checks the anchor again.
    private async Task<OperationAuthorization?> AuthorizeOperationAsync(ListActor actor, Guid workspaceId,
        IReadOnlyList<(Guid? ListId, ListAuthorizationAction Action)> requirements,
        Func<SqlServerListDbContext, Task<Guid[]>>? referenceTargets, CancellationToken ct)
    {
        if (authorizationProvider is null) return null;
        WorkspaceAnchor anchor;
        await using (var scope = await SqlServerListWorkspaceTransaction.OpenAsync(options, workspaceId, ct, limits.StatementTimeout))
        {
            await CheckConfigurationAsync(scope.Context, ct);
            var workspace = await scope.Context.Workspaces.AsNoTracking().SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId, ct)
                ?? throw NotFound();
            anchor = new(workspace.WorkspaceId, workspace.AuthorizationMode, workspace.AuthorizationDomain, workspace.AuthorizationScope);
            if (anchor.Mode != "host" || !StringComparer.Ordinal.Equals(anchor.Domain, authorizationDomain) ||
                !StringComparer.Ordinal.Equals(anchor.Scope, actor.TenantId)) throw NotFound();
            await scope.CommitAsync(ct);
        }
        var authorization = new OperationAuthorization(anchor);
        await EvaluateAsync(authorization, actor, new[] { ((Guid?)null, ListAuthorizationAction.UseLists) }.Concat(requirements), ct);
        if (referenceTargets is not null)
        {
            Guid[] targets;
            await using (var scope = await SqlServerListWorkspaceTransaction.OpenAsync(options, workspaceId, ct, limits.StatementTimeout))
            {
                var db = scope.Context;
                await SqlServerListDatabase.LockAsync(db, SqlServerListDatabase.WorkspaceLock(workspaceId), false, limits, ct);
                var workspace = await db.Workspaces.AsNoTracking().SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId, ct) ?? throw NotFound();
                RequireAnchor(workspace, authorization, actor);
                authorization.ValidateFreshness();
                RequireReserve(workspace);
                targets = await referenceTargets(db);
                await scope.CommitAsync(ct);
            }
            await EvaluateAsync(authorization, actor, targets.Distinct().Select(id => ((Guid?)id, ListAuthorizationAction.Read)), ct);
        }
        return authorization;
    }

    private async Task EvaluateAsync(OperationAuthorization authorization, ListActor actor,
        IEnumerable<(Guid? ListId, ListAuthorizationAction Action)> requirements, CancellationToken ct)
    {
        var requests = requirements.Distinct().Where(requirement => !authorization.Allows(requirement.ListId, requirement.Action))
            .Select(requirement => new ListAuthorizationRequest(authorization.Anchor.Domain!, actor.Subject, authorization.Anchor.Scope,
                authorization.Anchor.WorkspaceId, requirement.ListId, requirement.Action)).ToArray();
        if (requests.Length == 0) return;
        var started = DateTimeOffset.UtcNow;
        IReadOnlyList<ListAuthorizationDecision> decisions;
        try
        {
            if (authorizationProvider is IListBatchAuthorizationProvider batch)
                decisions = await batch.AuthorizeBatchAsync(requests, ct);
            else
            {
                var results = new List<ListAuthorizationDecision>();
                foreach (var request in requests) results.Add(await authorizationProvider!.AuthorizeAsync(request, ct));
                decisions = results;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw ListAuthorizationValidation.Unavailable(); }
        if (decisions is null || decisions.Count != requests.Length) throw ListAuthorizationValidation.Unavailable();
        for (var i = 0; i < requests.Length; i++)
        {
            ListAuthorizationValidation.ValidateDecision(decisions[i], started, DateTimeOffset.UtcNow);
            if (!decisions[i].Allowed) throw requests[i].ListId is null ? Forbidden() : NotFound();
            authorization.Decisions.Add((requests[i], decisions[i], started));
        }
    }

    private static void RequireAnchor(WorkspaceRow row, OperationAuthorization authorization, ListActor actor)
    {
        if (new WorkspaceAnchor(row.WorkspaceId, row.AuthorizationMode, row.AuthorizationDomain, row.AuthorizationScope) != authorization.Anchor ||
            !StringComparer.Ordinal.Equals(row.AuthorizationScope, actor.TenantId)) throw NotFound();
    }

    private sealed record WorkspaceAnchor(Guid WorkspaceId, string Mode, string? Domain, string? Scope);
    private sealed class OperationAuthorization(WorkspaceAnchor anchor)
    {
        internal WorkspaceAnchor Anchor => anchor;
        internal List<(ListAuthorizationRequest Request, ListAuthorizationDecision Decision, DateTimeOffset Started)> Decisions { get; } = [];
        internal bool Allows(Guid? listId, ListAuthorizationAction action) =>
            Decisions.Any(pair => pair.Request.ListId == listId && pair.Request.Action == action && pair.Decision.Allowed);
        internal bool Allows(Guid listId, ListAccess access) =>
            (!access.HasFlag(ListAccess.Read) || Allows(listId, ListAuthorizationAction.Read)) &&
            (!access.HasFlag(ListAccess.Write) || Allows(listId, ListAuthorizationAction.Write)) &&
            (!access.HasFlag(ListAccess.Design) || Allows(listId, ListAuthorizationAction.Design));
        internal void ValidateFreshness()
        {
            foreach (var pair in Decisions)
                ListAuthorizationValidation.ValidateDecision(pair.Decision, pair.Started, DateTimeOffset.UtcNow);
        }
    }
}
