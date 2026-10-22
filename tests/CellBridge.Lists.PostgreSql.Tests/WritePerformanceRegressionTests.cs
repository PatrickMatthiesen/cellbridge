using System.Text.Json.Nodes;
using System.Text.Json;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class WritePerformanceRegressionTests(ListDatabaseFixture database)
{
    [PostgreSqlFact]
    public async Task DefaultGrowthLimitAllowsMoreThanOneHundredThousandIdentities()
    {
        var workspace = await database.NewWorkspaceAsync("large-counter");
        var field = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Large", [field]));
        await database.SetWorkspaceContextAsync(workspace, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                UPDATE cellbridge_lists.list_counters SET identity_count=100000,next_number=100001
                WHERE workspace_id=$1 AND list_id=$2
                """, connection, transaction);
            command.Parameters.AddWithValue(workspace);
            command.Parameters.AddWithValue(list.Id);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        });

        var created = await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id,
            new CreateRecordRequest(Guid.NewGuid(), 1, Values(field, "one hundred thousand and one")));
        Assert.Equal(100001, created.Number);

        var bounded = new PostgreSqlListEngine(database.RuntimeSource!, new ListLimits
        {
            MaxRecordsPerList = 100001
        });
        var limited = await Assert.ThrowsAsync<ListException>(() => bounded.CreateRecordAsync(database.AdminActor,
            workspace, list.Id, new CreateRecordRequest(Guid.NewGuid(), 1, Values(field, "over limit"))));
        Assert.Equal(ListFailure.Limit, limited.Failure);
    }

    [PostgreSqlFact]
    public async Task ConcurrentIdenticalCreatesReturnOneIdentityAndOneNumber()
    {
        var workspace = await database.NewWorkspaceAsync("concurrent-create-retry");
        var field = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Concurrent creates", [field]));
        var request = new CreateRecordRequest(Guid.NewGuid(), 1, Values(field, "same request"));

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, request)));
        Assert.Single(results, x => !x.Replayed);
        Assert.Equal(7, results.Count(x => x.Replayed));
        Assert.Single(results.Select(x => x.Number).Distinct());

        await database.SetWorkspaceContextAsync(workspace, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT identity_count,next_number FROM cellbridge_lists.list_counters
                WHERE workspace_id=$1 AND list_id=$2
                """, connection, transaction);
            command.Parameters.AddWithValue(workspace);
            command.Parameters.AddWithValue(list.Id);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt64(0));
            Assert.Equal(2, reader.GetInt64(1));
        });
    }

    [PostgreSqlFact]
    public async Task ReferenceSwapsAndSelfReferencesUseRowLevelForeignKeyCoordination()
    {
        var workspace = await database.NewWorkspaceAsync("reference-row-locks");
        var title = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Nodes", [title]));
        var reference = new ListField(Guid.NewGuid(), "Next", ListFieldType.Reference,
            ReferenceListId: list.Id, ReferenceDisplayFieldId: title.Id);
        await database.Engine.PublishSchemaAsync(database.AdminActor, workspace, list.Id,
            new PublishSchemaRequest(1, list.Name, [title, reference]));

        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id,
            new CreateRecordRequest(firstId, 2, Values(title, "first")));
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id,
            new CreateRecordRequest(secondId, 2, Values(title, "second")));

        var updates = await Task.WhenAll(
            database.Engine.UpdateRecordAsync(database.AdminActor, workspace, list.Id, firstId,
                new UpdateRecordRequest(1, 2, [new FieldChange(reference.Id, JsonValue.Create(secondId.ToString("D")))])),
            database.Engine.UpdateRecordAsync(database.AdminActor, workspace, list.Id, secondId,
                new UpdateRecordRequest(1, 2, [new FieldChange(reference.Id, JsonValue.Create(firstId.ToString("D")))])));
        Assert.All(updates, record => Assert.Equal(2, record.Revision));

        var referenced = await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.DeleteRecordAsync(database.AdminActor, workspace, list.Id, firstId, 2));
        Assert.Equal(ListFailure.Conflict, referenced.Failure);
        await database.Engine.UpdateRecordAsync(database.AdminActor, workspace, list.Id, secondId,
            new UpdateRecordRequest(2, 2, [new FieldChange(reference.Id, Remove: true)]));
        await database.Engine.DeleteRecordAsync(database.AdminActor, workspace, list.Id, firstId, 2);
        await database.Engine.DeleteRecordAsync(database.AdminActor, workspace, list.Id, secondId, 3);

        var selfId = Guid.NewGuid();
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id,
            new CreateRecordRequest(selfId, 2, Values(title, "self")));
        await database.Engine.UpdateRecordAsync(database.AdminActor, workspace, list.Id, selfId,
            new UpdateRecordRequest(1, 2, [new FieldChange(reference.Id, JsonValue.Create(selfId.ToString("D")))]));
        await database.Engine.DeleteRecordAsync(database.AdminActor, workspace, list.Id, selfId, 2);
    }

    [PostgreSqlFact]
    public async Task WriterReadsPublishedSchemaAfterWaitingForSourceListLock()
    {
        var workspace = await database.NewWorkspaceAsync("schema-lock-snapshot");
        var title = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Schema race", [title]));
        var extension = new ListField(Guid.NewGuid(), "Extension", ListFieldType.Text);
        var next = list with { SchemaVersion = 2, Fields = [title, extension] };

        await using var connection = await database.RuntimeSource!.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand("SELECT set_config('cellbridge.workspace_id',$1,true)", connection, transaction))
        {
            context.Parameters.AddWithValue(workspace.ToString("D"));
            await context.ExecuteNonQueryAsync();
        }
        await using (var lockList = new NpgsqlCommand("""
            SELECT list_id FROM cellbridge_lists.lists
            WHERE workspace_id=$1 AND list_id=$2 FOR UPDATE
            """, connection, transaction))
        {
            lockList.Parameters.AddWithValue(workspace);
            lockList.Parameters.AddWithValue(list.Id);
            Assert.Equal(list.Id, await lockList.ExecuteScalarAsync());
        }
        var json = JsonSerializer.Serialize(next, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await using (var publish = new NpgsqlBatch(connection, transaction))
        {
            var insert = new NpgsqlBatchCommand("""
                INSERT INTO cellbridge_lists.list_schemas(workspace_id,list_id,schema_version,definition)
                VALUES (@workspace,@list,2,@definition::jsonb)
                """);
            insert.Parameters.AddWithValue("workspace", workspace);
            insert.Parameters.AddWithValue("list", list.Id);
            insert.Parameters.AddWithValue("definition", json);
            publish.BatchCommands.Add(insert);
            var update = new NpgsqlBatchCommand("""
                UPDATE cellbridge_lists.lists SET schema_version=2
                WHERE workspace_id=@workspace AND list_id=@list
                """);
            update.Parameters.AddWithValue("workspace", workspace);
            update.Parameters.AddWithValue("list", list.Id);
            publish.BatchCommands.Add(update);
            await publish.ExecuteNonQueryAsync();
        }

        var write = database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id,
            new CreateRecordRequest(Guid.NewGuid(), 2, new JsonObject
            {
                [title.Id.ToString("D")] = "current",
                [extension.Id.ToString("D")] = "published"
            }));
        await WaitForDatabaseLockAsync();
        await transaction.CommitAsync();
        var created = await write;
        Assert.Equal(1, created.Revision);
    }

    [PostgreSqlFact]
    public async Task ConcurrentCreateReplaySurvivesReferenceDeletionAfterInitialRead()
    {
        var workspace = await database.NewWorkspaceAsync("concurrent-reference-replay");
        var label = new ListField(Guid.NewGuid(), "Label", ListFieldType.Text);
        var target = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Targets", [label]));
        var targetId = Guid.NewGuid();
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, target.Id,
            new CreateRecordRequest(targetId, 1, Values(label, "target")));
        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference,
            ReferenceListId: target.Id, ReferenceDisplayFieldId: label.Id);
        var source = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Sources", [reference]));
        var request = new CreateRecordRequest(Guid.NewGuid(), 1,
            new JsonObject { [reference.Id.ToString("D")] = targetId.ToString("D") });

        var role = "lists_blocked_writer_" + Guid.NewGuid().ToString("N");
        var password = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var quotedRole = '"' + role + '"';
        await using (var createRole = database.AdminSource!.CreateCommand($"""
            CREATE ROLE {quotedRole} LOGIN PASSWORD '{password}'
              NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT
            """))
            await createRole.ExecuteNonQueryAsync();
        try
        {
            await PostgreSqlListSetup.InitializeAsync(database.AdminSource!, role);
            var baseConnectionString = Environment.GetEnvironmentVariable("CELLBRIDGE_LISTS_TEST_CONNECTION_STRING")
                ?? Environment.GetEnvironmentVariable("ConnectionStrings__lists")
                ?? throw new InvalidOperationException("The PostgreSQL test connection is missing.");
            var runtimeConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                Username = role,
                Password = password,
                ApplicationName = "CellBridge.Lists.BlockedWriter"
            }.ConnectionString;
            using var blocker = new ReferenceCommandBlocker();
            using var loggerFactory = new SingleProviderLoggerFactory(blocker);
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(runtimeConnectionString);
            dataSourceBuilder.UseLoggerFactory(loggerFactory);
            await using var blockedSource = dataSourceBuilder.Build();
            var blockedEngine = new PostgreSqlListEngine(blockedSource);
            await blockedEngine.CheckHealthAsync();
            blocker.Enable();
            try
            {
                var loser = Task.Run(() => blockedEngine.CreateRecordAsync(database.AdminActor,
                    workspace, source.Id, request));
                await blocker.WaitUntilBlockedAsync();
                var winner = await database.Engine.CreateRecordAsync(database.AdminActor, workspace, source.Id, request);
                await database.Engine.DeleteRecordAsync(database.AdminActor, workspace, source.Id, request.Id, 1);
                await database.Engine.DeleteRecordAsync(database.AdminActor, workspace, target.Id, targetId, 1);
                blocker.Release();

                var replay = await loser;
                Assert.True(replay.Replayed);
                Assert.Equal((winner.Number, winner.Revision), (replay.Number, replay.Revision));
                Assert.True(blocker.DatabaseClockReads > 0);
            }
            finally
            {
                blocker.Release();
            }
        }
        finally
        {
            await using var cleanup = database.AdminSource!.CreateCommand($"DROP OWNED BY {quotedRole}; DROP ROLE {quotedRole}");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private async Task WaitForDatabaseLockAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = database.AdminSource!.CreateCommand("""
                SELECT count(*) FROM pg_stat_activity
                WHERE datname=current_database() AND wait_event_type='Lock' AND wait_event<>'advisory'
                """);
            if ((long)(await command.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(20);
        }
        Assert.Fail("The writer did not wait for the source list row lock.");
    }

    private static JsonObject Values(ListField field, string value) =>
        new() { [field.Id.ToString("D")] = value };

    private sealed class ReferenceCommandBlocker : ILoggerProvider
    {
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _enabled;
        private int _claimed;
        private int _databaseClockReads;

        public int DatabaseClockReads => Volatile.Read(ref _databaseClockReads);

        public ILogger CreateLogger(string categoryName) => new BlockingLogger(this);
        public void Dispose() => _release.TrySetResult();
        public void Enable() => Interlocked.Exchange(ref _enabled, 1);
        public void Release() => _release.TrySetResult();

        public async Task WaitUntilBlockedAsync()
        {
            var completed = await Task.WhenAny(_blocked.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            if (completed != _blocked.Task) Assert.Fail("The reference validation command was not intercepted.");
            await _blocked.Task;
        }

        private sealed class BlockingLogger(ReferenceCommandBlocker owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                if (Volatile.Read(ref owner._enabled) != 0 &&
                    message.Contains("clock_timestamp()", StringComparison.Ordinal))
                    Interlocked.Increment(ref owner._databaseClockReads);
                if (Volatile.Read(ref owner._enabled) == 0 ||
                    !message.Contains("SELECT record_id FROM cellbridge_lists.records", StringComparison.Ordinal) ||
                    !message.Contains("FOR KEY SHARE", StringComparison.Ordinal) ||
                    Interlocked.CompareExchange(ref owner._claimed, 1, 0) != 0)
                    return;
                owner._blocked.TrySetResult();
                owner._release.Task.GetAwaiter().GetResult();
            }
        }
    }

    private sealed class SingleProviderLoggerFactory(ILoggerProvider provider) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider addedProvider) { }
        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);
        public void Dispose() { }
    }
}
