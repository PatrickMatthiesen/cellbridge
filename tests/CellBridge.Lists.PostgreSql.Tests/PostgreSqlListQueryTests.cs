using CellBridge.Lists;
using Npgsql;
using System.Text.Json.Nodes;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class PostgreSqlListQueryTests(ListDatabaseFixture database)
{
    [PostgreSqlFact]
    public async Task SqlSortAndPaginationMatchCoreForEveryTypeNullAndUnicode()
    {
        var (definition, records) = await TypedRecordsAsync();
        foreach (var field in definition.Fields)
        foreach (var descending in new[] { false, true })
            await AssertPagesAsync(definition, records, new(SortFieldId: field.Id, Descending: descending, Limit: 2));
        await AssertPagesAsync(definition, records, new(Limit: 3));
        await AssertPagesAsync(definition, records, new(Descending: true, Limit: 3));
    }

    [PostgreSqlFact]
    public async Task SqlFiltersMatchCoreForNullNumericPrecisionOrdinalTextAndConjunctions()
    {
        var (definition, records) = await TypedRecordsAsync();
        foreach (var field in definition.Fields)
        {
            foreach (var op in new[] { FilterOperator.Equal, FilterOperator.NotEqual })
                await AssertPagesAsync(definition, records, new(Filters: [new(field.Id, op, null)], SortFieldId: field.Id, Limit: 2));
            var operators = field.Type is ListFieldType.Boolean or ListFieldType.Choice or ListFieldType.Reference
                ? new[] { FilterOperator.Equal, FilterOperator.NotEqual }
                : Enum.GetValues<FilterOperator>();
            foreach (var op in operators)
            foreach (var descending in new[] { false, true })
            {
                var literal = records[3].Values[field.Id.ToString("D")]!.DeepClone();
                await AssertPagesAsync(definition, records, new(Filters: [new(field.Id, op, literal)], SortFieldId: field.Id, Descending: descending, Limit: 2));
            }
        }
        var integer = definition.Fields.Single(field => field.Type == ListFieldType.Integer);
        var text = definition.Fields.Single(field => field.Type == ListFieldType.Text);
        await AssertPagesAsync(definition, records, new(Filters:
            [new(integer.Id, FilterOperator.GreaterOrEqual, JsonValue.Create(0L)),
             new(integer.Id, FilterOperator.LessThan, JsonValue.Create(long.MaxValue)),
             new(text.Id, FilterOperator.NotEqual, JsonValue.Create("x' OR true --"))],
            SortFieldId: text.Id, Descending: true, Limit: 2));
    }

    [PostgreSqlFact]
    public async Task SqlCursorRejectsMissingDeletedForeignAndNonmatchingAnchors()
    {
        var (definition, records) = await TypedRecordsAsync();
        var integer = definition.Fields.Single(field => field.Type == ListFieldType.Integer);
        await RejectCursorAsync(definition, new(AfterRecordId: Guid.NewGuid()));
        await RejectCursorAsync(definition, new(Filters: [new(integer.Id, FilterOperator.GreaterThan, JsonValue.Create(0L))],
            SortFieldId: integer.Id, AfterRecordId: records[2].Id));
        var otherWorkspace = await database.NewWorkspaceAsync("cursor isolation");
        var other = await database.Engine.CreateListAsync(database.AdminActor, otherWorkspace,
            new(Guid.NewGuid(), "Other", [new(Guid.NewGuid(), "Text", ListFieldType.Text)]));
        var foreign = await database.Engine.CreateRecordAsync(database.AdminActor, otherWorkspace, other.Id, new(Guid.NewGuid(), 1, new()));
        await RejectCursorAsync(definition, new(AfterRecordId: foreign.Id));
        await database.Engine.DeleteRecordAsync(database.AdminActor, definition.WorkspaceId, definition.Id, records[3].Id, records[3].Revision);
        await RejectCursorAsync(definition, new(SortFieldId: integer.Id, AfterRecordId: records[3].Id));
        // An edit which removes the anchor from this filter invalidates continuation.
        var cursor = records[4];
        await database.Engine.UpdateRecordAsync(database.AdminActor, definition.WorkspaceId, definition.Id, cursor.Id,
            new(cursor.Revision, 1, [new(integer.Id, JsonValue.Create(-1L))]));
        await RejectCursorAsync(definition, new(Filters: [new(integer.Id, FilterOperator.GreaterOrEqual, JsonValue.Create(0L))],
            SortFieldId: integer.Id, AfterRecordId: cursor.Id));
    }

    [PostgreSqlFact]
    public async Task SqlQueriesAtOneHundredThousandRecordsMaterializeOnlyTheRequestedPage()
    {
        var workspace = await database.NewWorkspaceAsync("large SQL query");
        var integer = new ListField(Guid.NewGuid(), "Integer", ListFieldType.Integer);
        var definition = await database.Engine.CreateListAsync(database.AdminActor, workspace, new(Guid.NewGuid(), "Large", [integer]));
        await database.SetWorkspaceContextAsync(workspace, async (connection, transaction) =>
        {
            // Seed directly to isolate SQL query behavior from create throughput and quotas.
            await using var insert = new NpgsqlCommand("""
                INSERT INTO cellbridge_lists.records(workspace_id,list_id,record_id,schema_version,revision,number,
                  values_json,created_utc,modified_utc,created_by,modified_by)
                SELECT $1,$2,(lpad(to_hex(n),8,'0')||'-0000-0000-0000-000000000000')::uuid,1,1,n,
                  jsonb_build_object($3::text,n),now(),now(),'test:seed','test:seed'
                FROM generate_series(1,100000) n
                """, connection, transaction);
            insert.Parameters.AddWithValue(workspace);
            insert.Parameters.AddWithValue(definition.Id);
            insert.Parameters.AddWithValue(integer.Id.ToString("D"));
            Assert.Equal(100000, await insert.ExecuteNonQueryAsync());
        });
        var query = new ListQuery(Filters: [new(integer.Id, FilterOperator.GreaterOrEqual, JsonValue.Create(99990L))],
            SortFieldId: integer.Id, Descending: true, Limit: 3);
        var first = await database.Engine.QueryAsync(database.AdminActor, workspace, definition.Id, query);
        Assert.Equal(new long[] { 100000, 99999, 99998 }, first.Records.Select(record => record.Number));
        Assert.Equal(first.Records[^1].Id, first.NextRecordId);
        var second = await database.Engine.QueryAsync(database.AdminActor, workspace, definition.Id,
            query with { AfterRecordId = first.NextRecordId });
        Assert.Equal(new long[] { 99997, 99996, 99995 }, second.Records.Select(record => record.Number));
        // Poison a non-page object shape. Whole-list host materialization would fail;
        // an unrelated record cannot affect the bounded UUID page.
        await database.SetWorkspaceContextAsync(workspace, async (connection, transaction) =>
        {
            await using var poison = new NpgsqlCommand("UPDATE cellbridge_lists.records SET values_json='[]'::jsonb WHERE workspace_id=$1 AND list_id=$2 AND number=100000", connection, transaction);
            poison.Parameters.AddWithValue(workspace);
            poison.Parameters.AddWithValue(definition.Id);
            Assert.Equal(1, await poison.ExecuteNonQueryAsync());
        });
        var small = await database.Engine.QueryAsync(database.AdminActor, workspace, definition.Id, new(Limit: 2));
        Assert.Equal(new long[] { 1, 2 }, small.Records.Select(record => record.Number));
        Assert.Equal(small.Records[^1].Id, small.NextRecordId);
    }

    private async Task AssertPagesAsync(ListDefinition definition, IReadOnlyList<ListRecord> records, ListQuery query)
    {
        var limits = new ListLimits();
        var visited = new List<Guid>();
        for (var page = 0; page <= records.Count; page++)
        {
            var expected = ListQueryEvaluator.Query(definition, records, query, limits);
            var actual = await database.Engine.QueryAsync(database.AdminActor, definition.WorkspaceId, definition.Id, query);
            Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
            Assert.Equal(expected.Records.Select(record => record.Id), actual.Records.Select(record => record.Id));
            Assert.Equal(expected.NextRecordId, actual.NextRecordId);
            Assert.InRange(actual.Records.Count, 0, query.Limit);
            visited.AddRange(actual.Records.Select(record => record.Id));
            if (actual.NextRecordId is null) { Assert.Equal(visited.Count, visited.Distinct().Count()); return; }
            query = query with { AfterRecordId = actual.NextRecordId };
        }
        Assert.Fail("Pagination did not terminate.");
    }

    private async Task RejectCursorAsync(ListDefinition definition, ListQuery query)
    {
        var error = await Assert.ThrowsAsync<ListException>(() => database.Engine.QueryAsync(database.AdminActor,
            definition.WorkspaceId, definition.Id, query));
        Assert.Equal(ListFailure.Validation, error.Failure);
    }

    private async Task<(ListDefinition Definition, IReadOnlyList<ListRecord> Records)> TypedRecordsAsync()
    {
        var workspace = await database.NewWorkspaceAsync("query parity");
        var target = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new(Guid.NewGuid(), "Targets", [new(Guid.NewGuid(), "Name", ListFieldType.Text)]));
        var targetIds = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            var id = Guid.NewGuid();
            await database.Engine.CreateRecordAsync(database.AdminActor, workspace, target.Id, new(id, 1, new()));
            targetIds.Add(id);
        }
        var fields = Enum.GetValues<ListFieldType>().Select(type => new ListField(Guid.NewGuid(), type.ToString(), type,
            Choices: type == ListFieldType.Choice ? [new("a😀", "First"), new("a\uE000", "Last")] : null,
            ReferenceListId: type == ListFieldType.Reference ? target.Id : null)).ToArray();
        var definition = await database.Engine.CreateListAsync(database.AdminActor, workspace, new(Guid.NewGuid(), "Types", fields));
        string[] texts = ["", "a😀", "a\uE000", "x' OR true --", "a😀x"];
        long[] integers = [long.MinValue, -1, 0, 1, long.MaxValue];
        decimal[] decimals = [-9999999999999999999999999999m, -0.000000000001m, 0m, 0.000000000001m, 9999999999999999999999999999m];
        string[] dates = ["0001-01-01", "1900-02-28", "2024-02-29", "2026-01-01", "9999-12-31"];
        string[] timestamps = ["0001-01-01T00:00:00Z", "2026-01-01T00:00:00Z", "2026-01-01T00:00:00.0000001Z", "2026-01-01T00:00:00.0000002Z", "9999-12-31T23:59:59.9999999Z"];
        var records = new List<ListRecord>();
        for (var index = -2; index < 5; index++)
        {
            var values = new JsonObject();
            foreach (var field in fields)
            {
                if (index == -2) continue;
                if (index == -1) { values[field.Id.ToString("D")] = null; continue; }
                values[field.Id.ToString("D")] = field.Type switch
                {
                    ListFieldType.Text => JsonValue.Create(texts[index]),
                    ListFieldType.Integer => JsonValue.Create(integers[index]),
                    ListFieldType.Decimal => JsonValue.Create(decimals[index]),
                    ListFieldType.Boolean => JsonValue.Create(index % 2 == 0),
                    ListFieldType.Date => JsonValue.Create(dates[index]),
                    ListFieldType.Timestamp => JsonValue.Create(timestamps[index]),
                    ListFieldType.Choice => JsonValue.Create(index % 2 == 0 ? "a😀" : "a\uE000"),
                    ListFieldType.Reference => JsonValue.Create(targetIds[index].ToString("D")),
                    _ => throw new InvalidOperationException()
                };
            }
            var id = Guid.Parse($"{index + 3:x8}-0000-0000-0000-000000000000");
            await database.Engine.CreateRecordAsync(database.AdminActor, workspace, definition.Id, new(id, 1, values));
            records.Add(await database.Engine.GetRecordAsync(database.AdminActor, workspace, definition.Id, id));
        }
        return (definition, records);
    }
}
