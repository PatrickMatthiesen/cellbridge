using System.Collections.Concurrent;
using System.Data.Common;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class CompiledContextAndResponseRegressionTests(ListDatabaseFixture database)
{
    [PostgreSqlFact]
    public async Task FixedLookupsUseEachEnginesConsumerModelAndOptions()
    {
        var firstWorkspace = await database.NewWorkspaceAsync("compiled first");
        var secondWorkspace = await database.NewWorkspaceAsync("compiled second");
        var listId = Guid.NewGuid();
        var field = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        foreach (var workspace in new[] { firstWorkspace, secondWorkspace })
            await database.Engine.CreateListAsync(database.AdminActor, workspace,
                new(listId, "Compiled contexts", [field]));

        var firstObserver = new ContextObserver();
        var secondObserver = new ContextObserver();
        var first = new PostgreSqlListEngine(Options(firstObserver, 17));
        var second = new PostgreSqlListEngine(Options(secondObserver, 23));
        var recordId = Guid.NewGuid();
        await ExerciseAsync(first, firstWorkspace, "first");
        await ExerciseAsync(second, secondWorkspace, "second");
        Assert.True(firstObserver.ContextCompilations > 0);
        Assert.True(secondObserver.ContextCompilations > 0);
        Assert.All(firstObserver.Commands, command => Assert.Equal(17, command.Timeout));
        Assert.All(secondObserver.Commands, command => Assert.Equal(23, command.Timeout));
        var firstModel = Assert.Single(firstObserver.Commands.Select(command => command.Model).Distinct());
        var secondModel = Assert.Single(secondObserver.Commands.Select(command => command.Model).Distinct());
        Assert.NotSame(firstModel, secondModel);

        // Warm delegates must still evaluate the current subject and workspace.
        var stranger = new ListActor("test:compiled-stranger:" + Guid.NewGuid().ToString("N"));
        var denied = await Assert.ThrowsAsync<ListException>(() =>
            first.QueryAsync(stranger, firstWorkspace, listId, new()));
        Assert.Equal(ListFailure.Forbidden, denied.Failure);
        Assert.Equal("first updated", (await first.QueryAsync(database.AdminActor,
            firstWorkspace, listId, new())).Records[0].Values[field.Id.ToString("D")]!.GetValue<string>());

        DbContextOptions<PostgreSqlListDbContext> Options(ContextObserver observer, int timeout) =>
            new DbContextOptionsBuilder<PostgreSqlListDbContext>()
                .UseNpgsql(database.RuntimeSource!, provider => provider.CommandTimeout(timeout))
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .ReplaceService<IModelCacheKeyFactory, PerContextModelCacheKeyFactory>()
                .AddInterceptors(observer)
                .Options;

        async Task ExerciseAsync(PostgreSqlListEngine engine, Guid workspace, string text)
        {
            var request = new CreateRecordRequest(recordId, 1,
                new() { [field.Id.ToString("D")] = text });
            var created = await engine.CreateRecordAsync(database.AdminActor, workspace, listId, request);
            Assert.Equal((1L, 1L), (created.Revision, created.Number));
            Assert.True((await engine.CreateRecordAsync(database.AdminActor, workspace, listId, request)).Replayed);
            var patched = await engine.UpdateRecordAsync(database.AdminActor, workspace, listId, recordId,
                new(1, 1, [new(field.Id, JsonValue.Create(text + " updated"))]));
            Assert.Equal(2, patched.Revision);
            var page = await engine.QueryAsync(database.AdminActor, workspace, listId, new());
            Assert.Equal(text + " updated", Assert.Single(page.Records).Values[field.Id.ToString("D")]!.GetValue<string>());
        }
    }

    [PostgreSqlFact]
    public async Task SharedConsumerModelStillUsesEachEnginesCompilationInterceptors()
    {
        var workspace = await database.NewWorkspaceAsync("shared compiled model");
        var field = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new(Guid.NewGuid(), "Shared model", [field]));
        using var modelContext = new PostgreSqlListDbContext(
            new DbContextOptionsBuilder<PostgreSqlListDbContext>().UseNpgsql(database.RuntimeSource!).Options);
        var sharedModel = modelContext.Model;
        var firstObserver = new ContextObserver();
        var secondObserver = new ContextObserver();
        foreach (var observer in new[] { firstObserver, secondObserver })
        {
            var options = new DbContextOptionsBuilder<PostgreSqlListDbContext>()
                .UseNpgsql(database.RuntimeSource!)
                .UseModel(sharedModel)
                .AddInterceptors(observer)
                .Options;
            var engine = new PostgreSqlListEngine(options);
            var id = Guid.NewGuid();
            await engine.CreateRecordAsync(database.AdminActor, workspace, list.Id,
                new(id, 1, new() { [field.Id.ToString("D")] = "created" }));
            await engine.UpdateRecordAsync(database.AdminActor, workspace, list.Id, id,
                new(1, 1, [new(field.Id, JsonValue.Create("updated"))]));
            await engine.QueryAsync(database.AdminActor, workspace, list.Id, new());
            Assert.True(observer.ContextCompilations >= 3);
            Assert.All(observer.Commands, command => Assert.Same(sharedModel, command.Model));
        }
    }

    [PostgreSqlFact]
    public async Task WarmCreateLookupReplaysHistoricalReceiptAfterSchemaPublication()
    {
        var workspace = await database.NewWorkspaceAsync("compiled historic receipt");
        var field = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new(Guid.NewGuid(), "Historic receipt", [field]));
        var engine = new PostgreSqlListEngine(database.RuntimeSource!);
        var request = new CreateRecordRequest(Guid.NewGuid(), 1,
            new() { [field.Id.ToString("D")] = "original" });
        var original = await engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, request);
        var extension = new ListField(Guid.NewGuid(), "Extension", ListFieldType.Text);
        await database.Engine.PublishSchemaAsync(database.AdminActor, workspace, list.Id,
            new(1, list.Name, [field, extension]));
        var replay = await engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, request);
        Assert.True(replay.Replayed);
        Assert.Equal((original.Revision, original.Number), (replay.Revision, replay.Number));
        var altered = request with { Values = new() { [field.Id.ToString("D")] = "different" } };
        Assert.Equal(ListFailure.Conflict, (await Assert.ThrowsAsync<ListException>(() =>
            engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, altered))).Failure);
        Assert.Equal(ListFailure.Conflict, (await Assert.ThrowsAsync<ListException>(() =>
            engine.CreateRecordAsync(database.AdminActor, workspace, list.Id,
                request with { Id = Guid.NewGuid() }))).Failure);
        var fresh = await engine.CreateRecordAsync(database.AdminActor, workspace, list.Id,
            request with { Id = Guid.NewGuid(), SchemaVersion = 2 });
        Assert.False(fresh.Replayed);
        Assert.Equal(2, fresh.Number);
    }

    [PostgreSqlFact]
    public async Task WarmCreateLookupMaterializesReceiptsThroughConsumerInterceptor()
    {
        var workspace = await database.NewWorkspaceAsync("compiled receipt materialization");
        var field = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new(Guid.NewGuid(), "Materialized receipts", [field]));
        var interceptor = new ReceiptMaterializationInterceptor();
        var observer = new ContextObserver();
        var engine = new PostgreSqlListEngine(new DbContextOptionsBuilder<PostgreSqlListDbContext>()
            .UseNpgsql(database.RuntimeSource!)
            .AddInterceptors(interceptor, observer)
            .Options);
        var request = new CreateRecordRequest(Guid.NewGuid(), 1,
            new() { [field.Id.ToString("D")] = "original" });
        var created = await engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, request);
        Assert.False(created.Replayed);
        Assert.Equal((1L, 1L), (created.Revision, created.Number));
        Assert.Empty(interceptor.Models);
        var compilations = observer.ContextCompilations;
        Assert.True(compilations > 0);
        for (var index = 0; index < 2; index++)
        {
            var replay = await engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, request);
            Assert.True(replay.Replayed);
            Assert.Equal((ReceiptMaterializationInterceptor.Revision, ReceiptMaterializationInterceptor.Number),
                (replay.Revision, replay.Number));
        }
        Assert.Equal(2, interceptor.Models.Count);
        Assert.Single(interceptor.Models.Distinct());
        Assert.Equal(compilations, observer.ContextCompilations);
        var persisted = await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, request);
        Assert.True(persisted.Replayed);
        Assert.Equal((created.Revision, created.Number), (persisted.Revision, persisted.Number));
    }

    [PostgreSqlFact]
    public async Task ExplicitConnectionFallsBackWhenConsumerSelectsAnotherModelOnEachLease()
    {
        var workspace = await database.NewWorkspaceAsync("compiled model fallback");
        var field = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new(Guid.NewGuid(), "Changing models", [field]));
        await using var connection = database.RuntimeSource!.CreateConnection();
        await connection.OpenAsync();
        var observer = new ContextObserver();
        var interceptor = new ReceiptMaterializationInterceptor();
        var options = new DbContextOptionsBuilder<PostgreSqlListDbContext>()
            .UseNpgsql(connection, false, provider => provider.CommandTimeout(29))
            .ReplaceService<IModelCacheKeyFactory, PerContextModelCacheKeyFactory>()
            .AddInterceptors(observer, interceptor)
            .Options;
        var engine = new PostgreSqlListEngine(options);
        var recordId = Guid.NewGuid();
        var request = new CreateRecordRequest(recordId, 1, new() { [field.Id.ToString("D")] = "before" });
        await engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, request);
        var firstCreateModel = observer.Commands.Last().Model;
        Assert.Empty(interceptor.Models);
        for (var index = 0; index < 2; index++)
        {
            var replay = await engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, request);
            Assert.True(replay.Replayed);
            Assert.Equal((ReceiptMaterializationInterceptor.Revision, ReceiptMaterializationInterceptor.Number),
                (replay.Revision, replay.Number));
        }
        Assert.Equal(2, interceptor.Models.Count);
        Assert.Equal(2, interceptor.Models.Distinct().Count());
        Assert.All(interceptor.Models, model => Assert.NotSame(firstCreateModel, model));
        var persisted = await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, request);
        Assert.True(persisted.Replayed);
        Assert.Equal((1L, 1L), (persisted.Revision, persisted.Number));
        await engine.UpdateRecordAsync(database.AdminActor, workspace, list.Id, recordId,
            new(1, 1, [new(field.Id, JsonValue.Create("after"))]));
        Assert.Equal("after", Assert.Single((await engine.QueryAsync(database.AdminActor,
            workspace, list.Id, new())).Records).Values[field.Id.ToString("D")]!.GetValue<string>());
        Assert.True(observer.Commands.Select(command => command.Model).Distinct().Count() >= 3);
        Assert.All(observer.Commands, command => Assert.Equal(29, command.Timeout));
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [PostgreSqlFact]
    public async Task PatchResponseUsesValuesReplacedByConsumerSaveInterceptor()
    {
        var workspace = await database.NewWorkspaceAsync("intercepted patch response");
        var field = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new(Guid.NewGuid(), "Intercepted patch", [field]));
        var recordId = Guid.NewGuid();
        var key = field.Id.ToString("D");
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id,
            new(recordId, 1, new() { [key] = "original" }));
        var interceptor = new RecordValuesSaveInterceptor(recordId, key);
        var engine = new PostgreSqlListEngine(new DbContextOptionsBuilder<PostgreSqlListDbContext>()
            .UseNpgsql(database.RuntimeSource!)
            .AddInterceptors(interceptor)
            .Options);
        var submitted = JsonValue.Create("before");
        var response = await engine.UpdateRecordAsync(database.AdminActor, workspace, list.Id, recordId,
            new(1, 1, [new(field.Id, submitted)]));
        var persisted = await database.Engine.GetRecordAsync(database.AdminActor, workspace, list.Id, recordId);
        Assert.Equal(1, interceptor.Replacements);
        Assert.Equal("before", submitted!.GetValue<string>());
        Assert.Equal("AFTER!", response.Values[key]!.GetValue<string>());
        Assert.Equal("AFTER!", persisted.Values[key]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(persisted.Values, response.Values));
        Assert.Equal(2, response.Revision);
        Assert.Equal(persisted.Revision, response.Revision);
        Assert.Equal(persisted.ModifiedUtc, response.ModifiedUtc);
    }

    [PostgreSqlFact]
    public async Task PatchResponseKeepsScalarGettersNormalizationAndIndependentOwnership()
    {
        var workspace = await database.NewWorkspaceAsync("owned patch scalars");
        var cases = new (ListFieldType Type, JsonNode? Value)[]
        {
            (ListFieldType.Integer, JsonValue.Create(7)),
            (ListFieldType.Integer, JsonValue.Create(long.MaxValue)),
            (ListFieldType.Integer, JsonValue.Create((short)-12)),
            (ListFieldType.Decimal, JsonValue.Create(123.4500m)),
            (ListFieldType.Decimal, JsonNode.Parse("-0e-20000")),
            (ListFieldType.Decimal, JsonValue.Create(0.000000m)),
            (ListFieldType.Boolean, JsonValue.Create(true)),
            (ListFieldType.Text, JsonValue.Create("typed text")),
            (ListFieldType.Text, JsonValue.Create('x')),
            (ListFieldType.Text, JsonValue.Create(Guid.NewGuid())),
            (ListFieldType.Text, JsonValue.Create(new DateTimeOffset(2026, 10, 22, 10, 11, 12, TimeSpan.Zero))),
            (ListFieldType.Date, JsonValue.Create(new DateOnly(2026, 10, 22))),
            (ListFieldType.Timestamp, JsonValue.Create(new DateTime(2026, 10, 22, 10, 11, 12, DateTimeKind.Utc))),
            (ListFieldType.Integer, JsonValue.Create(JsonSerializer.SerializeToElement(42))),
            (ListFieldType.Text, JsonNode.Parse("\"parsed text\"")),
            (ListFieldType.Text, null)
        };
        var fields = cases.Select((item, index) => new ListField(Guid.NewGuid(), "Value " + index, item.Type)).ToArray();
        var retained = new ListField(Guid.NewGuid(), "Retained", ListFieldType.Text);
        var removed = new ListField(Guid.NewGuid(), "Removed", ListFieldType.Text);
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new(Guid.NewGuid(), "Owned scalars", fields.Concat(new[] { retained, removed }).ToArray()));
        var recordId = Guid.NewGuid();
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id,
            new(recordId, 1, new()
            {
                [retained.Id.ToString("D")] = "retained",
                [removed.Id.ToString("D")] = "remove me"
            }));
        var originals = cases.Select(item => item.Value?.ToJsonString()).ToArray();
        var changes = fields.Select((field, index) => new FieldChange(field.Id, cases[index].Value))
            .Append(new FieldChange(removed.Id, Remove: true)).ToArray();
        var response = await database.Engine.UpdateRecordAsync(database.AdminActor, workspace, list.Id, recordId,
            new(1, 1, changes));
        var reread = await database.Engine.GetRecordAsync(database.AdminActor, workspace, list.Id, recordId);
        Assert.Equal(2, response.Revision);
        Assert.Equal(reread.ModifiedUtc, response.ModifiedUtc);
        Assert.False(response.Values.ContainsKey(removed.Id.ToString("D")));
        Assert.Equal("retained", response.Values[retained.Id.ToString("D")]!.GetValue<string>());
        for (var index = 0; index < fields.Length; index++)
        {
            Assert.Equal(originals[index], cases[index].Value?.ToJsonString());
            Assert.Same(cases[index].Value, changes[index].Value);
            var key = fields[index].Id.ToString("D");
            var result = response.Values[key];
            var stored = reread.Values[key];
            Assert.True(JsonNode.DeepEquals(stored, result));
            if (result is null) continue;
            Assert.NotSame(cases[index].Value, result);
            Assert.NotSame(stored, result);
            // Parsed responses supported both narrow and wide numeric getters.
            switch (fields[index].Type)
            {
                case ListFieldType.Integer:
                    Assert.Equal(stored!.GetValue<long>(), result.GetValue<long>());
                    Assert.Equal(stored.GetValue<decimal>(), result.GetValue<decimal>());
                    break;
                case ListFieldType.Decimal:
                    Assert.Equal(stored!.GetValue<decimal>(), result.GetValue<decimal>());
                    break;
                case ListFieldType.Boolean:
                    Assert.Equal(stored!.GetValue<bool>(), result.GetValue<bool>());
                    break;
                default:
                    Assert.Equal(stored!.GetValue<string>(), result.GetValue<string>());
                    break;
            }
        }
        Assert.Equal("0", response.Values[fields[4].Id.ToString("D")]!.ToJsonString());
        Assert.Equal("0", response.Values[fields[5].Id.ToString("D")]!.ToJsonString());
        response.Values[retained.Id.ToString("D")] = "response edit";
        Assert.Equal("retained", reread.Values[retained.Id.ToString("D")]!.GetValue<string>());
        var finalRead = await database.Engine.GetRecordAsync(database.AdminActor, workspace, list.Id, recordId);
        Assert.Equal("retained", finalRead.Values[retained.Id.ToString("D")]!.GetValue<string>());
    }

    private sealed class RecordValuesSaveInterceptor(Guid recordId, string fieldKey) : SaveChangesInterceptor
    {
        public int Replacements { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            foreach (var entry in eventData.Context!.ChangeTracker.Entries().Where(entry =>
                         entry.State == EntityState.Modified && entry.Metadata.GetTableName() == "records" &&
                         Equals(entry.Property("RecordId").CurrentValue, recordId)))
            {
                var property = entry.Property("ValuesJson");
                var values = JsonNode.Parse((string)property.CurrentValue!)!.AsObject();
                Assert.Equal("before", values[fieldKey]!.GetValue<string>());
                // Equal-length text keeps the existing canonical byte accounting unchanged.
                values[fieldKey] = "AFTER!";
                property.CurrentValue = values.ToJsonString();
                Replacements++;
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ReceiptMaterializationInterceptor : IMaterializationInterceptor
    {
        public const long Revision = 71;
        public const long Number = 83;
        public ConcurrentQueue<IModel> Models { get; } = new();

        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            var entityType = materializationData.EntityType;
            if (entityType.GetTableName() == "create_receipts")
            {
                Models.Enqueue(materializationData.Context.Model);
                entityType.FindProperty("Revision")!.PropertyInfo!.SetValue(entity, Revision);
                entityType.FindProperty("Number")!.PropertyInfo!.SetValue(entity, Number);
            }
            return entity;
        }
    }

    public sealed class PerContextModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            (context.GetType(), context.ContextId.InstanceId, designTime);
    }

    private sealed class ContextObserver : DbCommandInterceptor, IQueryExpressionInterceptor
    {
        private int _contextCompilations;
        public ConcurrentQueue<(IModel Model, int Timeout)> Commands { get; } = new();
        public int ContextCompilations => Volatile.Read(ref _contextCompilations);

        public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
        {
            if (queryExpression.ToString().Contains("ContextProjection", StringComparison.Ordinal) ||
                queryExpression.ToString().Contains("ListDefinitionProjection", StringComparison.Ordinal))
                Interlocked.Increment(ref _contextCompilations);
            return queryExpression;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Enqueue((eventData.Context!.Model, command.CommandTimeout));
            return ValueTask.FromResult(result);
        }
    }
}
