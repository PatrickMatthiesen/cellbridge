using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class WriteReservationRegressionTests(ListDatabaseFixture database)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PersistenceFailureRollsBackReservationAndIdentity(bool patch, bool consumerOptions)
    {
        var (workspace, list, field, record) = await PrepareAsync();
        var engine = consumerOptions
            ? new PostgreSqlListEngine(new DbContextOptionsBuilder<PostgreSqlListDbContext>().UseNpgsql(database.RuntimeSource!).Options)
            : database.Engine;
        var before = await StateAsync(workspace);
        var function = "prototype_failure_" + Guid.NewGuid().ToString("N");
        var trigger = "prototype_trigger_" + Guid.NewGuid().ToString("N");
        await using (var setup = database.AdminSource!.CreateCommand($"""
            CREATE FUNCTION cellbridge_lists.{function}() RETURNS trigger
            LANGUAGE plpgsql AS $f$ BEGIN RAISE EXCEPTION 'injected persistence failure' USING ERRCODE='23514'; END $f$;
            CREATE TRIGGER {trigger} BEFORE INSERT OR UPDATE ON cellbridge_lists.records
            FOR EACH ROW WHEN (NEW.workspace_id='{workspace}'::uuid) EXECUTE FUNCTION cellbridge_lists.{function}();
            """))
            await setup.ExecuteNonQueryAsync();
        var request = new CreateRecordRequest(Guid.NewGuid(), 1, Values(field, "failed"));
        try
        {
            var error = await Assert.ThrowsAsync<ListException>(async () =>
            {
                if (patch) await engine.UpdateRecordAsync(database.AdminActor, workspace, list, record,
                    new(1, 1, [new(field, JsonValue.Create("failed"))]));
                else await engine.CreateRecordAsync(database.AdminActor, workspace, list, request);
            });
            Assert.Equal(ListFailure.Unavailable, error.Failure);
        }
        finally
        {
            await using var cleanup = database.AdminSource!.CreateCommand($"DROP TRIGGER {trigger} ON cellbridge_lists.records; DROP FUNCTION cellbridge_lists.{function}()");
            await cleanup.ExecuteNonQueryAsync();
        }
        Assert.Equal(before, await StateAsync(workspace));
        Assert.Equal(1, (await engine.GetRecordAsync(database.AdminActor, workspace, list, record)).Revision);
        if (patch)
        {
            var saved = await engine.UpdateRecordAsync(database.AdminActor, workspace, list, record,
                new(1, 1, [new(field, JsonValue.Create("success"))]));
            Assert.Equal(2, saved.Revision);
        }
        else
        {
            var saved = await engine.CreateRecordAsync(database.AdminActor, workspace, list, request);
            Assert.Equal(2, saved.Number);
            Assert.False(saved.Replayed);
            Assert.True((await engine.CreateRecordAsync(database.AdminActor, workspace, list, request)).Replayed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterReservationDuringSaveRollsBackAllWrites(bool patch)
    {
        var (workspace, list, field, record) = await PrepareAsync();
        var before = await StateAsync(workspace);
        var function = "prototype_cancel_" + Guid.NewGuid().ToString("N");
        var trigger = "prototype_trigger_" + Guid.NewGuid().ToString("N");
        await using (var setup = database.AdminSource!.CreateCommand($"""
            CREATE FUNCTION cellbridge_lists.{function}() RETURNS trigger
            LANGUAGE plpgsql AS $f$ BEGIN PERFORM pg_sleep(30); RETURN NEW; END $f$;
            CREATE TRIGGER {trigger} BEFORE INSERT OR UPDATE ON cellbridge_lists.records
            FOR EACH ROW WHEN (NEW.workspace_id='{workspace}'::uuid) EXECUTE FUNCTION cellbridge_lists.{function}();
            """))
            await setup.ExecuteNonQueryAsync();
        var request = new CreateRecordRequest(Guid.NewGuid(), 1, Values(field, "cancel save"));
        var applicationName = "CellBridge.Reservation.Cancel." + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(database.RuntimeConnectionString)
            { ApplicationName = applicationName };
        await using var source = NpgsqlDataSource.Create(settings.ConnectionString);
        var engine = new PostgreSqlListEngine(source);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task write = patch
            ? engine.UpdateRecordAsync(database.AdminActor, workspace, list, record,
                new(1, 1, [new(field, JsonValue.Create("cancel save"))]), cancellation.Token)
            : engine.CreateRecordAsync(database.AdminActor, workspace, list, request, cancellation.Token);
        try
        {
            var reachedSave = false;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                await using var waiting = database.AdminSource!.CreateCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND application_name=$1 AND wait_event='PgSleep')");
                waiting.Parameters.AddWithValue(applicationName);
                if ((bool)(await waiting.ExecuteScalarAsync())!) { reachedSave = true; break; }
                await Task.Delay(20);
            }
            Assert.True(reachedSave, "The mutation never reached the record persistence trigger.");
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await write; } catch { }
            await using var cleanup = database.AdminSource!.CreateCommand($"DROP TRIGGER {trigger} ON cellbridge_lists.records; DROP FUNCTION cellbridge_lists.{function}()");
            await cleanup.ExecuteNonQueryAsync();
        }
        Assert.Equal(before, await StateAsync(workspace));
        Assert.Equal(1, (await database.Engine.GetRecordAsync(database.AdminActor, workspace, list, record)).Revision);
        Assert.Equal(2, (await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list, request)).Number);
    }

    [Theory]
    [InlineData("next_number", false)]
    [InlineData("identity_count", false)]
    [InlineData("stored_bytes", false)]
    [InlineData("audit_count", false)]
    [InlineData("stored_bytes", true)]
    [InlineData("audit_count", true)]
    public async Task CheckedOverflowDoesNotPersistPartialAccounting(string column, bool patch)
    {
        var (workspace, list, field, record) = await PrepareAsync();
        var counter = column is "identity_count" or "next_number";
        await using (var corrupt = database.AdminSource!.CreateCommand($"UPDATE cellbridge_lists.{(counter ? "list_counters" : "workspaces")} SET {column}=9223372036854775807 WHERE workspace_id=$1"))
        {
            corrupt.Parameters.AddWithValue(workspace);
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
        }
        var before = await StateAsync(workspace);
        await Assert.ThrowsAsync<OverflowException>(async () =>
        {
            if (patch) await database.Engine.UpdateRecordAsync(database.AdminActor, workspace, list, record,
                new(1, 1, [new(field, JsonValue.Create("overflow"))]));
            else await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list,
                new(Guid.NewGuid(), 1, Values(field, "overflow")));
        });
        Assert.Equal(before, await StateAsync(workspace));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FractionalReceiptLifetimeAndReturnedDatabaseFieldsMatch(bool consumerOptions)
    {
        var (workspace, list, field, _) = await PrepareAsync();
        var lifetime = TimeSpan.FromSeconds(30.125);
        var limits = new ListLimits { ReceiptLifetime = lifetime };
        var engine = consumerOptions
            ? new PostgreSqlListEngine(new DbContextOptionsBuilder<PostgreSqlListDbContext>().UseNpgsql(database.RuntimeSource!).Options, limits)
            : new PostgreSqlListEngine(database.RuntimeSource!, limits);
        var request = new CreateRecordRequest(Guid.NewGuid(), 1, Values(field, "fractional"));
        var result = await engine.CreateRecordAsync(database.AdminActor, workspace, list, request);
        Assert.Equal((request.Id, 1L, 2L, false), (result.Id, result.Revision, result.Number, result.Replayed));
        var saved = await engine.GetRecordAsync(database.AdminActor, workspace, list, request.Id);
        Assert.Equal("fractional", saved.Values[field.ToString("D")]!.GetValue<string>());
        Assert.Equal(saved.CreatedUtc, saved.ModifiedUtc);
        Assert.Equal(database.AdminActor.Subject, saved.CreatedBy);
        Assert.Equal(database.AdminActor.Subject, saved.ModifiedBy);
        await using var check = database.AdminSource!.CreateCommand("SELECT expires_utc-created_utc,created_utc FROM cellbridge_lists.create_receipts WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3");
        check.Parameters.AddWithValue(workspace); check.Parameters.AddWithValue(list); check.Parameters.AddWithValue(request.Id);
        await using var reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(lifetime, reader.GetTimeSpan(0));
        Assert.Equal(saved.CreatedUtc, reader.GetFieldValue<DateTimeOffset>(1));
        Assert.True((await engine.CreateRecordAsync(database.AdminActor, workspace, list, request)).Replayed);
    }

    [PostgreSqlFact]
    public async Task RestrictedRoleCannotReserveAnotherWorkspace()
    {
        var (workspace, list, _, _) = await PrepareAsync();
        var other = await database.NewWorkspaceAsync("other reservation");
        var before = await StateAsync(workspace);
        await using var connection = await database.RuntimeSource!.OpenConnectionAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ContextAsync(connection, transaction, other);
            await using var patch = new NpgsqlCommand("SELECT status FROM cellbridge_lists.reserve_patch($1,'{}',1000,0,100,NULL,NULL)", connection, transaction);
            patch.Parameters.AddWithValue(workspace);
            Assert.Equal(5, await patch.ExecuteScalarAsync());
            await transaction.RollbackAsync();
        }
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ContextAsync(connection, transaction, other);
            await using var create = new NpgsqlCommand("SELECT status FROM cellbridge_lists.reserve_create($1,$2,$3,'{}',1000,100,100,NULL,NULL,NULL)", connection, transaction);
            create.Parameters.AddWithValue(workspace); create.Parameters.AddWithValue(list); create.Parameters.AddWithValue(Guid.NewGuid());
            var error = await Assert.ThrowsAsync<PostgresException>(() => create.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            await transaction.RollbackAsync();
        }
        Assert.Equal(before, await StateAsync(workspace));
    }

    [PostgreSqlFact]
    public async Task ReinitializationPreservesRowsAndHealthRejectsDefinerFunctions()
    {
        var (workspace, _, _, _) = await PrepareAsync();
        var before = await StateAsync(workspace);
        var role = new NpgsqlConnectionStringBuilder(database.RuntimeConnectionString).Username!;
        await PostgreSqlListSetup.InitializeAsync(database.AdminSource!, role);
        await PostgreSqlListSetup.InitializeAsync(database.AdminSource!, role);
        Assert.Equal(before, await StateAsync(workspace));
        const string signature = "cellbridge_lists.reserve_patch(uuid,text,bigint,bigint,bigint,bigint,bigint)";
        await using (var change = database.AdminSource!.CreateCommand($"ALTER FUNCTION {signature} SECURITY DEFINER"))
            await change.ExecuteNonQueryAsync();
        try
        {
            var error = await Assert.ThrowsAsync<ListException>(() => new PostgreSqlListEngine(database.RuntimeSource!).CheckHealthAsync());
            Assert.Equal(ListFailure.Unavailable, error.Failure);
        }
        finally
        {
            await using var restore = database.AdminSource!.CreateCommand($"ALTER FUNCTION {signature} SECURITY INVOKER");
            await restore.ExecuteNonQueryAsync();
        }
        await new PostgreSqlListEngine(database.RuntimeSource!).CheckHealthAsync();
    }

    [PostgreSqlFact]
    public async Task VersionFourWithoutReservationsMigratesExplicitlyAndRetainsHistoryMetadata()
    {
        var connectionString = Environment.GetEnvironmentVariable("CELLBRIDGE_LISTS_TEST_CONNECTION_STRING") ?? Environment.GetEnvironmentVariable("ConnectionStrings__lists") ?? throw new InvalidOperationException("The PostgreSQL test connection is missing.");
        var name = "reservation_migration_" + Guid.NewGuid().ToString("N");
        await using (var create = database.AdminSource!.CreateCommand($"CREATE DATABASE {name}"))
            await create.ExecuteNonQueryAsync();
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(connectionString) { Database = name };
            await using var admin = NpgsqlDataSource.Create(settings.ConnectionString);
            var role = new NpgsqlConnectionStringBuilder(database.RuntimeConnectionString).Username!;
            await PostgreSqlListSetup.InitializeAsync(admin, role);
            var workspace = Guid.NewGuid();
            await PostgreSqlListSetup.RegisterWorkspaceAsync(admin, workspace, "before migration", "owner");
            await using (var old = admin.CreateCommand("""
                DROP FUNCTION cellbridge_lists.reserve_patch(uuid,text,bigint,bigint,bigint,bigint,bigint);
                DROP FUNCTION cellbridge_lists.reserve_create(uuid,uuid,uuid,text,bigint,bigint,bigint,bigint,bigint,bigint);
                DELETE FROM cellbridge_lists."__CellBridgeListsMigrationsHistory" WHERE "MigrationId"='20261027090000_WriteReservations';
                UPDATE cellbridge_lists."__CellBridgeListsMigrationsHistory" SET "ProductVersion"='10.0.4';
                """))
                await old.ExecuteNonQueryAsync();
            await PostgreSqlListSetup.InitializeAsync(admin, role);
            await PostgreSqlListSetup.InitializeAsync(admin, role);
            await using var check = admin.CreateCommand("""
                SELECT (SELECT count(*) FROM cellbridge_lists."__CellBridgeListsMigrationsHistory"),
                  (SELECT count(*) FROM cellbridge_lists."__CellBridgeListsMigrationsHistory" WHERE "ProductVersion"='10.0.4'),
                  (SELECT count(*) FROM cellbridge_lists.workspaces)
                """);
            await using var reader = await check.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(3, reader.GetInt64(0)); Assert.Equal(2, reader.GetInt64(1)); Assert.Equal(1, reader.GetInt64(2));
        }
        finally
        {
            await using var drop = database.AdminSource!.CreateCommand($"DROP DATABASE {name} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReservationResultsContainAcceptedLedgerCounters(bool create)
    {
        var (workspace, list, _, _) = await PrepareAsync();
        var before = await StateAsync(workspace);
        await using var connection = await database.RuntimeSource!.OpenConnectionAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ContextAsync(connection, transaction, workspace);
            var sql = create
                ? "SELECT * FROM cellbridge_lists.reserve_create($1,$2,$3,'{}',1000,100,100,NULL,NULL,NULL)"
                : "SELECT * FROM cellbridge_lists.reserve_patch($1,'{}',1000,0,100,NULL,NULL)";
            await using var reserve = new NpgsqlCommand(sql, connection, transaction);
            reserve.Parameters.AddWithValue(workspace);
            if (create)
            {
                reserve.Parameters.AddWithValue(list);
                reserve.Parameters.AddWithValue(Guid.NewGuid());
            }
            (long Bytes, long Audits, int SecurityAudits) accepted;
            await using (var reader = await reserve.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(0, reader.GetInt32(0));
                var ordinal = create ? 4 : 3;
                accepted = (reader.GetInt64(ordinal), reader.GetInt64(ordinal + 1), reader.GetInt32(ordinal + 2));
            }
            await using var ledger = new NpgsqlCommand(
                "SELECT stored_bytes,audit_count,security_audit_count FROM cellbridge_lists.workspaces WHERE workspace_id=$1",
                connection, transaction);
            ledger.Parameters.AddWithValue(workspace);
            await using (var reader = await ledger.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2)), accepted);
            }
            await transaction.RollbackAsync();
        }
        Assert.Equal(before, await StateAsync(workspace));
    }

    [PostgreSqlFact]
    public async Task ConsumerOptionsStillMaterializeAndSaveAccountingEntities()
    {
        var (workspace, list, field, _) = await PrepareAsync();
        var observer = new AccountingObserver();
        var engine = new PostgreSqlListEngine(new DbContextOptionsBuilder<PostgreSqlListDbContext>()
            .UseNpgsql(database.RuntimeSource!).AddInterceptors(observer).Options);
        var created = await engine.CreateRecordAsync(database.AdminActor, workspace, list,
            new(Guid.NewGuid(), 1, Values(field, "tracked create")));
        await engine.UpdateRecordAsync(database.AdminActor, workspace, list, created.Id,
            new(1, 1, [new(field, JsonValue.Create("tracked patch"))]));
        Assert.True(observer.WorkspacesMaterialized >= 2);
        Assert.True(observer.CountersMaterialized >= 1);
        Assert.True(observer.WorkspacesSaved >= 2);
        Assert.True(observer.CountersSaved >= 1);
    }

    private sealed class AccountingObserver : SaveChangesInterceptor, IMaterializationInterceptor
    {
        public int WorkspacesMaterialized;
        public int CountersMaterialized;
        public int WorkspacesSaved;
        public int CountersSaved;
        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (entity.GetType().Name == "WorkspaceRow") WorkspacesMaterialized++;
            if (entity.GetType().Name == "ListCounterRow") CountersMaterialized++;
            return entity;
        }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            foreach (var entry in eventData.Context!.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Modified))
            {
                if (entry.Entity.GetType().Name == "WorkspaceRow") WorkspacesSaved++;
                if (entry.Entity.GetType().Name == "ListCounterRow") CountersSaved++;
            }
            return ValueTask.FromResult(result);
        }
    }

    private async Task<(Guid Workspace, Guid List, Guid Field, Guid Record)> PrepareAsync()
    {
        var workspace = await database.NewWorkspaceAsync("reservation");
        var field = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace, new(Guid.NewGuid(), "Reservation", [field]));
        var record = await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id,
            new(Guid.NewGuid(), 1, Values(field.Id, "original")));
        return (workspace, list.Id, field.Id, record.Id);
    }
    private static JsonObject Values(Guid field, string value) => new() { [field.ToString("D")] = value };
    private async Task<string> StateAsync(Guid workspace)
    {
        await using var command = database.AdminSource!.CreateCommand("""
            SELECT concat_ws(',',stored_bytes,audit_count,
              (SELECT count(*) FROM cellbridge_lists.records WHERE workspace_id=$1),
              (SELECT count(*) FROM cellbridge_lists.create_receipts WHERE workspace_id=$1),
              (SELECT count(*) FROM cellbridge_lists.record_tombstones WHERE workspace_id=$1),
              (SELECT count(*) FROM cellbridge_lists.audit_events WHERE workspace_id=$1),
              (SELECT sum(identity_count::numeric) FROM cellbridge_lists.list_counters WHERE workspace_id=$1),
              (SELECT sum(next_number::numeric) FROM cellbridge_lists.list_counters WHERE workspace_id=$1))
            FROM cellbridge_lists.workspaces WHERE workspace_id=$1
            """);
        command.Parameters.AddWithValue(workspace);
        return (string)(await command.ExecuteScalarAsync())!;
    }
    private static async Task ContextAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid workspace)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('cellbridge.workspace_id',$1,true)", connection, transaction);
        command.Parameters.AddWithValue(workspace.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }
}
