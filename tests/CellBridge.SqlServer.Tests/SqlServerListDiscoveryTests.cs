using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CellBridge.Lists;
using CellBridge.Lists.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.SqlServer.Tests;

public sealed partial class SqlServerListEngineTests
{
    [Fact]
    public async Task HostDiscoveryBoundsCandidateCallbacksAndReadsOnlyAllowedDefinitionsAcrossEmptyPages()
    {
        var reads = new DiscoveryReads();
        var env = await SetupAsync(reads);
        var workspace = Guid.NewGuid();
        var actor = new ListActor("host", "tenant");
        var policy = new DiscoveryPolicy();
        await SqlServerListSetup.RegisterHostWorkspaceAsync(env.AdminOptions, workspace, "Host", policy.PolicyDomain, actor.TenantId!);
        var engine = new SqlServerListEngine(env.RuntimeOptions, null, policy);
        var ids = new[] { Guid.Parse("00000001-0000-0000-0000-ffffffffffff"), Guid.Parse("80000000-0000-0000-0000-000000000002"), Guid.Parse("ffffffff-0000-0000-0000-000000000001") };
        for (var index = 0; index < ids.Length; index++) await engine.CreateListAsync(actor, workspace, NewListRequest() with { Id = ids[index], Name = "List " + (3 - index) });
        policy.Readable.Add(ids[2]);
        policy.Requests.Clear();
        reads.Reset();
        policy.OnCandidates = _ =>
        {
            Assert.Equal(0, reads.Definitions);
            return Task.CompletedTask;
        };
        var first = await engine.GetListsPageAsync(actor, workspace, candidateLimit: 2);
        Assert.Empty(first.Lists);
        Assert.NotNull(first.Continuation);
        Assert.Equal(0, reads.Definitions);
        Assert.Equal(ids.Take(2), policy.Requests.Where(request => request.ListId is not null).Select(request => request.ListId!.Value));
        Assert.All(reads.CandidateSql, sql => Assert.Contains("TOP(", sql.Replace(" ", "")));
        Assert.DoesNotContain(reads.CandidateSql, sql => sql.Contains("[name]", StringComparison.Ordinal));
        policy.OnCandidates = null;
        policy.Requests.Clear();
        var next = await engine.GetListsPageAsync(actor, workspace, first.Continuation, 2);
        Assert.Equal(ids[2], Assert.Single(next.Lists).Id);
        Assert.Null(next.Continuation);
        Assert.Equal(1, reads.Definitions);
        Assert.Single(policy.Requests, request => request.ListId is not null);
        var smallResultBudget = new SqlServerListEngine(env.RuntimeOptions, new ListLimits { MaxLists = 2 }, policy);
        Assert.Equal(ids[2], Assert.Single(await smallResultBudget.GetListsAsync(actor, workspace)).Id);
        policy.Scope = ListQueryAuthorizationScope.All;
        await Failure(ListFailure.Limit, () => smallResultBudget.GetListsAsync(actor, workspace));
        policy.UseLists = false;
        reads.Reset();
        await Failure(ListFailure.Forbidden, () => engine.GetListsPageAsync(actor, workspace));
        Assert.Empty(reads.CandidateSql);
        Assert.Equal(0, reads.Definitions);
        await Failure(ListFailure.NotFound, () => engine.GetListsPageAsync(actor with { TenantId = "TENANT" }, workspace));
    }

    [Theory]
    [InlineData(ListQueryAuthorizationScope.All)]
    [InlineData(ListQueryAuthorizationScope.None)]
    [InlineData(ListQueryAuthorizationScope.CheckCandidates)]
    public async Task HostQueryScopesAndReserveAreCheckedWithoutLeakingDeniedDefinitions(ListQueryAuthorizationScope scope)
    {
        var reads = new DiscoveryReads();
        var env = await SetupAsync(reads);
        var workspace = Guid.NewGuid();
        var actor = new ListActor("host", "tenant");
        var policy = new DiscoveryPolicy { Scope = scope };
        await SqlServerListSetup.RegisterHostWorkspaceAsync(env.AdminOptions, workspace, "Host", policy.PolicyDomain, actor.TenantId!);
        var engine = new SqlServerListEngine(env.RuntimeOptions, new ListLimits { MaxSecurityAuditEvents = 2 }, policy);
        var list = await engine.CreateListAsync(actor, workspace, NewListRequest());
        policy.Requests.Clear(); reads.Reset();
        var page = await engine.GetListsPageAsync(actor, workspace, candidateLimit: 1);
        if (scope == ListQueryAuthorizationScope.All) Assert.Equal(list.Id, Assert.Single(page.Lists).Id);
        else Assert.Empty(page.Lists);
        Assert.Equal(scope == ListQueryAuthorizationScope.CheckCandidates ? 1 : 0, policy.Requests.Count(request => request.ListId is not null));
        Assert.Equal(scope == ListQueryAuthorizationScope.All ? 1 : 0, reads.Definitions);
        if (scope == ListQueryAuthorizationScope.None) Assert.Empty(reads.CandidateSql);
        await using (var update = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, workspace))
        {
            (await update.Context.Workspaces.SingleAsync()).SecurityAuditCount = 2;
            await update.Context.SaveChangesAsync(); await update.CommitAsync();
        }
        reads.Reset(); policy.Requests.Clear();
        await Failure(ListFailure.Unavailable, () => engine.GetListsPageAsync(actor, workspace));
        Assert.Equal(0, reads.Definitions);
        Assert.Empty(reads.CandidateSql);
        Assert.DoesNotContain(policy.Requests, request => request.ListId is not null);
    }

    [Fact]
    public async Task NoneScopeDoesNotReadLockedListsAndCandidateCallbacksRunOutsideTransactions()
    {
        var env = await SetupAsync();
        var workspace = Guid.NewGuid();
        var actor = new ListActor("host", "tenant");
        var policy = new DiscoveryPolicy { Scope = ListQueryAuthorizationScope.None };
        await SqlServerListSetup.RegisterHostWorkspaceAsync(env.AdminOptions, workspace, "Host", policy.PolicyDomain, actor.TenantId!);
        var engine = new SqlServerListEngine(env.RuntimeOptions, new ListLimits { MaxSecurityAuditEvents = 2, StatementTimeout = TimeSpan.FromMilliseconds(200) }, policy);
        await engine.CreateListAsync(actor, workspace, NewListRequest());
        await using (var blocker = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, workspace))
        {
            await blocker.Context.Database.ExecuteSqlRawAsync("SELECT * FROM cellbridge_lists.lists WITH (TABLOCKX,HOLDLOCK); SELECT * FROM cellbridge_lists.list_schemas WITH (TABLOCKX,HOLDLOCK);");
            Assert.Empty((await engine.GetListsPageAsync(actor, workspace)).Lists);
        }
        policy.Scope = ListQueryAuthorizationScope.CheckCandidates;
        policy.OnCandidates = async _ =>
        {
            // Acquiring Exclusive here would deadlock if a discovery transaction
            // still held its Shared workspace lock during the host callback.
            await using var scope = await SqlServerListWorkspaceTransaction.OpenAsync(env.AdminOptions, workspace);
            await SqlServerListDatabase.LockAsync(scope.Context, SqlServerListDatabase.WorkspaceLock(workspace), true, new ListLimits(), default);
            (await scope.Context.Workspaces.SingleAsync()).SecurityAuditCount = 2;
            await scope.Context.SaveChangesAsync(); await scope.CommitAsync();
        };
        await Failure(ListFailure.Unavailable, () => engine.GetListsPageAsync(actor, workspace));
    }

    [Fact]
    public async Task HostDiscoveryRejectsWrongDecisionContextFreshnessAndBatchSizes()
    {
        var reads = new DiscoveryReads();
        var env = await SetupAsync(reads);
        var workspace = Guid.NewGuid(); var actor = new ListActor("host", "tenant"); var policy = new DiscoveryPolicy();
        await SqlServerListSetup.RegisterHostWorkspaceAsync(env.AdminOptions, workspace, "Host", policy.PolicyDomain, actor.TenantId!);
        var engine = new SqlServerListEngine(env.RuntimeOptions, null, policy);
        var list = await engine.CreateListAsync(actor, workspace, NewListRequest());
        policy.Readable.Add(list.Id);
        reads.Reset(); policy.BadContext = true;
        await Failure(ListFailure.Unavailable, () => engine.GetListsPageAsync(actor, workspace));
        Assert.Empty(reads.CandidateSql);
        policy.BadContext = false; policy.Expired = true;
        await Failure(ListFailure.Unavailable, () => engine.GetListsPageAsync(actor, workspace));
        Assert.Empty(reads.CandidateSql);
        policy.Expired = false; policy.BadCandidateBatch = true;
        await Failure(ListFailure.Unavailable, () => engine.GetListsPageAsync(actor, workspace));
        Assert.Equal(0, reads.Definitions);
        policy.BadCandidateBatch = false;
        Assert.Single((await engine.GetListsPageAsync(actor, workspace)).Lists);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListContinuationsAreBoundedAuthenticatedContextBoundAndReusableWithACopiedSharedKey(bool host)
    {
        var env = await SetupAsync();
        var workspace = Guid.NewGuid();
        var subject = string.Concat(Enumerable.Repeat("é\"\\/", 256));
        var tenant = string.Concat(Enumerable.Repeat("界\"\\/", 256));
        var actor = new ListActor(subject, tenant);
        var domain = string.Concat(Enumerable.Repeat("策\"\\/", 50));
        var policy = new HostPolicy(domain);
        if (host) await SqlServerListSetup.RegisterHostWorkspaceAsync(env.AdminOptions, workspace, "Host", domain, actor.TenantId!);
        else await SqlServerListSetup.RegisterWorkspaceAsync(env.AdminOptions, workspace, "Builtin", actor.Subject);
        var key = RandomNumberGenerator.GetBytes(32); var original = key.ToArray();
        var engine = host ? new SqlServerListEngine(env.RuntimeOptions, null, policy, key) : new SqlServerListEngine(env.RuntimeOptions, null, key);
        for (var index = 0; index < 2; index++) await engine.CreateListAsync(actor, workspace, NewListRequest());
        var page = await engine.GetListsPageAsync(actor, workspace, candidateLimit: 1);
        var token = Assert.IsType<string>(page.Continuation);
        Assert.InRange(token.Length, 1, 512);
        key[0] ^= 1;
        Assert.Single((await engine.GetListsPageAsync(actor, workspace, token, 1)).Lists);
        var other = host ? new SqlServerListEngine(env.RuntimeOptions, null, policy, original) : new SqlServerListEngine(env.RuntimeOptions, null, original);
        Assert.Null((await other.GetListsPageAsync(actor, workspace, token, 1)).Continuation);
        var changedKey = host ? new SqlServerListEngine(env.RuntimeOptions, null, policy, key) : new SqlServerListEngine(env.RuntimeOptions, null, key);
        await Failure(ListFailure.Validation, () => changedKey.GetListsPageAsync(actor, workspace, token, 1));
        var differentMode = host ? new SqlServerListEngine(env.RuntimeOptions, null, original)
            : new SqlServerListEngine(env.RuntimeOptions, null, policy, original);
        await Failure(ListFailure.Validation, () => differentMode.GetListsPageAsync(actor, workspace, token, 1));
        var differentDomain = new SqlServerListEngine(env.RuntimeOptions, null, new HostPolicy("different"), original);
        await Failure(ListFailure.Validation, () => differentDomain.GetListsPageAsync(actor, workspace, token, 1));
        await Failure(ListFailure.Validation, () => engine.GetListsPageAsync(actor with { Subject = actor.Subject[..^1] + "x" }, workspace, token, 1));
        await Failure(ListFailure.Validation, () => engine.GetListsPageAsync(actor with { TenantId = "different" }, workspace, token, 1));
        await Failure(ListFailure.Validation, () => engine.GetListsPageAsync(actor, Guid.NewGuid(), token, 1));
        var tampered = Convert.FromBase64String(token); tampered[^1] ^= 1;
        foreach (var invalid in new[] { "", "!", "AA==", new string('A', 513), Convert.ToBase64String(tampered) })
            await Failure(ListFailure.Validation, () => engine.GetListsPageAsync(actor, workspace, invalid, 1));
        var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new string?[] { actor.Subject, actor.TenantId, host ? "host" : "builtin", host ? domain : null }));
        var expired = JsonSerializer.SerializeToUtf8Bytes(new { workspaceId = workspace, fingerprint, after = page.Lists[0].Id, expires = DateTimeOffset.UtcNow.AddSeconds(-1) });
        var nonce = RandomNumberGenerator.GetBytes(12); var ciphertext = new byte[expired.Length]; var tag = new byte[16];
        using (var aes = new AesGcm(original, 16)) aes.Encrypt(nonce, expired, ciphertext, tag, Encoding.UTF8.GetBytes("CellBridge.Lists.SqlServer.Page.v1"));
        await Failure(ListFailure.Validation, () => engine.GetListsPageAsync(actor, workspace, Convert.ToBase64String(nonce.Concat(tag).Concat(ciphertext).ToArray()), 1));
        await Failure(ListFailure.Validation, () => engine.GetListsPageAsync(actor, workspace, candidateLimit: 0));
        await Failure(ListFailure.Validation, () => engine.GetListsPageAsync(actor, workspace, candidateLimit: 257));
    }

    [Fact]
    public async Task BuiltinDiscoveryPreservesWriteAndDesignVisibilityWithoutPointRead()
    {
        var env = await SetupAsync();
        var actor = new ListActor("limited");
        await env.Engine.SetMembershipAsync(Admin, env.Workspace, actor.Subject, true, false, false);
        var expected = new List<Guid>();
        foreach (var access in new[] { ListAccess.Write, ListAccess.Design, ListAccess.Read, ListAccess.None })
        {
            var list = await env.CreateListAsync();
            await env.Engine.SetListGrantAsync(Admin, env.Workspace, list.Id, actor.Subject, access);
            if (access != ListAccess.None) expected.Add(list.Id);
        }
        var actual = new List<Guid>(); string? continuation = null;
        do
        {
            var page = await env.Engine.GetListsPageAsync(actor, env.Workspace, continuation, 1);
            actual.AddRange(page.Lists.Select(list => list.Id)); continuation = page.Continuation;
        } while (continuation is not null);
        Assert.Equal(expected.OrderBy(id => id.ToString("D"), StringComparer.Ordinal), actual);
        foreach (var id in expected.Take(2)) await Failure(ListFailure.NotFound, () => env.Engine.GetListAsync(actor, env.Workspace, id));
        Assert.Equal(3, (await env.Engine.GetListsAsync(actor, env.Workspace)).Count);
    }

    private sealed class DiscoveryPolicy : IListBatchAuthorizationProvider, IListQueryAuthorizationProvider
    {
        public string PolicyDomain => "discovery";
        public bool UseLists = true, BadContext, Expired, BadCandidateBatch;
        public ListQueryAuthorizationScope Scope = ListQueryAuthorizationScope.CheckCandidates;
        public HashSet<Guid> Readable { get; } = [];
        public List<ListAuthorizationRequest> Requests { get; } = [];
        public Func<IReadOnlyList<ListAuthorizationRequest>, Task>? OnCandidates;
        public ValueTask<ListAuthorizationDecision> AuthorizeAsync(ListAuthorizationRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new ListAuthorizationDecision(request.Action == ListAuthorizationAction.UseLists ? UseLists :
                request.Action != ListAuthorizationAction.Read || request.ListId is not { } id || Readable.Contains(id)));
        }
        public async ValueTask<IReadOnlyList<ListAuthorizationDecision>> AuthorizeBatchAsync(IReadOnlyList<ListAuthorizationRequest> requests, CancellationToken ct = default)
        {
            var candidates = requests.All(request => request.Action == ListAuthorizationAction.Read && request.ListId is not null);
            if (candidates && OnCandidates is not null) await OnCandidates(requests);
            var result = new List<ListAuthorizationDecision>();
            foreach (var request in requests) result.Add(await AuthorizeAsync(request, ct));
            return candidates && BadCandidateBatch ? result.Take(Math.Max(0, result.Count - 1)).ToArray() : result;
        }
        public ValueTask<ListQueryAuthorizationDecision> AuthorizeQueryAsync(ListAuthorizationRequest request, CancellationToken ct = default) =>
            ValueTask.FromResult(new ListQueryAuthorizationDecision(Scope, BadContext ? "wrong" : request.PolicyDomain,
                request.Subject, request.TenantId, request.WorkspaceId, FreshUntilUtc: Expired ? DateTimeOffset.UtcNow.AddSeconds(-1) : null));
    }
    private sealed class DiscoveryReads : DbCommandInterceptor
    {
        public int Definitions;
        public List<string> CandidateSql { get; } = [];
        public void Reset() { Definitions = 0; CandidateSql.Clear(); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[cellbridge_lists].[list_schemas]", StringComparison.Ordinal)) Definitions++;
            else if (command.CommandText.Contains("FROM [cellbridge_lists].[lists]", StringComparison.Ordinal)) CandidateSql.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
