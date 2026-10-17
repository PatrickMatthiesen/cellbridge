using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class CachedHostAuthorizationIntegrationTests(ListDatabaseFixture database)
{
    [PostgreSqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revocation_rechecks_continuations_reads_and_writes(bool cached)
    {
        await using var host = await Host.CreateAsync(database, cached);
        var first = await host.CreateListAsync(host.Workspace, Guid.NewGuid());
        await host.CreateListAsync(host.Workspace, Guid.NewGuid());
        var record = await host.Engine.CreateRecordAsync(host.Actor, host.Workspace, first.Id,
            new(Guid.NewGuid(), 1, new JsonObject { [host.Field.Id.ToString("D")] = "original" }));
        await host.Engine.GetRecordAsync(host.Actor, host.Workspace, first.Id, record.Id);
        var page = await host.Engine.GetListsPageAsync(host.Actor, host.Workspace, candidateLimit: 1);
        Assert.Single(page.Lists);
        Assert.NotNull(page.Continuation);

        host.Authority.Scopes[(host.Actor.Subject, host.Workspace)] = ListQueryAuthorizationScope.None;
        host.Authority.Denied[(host.Actor.Subject, host.Workspace, first.Id)] = true;
        host.Cache?.InvalidateList(first.Id); // Must discard a workspace-wide All query too.

        var after = await host.Engine.GetListsPageAsync(host.Actor, host.Workspace, page.Continuation, 1);
        Assert.Empty(after.Lists);
        Assert.Null(after.Continuation);
        await NotFound(() => host.Engine.GetRecordAsync(host.Actor, host.Workspace, first.Id, record.Id));
        await NotFound(() => host.Engine.UpdateRecordAsync(host.Actor, host.Workspace, first.Id, record.Id,
            new(1, 1, [new(host.Field.Id, JsonValue.Create("denied"))])));
        await NotFound(() => host.Engine.CreateRecordAsync(host.Actor, host.Workspace, first.Id,
            new(Guid.NewGuid(), 1, new JsonObject { [host.Field.Id.ToString("D")] = "denied" })));

        var observer = new ListActor("observer", host.Actor.TenantId);
        var persisted = await host.Engine.GetRecordAsync(observer, host.Workspace, first.Id, record.Id);
        Assert.Equal(1, persisted.Revision);
        Assert.Equal("original", persisted.Values[host.Field.Id.ToString("D")]!.GetValue<string>());
        Assert.Single((await host.Engine.QueryAsync(observer, host.Workspace, first.Id, new())).Records);
    }

    [PostgreSqlFact]
    public async Task Ttl_bounds_stale_allows_and_denies_without_invalidation()
    {
        await using var host = await Host.CreateAsync(database, cached: true);
        var list = await host.CreateListAsync(host.Workspace, Guid.NewGuid());
        await host.Engine.GetListAsync(host.Actor, host.Workspace, list.Id);
        await host.Engine.GetListsPageAsync(host.Actor, host.Workspace);
        host.Authority.Denied[(host.Actor.Subject, host.Workspace, list.Id)] = true;
        host.Authority.Scopes[(host.Actor.Subject, host.Workspace)] = ListQueryAuthorizationScope.None;
        var calls = host.Authority.Calls;
        Assert.Equal(list.Id, (await host.Engine.GetListAsync(host.Actor, host.Workspace, list.Id)).Id);
        Assert.Single((await host.Engine.GetListsPageAsync(host.Actor, host.Workspace)).Lists);
        Assert.Equal(calls, host.Authority.Calls);

        host.Clock.Advance(TimeSpan.FromSeconds(6));
        await NotFound(() => host.Engine.GetListAsync(host.Actor, host.Workspace, list.Id));
        Assert.Empty((await host.Engine.GetListsPageAsync(host.Actor, host.Workspace)).Lists);
        host.Authority.Denied.Clear();
        host.Authority.Scopes.Clear();
        calls = host.Authority.Calls;
        await NotFound(() => host.Engine.GetListAsync(host.Actor, host.Workspace, list.Id));
        Assert.Empty((await host.Engine.GetListsPageAsync(host.Actor, host.Workspace)).Lists);
        Assert.Equal(calls, host.Authority.Calls);

        host.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(list.Id, (await host.Engine.GetListAsync(host.Actor, host.Workspace, list.Id)).Id);
        Assert.Single((await host.Engine.GetListsPageAsync(host.Actor, host.Workspace)).Lists);
    }

    [PostgreSqlFact]
    public async Task Warm_cache_separates_subject_workspace_and_trusted_tenant()
    {
        await using var host = await Host.CreateAsync(database, cached: true);
        var id = Guid.NewGuid();
        await host.CreateListAsync(host.Workspace, id);
        await host.CreateListAsync(host.OtherWorkspace, id);
        var other = new ListActor("other-user", host.Actor.TenantId);
        host.Authority.Denied[(other.Subject, host.Workspace, id)] = true;
        host.Authority.Denied[(host.Actor.Subject, host.OtherWorkspace, id)] = true;
        host.Authority.Scopes[(other.Subject, host.Workspace)] = ListQueryAuthorizationScope.None;
        host.Authority.Scopes[(host.Actor.Subject, host.OtherWorkspace)] = ListQueryAuthorizationScope.None;

        await host.Engine.GetListAsync(host.Actor, host.Workspace, id);
        Assert.Single((await host.Engine.GetListsPageAsync(host.Actor, host.Workspace)).Lists);
        await NotFound(() => host.Engine.GetListAsync(other, host.Workspace, id));
        await NotFound(() => host.Engine.GetListAsync(host.Actor, host.OtherWorkspace, id));
        Assert.Empty((await host.Engine.GetListsPageAsync(other, host.Workspace)).Lists);
        Assert.Empty((await host.Engine.GetListsPageAsync(host.Actor, host.OtherWorkspace)).Lists);
        var calls = host.Authority.Calls;
        await NotFound(() => host.Engine.GetListAsync(host.Actor with { TenantId = "wrong-tenant" }, host.Workspace, id));
        Assert.Equal(calls, host.Authority.Calls);
    }

    private static async Task NotFound(Func<Task> operation) =>
        Assert.Equal(ListFailure.NotFound, (await Assert.ThrowsAsync<ListException>(operation)).Failure);

    private sealed class Host(ListDatabaseFixture database) : IAsyncDisposable
    {
        public Guid Workspace { get; } = Guid.NewGuid();
        public Guid OtherWorkspace { get; } = Guid.NewGuid();
        public ListActor Actor { get; } = new("example-owner", "tenant:" + Guid.NewGuid().ToString("N"));
        public ListField Field { get; } = new(Guid.NewGuid(), "Title", ListFieldType.Text);
        public Authority Authority { get; } = new("cached-integration:" + Guid.NewGuid().ToString("N"));
        public ManualClock Clock { get; } = new();
        public CachedListAuthorizationProvider? Cache { get; private set; }
        public PostgreSqlListEngine Engine { get; private set; } = null!;

        public static async Task<Host> CreateAsync(ListDatabaseFixture database, bool cached)
        {
            var host = new Host(database);
            try
            {
                foreach (var workspace in new[] { host.Workspace, host.OtherWorkspace })
                    await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, workspace,
                        "Cached integration", host.Authority.PolicyDomain, host.Actor.TenantId!);
                host.Cache = cached ? new(host.Authority,
                    new() { Capacity = 64, DecisionTtl = TimeSpan.FromSeconds(5) }, host.Clock) : null;
                host.Engine = new(database.RuntimeSource!, null, (IListAuthorizationProvider?)host.Cache ?? host.Authority);
                return host;
            }
            catch { await host.DisposeAsync(); throw; }
        }

        public Task<ListDefinition> CreateListAsync(Guid workspace, Guid id) =>
            Engine.CreateListAsync(Actor, workspace, new(id, "List " + id, [Field]));

        public async ValueTask DisposeAsync()
        {
            await using var admin = await database.AdminSource!.OpenConnectionAsync();
            foreach (var workspace in new[] { Workspace, OtherWorkspace })
            {
                await using var transaction = await admin.BeginTransactionAsync();
                await using var command = admin.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT set_config('cellbridge.workspace_id',@workspace::text,true);
                    DELETE FROM cellbridge_lists.records WHERE workspace_id=@workspace;
                    DELETE FROM cellbridge_lists.workspaces WHERE workspace_id=@workspace;
                    """;
                command.Parameters.AddWithValue("workspace", workspace);
                await command.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
        }
    }

    private sealed class Authority(string domain) : IListAuthorizationProvider, IListQueryAuthorizationProvider
    {
        public string PolicyDomain => domain;
        public int Calls { get; private set; }
        public ConcurrentDictionary<(string, Guid, Guid), bool> Denied { get; } = new();
        public ConcurrentDictionary<(string, Guid), ListQueryAuthorizationScope> Scopes { get; } = new();

        public async ValueTask<ListAuthorizationDecision> AuthorizeAsync(ListAuthorizationRequest request, CancellationToken ct = default)
        {
            Calls++;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            return new(request.ListId is not { } id || !Denied.ContainsKey((request.Subject, request.WorkspaceId, id)));
        }

        public async ValueTask<ListQueryAuthorizationDecision> AuthorizeQueryAsync(ListAuthorizationRequest request, CancellationToken ct = default)
        {
            Calls++;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            return new(Scopes.GetValueOrDefault((request.Subject, request.WorkspaceId), ListQueryAuthorizationScope.All),
                request.PolicyDomain, request.Subject, request.TenantId, request.WorkspaceId);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) { _now += duration; _timestamp += duration.Ticks; }
    }
}

public sealed class PostgreSqlTheoryAttribute : TheoryAttribute
{
    public PostgreSqlTheoryAttribute()
    {
        if (new PostgreSqlFactAttribute().Skip is { } reason) Skip = reason;
    }
}
