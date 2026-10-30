using System.Data.Common;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.Tests;

public static class CoherentPartitionMappingScenario
{
    public static async Task RunAsync(StorageProvider provider, Func<IInterceptor[], IDocumentRepository> observed)
    {
        var actor = new CellBridgeActor(new("mapping-snapshot:owner", "mapping-owner", "Mapping owner"), CanCreate: true);
        var service = new CellBridgeDocumentService(provider);
        var originalBytes = MinimalDocx.Create("mapping snapshot before");
        var initialHead = await service.CreateAsync("/mapping-snapshot-" + Guid.NewGuid().ToString("N") + ".docx", originalBytes, actor)
            ?? throw new InvalidOperationException("The mapping snapshot document was not created.");
        var acquiredLock = await new SharedDocumentLocks(service).ApplyAsync(initialHead.ResourceId,
            HostLockOperation.Acquire, "mapping-snapshot-host-token", actor);
        Assert.True(acquiredLock.Success, "The fixture must acquire the document's host lock before its whole-file save.");
        var hostLockToken = acquiredLock.WriteToken?.LockToken
            ?? throw new InvalidOperationException("The acquired host lock did not return its write token.");
        var request = new PartitionReadRequest(0) { IncludeCatalog = true, IncludeMappings = true };
        var oldRead = await provider.Documents.ReadPartitionAsync(initialHead.ResourceId, request)
            ?? throw new InvalidOperationException("The original partition could not be read.");
        Assert.NotEmpty(oldRead.Elements);
        Assert.NotEmpty(oldRead.Mappings);
        Assert.Equal(initialHead.ResourceId, oldRead.Current.ResourceId);
        Assert.Equal(initialHead.LifecycleGeneration, oldRead.Current.LifecycleGeneration);

        ulong acceptedRevisionNumber = 0;
        var commands = new Commands(async () =>
        {
            using var replacement = new MemoryStream(MinimalDocx.Create("mapping snapshot after"));
            var saved = await service.SaveWholeFileAsync(initialHead.ResourceId, replacement, "mapping-snapshot-update", actor, hostLockToken);
            Assert.False(saved.IsReplay);
            acceptedRevisionNumber = saved.Revision.RevisionNumber;
        });
        var transactions = new Transactions();
        var repository = observed([commands, transactions]);
        var selected = await repository.ReadPartitionAsync(initialHead.ResourceId, request)
            ?? throw new InvalidOperationException("The observed partition could not be read.");

        Assert.True(commands.Injected, "The concurrent save must run after the catalogue reader and before mapping selection.");
        Assert.Equal(oldRead.Current, selected.Current);
        Assert.Equal(oldRead.Partition, selected.Partition);
        Assert.Equal(Canonical(oldRead.Elements), Canonical(selected.Elements));
        Assert.Equal(Canonical(oldRead.Mappings), Canonical(selected.Mappings));
        Assert.Equal(1, transactions.Started);
        Assert.Equal(1, transactions.Committed);
        AssertNoPartitionSequenceOnlySelect(commands.Items);

        var current = await provider.Documents.ReadPartitionAsync(initialHead.ResourceId, request)
            ?? throw new InvalidOperationException("The updated partition could not be read.");
        Assert.Equal(acceptedRevisionNumber, current.Current.LatestRevisionNumber);
        Assert.True(current.Current.StateVersion > oldRead.Current.StateVersion);
        Assert.True(current.Partition.PublicationSequence > oldRead.Partition.PublicationSequence);
        Assert.NotEqual(oldRead.Partition.StorageIndex, current.Partition.StorageIndex);
        Assert.NotEqual(string.Join("\n", Canonical(oldRead.Mappings)), string.Join("\n", Canonical(current.Mappings)));
        Assert.NotEqual(string.Join("\n", Canonical(oldRead.Elements)), string.Join("\n", Canonical(current.Elements)));

        static string[] Canonical<T>(IEnumerable<T> values) => values.Select(value => JsonSerializer.Serialize(value)).Order(StringComparer.Ordinal).ToArray();
    }

    private static void AssertNoPartitionSequenceOnlySelect(IReadOnlyCollection<(string Sql, bool InTransaction)> commands)
    {
        var partitionSelects = commands.Where(command => command.Sql.Contains("repository_partitions", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Single(partitionSelects);
        Assert.False(IsSequenceOnlyProjection(partitionSelects[0].Sql));
    }

    private static bool IsSequenceOnlyProjection(string sql)
    {
        var select = sql.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase);
        var from = sql.IndexOf("FROM", select + 6, StringComparison.OrdinalIgnoreCase);
        if (select < 0 || from < 0) return false;
        var projection = sql[(select + 6)..from].Trim();
        projection = System.Text.RegularExpressions.Regex.Replace(projection, @"^TOP\s*\(?\s*\d+\s*\)?\s*", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (projection.Contains(',')) return false;
        var alias = projection.IndexOf(" AS ", StringComparison.OrdinalIgnoreCase);
        if (alias >= 0) projection = projection[..alias].Trim();
        var column = projection[(projection.LastIndexOf('.') + 1)..].Trim().Trim('[', ']', '"', '`');
        return string.Equals(column, "Sequence", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Commands(Func<Task> inject) : DbCommandInterceptor
    {
        public List<(string Sql, bool InTransaction)> Items { get; } = [];
        public bool Injected;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Items.Add((command.CommandText, command.Transaction is not null));
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Injected && command.CommandText.Contains("repository_elements", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("repository_visibility", StringComparison.OrdinalIgnoreCase))
            {
                Injected = true;
                try { await inject(); }
                catch { await result.DisposeAsync(); throw; }
            }
            return result;
        }
    }

    private sealed class Transactions : DbTransactionInterceptor
    {
        public int Started;
        public int Committed;

        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData,
            DbTransaction result, CancellationToken cancellationToken = default)
        {
            Started++;
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Committed++;
            return Task.CompletedTask;
        }
    }
}
