using System.Collections.Concurrent;
using System.Data.Common;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class ContextPoolingRegressionTests(ListDatabaseFixture database)
{
    [PostgreSqlFact]
    public async Task PooledLeaseReturnsAfterDenialFailureAndCancellationWithoutWorkspaceLeak()
    {
        var (firstWorkspace, secondWorkspace, listId) = await CreateTwinListsAsync();
        var denied = new ListActor("test:pool-denied:" + Guid.NewGuid().ToString("N"));
        await database.Engine.SetMembershipAsync(database.AdminActor, firstWorkspace, denied.Subject,
            true, false, false);

        var interceptor = new LeaseTrackingInterceptor();
        var options = new DbContextOptionsBuilder<PostgreSqlListDbContext>()
            .UseNpgsql(database.RuntimeSource!, provider => provider.CommandTimeout(17))
            .AddInterceptors(interceptor)
            .Options;
        var engine = new PostgreSqlListEngine(options);
        await engine.CheckHealthAsync();
        interceptor.Clear();

        Assert.Equal("First workspace", (await engine.GetListAsync(
            database.AdminActor, firstWorkspace, listId)).Name);
        Assert.Equal(ListFailure.NotFound, (await Assert.ThrowsAsync<ListException>(() =>
            engine.GetListAsync(denied, firstWorkspace, listId))).Failure);

        interceptor.FailNext();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.GetListAsync(database.AdminActor, firstWorkspace, listId));

        using var cancellation = new CancellationTokenSource();
        interceptor.CancelNext(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.GetListAsync(database.AdminActor, firstWorkspace, listId, cancellation.Token));

        Assert.Equal("Second workspace", (await engine.GetListAsync(
            database.AdminActor, secondWorkspace, listId)).Name);

        var leases = interceptor.Leases.ToArray();
        Assert.Single(leases.Select(lease => lease.InstanceId).Distinct());
        Assert.True(leases.Select(lease => lease.Lease).Distinct().Count() >= 5);
        Assert.All(leases, lease => Assert.Equal(17, lease.CommandTimeout));
    }

    [PostgreSqlFact]
    public async Task ConcurrentOperationsUseSeparateLeasesAndKeepWorkspaceStateIsolated()
    {
        var (firstWorkspace, secondWorkspace, listId) = await CreateTwinListsAsync();
        var interceptor = new OverlappingLeaseInterceptor();
        var options = new DbContextOptionsBuilder<PostgreSqlListDbContext>()
            .UseNpgsql(database.RuntimeSource!)
            .AddInterceptors(interceptor)
            .Options;
        var engine = new PostgreSqlListEngine(options);
        await engine.CheckHealthAsync();
        interceptor.Arm();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var definitions = await Task.WhenAll(
            engine.GetListAsync(database.AdminActor, firstWorkspace, listId, timeout.Token),
            engine.GetListAsync(database.AdminActor, secondWorkspace, listId, timeout.Token));

        Assert.Equal(new[] { "First workspace", "Second workspace" }, definitions.Select(value => value.Name));
        Assert.Equal(2, interceptor.InstanceIds.Distinct().Count());
    }

    [PostgreSqlFact]
    public async Task ExplicitConnectionOptionsStayUnpooledAndCallerOwned()
    {
        var workspace = await database.NewWorkspaceAsync("explicit connection fallback");
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new(Guid.NewGuid(), "Explicit connection", [new(Guid.NewGuid(), "Title", ListFieldType.Text)]));
        await using var connection = database.RuntimeSource!.CreateConnection();
        await connection.OpenAsync();
        var interceptor = new LeaseTrackingInterceptor();
        var options = new DbContextOptionsBuilder<PostgreSqlListDbContext>()
            .UseNpgsql(connection, false, provider => provider.CommandTimeout(23))
            .AddInterceptors(interceptor)
            .Options;
        var engine = new PostgreSqlListEngine(options);
        await engine.CheckHealthAsync();
        interceptor.Clear();

        Assert.Equal(list.Id, (await engine.GetListAsync(database.AdminActor, workspace, list.Id)).Id);
        Assert.Equal(list.Id, (await engine.GetListAsync(database.AdminActor, workspace, list.Id)).Id);

        var leases = interceptor.Leases.ToArray();
        Assert.Equal(2, leases.Select(lease => lease.InstanceId).Distinct().Count());
        Assert.All(leases, lease =>
        {
            Assert.Equal(0, lease.Lease);
            Assert.Equal(23, lease.CommandTimeout);
        });

        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        Assert.Equal(1, await command.ExecuteScalarAsync());
    }

    private async Task<(Guid FirstWorkspace, Guid SecondWorkspace, Guid ListId)> CreateTwinListsAsync()
    {
        var firstWorkspace = await database.NewWorkspaceAsync("pool first workspace");
        var secondWorkspace = await database.NewWorkspaceAsync("pool second workspace");
        var listId = Guid.NewGuid();
        await database.Engine.CreateListAsync(database.AdminActor, firstWorkspace,
            new(listId, "First workspace", [new(Guid.NewGuid(), "First", ListFieldType.Text)]));
        await database.Engine.CreateListAsync(database.AdminActor, secondWorkspace,
            new(listId, "Second workspace", [new(Guid.NewGuid(), "Second", ListFieldType.Integer)]));
        return (firstWorkspace, secondWorkspace, listId);
    }

    private sealed class LeaseTrackingInterceptor : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<ObservedLease> _leases = new();
        private CancellationTokenSource? _cancellation;
        private int _nextAction;

        public IReadOnlyCollection<ObservedLease> Leases => _leases;

        public void Clear()
        {
            while (_leases.TryDequeue(out _)) { }
        }

        public void FailNext() => Interlocked.Exchange(ref _nextAction, 1);

        public void CancelNext(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
            Interlocked.Exchange(ref _nextAction, 2);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var contextId = eventData.Context?.ContextId
                ?? throw new InvalidOperationException("The intercepted command has no EF context.");
            _leases.Enqueue(new(contextId.InstanceId, contextId.Lease, command.CommandTimeout));
            switch (Interlocked.Exchange(ref _nextAction, 0))
            {
                case 1:
                    throw new InvalidOperationException("Injected pooled operation failure.");
                case 2:
                    Interlocked.Exchange(ref _cancellation, null)?.Cancel();
                    throw new OperationCanceledException(cancellationToken);
                default:
                    return ValueTask.FromResult(result);
            }
        }
    }

    private sealed class OverlappingLeaseInterceptor : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<Guid> _instanceIds = new();
        private TaskCompletionSource<bool> _bothEntered = NewSignal();
        private int _remaining;

        public IReadOnlyCollection<Guid> InstanceIds => _instanceIds;

        public void Arm()
        {
            while (_instanceIds.TryDequeue(out _)) { }
            _bothEntered = NewSignal();
            Volatile.Write(ref _remaining, 2);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _remaining) <= 0) return result;
            var remaining = Interlocked.Decrement(ref _remaining);
            if (remaining < 0) return result;
            var context = eventData.Context
                ?? throw new InvalidOperationException("The intercepted command has no EF context.");
            _instanceIds.Enqueue(context.ContextId.InstanceId);
            if (remaining == 0) _bothEntered.TrySetResult(true);
            await _bothEntered.Task.WaitAsync(cancellationToken);
            return result;
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record ObservedLease(Guid InstanceId, int Lease, int CommandTimeout);
}
