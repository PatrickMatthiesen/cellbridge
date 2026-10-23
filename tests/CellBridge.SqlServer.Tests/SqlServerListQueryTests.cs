using System.Data.Common;
using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.SqlServer.Tests;

public sealed partial class SqlServerListEngineTests
{
    [Fact]
    public async Task ExactQueryKeysPreserveDecimalDomainUtf16LongTextAndTimestampPaging()
    {
        var env = await SetupAsync();
        var text = new ListField(Guid.NewGuid(), "Text", ListFieldType.Text, MaxLength: 65536);
        var number = new ListField(Guid.NewGuid(), "Decimal", ListFieldType.Decimal);
        var timestamp = new ListField(Guid.NewGuid(), "Timestamp", ListFieldType.Timestamp);
        var list = await env.Engine.CreateListAsync(Admin, env.Workspace, new(Guid.NewGuid(), "Exact keys", [text, number, timestamp]));
        var texts = new[] { "", "a", "a ", "\U0001F600", "\uE000", new string('x', 5001) + "a", new string('x', 5001) + "b" };
        var numbers = new[] { "-9999999999999999999999999999", "-1e-12", "0e2147483647", "1.000000000000e-12", "9999999999999999999999999999", "1e27", "-0" };
        var times = new[] { "2026-01-01T00:00:00Z", "2026-01-01T00:00:00.1Z", "2026-01-01T00:00:00.0000001Z", "0001-01-01T00:00:00Z",
            "9999-12-31T23:59:59.9999999Z", "2026-01-01T00:00:00.12Z", "2026-01-01T00:00:00.2Z" };
        var records = new List<ListRecord>();
        for (var index = 0; index < texts.Length; index++)
        {
            // These IDs deliberately disagree with SQL Server uniqueidentifier order.
            var id = Guid.Parse(index.ToString("x8") + "-0000-0000-0000-" + (texts.Length - index).ToString("x12"));
            var request = new CreateRecordRequest(id, 1, new JsonObject { [text.Id.ToString("D")] = texts[index],
                [number.Id.ToString("D")] = JsonNode.Parse(numbers[index]), [timestamp.Id.ToString("D")] = times[index] });
            await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request);
            records.Add(await env.Engine.GetRecordAsync(Admin, env.Workspace, list.Id, id));
        }
        foreach (var values in new[] { new JsonObject(), new JsonObject { [text.Id.ToString("D")] = null, [number.Id.ToString("D")] = null, [timestamp.Id.ToString("D")] = null } })
        {
            var id = Guid.NewGuid();
            await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, new(id, 1, values));
            records.Add(await env.Engine.GetRecordAsync(Admin, env.Workspace, list.Id, id));
        }
        foreach (var sort in new Guid?[] { null, text.Id, number.Id, timestamp.Id })
            foreach (var descending in new[] { false, true })
                await AssertQueryPagesAsync(env, list, records, new(SortFieldId: sort, Descending: descending, Limit: 2));
        foreach (var operation in Enum.GetValues<FilterOperator>())
        {
            await AssertQueryPagesAsync(env, list, records, new([new(number.Id, operation, JsonNode.Parse("1e-12"))], number.Id, Limit: 2));
            await AssertQueryPagesAsync(env, list, records, new([new(text.Id, operation, JsonValue.Create("a "))], text.Id, Limit: 2));
            await AssertQueryPagesAsync(env, list, records, new([new(timestamp.Id, operation, JsonValue.Create(times[1]))], timestamp.Id, Limit: 2));
        }
        await AssertQueryPagesAsync(env, list, records, new([new(number.Id, FilterOperator.Equal, null)], number.Id, Limit: 1));
        await AssertQueryPagesAsync(env, list, records, new([new(text.Id, FilterOperator.GreaterThan, JsonValue.Create(new string('x', 5001) + "a"))], text.Id, Limit: 1));
    }

    [Theory]
    [InlineData(ListFieldType.Text)]
    [InlineData(ListFieldType.Boolean)]
    [InlineData(ListFieldType.Integer)]
    [InlineData(ListFieldType.Decimal)]
    [InlineData(ListFieldType.Date)]
    [InlineData(ListFieldType.Timestamp)]
    [InlineData(ListFieldType.Choice)]
    [InlineData(ListFieldType.Reference)]
    public async Task EveryScalarMatchesDomainFiltersNullsConjunctionsAndBothPageDirections(ListFieldType type)
    {
        var env = await SetupAsync();
        var field = new ListField(Guid.NewGuid(), "Value", type, MaxLength: 65536);
        JsonNode?[] values;
        if (type == ListFieldType.Reference)
        {
            var target = await env.CreateListAsync();
            var ids = new[] { Guid.Parse("ffffffff-0000-0000-0000-000000000001"), Guid.Parse("00000001-0000-0000-0000-ffffffffffff"), Guid.NewGuid() };
            foreach (var id in ids) await env.Engine.CreateRecordAsync(Admin, env.Workspace, target.Id, new(id, 1, new JsonObject()));
            field = field with { ReferenceListId = target.Id };
            values = ids.Select(id => (JsonNode?)JsonValue.Create(id.ToString("D"))).ToArray();
        }
        else if (type == ListFieldType.Choice)
        {
            var choices = new[] { "a", "a ", "A", "é" };
            field = field with { Choices = choices.Select(value => new FieldChoice(value, value)).ToArray() };
            values = choices.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray();
        }
        else if (type == ListFieldType.Text)
            values = new[] { "\u0001", "a", "a ", "a  ", "a\t", "é", "e\u0301", "\U0001F600", "\uE000" }.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray();
        else if (type == ListFieldType.Date)
            values = new[] { "0001-01-01", "2024-02-29", "2026-01-01", "9999-12-31" }.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray();
        else if (type == ListFieldType.Timestamp)
            values = new[] { "2026-01-01T00:00:00Z", "2026-01-01T00:00:00.0000001Z", "2026-01-01T00:00:00.1Z", "2026-01-01T00:00:00.12Z" }
                .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray();
        else
            values = (type switch
            {
                ListFieldType.Integer => new[] { "-9223372036854775808", "-1", "0", "2", "9223372036854775807" },
                ListFieldType.Decimal => new[] { "-9999999999999999.999999999999", "9999999999999999.999999999999", "1E+12",
                    "10e-12", "-1e27", "-10.00", "100000000000000000000000000000000000e-20", "0e-2147483648", "-0e2147483647" },
                _ => new[] { "true", "false" }
            }).Select(value => JsonNode.Parse(value)).ToArray();
        var list = await env.Engine.CreateListAsync(Admin, env.Workspace, new(Guid.NewGuid(), "Scalars", [field]));
        var records = new List<ListRecord>();
        foreach (var value in values.Concat(new JsonNode?[] { null }))
        {
            var id = Guid.NewGuid();
            await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, new(id, 1, new JsonObject { [field.Id.ToString("D")] = value?.DeepClone() }));
            records.Add(await env.Engine.GetRecordAsync(Admin, env.Workspace, list.Id, id));
        }
        var missing = Guid.NewGuid();
        await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, new(missing, 1, new JsonObject()));
        records.Add(await env.Engine.GetRecordAsync(Admin, env.Workspace, list.Id, missing));
        foreach (var descending in new[] { false, true })
            await AssertQueryPagesAsync(env, list, records, new(SortFieldId: field.Id, Descending: descending, Limit: 2));
        foreach (var operation in Enum.GetValues<FilterOperator>())
        {
            if (type is ListFieldType.Boolean or ListFieldType.Choice or ListFieldType.Reference && operation is not FilterOperator.Equal and not FilterOperator.NotEqual) continue;
            await AssertQueryPagesAsync(env, list, records, new([new(field.Id, operation, values[Math.Min(2, values.Length - 1)]?.DeepClone())], field.Id, Limit: 2));
        }
        foreach (var operation in new[] { FilterOperator.Equal, FilterOperator.NotEqual })
            await AssertQueryPagesAsync(env, list, records, new([new(field.Id, operation, null)], field.Id, Descending: true, Limit: 1));
        await AssertQueryPagesAsync(env, list, records, new([new(field.Id, FilterOperator.NotEqual, values[0]?.DeepClone()),
            new(field.Id, FilterOperator.NotEqual, values[1]?.DeepClone())], field.Id, Limit: 1));
        await Failure(ListFailure.Validation, () => env.Engine.QueryAsync(Admin, env.Workspace, list.Id,
            new([new(field.Id, FilterOperator.Equal, new JsonArray(1, 2))])));
    }

    [Fact]
    public async Task QueryBoundsAndChangedDeletedOrFilteredAnchorsMatchTheDomainContract()
    {
        var observer = new RecordCommands();
        var env = await SetupAsync(observer);
        var field = new ListField(Guid.NewGuid(), "Number", ListFieldType.Integer);
        var list = await env.Engine.CreateListAsync(Admin, env.Workspace, new(Guid.NewGuid(), "Anchors", [field]));
        var ids = new List<Guid>();
        foreach (var value in new[] { 1, 2, 3 })
        {
            var id = Guid.NewGuid(); ids.Add(id);
            await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, new(id, 1, new JsonObject { [field.Id.ToString("D")] = value }));
        }
        observer.Commands.Clear();
        var query = new ListQuery(SortFieldId: field.Id, Limit: 1);
        var first = await env.Engine.QueryAsync(Admin, env.Workspace, list.Id, query);
        Assert.Equal(ids[0], first.NextRecordId);
        var second = await env.Engine.QueryAsync(Admin, env.Workspace, list.Id, query with { AfterRecordId = first.NextRecordId });
        Assert.Equal(ids[1], Assert.Single(second.Records).Id);
        Assert.NotEmpty(observer.Commands);
        Assert.All(observer.Commands, sql => Assert.Contains("TOP(", sql.Replace(" ", "")));
        Assert.Contains(observer.Commands, sql => sql.Contains("scalar_key", StringComparison.Ordinal) && sql.Contains("ORDER BY", StringComparison.Ordinal));
        await env.Engine.UpdateRecordAsync(Admin, env.Workspace, list.Id, ids[0], new(1, 1, [new(field.Id, JsonValue.Create(4))]));
        Assert.Empty((await env.Engine.QueryAsync(Admin, env.Workspace, list.Id, query with { AfterRecordId = ids[0] })).Records);
        await Failure(ListFailure.Validation, () => env.Engine.QueryAsync(Admin, env.Workspace, list.Id,
            query with { AfterRecordId = ids[0], Filters = [new(field.Id, FilterOperator.LessThan, JsonValue.Create(4))] }));
        await env.Engine.DeleteRecordAsync(Admin, env.Workspace, list.Id, ids[0], 2);
        await Failure(ListFailure.Validation, () => env.Engine.QueryAsync(Admin, env.Workspace, list.Id, query with { AfterRecordId = ids[0] }));
        await Failure(ListFailure.Validation, () => env.Engine.QueryAsync(Admin, env.Workspace, list.Id, query with { AfterRecordId = Guid.Empty }));
        await Failure(ListFailure.Validation, () => env.Engine.QueryAsync(Admin, env.Workspace, list.Id, query with { SortFieldId = Guid.NewGuid() }));
        await Failure(ListFailure.Limit, () => env.Engine.QueryAsync(Admin, env.Workspace, list.Id, query with { Limit = 0 }));
        await Failure(ListFailure.Limit, () => env.Engine.QueryAsync(Admin, env.Workspace, list.Id, query with { Limit = 501 }));
        await Failure(ListFailure.Limit, () => new SqlServerListEngine(env.RuntimeOptions, new ListLimits { MaxFilters = 1 })
            .QueryAsync(Admin, env.Workspace, list.Id, new([new(field.Id, FilterOperator.Equal, JsonValue.Create(2)), new(field.Id, FilterOperator.Equal, JsonValue.Create(3))])));
        Assert.Null((await env.Engine.QueryAsync(Admin, env.Workspace, list.Id, new([new(field.Id, FilterOperator.Equal, JsonValue.Create(99))]))).NextRecordId);
    }

    [Fact]
    public async Task QueryPageKeepsItsAnchorStableAgainstConcurrentMutationAndCleansUpCancellation()
    {
        var env = await SetupAsync();
        var list = await env.CreateListAsync();
        var request = Request(list);
        await env.Engine.CreateRecordAsync(Admin, env.Workspace, list.Id, request);
        var pause = new PauseRecordPage();
        var query = new SqlServerListEngine(env.OptionsWith(pause)).QueryAsync(Admin, env.Workspace, list.Id, new());
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<ListRecord> mutation;
        try
        {
            var waiting = new ObserveWorkspaceLock();
            mutation = new SqlServerListEngine(env.OptionsWith(waiting)).UpdateRecordAsync(Admin, env.Workspace, list.Id, request.Id,
                new(1, 1, [new(list.Fields[0].Id, JsonValue.Create("after page"))]));
            await waiting.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(mutation.IsCompleted);
        }
        finally { pause.Release.TrySetResult(); }
        Assert.Equal("initial", Assert.Single((await query).Records).Values[list.Fields[0].Id.ToString("D")]!.GetValue<string>());
        Assert.Equal(2, (await mutation).Revision);
        using var cancellation = new CancellationTokenSource();
        var cancel = new PauseRecordPage();
        var canceled = new SqlServerListEngine(env.OptionsWith(cancel)).QueryAsync(Admin, env.Workspace, list.Id, new(), cancellation.Token);
        await cancel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Equal(0, await UnscopedCountAsync(env.Runtime));
        Assert.Single((await env.Engine.QueryAsync(Admin, env.Workspace, list.Id, new())).Records);
    }

    private sealed class RecordCommands : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [cellbridge_lists].[records]", StringComparison.Ordinal)) Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PauseRecordPage : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [cellbridge_lists].[records]", StringComparison.Ordinal) && command.CommandText.Contains("ORDER BY", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private static async Task AssertQueryPagesAsync(Environment env, ListDefinition list, IReadOnlyList<ListRecord> records, ListQuery query)
    {
        var expected = ListQueryEvaluator.Query(list, records, query with { Limit = 500, AfterRecordId = null }, new ListLimits());
        var actual = new List<Guid>();
        Guid? after = null;
        for (var count = 0; count <= records.Count; count++)
        {
            var page = await env.Engine.QueryAsync(Admin, env.Workspace, list.Id, query with { AfterRecordId = after });
            Assert.True(page.Records.Count <= query.Limit);
            actual.AddRange(page.Records.Select(row => row.Id));
            if (page.NextRecordId is null) break;
            Assert.NotEqual(after, page.NextRecordId);
            after = page.NextRecordId;
        }
        Assert.Equal(expected.Records.Select(row => row.Id), actual);
    }
}
