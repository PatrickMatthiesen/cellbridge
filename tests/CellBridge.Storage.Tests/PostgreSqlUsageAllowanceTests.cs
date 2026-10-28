using System.Collections.Immutable;
using System.Data.Common;
using System.Reflection;
using System.Security.Cryptography;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class PostgreSqlUsageAllowanceTests
{
    [Fact]
    public void DocumentsSharingAnIdPrefixDistributeAcrossBuckets()
    {
        var buckets = Enumerable.Range(0, 64).Select(index =>
            Bucket(new Guid($"019a28c0-0000-7000-8000-{index:x12}"))).Distinct();
        Assert.True(buckets.Count() >= 32);
    }

    [PostgreSqlFact]
    public Task ConcurrentSlowSavesInOneBucketPreserveUnusedAllowanceFromTheFirstRefill() => WithDatabase(async source =>
    {
        var initial = await SeedAsync(source);
        var plainStates = new PostgreSqlStateStore(source);
        Guid otherId;
        do { otherId = Guid.NewGuid(); }
        while (Bucket(otherId) != Bucket(initial.ResourceId));
        var other = initial with
        {
            ResourceId = otherId,
            Path = "/allowance-same-bucket.docx",
            PathKey = StorageIds.PathKey("/allowance-same-bucket.docx"),
            Revisions = initial.Revisions.Select(revision => revision with { ResourceId = otherId }).ToImmutableArray(),
        };
        Assert.True(await plainStates.TryCreateAsync(other));
        Assert.Equal(Bucket(initial.ResourceId), Bucket(otherId));
        var actual = (await InventoryAsync(source)).Actual;
        await new PostgreSqlStorageMigrator(source,
            new StorageLimits { MaxStoredBytes = actual + 16L * 1024 * 1024 }).MigrateAsync();
        var before = await InventoryAsync(source);
        Assert.Equal(0, before.Available);
        var barrier = new ConcurrentGlobalQuotaLocks();
        var options = new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source)
            .AddInterceptors(barrier).Options;
        var states = new PostgreSqlStateStore(options);
        var content = new PostgreSqlContentStore(options);
        var firstBytes = RandomNumberGenerator.GetBytes(65537);
        var secondBytes = RandomNumberGenerator.GetBytes(65538);

        var accepted = await Task.WhenAll(
            SaveAsync(states, content, initial.ResourceId, firstBytes),
            SaveAsync(states, content, otherId, secondBytes)).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.All(accepted, value => Assert.True(value));
        Assert.Equal(2, barrier.Arrivals);
        var after = await InventoryAsync(source);
        Assert.Equal(before.Objects + 2, after.Objects);
        Assert.Equal(before.States + 2, after.States);
        Assert.Equal(after.Actual, after.Charged - after.Available);
        Assert.Equal(4L * 1024 * 1024, after.Available);
        Assert.Equal(firstBytes, await content.ReadVerifiedAsync(Handle(firstBytes)));
        Assert.Equal(secondBytes, await content.ReadVerifiedAsync(Handle(secondBytes)));
    });

    [PostgreSqlFact]
    public Task FundedAtomicSaveCommitsWhileAnotherTransactionLocksTheGlobalLedger() => WithDatabase(async source =>
    {
        var initial = await SeedAsync(source);
        var states = new PostgreSqlStateStore(source);
        var content = new PostgreSqlContentStore(source);
        await SaveAsync(states, content, initial.ResourceId, RandomNumberGenerator.GetBytes(1024));
        var before = await InventoryAsync(source);
        Assert.True(before.Available > 0);
        var bytes = RandomNumberGenerator.GetBytes(65537);

        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var locked = new NpgsqlCommand("SELECT stored_bytes FROM cellbridge_usage FOR UPDATE", connection, transaction))
            await locked.ExecuteScalarAsync();
        var save = SaveAsync(states, content, initial.ResourceId, bytes);
        try
        {
            Assert.True(await save.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await transaction.RollbackAsync();
            await save.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var after = await InventoryAsync(source);
        Assert.Equal(before.Charged, after.Charged);
        Assert.True(after.Available < before.Available);
        Assert.Equal(after.Actual, after.Charged - after.Available);
        Assert.Equal(bytes, await content.ReadVerifiedAsync(Handle(bytes)));
        Assert.Equal(2, (await states.FindByResourceIdAsync(initial.ResourceId))!.Receipts.Length);
    });

    [PostgreSqlFact]
    public Task ExactQuotaReclaimsOtherBucketsAndChargesDuplicateContentOnce() => WithDatabase(async source =>
    {
        var initial = await SeedAsync(source);
        var states = new PostgreSqlStateStore(source);
        var content = new PostgreSqlContentStore(source);
        DocumentState other;
        var index = 0;
        do
        {
            other = (await new CellBridgeDocumentService(new(states, content))
                .CreateAsync($"/allowance-other-{index++}.docx", MinimalDocx.Create(), TestActor.Value))!;
        } while (Bucket(other.ResourceId) == Bucket(initial.ResourceId));
        var firstBytes = RandomNumberGenerator.GetBytes(65537);
        var secondBytes = RandomNumberGenerator.GetBytes(65538);
        var firstSnapshot = AddReceipt(other, Handle(firstBytes)) with { StateVersion = other.StateVersion + 1 };
        var secondSnapshot = AddReceipt(initial, Handle(secondBytes)) with { StateVersion = initial.StateVersion + 1 };
        var before = await InventoryAsync(source);
        var exactLimit = before.Actual + firstBytes.LongLength + secondBytes.LongLength +
            DocumentStateJson.GetSerializedSize(firstSnapshot) + DocumentStateJson.GetSerializedSize(secondSnapshot);
        await new PostgreSqlStorageMigrator(source, new StorageLimits { MaxStoredBytes = exactLimit }).MigrateAsync();

        Assert.True(await SaveAsync(states, content, other.ResourceId, firstBytes));
        var funded = await InventoryAsync(source);
        Assert.Equal(exactLimit, funded.Charged);
        Assert.True(await AvailableAsync(source, Bucket(other.ResourceId)) > 0);
        Assert.Equal(0, await AvailableAsync(source, Bucket(initial.ResourceId)));

        Assert.True(await states.TransitionWithContentAsync(initial.ResourceId, content,
            new ReadOnlyMemory<byte>[] { secondBytes, secondBytes },
            (current, _) => new StateTransition<bool>(AddReceipt(current, Handle(secondBytes)), true)));

        var after = await InventoryAsync(source);
        Assert.Equal(exactLimit, after.Actual);
        Assert.Equal(after.Actual, after.Charged - after.Available);
        Assert.Equal(0, after.Available);
        Assert.Equal(before.Objects + 2, after.Objects);
        Assert.Equal(before.States + 2, after.States);
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() =>
            SaveAsync(states, content, initial.ResourceId, RandomNumberGenerator.GetBytes(1)));
        Assert.Equal(after, await InventoryAsync(source));
        Assert.Equal(firstBytes, await content.ReadVerifiedAsync(Handle(firstBytes)));
        Assert.Equal(secondBytes, await content.ReadVerifiedAsync(Handle(secondBytes)));
    });

    [PostgreSqlFact]
    public Task InjectedFailureBeforeCommitRestoresConsumedAllowanceAndStagedContent() => WithDatabase(async source =>
    {
        var initial = await SeedAsync(source);
        var plainStates = new PostgreSqlStateStore(source);
        var plainContent = new PostgreSqlContentStore(source);
        await SaveAsync(plainStates, plainContent, initial.ResourceId, RandomNumberGenerator.GetBytes(1024));
        var current = (await plainStates.FindByResourceIdAsync(initial.ResourceId))!;
        var before = await InventoryAsync(source);
        var available = await AvailableAsync(source, Bucket(initial.ResourceId));
        using var cancellation = new CancellationTokenSource();
        var options = new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source)
            .AddInterceptors(new CancelBeforeCommit(cancellation)).Options;
        var states = new PostgreSqlStateStore(options);
        var content = new PostgreSqlContentStore(options);
        var bytes = RandomNumberGenerator.GetBytes(65537);
        var callbacks = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => states.TransitionWithContentAsync(
            initial.ResourceId, content, new ReadOnlyMemory<byte>[] { bytes }, (state, _) =>
            {
                callbacks++;
                return new StateTransition<bool>(AddReceipt(state, Handle(bytes)), true);
            }, cancellation.Token).AsTask());

        Assert.Equal(1, callbacks);
        Assert.Equal(before, await InventoryAsync(source));
        Assert.Equal(available, await AvailableAsync(source, Bucket(initial.ResourceId)));
        Assert.Equal(DocumentStateJson.Serialize(current),
            DocumentStateJson.Serialize((await plainStates.FindByResourceIdAsync(initial.ResourceId))!));
    });

    [PostgreSqlFact]
    public Task RepeatedInitializationFoldsAllowancesAndAppliesLimitsWithoutChangingDocuments() => WithDatabase(async source =>
    {
        var initial = await SeedAsync(source);
        var states = new PostgreSqlStateStore(source);
        var content = new PostgreSqlContentStore(source);
        var firstBytes = RandomNumberGenerator.GetBytes(65537);
        await SaveAsync(states, content, initial.ResourceId, firstBytes);
        var before = await InventoryAsync(source);
        Assert.True(before.Available > 0);
        var current = (await states.FindByResourceIdAsync(initial.ResourceId))!;

        var rejected = new PostgreSqlStorageMigrator(source, new StorageLimits { MaxStoredBytes = before.Actual - 1 });
        var error = await Assert.ThrowsAsync<StorageQuotaExceededException>(() => rejected.MigrateAsync());
        Assert.Equal("total stored bytes", error.Budget);
        Assert.Equal(before.Actual, error.Used);
        Assert.Equal(before, await InventoryAsync(source));

        var exact = new PostgreSqlStorageMigrator(source, new StorageLimits { MaxStoredBytes = before.Actual });
        await exact.MigrateAsync();
        await exact.MigrateAsync();
        var after = await InventoryAsync(source);
        Assert.Equal(before with { Charged = before.Actual, Available = 0, Limit = before.Actual }, after);
        Assert.Equal(DocumentStateJson.Serialize(current),
            DocumentStateJson.Serialize((await states.FindByResourceIdAsync(initial.ResourceId))!));
        Assert.Equal(firstBytes, await content.ReadVerifiedAsync(Handle(firstBytes)));
        Assert.Equal(64, await BucketCountAsync(source));
    });

    private static Task<bool> SaveAsync(PostgreSqlStateStore states, PostgreSqlContentStore content, Guid id, byte[] bytes) =>
        states.TransitionWithContentAsync(id, content, new ReadOnlyMemory<byte>[] { bytes },
            (current, _) => new StateTransition<bool>(AddReceipt(current, Handle(bytes)), true)).AsTask();

    private static DocumentState AddReceipt(DocumentState current, ContentHandle handle) => current with
    {
        Receipts = current.Receipts.Add(new("allowance-" + current.Receipts.Length, handle.Sha256,
            current.ContentVersion, handle, TestActor.Value.Identity.Subject)),
    };

    private static ContentHandle Handle(byte[] bytes)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new(digest, bytes.LongLength, digest);
    }

    private static readonly Func<Guid, int> HashBucket = typeof(PostgreSqlStateStore).Assembly
        .GetType("CellBridge.Storage.Shared.UsageAllowances")!
        .GetMethod("Bucket", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<Func<Guid, int>>();

    private static int Bucket(Guid resourceId) => HashBucket(resourceId);

    private static async Task<long> AvailableAsync(NpgsqlDataSource source, int bucket)
    {
        await using var command = source.CreateCommand("SELECT available_bytes FROM cellbridge_usage_buckets WHERE bucket_id=$1");
        command.Parameters.AddWithValue(bucket);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> BucketCountAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("SELECT count(*) FROM cellbridge_usage_buckets");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed record Inventory(long Charged, long Available, long Actual, long Limit, long Objects, long States);

    private static async Task<Inventory> InventoryAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("""
            SELECT stored_bytes,
                COALESCE((SELECT SUM(available_bytes) FROM cellbridge_usage_buckets),0)::bigint,
                (COALESCE((SELECT SUM(length) FROM cellbridge_objects),0) +
                 COALESCE((SELECT SUM(octet_length(state_json)) FROM cellbridge_states),0) +
                 COALESCE((SELECT SUM(octet_length(receipt_json)) FROM cellbridge_recovery_receipts),0))::bigint,
                max_stored_bytes, (SELECT count(*) FROM cellbridge_objects), (SELECT count(*) FROM cellbridge_states)
            FROM cellbridge_usage
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5));
    }

    private static async Task<DocumentState> SeedAsync(NpgsqlDataSource source)
    {
        await new PostgreSqlStorageMigrator(source).MigrateAsync();
        return (await new CellBridgeDocumentService(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)))
            .CreateAsync("/allowance.docx", MinimalDocx.Create(), TestActor.Value))!;
    }

    private static async Task WithDatabase(Func<NpgsqlDataSource, Task> test)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!;
        await using var admin = NpgsqlDataSource.Create(connectionString);
        var database = "cellbridge_allowance_" + Guid.NewGuid().ToString("N");
        await using (var create = admin.CreateCommand($"CREATE DATABASE {database}")) await create.ExecuteNonQueryAsync();
        try
        {
            await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connectionString)
                { Database = database }.ConnectionString);
            await test(source);
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP DATABASE {database} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class ConcurrentGlobalQuotaLocks : DbCommandInterceptor
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        public int Arrivals => Volatile.Read(ref arrivals);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM cellbridge_usage WHERE singleton=true FOR UPDATE", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref arrivals) == 2) ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    private sealed class CancelBeforeCommit(CancellationTokenSource cancellation) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            // Production deliberately shields COMMIT from caller cancellation.
            // Inject a failure before the provider sends it to verify rollback
            // after a successful allowance debit.
            cancellation.Token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }
}
