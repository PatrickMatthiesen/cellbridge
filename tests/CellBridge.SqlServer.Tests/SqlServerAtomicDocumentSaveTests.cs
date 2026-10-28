using System.Data.Common;
using System.Security.Cryptography;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.SqlServer;
using CellBridge.TestFixtures;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.SqlServer.Tests;

[Collection("SQL Server")]
public sealed class SqlServerAtomicDocumentSaveTests(SqlServerFixture fixture)
{
    private static readonly CellBridgeActor Actor = new(new("local:atomic", "atomic", "Atomic save test"), CanCreate: true);

    [Fact]
    public async Task StateAndContentUseOneCommitAndQuotaRejectionRollsBackBoth()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var originalOptions = new DbContextOptionsBuilder<SqlServerDocumentDbContext>().UseSqlServer(connectionString).Options;
        await new SqlServerStorageMigrator(originalOptions).MigrateAsync();
        var initial = (await new CellBridgeDocumentService(new(new SqlServerStateStore(originalOptions),
            new SqlServerContentStore(originalOptions))).CreateAsync("/atomic.docx", MinimalDocx.Create(), Actor))!;
        var before = await InventoryAsync(connectionString);
        var commits = new CountCommits();
        var options = new DbContextOptionsBuilder<SqlServerDocumentDbContext>(originalOptions).AddInterceptors(commits).Options;
        var states = new SqlServerStateStore(options);
        var content = new SqlServerContentStore(options);
        var atomic = Assert.IsAssignableFrom<IAtomicDocumentSaveStore>(states);
        Assert.True(atomic.SupportsContentStore(content));
        Assert.False(atomic.SupportsContentStore(new SqlServerContentStore(originalOptions)));
        var bytes = RandomNumberGenerator.GetBytes(65537);
        var handle = Handle(bytes);
        var callbacks = 0;
        Assert.True(await atomic.TransitionWithContentAsync(initial.ResourceId, content,
            new ReadOnlyMemory<byte>[] { bytes, bytes }, (current, now) =>
            {
                callbacks++;
                Assert.Equal(DateTimeKind.Utc, now.Kind);
                return new StateTransition<bool>(AddReceipt(current, handle), true);
            }));

        Assert.Equal(1, callbacks);
        Assert.Equal(1, commits.Commits);
        var saved = (await new SqlServerStateStore(originalOptions).FindByResourceIdAsync(initial.ResourceId))!;
        Assert.Equal(initial.StateVersion + 1, saved.StateVersion);
        Assert.Equal(handle, Assert.Single(saved.Receipts).Response);
        Assert.Equal(bytes, await content.ReadVerifiedAsync(handle));
        var accepted = await InventoryAsync(connectionString);
        Assert.Equal(before with { Objects = before.Objects + 1, Chunks = before.Chunks + 2,
            States = before.States + 1, StoredBytes = before.StoredBytes + bytes.LongLength + DocumentStateJson.GetSerializedSize(saved) }, accepted);

        var rejectedBytes = RandomNumberGenerator.GetBytes(17 * 65536);
        await using (var context = new SqlServerDocumentDbContext(originalOptions))
            await context.Database.ExecuteSqlAsync($"UPDATE cellbridge_documents.usage SET MaxStoredBytes={accepted.StoredBytes + rejectedBytes.LongLength - 1}");
        var error = await Assert.ThrowsAsync<StorageQuotaExceededException>(() => atomic.TransitionWithContentAsync(
            initial.ResourceId, content, new ReadOnlyMemory<byte>[] { rejectedBytes },
            (current, _) => new StateTransition<bool>(AddReceipt(current, Handle(rejectedBytes)), true)).AsTask());
        Assert.Equal("total stored bytes", error.Budget);
        Assert.Equal(accepted, await InventoryAsync(connectionString));
        Assert.Equal(DocumentStateJson.Serialize(saved), DocumentStateJson.Serialize(
            (await new SqlServerStateStore(originalOptions).FindByResourceIdAsync(initial.ResourceId))!));
        Assert.Equal(saved.StateVersion, (await new SqlServerStateStore(originalOptions)
            .FindReadMetadataByResourceIdAsync(initial.ResourceId))!.StateVersion);
    }

    [Fact]
    public async Task ExistingCommonContentDoesNotSerializeIndependentAtomicSaves()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<SqlServerDocumentDbContext>().UseSqlServer(connectionString).Options;
        await new SqlServerStorageMigrator(options).MigrateAsync();
        var states = new SqlServerStateStore(options);
        var content = new SqlServerContentStore(options);
        var service = new CellBridgeDocumentService(new(states, content));
        var initial = (await service.CreateAsync("/atomic-first.docx", MinimalDocx.Create(), Actor))!;
        var other = (await service.CreateAsync("/atomic-other.docx", MinimalDocx.Create(), Actor))!;
        var common = RandomNumberGenerator.GetBytes(4096);
        await content.WriteAsync(new MemoryStream(common, writable: false));
        var firstBytes = RandomNumberGenerator.GetBytes(65537);
        var secondBytes = RandomNumberGenerator.GetBytes(65538);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = states.TransitionWithContentAsync(initial.ResourceId, content,
            new ReadOnlyMemory<byte>[] { common, firstBytes }, (current, _) =>
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(15)), "The first save was not released.");
                return new StateTransition<bool>(AddReceipt(current, Handle(firstBytes)), true);
            }).AsTask();
        Task<bool>? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = states.TransitionWithContentAsync(other.ResourceId, content,
                new ReadOnlyMemory<byte>[] { common, secondBytes },
                (current, _) => new StateTransition<bool>(AddReceipt(current, Handle(secondBytes)), true)).AsTask();
            Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(first.IsCompleted);
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(firstBytes, await content.ReadVerifiedAsync(Handle(firstBytes)));
        Assert.Equal(secondBytes, await content.ReadVerifiedAsync(Handle(secondBytes)));
    }

    [Fact]
    public async Task LedgerArithmeticOverflowKeepsTheCheckedExceptionAndRollsBack()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<SqlServerDocumentDbContext>().UseSqlServer(connectionString).Options;
        await new SqlServerStorageMigrator(options).MigrateAsync();
        var states = new SqlServerStateStore(options);
        var content = new SqlServerContentStore(options);
        var initial = (await new CellBridgeDocumentService(new(states, content))
            .CreateAsync("/atomic.docx", MinimalDocx.Create(), Actor))!;
        await using (var context = new SqlServerDocumentDbContext(options))
            await context.Database.ExecuteSqlAsync($"UPDATE cellbridge_documents.usage SET StoredBytes={long.MaxValue}, MaxStoredBytes={long.MaxValue}");
        var before = await InventoryAsync(connectionString);
        var bytes = RandomNumberGenerator.GetBytes(65537);
        await Assert.ThrowsAsync<OverflowException>(() => states.TransitionWithContentAsync(initial.ResourceId, content,
            new ReadOnlyMemory<byte>[] { bytes },
            (current, _) => new StateTransition<bool>(AddReceipt(current, Handle(bytes)), true)).AsTask());

        Assert.Equal(before, await InventoryAsync(connectionString));
        Assert.Equal(DocumentStateJson.Serialize(initial), DocumentStateJson.Serialize(
            (await states.FindByResourceIdAsync(initial.ResourceId))!));
    }

    [Fact]
    public async Task CancellationDuringChunkStagingLeavesStateAndContentAccountingUnchanged()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var originalOptions = new DbContextOptionsBuilder<SqlServerDocumentDbContext>().UseSqlServer(connectionString).Options;
        await new SqlServerStorageMigrator(originalOptions).MigrateAsync();
        var initial = (await new CellBridgeDocumentService(new(new SqlServerStateStore(originalOptions),
            new SqlServerContentStore(originalOptions))).CreateAsync("/atomic.docx", MinimalDocx.Create(), Actor))!;
        var before = await InventoryAsync(connectionString);
        using var cancellation = new CancellationTokenSource();
        var options = new DbContextOptionsBuilder<SqlServerDocumentDbContext>(originalOptions)
            .AddInterceptors(new CancelChunkBatch(cancellation)).Options;
        var states = new SqlServerStateStore(options);
        var callbacks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => states.TransitionWithContentAsync(initial.ResourceId,
            new SqlServerContentStore(options), new ReadOnlyMemory<byte>[] { new byte[17 * 65536] }, (current, _) =>
            {
                callbacks++;
                return new StateTransition<bool>(current, true);
            }, cancellation.Token).AsTask());

        Assert.Equal(0, callbacks);
        Assert.Equal(before, await InventoryAsync(connectionString));
        Assert.Equal(DocumentStateJson.Serialize(initial), DocumentStateJson.Serialize(
            (await new SqlServerStateStore(originalOptions).FindByResourceIdAsync(initial.ResourceId))!));
    }

    private static DocumentState AddReceipt(DocumentState state, ContentHandle handle) => state with
    {
        Receipts = state.Receipts.Add(new("atomic-" + state.Receipts.Length, handle.Sha256,
            state.ContentVersion, handle, Actor.Identity.Subject)),
    };

    private static ContentHandle Handle(byte[] bytes)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new(digest, bytes.LongLength, digest);
    }

    private sealed record Inventory(long Objects, long Chunks, long States, long StoredBytes, long Documents);

    private static async Task<Inventory> InventoryAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT (SELECT count_big(*) FROM cellbridge_documents.objects),
                (SELECT count_big(*) FROM cellbridge_documents.chunks),
                (SELECT count_big(*) FROM cellbridge_documents.snapshots),
                StoredBytes - (SELECT SUM(AvailableBytes) FROM cellbridge_documents.usage_buckets), DocumentCount
            FROM cellbridge_documents.usage
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
    }

    private sealed class CountCommits : DbTransactionInterceptor
    {
        public int Commits { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        { Commits++; return Task.CompletedTask; }
    }

    private sealed class CancelChunkBatch(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        { cancellation.Cancel(); return ValueTask.FromResult(result); }
    }
}
