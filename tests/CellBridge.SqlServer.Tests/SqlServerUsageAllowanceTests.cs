using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.Shared;
using CellBridge.Storage.SqlServer;
using CellBridge.TestFixtures;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CellBridge.SqlServer.Tests;

[Collection("SQL Server")]
public sealed class SqlServerUsageAllowanceTests(SqlServerFixture fixture)
{
    private static readonly CellBridgeActor Actor = new(new("sql:allowance", "allowance", "Allowance test"), CanCreate: true);

    [Fact]
    public async Task FundedAtomicSaveCommitsWhileAnotherTransactionHoldsTheGlobalUpdateLock()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var options = Options(connectionString);
        var initial = await SeedAsync(options);
        var states = new SqlServerStateStore(options);
        var content = new SqlServerContentStore(options);
        await SaveAsync(states, content, initial.ResourceId, RandomNumberGenerator.GetBytes(1024));
        var before = await InventoryAsync(connectionString);
        Assert.True(before.Available > 0);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction,
            "SELECT StoredBytes FROM cellbridge_documents.usage WITH (UPDLOCK,HOLDLOCK) WHERE Id=1");
        var bytes = RandomNumberGenerator.GetBytes(65537);
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

        var after = await InventoryAsync(connectionString);
        Assert.Equal(before.Charged, after.Charged);
        Assert.True(after.Available < before.Available);
        Assert.Equal(after.Actual, after.Charged - after.Available);
        Assert.Equal(bytes, await content.ReadVerifiedAsync(Handle(bytes)));
    }

    [Fact]
    public async Task FailedClaimReleasesItsBucketBeforeWaitingForTheGlobalFirstPoolLock()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var options = Options(connectionString);
        await new SqlServerStorageMigrator(options, new StorageLimits { MaxStoredBytes = 1024 * 1024 }).MigrateAsync();
        Assert.Equal(0, await AvailableAsync(connectionString, 7));
        await using var blocker = new SqlConnection(connectionString);
        await blocker.OpenAsync();
        await using (var escalation = new SqlCommand("""
            SELECT lock_escalation_desc FROM sys.tables
            WHERE object_id=OBJECT_ID('cellbridge_documents.usage_buckets')
            """, blocker))
            Assert.Equal("DISABLE", (string)(await escalation.ExecuteScalarAsync())!);
        var blockerSession = blocker.ServerProcessId;
        await using var held = (SqlTransaction)await blocker.BeginTransactionAsync();
        await ExecuteAsync(blocker, held,
            "SELECT StoredBytes FROM cellbridge_documents.usage WITH (UPDLOCK,HOLDLOCK) WHERE Id=1");
        await ExecuteAsync(blocker, held, "SAVE TRANSACTION bucket_blocker");
        await ExecuteAsync(blocker, held, """
            SELECT AvailableBytes FROM cellbridge_documents.usage_buckets WITH (ROWLOCK,XLOCK,HOLDLOCK)
            WHERE BucketId=7
            """);

        await using var claimant = new SqlServerDocumentDbContext(options);
        await using var transaction = await claimant.Database.BeginTransactionAsync();
        await SqlServerDocumentDatabase.ConfigureTransactionAsync(claimant, CancellationToken.None);
        var claimantSession = await claimant.Database.SqlQueryRaw<int>("SELECT CONVERT(int,@@SPID) AS Value").SingleAsync();
        var database = new SqlServerDocumentDatabase(options);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var claim = database.TryConsumeUsageAllowanceAsync(claimant, 7, 1, deadline.Token);
        Task<(UsageRow Usage, IReadOnlyList<UsageAllowanceRow> Allowances)>? pool = null;
        try
        {
            // Force the UPDATE to request this key's lock before it can discover
            // insufficient credit. A test with an uncontended empty bucket would
            // not establish whether a bucket lock was acquired at all.
            var wait = await WaitBlockedAsync(connectionString, claimantSession, blockerSession);
            Assert.StartsWith("KEY:", wait.Resource);
            Assert.StartsWith("LCK_M_", wait.Type);
            await ExecuteAsync(blocker, held, "ROLLBACK TRANSACTION bucket_blocker");
            Assert.False(await claim.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, await BlockingBucketLocksAsync(connectionString, claimantSession));

            pool = database.LockUsageAllowancePoolAsync(claimant, deadline.Token);
            await WaitBlockedAsync(connectionString, claimantSession, blockerSession);
            // The claimant is now waiting for global. If rollback retained its
            // bucket lock, this global owner could not acquire that same bucket.
            await ExecuteAsync(blocker, held, """
                SELECT AvailableBytes FROM cellbridge_documents.usage_buckets WITH (ROWLOCK,UPDLOCK,HOLDLOCK)
                WHERE BucketId=7
                """).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await held.RollbackAsync();
            await claim.WaitAsync(TimeSpan.FromSeconds(10));
            if (pool is not null) await pool.WaitAsync(TimeSpan.FromSeconds(10));
            await transaction.RollbackAsync();
        }
        Assert.NotNull(pool);
        Assert.Equal(64, (await pool).Allowances.Count);
    }

    [Fact]
    public async Task StrictQuotaReclaimsOtherBucketsAndRejectsTheNextSaveWithoutChangingCredits()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var options = Options(connectionString);
        var initial = await SeedAsync(options);
        var states = new SqlServerStateStore(options);
        var content = new SqlServerContentStore(options);
        DocumentState other;
        var index = 0;
        do
        {
            other = (await new CellBridgeDocumentService(new(states, content))
                .CreateAsync($"/allowance-other-{index++}.docx", MinimalDocx.Create(), Actor))!;
        } while (UsageAllowances.Bucket(other.ResourceId) == UsageAllowances.Bucket(initial.ResourceId));
        var firstBytes = RandomNumberGenerator.GetBytes(65537);
        var secondBytes = RandomNumberGenerator.GetBytes(65538);
        var firstSnapshot = AddReceipt(other, Handle(firstBytes)) with { StateVersion = other.StateVersion + 1 };
        var secondSnapshot = AddReceipt(initial, Handle(secondBytes)) with { StateVersion = initial.StateVersion + 1 };
        var before = await InventoryAsync(connectionString);
        var exactLimit = before.Actual + firstBytes.LongLength + secondBytes.LongLength +
            DocumentStateJson.GetSerializedSize(firstSnapshot) + DocumentStateJson.GetSerializedSize(secondSnapshot);
        await new SqlServerStorageMigrator(options, new StorageLimits { MaxStoredBytes = exactLimit }).MigrateAsync();
        await SaveAsync(states, content, other.ResourceId, firstBytes);
        Assert.True(await AvailableAsync(connectionString, UsageAllowances.Bucket(other.ResourceId)) > 0);
        Assert.Equal(0, await AvailableAsync(connectionString, UsageAllowances.Bucket(initial.ResourceId)));
        var funded = await InventoryAsync(connectionString);
        var rejected = await Assert.ThrowsAsync<StorageQuotaExceededException>(() =>
            SaveAsync(states, content, initial.ResourceId, RandomNumberGenerator.GetBytes(secondBytes.Length + 1)));
        Assert.Equal("total stored bytes", rejected.Budget);
        Assert.Equal(funded, await InventoryAsync(connectionString));
        Assert.True(await AvailableAsync(connectionString, UsageAllowances.Bucket(other.ResourceId)) > 0);

        Assert.True(await states.TransitionWithContentAsync(initial.ResourceId, content,
            new ReadOnlyMemory<byte>[] { secondBytes, secondBytes },
            (current, _) => new StateTransition<bool>(AddReceipt(current, Handle(secondBytes)), true)));
        var full = await InventoryAsync(connectionString);
        Assert.Equal(exactLimit, full.Actual);
        Assert.Equal(full.Actual, full.Charged);
        Assert.Equal(0, full.Available);
        Assert.Equal(before.Objects + 2, full.Objects);
        var error = await Assert.ThrowsAsync<StorageQuotaExceededException>(() =>
            SaveAsync(states, content, initial.ResourceId, RandomNumberGenerator.GetBytes(1)));
        Assert.Equal("total stored bytes", error.Budget);
        Assert.Equal(full, await InventoryAsync(connectionString));
    }

    [Fact]
    public async Task CancellationAfterClaimBeforeProviderCommitRestoresCreditsAndInventory()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var options = Options(connectionString);
        var initial = await SeedAsync(options);
        var plainStates = new SqlServerStateStore(options);
        await SaveAsync(plainStates, new SqlServerContentStore(options), initial.ResourceId, RandomNumberGenerator.GetBytes(1024));
        var current = (await plainStates.FindByResourceIdAsync(initial.ResourceId))!;
        var before = await InventoryAsync(connectionString);
        var bucket = UsageAllowances.Bucket(initial.ResourceId);
        var available = await AvailableAsync(connectionString, bucket);
        using var cancellation = new CancellationTokenSource();
        var cancel = new CancelAfterClaim(cancellation, bucket, available);
        var interrupted = new DbContextOptionsBuilder<SqlServerDocumentDbContext>(options).AddInterceptors(cancel).Options;
        var callbacks = 0;
        var bytes = RandomNumberGenerator.GetBytes(65537);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SqlServerStateStore(interrupted)
            .TransitionWithContentAsync(initial.ResourceId, new SqlServerContentStore(interrupted),
                new ReadOnlyMemory<byte>[] { bytes }, (state, _) =>
                {
                    callbacks++;
                    return new StateTransition<bool>(AddReceipt(state, Handle(bytes)), true);
                }, cancellation.Token).AsTask());

        Assert.True(cancel.ObservedConsumedCredit);
        Assert.Equal(1, callbacks);
        Assert.Equal(before, await InventoryAsync(connectionString));
        Assert.Equal(available, await AvailableAsync(connectionString, bucket));
        Assert.Equal(DocumentStateJson.Serialize(current),
            DocumentStateJson.Serialize((await plainStates.FindByResourceIdAsync(initial.ResourceId))!));
    }

    [Fact]
    public async Task VersionOneMigrationPreservesContentAndRepeatedInitializationFoldsCredits()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var options = Options(connectionString);
        var original = RandomNumberGenerator.GetBytes(128);
        var handle = Handle(original);
        await using (var db = new SqlServerDocumentDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261024194606_InitialDocumentStorage");
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO cellbridge_documents.objects ([Key],Length,Sha256) VALUES ({handle.Key},{handle.Length},{handle.Sha256});
                INSERT INTO cellbridge_documents.chunks ([Key],Ordinal,Bytes) VALUES ({handle.Key},0,{original});
                UPDATE cellbridge_documents.usage SET StoredBytes={handle.Length} WHERE Id=1;
                """);
            Assert.Equal(1, await db.SchemaVersions.Select(row => row.Version).SingleAsync());
        }
        await new SqlServerStorageMigrator(options, new StorageLimits { MaxStoredBytes = 1024 * 1024 }).MigrateAsync();
        await using (var db = new SqlServerDocumentDbContext(options))
        {
            Assert.Equal(2, await db.SchemaVersions.Select(row => row.Version).SingleAsync());
            Assert.Equal(64, await db.UsageBuckets.CountAsync());
            Assert.Equal(0, await db.UsageBuckets.SumAsync(row => row.AvailableBytes));
        }
        var content = new SqlServerContentStore(options);
        Assert.Equal(original, await content.ReadVerifiedAsync(handle));
        var initial = (await new CellBridgeDocumentService(new(new SqlServerStateStore(options), content))
            .CreateAsync("/migrated.docx", MinimalDocx.Create(), Actor))!;
        var states = new SqlServerStateStore(options);
        await SaveAsync(states, content, initial.ResourceId, RandomNumberGenerator.GetBytes(1024));
        var current = (await states.FindByResourceIdAsync(initial.ResourceId))!;
        var before = await InventoryAsync(connectionString);
        Assert.True(before.Available > 0);
        var error = await Assert.ThrowsAsync<StorageQuotaExceededException>(() =>
            new SqlServerStorageMigrator(options, new StorageLimits { MaxStoredBytes = before.Actual - 1 }).MigrateAsync());
        Assert.Equal(before.Actual, error.Used);
        Assert.Equal(before, await InventoryAsync(connectionString));

        var migrator = new SqlServerStorageMigrator(options, new StorageLimits { MaxStoredBytes = before.Actual });
        await migrator.MigrateAsync();
        await migrator.MigrateAsync();
        Assert.Equal(before with { Charged = before.Actual, Available = 0, Limit = before.Actual },
            await InventoryAsync(connectionString));
        Assert.Equal(DocumentStateJson.Serialize(current),
            DocumentStateJson.Serialize((await states.FindByResourceIdAsync(initial.ResourceId))!));
        Assert.Equal(original, await content.ReadVerifiedAsync(handle));
    }

    private static DbContextOptions<SqlServerDocumentDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<SqlServerDocumentDbContext>().UseSqlServer(connectionString,
            sql => sql.MigrationsHistoryTable(SqlServerDocumentDbContext.MigrationsHistoryTable, SqlServerDocumentDbContext.Schema)).Options;

    private static async Task<DocumentState> SeedAsync(DbContextOptions<SqlServerDocumentDbContext> options)
    {
        await new SqlServerStorageMigrator(options).MigrateAsync();
        return (await new CellBridgeDocumentService(new(new SqlServerStateStore(options), new SqlServerContentStore(options)))
            .CreateAsync("/allowance.docx", MinimalDocx.Create(), Actor))!;
    }

    private static Task<bool> SaveAsync(SqlServerStateStore states, SqlServerContentStore content, Guid id, byte[] bytes) =>
        states.TransitionWithContentAsync(id, content, new ReadOnlyMemory<byte>[] { bytes },
            (current, _) => new StateTransition<bool>(AddReceipt(current, Handle(bytes)), true)).AsTask();

    private static DocumentState AddReceipt(DocumentState current, ContentHandle handle) => current with
    {
        Receipts = current.Receipts.Add(new("allowance-" + current.Receipts.Length, handle.Sha256,
            current.ContentVersion, handle, Actor.Identity.Subject)),
    };

    private static ContentHandle Handle(byte[] bytes)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new(hash, bytes.LongLength, hash);
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(string Type, string Resource)> WaitBlockedAsync(string connectionString, int session, int blocker)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            await using var command = new SqlCommand("""
                SELECT wait_type,wait_resource FROM sys.dm_exec_requests
                WHERE session_id=@session AND blocking_session_id=@blocker
                """, connection);
            command.Parameters.AddWithValue("@session", session);
            command.Parameters.AddWithValue("@blocker", blocker);
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync()) return (reader.GetString(0), reader.GetString(1));
            await Task.Delay(25);
        }
        Assert.Fail($"Session {session} did not wait for session {blocker}.");
        throw new InvalidOperationException();
    }

    private static async Task<int> BlockingBucketLocksAsync(string connectionString, int session)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT COUNT(*) FROM sys.dm_tran_locks locks
            WHERE request_session_id=@session AND request_status='GRANT'
                AND resource_database_id=DB_ID()
                AND ((resource_type IN ('KEY','PAGE','RID') AND resource_associated_entity_id IN
                    (SELECT hobt_id FROM sys.partitions WHERE object_id=OBJECT_ID('cellbridge_documents.usage_buckets'))
                    AND request_mode IN ('S','U','X','SIX'))
                    OR (resource_type='OBJECT' AND resource_associated_entity_id=OBJECT_ID('cellbridge_documents.usage_buckets')
                        AND request_mode IN ('S','X','SIX')))
            """, connection);
        command.Parameters.AddWithValue("@session", session);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> AvailableAsync(string connectionString, int bucket)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT AvailableBytes FROM cellbridge_documents.usage_buckets WHERE BucketId=@bucket", connection);
        command.Parameters.AddWithValue("@bucket", bucket);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed record Inventory(long Charged, long Available, long Actual, long Limit, long Objects, long States);

    private static async Task<Inventory> InventoryAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT StoredBytes, (SELECT SUM(AvailableBytes) FROM cellbridge_documents.usage_buckets),
                COALESCE((SELECT SUM(Length) FROM cellbridge_documents.objects),0) +
                COALESCE((SELECT SUM(CONVERT(bigint,DATALENGTH(Data))) FROM cellbridge_documents.snapshots),0) +
                COALESCE((SELECT SUM(CONVERT(bigint,DATALENGTH(Data))) FROM cellbridge_documents.recovery_receipts),0),
                MaxStoredBytes, (SELECT COUNT_BIG(*) FROM cellbridge_documents.objects),
                (SELECT COUNT_BIG(*) FROM cellbridge_documents.snapshots)
            FROM cellbridge_documents.usage WHERE Id=1
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var inventory = new Inventory(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5));
        Assert.Equal(inventory.Actual, inventory.Charged - inventory.Available);
        return inventory;
    }

    private sealed class CancelAfterClaim(CancellationTokenSource cancellation, int bucket, long before) : DbTransactionInterceptor
    {
        public bool ObservedConsumedCredit { get; private set; }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            await using var command = transaction.Connection!.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT AvailableBytes FROM cellbridge_documents.usage_buckets WHERE BucketId=@bucket";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@bucket";
            parameter.Value = bucket;
            command.Parameters.Add(parameter);
            ObservedConsumedCredit = (long)(await command.ExecuteScalarAsync())! < before;
            Assert.True(ObservedConsumedCredit);
            cancellation.Cancel();
            // COMMIT intentionally uses CancellationToken.None. Inject a known
            // cancellation before the provider sees COMMIT, after proving that
            // this transaction consumed durable allowance credit.
            cancellation.Token.ThrowIfCancellationRequested();
            return result;
        }
    }
}
