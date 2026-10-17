using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Npgsql;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class HostStoreFailureTests(ListDatabaseFixture database)
{
    [PostgreSqlTheory]
    [InlineData("read")]
    [InlineData("create")]
    [InlineData("page")]
    public async Task ConnectionFailureAfterReadinessIsUnavailableBeforeAuthorityCalls(string operation)
    {
        await using var host = await Host.CreateAsync(database);
        await host.DisableLoginAsync();
        Task Attempt() => operation switch
        {
            "read" => host.Engine.GetListAsync(host.Actor, host.Workspace, Guid.NewGuid()),
            "create" => host.Engine.CreateListAsync(host.Actor, host.Workspace,
                new(Guid.NewGuid(), "Rejected", [new(Guid.NewGuid(), "Title", ListFieldType.Text)])),
            _ => host.Engine.GetListsPageAsync(host.Actor, host.Workspace)
        };

        Assert.Equal(ListFailure.Unavailable, (await Assert.ThrowsAsync<ListException>(Attempt)).Failure);
        Assert.Equal(0, host.Authority.Calls);
    }

    [PostgreSqlFact]
    public async Task ConnectionFailureAfterAuthorityAdmissionIsUnavailableBeforeCandidateReads()
    {
        await using var host = await Host.CreateAsync(database);
        host.Authority.AfterAuthorization = host.DisableLoginAsync;

        Assert.Equal(ListFailure.Unavailable, (await Assert.ThrowsAsync<ListException>(() =>
            host.Engine.GetListsPageAsync(host.Actor, host.Workspace))).Failure);
        Assert.Equal(1, host.Authority.Calls);
    }

    [PostgreSqlFact]
    public async Task AnchorStatementTimeoutIsUnavailableWithoutAuthorityCalls()
    {
        await using var host = await Host.CreateAsync(database, new() { StatementTimeout = TimeSpan.FromMilliseconds(50) });
        await using var connection = await database.AdminSource!.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("LOCK TABLE cellbridge_lists.workspaces IN ACCESS EXCLUSIVE MODE", connection, transaction))
            await command.ExecuteNonQueryAsync();

        var failure = await Assert.ThrowsAsync<ListException>(() => host.Engine.GetListAsync(
            host.Actor, host.Workspace, Guid.NewGuid()));

        Assert.Equal(ListFailure.Unavailable, failure.Failure);
        Assert.Contains("timed out", failure.Message);
        Assert.Equal(0, host.Authority.Calls);
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task CallerCancellationRemainsCancellationAfterReadiness()
    {
        await using var host = await Host.CreateAsync(database);
        await host.DisableLoginAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Engine.GetListAsync(
            host.Actor, host.Workspace, Guid.NewGuid(), cancellation.Token));
        Assert.Equal(0, host.Authority.Calls);
    }

    [PostgreSqlFact]
    public async Task DisposedDataSourceRemainsAProgrammingError()
    {
        await using var host = await Host.CreateAsync(database);
        await host.Source.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.Engine.GetListAsync(
            host.Actor, host.Workspace, Guid.NewGuid()));
        Assert.Equal(0, host.Authority.Calls);
    }

    private sealed class Host(ListDatabaseFixture database) : IAsyncDisposable
    {
        private readonly string _role = "lists_failure_" + Guid.NewGuid().ToString("N");
        private bool _roleCreated;
        public Guid Workspace { get; } = Guid.NewGuid();
        public ListActor Actor { get; } = new("host:failure-test", "failure-test-tenant");
        public Authority Authority { get; } = new();
        public NpgsqlDataSource Source { get; private set; } = null!;
        public PostgreSqlListEngine Engine { get; private set; } = null!;

        public static async Task<Host> CreateAsync(ListDatabaseFixture database, ListLimits? limits = null)
        {
            var host = new Host(database);
            try
            {
                var settings = new NpgsqlConnectionStringBuilder(database.RuntimeSource!.ConnectionString);
                var password = Guid.NewGuid().ToString("N");
                await using (var command = database.AdminSource!.CreateCommand($"""
                    CREATE ROLE "{host._role}" LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE INHERIT;
                    """))
                    await command.ExecuteNonQueryAsync();
                host._roleCreated = true;
                await using (var command = database.AdminSource.CreateCommand($"GRANT \"{settings.Username}\" TO \"{host._role}\""))
                    await command.ExecuteNonQueryAsync();
                settings.Username = host._role;
                settings.Password = password;
                settings.Pooling = false;
                settings.Timeout = 2;
                host.Source = NpgsqlDataSource.Create(settings.ConnectionString);
                host.Engine = new(host.Source, limits, host.Authority);
                await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, host.Workspace,
                    "Store failure test", host.Authority.PolicyDomain, host.Actor.TenantId!);
                await host.Engine.CheckHealthAsync();
                return host;
            }
            catch { await host.DisposeAsync(); throw; }
        }

        public async Task DisableLoginAsync()
        {
            await using var command = database.AdminSource!.CreateCommand($"ALTER ROLE \"{_role}\" NOLOGIN");
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (Source is not null) await Source.DisposeAsync();
            await using (var connection = await database.AdminSource!.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using var command = new NpgsqlCommand("""
                    SELECT set_config('cellbridge.workspace_id',@workspace::text,true);
                    DELETE FROM cellbridge_lists.workspaces WHERE workspace_id=@workspace;
                    """, connection, transaction);
                command.Parameters.AddWithValue("workspace", Workspace);
                await command.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            if (_roleCreated)
            {
                await using var command = database.AdminSource!.CreateCommand($"DROP ROLE \"{_role}\"");
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    private sealed class Authority : IListAuthorizationProvider
    {
        public string PolicyDomain => "host:store-failure-tests";
        public int Calls { get; private set; }
        public Func<Task>? AfterAuthorization { get; set; }
        public async ValueTask<ListAuthorizationDecision> AuthorizeAsync(ListAuthorizationRequest request, CancellationToken ct = default)
        {
            Calls++;
            if (AfterAuthorization is not null) await AfterAuthorization();
            return new(true);
        }
    }
}
