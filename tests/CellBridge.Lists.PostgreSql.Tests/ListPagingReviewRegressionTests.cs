using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Npgsql;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class ListPagingReviewRegressionTests(ListDatabaseFixture database)
{
    [PostgreSqlTheory]
    [InlineData(ListQueryAuthorizationScope.All)]
    [InlineData(ListQueryAuthorizationScope.CheckCandidates)]
    public async Task Host_enumeration_rejects_an_exhausted_security_audit_reserve(ListQueryAuthorizationScope scope)
    {
        var host = await CreateHostAsync(scope);
        try
        {
            await SetReserveAsync(host.Workspace, 2);
            await Unavailable(() => host.Engine.GetListsPageAsync(host.Actor, host.Workspace));
            await Unavailable(() => host.Engine.GetListsAsync(host.Actor, host.Workspace));
            Assert.Equal(0, host.Authority.CandidateReads);
        }
        finally { await DeleteWorkspaceAsync(host.Workspace); }
    }

    [PostgreSqlTheory]
    [InlineData(ListQueryAuthorizationScope.All)]
    [InlineData(ListQueryAuthorizationScope.CheckCandidates)]
    public async Task Host_enumeration_rechecks_reserve_after_candidate_authorization(ListQueryAuthorizationScope scope)
    {
        var host = await CreateHostAsync(scope);
        try
        {
            await using var blocker = await database.AdminSource!.OpenConnectionAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using (var command = new NpgsqlCommand("""
                SELECT set_config('cellbridge.workspace_id',@workspace::text,true);
                SELECT pg_advisory_xact_lock(hashtextextended(@workspace::text,391813));
                """, blocker, transaction))
            {
                command.Parameters.AddWithValue("workspace", host.Workspace);
                await command.ExecuteNonQueryAsync();
            }

            var pending = host.Engine.GetListsPageAsync(host.Actor, host.Workspace, candidateLimit: 1);
            // Candidate discovery and host callbacks finish before the definition transaction
            // waits on this workspace lock. Exhaust the reserve while that transaction waits.
            await database.WaitForAdvisoryWaitAsync();
            Assert.Equal(scope == ListQueryAuthorizationScope.CheckCandidates ? 1 : 0, host.Authority.CandidateReads);
            await SetReserveAsync(host.Workspace, 2, blocker, transaction);
            await transaction.CommitAsync();
            await Unavailable(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { await DeleteWorkspaceAsync(host.Workspace); }
    }

    [PostgreSqlFact]
    public async Task Native_pages_preserve_write_and_design_only_visibility_across_continuations()
    {
        var workspace = await database.NewWorkspaceAsync("Native paging grants");
        try
        {
            var actor = new ListActor("paging:" + Guid.NewGuid().ToString("N"));
            await database.Engine.SetMembershipAsync(database.AdminActor, workspace, actor.Subject, true, false, false);
            var grants = new[] { ListAccess.Write, ListAccess.Design, ListAccess.Read, ListAccess.None };
            var ids = new List<Guid>();
            for (var index = 0; index < grants.Length; index++)
            {
                var id = Guid.Parse("00000000-0000-0000-0000-" + (index + 1).ToString("D12"));
                var field = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
                await database.Engine.CreateListAsync(database.AdminActor, workspace,
                    new(id, "List " + (grants.Length - index), [field]));
                await database.Engine.SetListGrantAsync(database.AdminActor, workspace, id, actor.Subject, grants[index]);
                ids.Add(id);
            }

            var expected = ids.Take(3).ToArray();
            var unpaged = await database.Engine.GetListsAsync(actor, workspace);
            Assert.Equal(expected, unpaged.Select(list => list.Id).Order().ToArray());
            var paged = new List<Guid>();
            string? continuation = null;
            for (var index = 0; index < expected.Length; index++)
            {
                var page = await database.Engine.GetListsPageAsync(actor, workspace, continuation, candidateLimit: 1);
                paged.Add(Assert.Single(page.Lists).Id);
                continuation = page.Continuation;
                if (index < expected.Length - 1) Assert.NotNull(continuation);
                else Assert.Null(continuation);
            }
            Assert.Equal(expected, paged);
            // Enumeration preserves existing visibility without granting point Read access.
            foreach (var id in ids.Take(2))
                Assert.Equal(ListFailure.NotFound, (await Assert.ThrowsAsync<ListException>(() =>
                    database.Engine.GetListAsync(actor, workspace, id))).Failure);
        }
        finally { await DeleteWorkspaceAsync(workspace); }
    }

    private async Task<Host> CreateHostAsync(ListQueryAuthorizationScope scope)
    {
        var workspace = Guid.NewGuid();
        var actor = new ListActor("host:paging", "tenant:" + Guid.NewGuid().ToString("N"));
        var authority = new PageAuthority("paging:" + Guid.NewGuid().ToString("N"), scope);
        try
        {
            await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, workspace,
                "Host paging reserve", authority.PolicyDomain, actor.TenantId!);
            var engine = new PostgreSqlListEngine(database.RuntimeSource!, new() { MaxSecurityAuditEvents = 2 }, authority);
            await engine.CreateListAsync(actor, workspace,
                new(Guid.NewGuid(), "Visible", [new(Guid.NewGuid(), "Title", ListFieldType.Text)]));
            return new(workspace, actor, authority, engine);
        }
        catch { await DeleteWorkspaceAsync(workspace); throw; }
    }

    private async Task SetReserveAsync(Guid workspace, int count)
    {
        await using var connection = await database.AdminSource!.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetReserveAsync(workspace, count, connection, transaction);
        await transaction.CommitAsync();
    }

    private static async Task SetReserveAsync(Guid workspace, int count, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand("""
            SELECT set_config('cellbridge.workspace_id',@workspace::text,true);
            UPDATE cellbridge_lists.workspaces SET security_audit_count=@count WHERE workspace_id=@workspace;
            """, connection, transaction);
        command.Parameters.AddWithValue("workspace", workspace);
        command.Parameters.AddWithValue("count", count);
        await command.ExecuteNonQueryAsync();
    }

    private async Task DeleteWorkspaceAsync(Guid workspace)
    {
        await using var connection = await database.AdminSource!.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT set_config('cellbridge.workspace_id',@workspace::text,true);
            DELETE FROM cellbridge_lists.records WHERE workspace_id=@workspace;
            DELETE FROM cellbridge_lists.workspaces WHERE workspace_id=@workspace;
            """, connection, transaction);
        command.Parameters.AddWithValue("workspace", workspace);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task Unavailable(Func<Task> operation) =>
        Assert.Equal(ListFailure.Unavailable, (await Assert.ThrowsAsync<ListException>(operation)).Failure);

    private sealed record Host(Guid Workspace, ListActor Actor, PageAuthority Authority, PostgreSqlListEngine Engine);

    private sealed class PageAuthority(string domain, ListQueryAuthorizationScope scope)
        : IListAuthorizationProvider, IListQueryAuthorizationProvider
    {
        private int _candidateReads;
        public string PolicyDomain => domain;
        public int CandidateReads => Volatile.Read(ref _candidateReads);
        public ValueTask<ListAuthorizationDecision> AuthorizeAsync(ListAuthorizationRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (request.ListId is not null && request.Action == ListAuthorizationAction.Read)
                Interlocked.Increment(ref _candidateReads);
            return ValueTask.FromResult(new ListAuthorizationDecision(true));
        }
        public ValueTask<ListQueryAuthorizationDecision> AuthorizeQueryAsync(ListAuthorizationRequest request, CancellationToken ct = default) =>
            ValueTask.FromResult(new ListQueryAuthorizationDecision(scope, request.PolicyDomain,
                request.Subject, request.TenantId, request.WorkspaceId));
    }
}
