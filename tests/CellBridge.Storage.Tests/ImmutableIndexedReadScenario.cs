using System.Data.Common;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.Tests;

public static class ImmutableIndexedReadScenario
{
    public static async Task RunHeadReadAsync(StorageProvider provider, Func<IInterceptor[], IDocumentRepository> observed)
    {
        var actor = new CellBridgeActor(new("head:owner", "head-owner", "Head owner"), CanCreate: true);
        var head = (await new CellBridgeDocumentService(provider).CreateAsync("/single-head.docx", MinimalDocx.Create("head reads"), actor))!;
        var commands = new Commands(); var transactions = new Transactions();
        var repository = observed([commands, transactions]);
        await AssertHeadAsync(new(head.ResourceId), head);
        await AssertHeadAsync(new(Guid.NewGuid()), null);

        // Commit a complete head change after dispatch, before the single SELECT.
        // The result must contain the published version, grants, scalars and counters.
        var before = head;
        var update = new BeforeHeadQuery(async () =>
        {
            var admission = new DocumentActorAdmission(actor.Identity, before.ResourceId, before.LifecycleGeneration,
                before.Security.AuthorizationPolicy, before.Security.AccessFor(actor.Identity.Subject))
                { AuthorityGeneration = before.AuthorityGeneration, StateVersion = before.StateVersion };
            var changed = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(before.ResourceId,
                admission, before.Security with { Grants = before.Security.Grants.Add("head:reader", DocumentAccess.Read) }, before.AuthorityGeneration));
            Assert.True(changed.Changed); head = changed.Current.Head;
        });
        repository = observed([commands, transactions, update]);
        commands.Items.Clear(); transactions.Started = 0; transactions.Committed = 0;
        var selected = await repository.FindAsync(new DocumentIdentity.Resource(head.ResourceId));
        Assert.True(update.Triggered); Assert.True(head.StateVersion > before.StateVersion);
        Assert.Equal(JsonSerializer.Serialize(head), JsonSerializer.Serialize(selected));
        AssertSingleHeadCommand();
        await AssertHeadAsync(new(head.ResourceId), head);

        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.FindAsync(new DocumentIdentity.Resource(head.ResourceId), cancellation.Token).AsTask())).CancellationToken);
        await AssertHeadAsync(new(head.ResourceId), head);
        var retired = await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(head.ResourceId,
            head.LifecycleGeneration, head.StateVersion));
        Assert.True(retired.Applied); Assert.True(retired.Current!.IsDeleted);
        await AssertHeadAsync(new(head.ResourceId), null);
        await AssertHeadAsync(new(head.ResourceId, IncludeDeleted: true), retired.Current);

        async Task AssertHeadAsync(DocumentIdentity.Resource identity, DocumentHead? expected)
        {
            commands.Items.Clear(); transactions.Started = 0; transactions.Committed = 0;
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(await repository.FindAsync(identity)));
            AssertSingleHeadCommand();
        }
        void AssertSingleHeadCommand()
        {
            var command = Assert.Single(commands.Items);
            Assert.False(command.Transaction); Assert.Equal(42, command.Timeout);
            Assert.StartsWith("SELECT", command.Sql.TrimStart(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("repository_heads", command.Sql, StringComparison.Ordinal);
            Assert.Equal(0, transactions.Started); Assert.Equal(0, transactions.Committed);
        }
    }

    public static async Task RunAsync(StorageProvider provider, Func<IInterceptor[], IDocumentRepository> observed)
    {
        var actor = new CellBridgeActor(new("immutable:owner", "immutable-owner", "Immutable owner"), CanCreate: true);
        var head = (await new CellBridgeDocumentService(provider).CreateAsync("/immutable.docx", MinimalDocx.Create("point reads"), actor))!;
        var snapshot = (await provider.Documents.ExportDocumentAsync(head.ResourceId))!;
        var scope = new IndexedGraphScope(head.ResourceId, head.LifecycleGeneration, 0);
        var elements = snapshot.Elements.Where(e => e.Scope == scope).Select(e => e.Element).ToArray();
        var revision = snapshot.GraphRevisions.First(r => r.Scope == scope);
        var claims = snapshot.MappingClaims.Where(c => c.Scope == scope).ToArray();
        Assert.NotEmpty(elements); Assert.NotEmpty(claims);
        var requestedIds = elements.Select(e => e.Id).Concat(Enumerable.Range(0, 300).Select(_ => new ExtendedId(1, Guid.NewGuid()))).ToArray();
        var requestedSerials = claims.Select(c => c.Serial).Concat(Enumerable.Range(0, 300).Select(index => new SerialId(Guid.Empty, (ulong)(10000 + index)))).ToArray();
        var commands = new Commands(); var transactions = new Transactions();
        var repository = observed([commands, transactions]);
        var emptyRepository = observed([new RejectConnections()]);
        Assert.Empty(await emptyRepository.ReadElementsAsync(scope, []));
        Assert.Empty(await emptyRepository.ReadMappingClaimsAsync(scope, []));
        Assert.Empty(await emptyRepository.ReadVisibleElementIdsAsync(scope, 0, []));
        using (var emptyCancellation = new CancellationTokenSource())
        {
            emptyCancellation.Cancel();
            var token = emptyCancellation.Token;
            foreach (var read in new Func<Task>[]
            {
                async () => _ = await emptyRepository.ReadElementsAsync(scope, [], token),
                async () => _ = await emptyRepository.ReadMappingClaimsAsync(scope, [], token),
                async () => _ = await emptyRepository.ReadVisibleElementIdsAsync(scope, 0, [], token),
            })
                Assert.Equal(token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(read)).CancellationToken);
        }
        var reads = new Func<Task>[]
        {
            async () => Assert.Equal(Canonical(elements), Canonical(await repository.ReadElementsAsync(scope, requestedIds))),
            async () => Assert.Equal(JsonSerializer.Serialize(revision), JsonSerializer.Serialize(await repository.ReadRevisionAsync(scope, revision.ElementId))),
            async () => Assert.Equal(Canonical(claims), Canonical(await repository.ReadMappingClaimsAsync(scope, requestedSerials))),
        };
        foreach (var read in reads) await AssertNoTransactionAsync(read);
        Assert.True((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(head.ResourceId,
            head.LifecycleGeneration, head.StateVersion))).Applied);
        Assert.Null(await provider.Documents.FindAsync(new DocumentIdentity.Resource(head.ResourceId)));
        // Immutable scoped records survive a mutable lifecycle transition.
        foreach (var read in reads) await AssertNoTransactionAsync(read);
        var wrongScope = scope with { LifecycleGeneration = scope.LifecycleGeneration + 1 };
        Assert.Empty(await repository.ReadElementsAsync(wrongScope, requestedIds));
        Assert.Null(await repository.ReadRevisionAsync(wrongScope, revision.ElementId));
        Assert.Empty(await repository.ReadMappingClaimsAsync(wrongScope, requestedSerials));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var cancellations = new Func<Task>[]
        {
            async () => _ = await repository.ReadElementsAsync(scope, requestedIds, cancelled.Token),
            async () => _ = await repository.ReadRevisionAsync(scope, revision.ElementId, cancelled.Token),
            async () => _ = await repository.ReadMappingClaimsAsync(scope, requestedSerials, cancelled.Token),
        };
        foreach (var read in cancellations)
            Assert.Equal(cancelled.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(read)).CancellationToken);

        async Task AssertNoTransactionAsync(Func<Task> read)
        {
            commands.Items.Clear(); transactions.Started = 0; transactions.Committed = 0;
            await read();
            Assert.NotEmpty(commands.Items); Assert.Equal(0, transactions.Started); Assert.Equal(0, transactions.Committed);
            Assert.All(commands.Items, command =>
            {
                Assert.False(command.Transaction);
                Assert.Equal(42, command.Timeout);
                Assert.StartsWith("SELECT", command.Sql.TrimStart(), StringComparison.OrdinalIgnoreCase);
            });
        }
    }

    private static string[] Canonical<T>(IEnumerable<T> values) => values.Select(value => JsonSerializer.Serialize(value)).Order(StringComparer.Ordinal).ToArray();
    private sealed class Commands : DbCommandInterceptor
    {
        public List<(string Sql, int Timeout, bool Transaction)> Items { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { Items.Add((command.CommandText, command.CommandTimeout, command.Transaction is not null)); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken ct = default)
        { Items.Add((command.CommandText, command.CommandTimeout, command.Transaction is not null)); return ValueTask.FromResult(result); }
    }
    private sealed class Transactions : DbTransactionInterceptor
    {
        public int Started;
        public int Committed;
        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData,
            DbTransaction result, CancellationToken ct = default)
        { Started++; return ValueTask.FromResult(result); }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        { Committed++; return Task.CompletedTask; }
    }
    private sealed class RejectConnections : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result) => throw new InvalidOperationException("An empty indexed lookup attempted database I/O.");
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken ct = default) => throw new InvalidOperationException("An empty indexed lookup attempted database I/O.");
    }
    private sealed class BeforeHeadQuery(Func<Task> action) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (!Triggered && command.CommandText.Contains("repository_heads", StringComparison.Ordinal))
            { Triggered = true; await action(); }
            return result;
        }
    }
}
