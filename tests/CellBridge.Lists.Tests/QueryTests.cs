using CellBridge.Lists;
using System.Text.Json.Nodes;
using static CellBridge.Lists.Tests.ValidationTests;

namespace CellBridge.Lists.Tests;

public class QueryTests
{
    private static ListRecord Record(int number, JsonObject values) => new(
        Guid.Parse($"{number:x8}-0000-0000-0000-000000000000"), Workspace, List, 1, 1, number, values,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "issuer:editor", "issuer:editor");

    [Fact]
    public void Repeated_predicates_and_sort_normalize_parsed_and_clr_integer_values_equally()
    {
        var records = new[]
        {
            Record(1, new() { [Field.ToString("D")] = JsonValue.Create(9) }),
            Record(2, new() { [Field.ToString("D")] = JsonValue.Create(10L) }),
            Record(3, Values("10")),
            Record(4, new() { [Field.ToString("D")] = JsonValue.Create((short)11) }),
            Record(5, Values("null")), Record(6, new())
        };
        var query = new ListQuery(Filters:
            [new(Field, FilterOperator.GreaterOrEqual, JsonValue.Create(10)),
             new(Field, FilterOperator.LessThan, JsonNode.Parse("12"))], SortFieldId: Field, Descending: true, Limit: 2);
        var first = ListQueryEvaluator.Query(Definition(ListFieldType.Integer), records, query, new ListLimits());
        Assert.Equal(new long[] { 4, 3 }, first.Records.Select(record => record.Number));
        var second = ListQueryEvaluator.Query(Definition(ListFieldType.Integer), records,
            query with { AfterRecordId = first.NextRecordId }, new ListLimits());
        Assert.Equal(2, Assert.Single(second.Records).Number);
        Assert.Null(second.NextRecordId);
        Assert.Equal((short)11, records[3].Values[Field.ToString("D")]!.GetValue<short>());
    }

    [Fact]
    public void Decimal_sort_uses_numeric_comparison_across_integer_decimal_and_json_values()
    {
        var records = new[]
        {
            Record(1, new() { [Field.ToString("D")] = JsonValue.Create(2) }),
            Record(2, new() { [Field.ToString("D")] = JsonValue.Create(10L) }),
            Record(3, new() { [Field.ToString("D")] = JsonValue.Create(2.50m) }),
            Record(4, Values("2.5")), Record(5, Values("null"))
        };
        var query = new ListQuery(Filters: [new(Field, FilterOperator.LessThan, JsonValue.Create(10m))], SortFieldId: Field);
        var page = ListQueryEvaluator.Query(Definition(ListFieldType.Decimal), records, query, new ListLimits());
        Assert.Equal(new long[] { 1, 3, 4 }, page.Records.Select(record => record.Number));
    }

    [Fact]
    public void Timestamp_filter_and_sort_share_chronological_fraction_semantics()
    {
        var records = new[]
        {
            Record(1, Values("\"2026-01-01T00:00:00.1Z\"")),
            Record(2, new() { [Field.ToString("D")] = "2026-01-01T00:00:00Z" }),
            Record(3, Values("\"2026-01-01T00:00:00.2Z\""))
        };
        var query = new ListQuery(Filters: [new(Field, FilterOperator.GreaterOrEqual, JsonValue.Create("2026-01-01T00:00:00.1Z"))], SortFieldId: Field);
        Assert.Equal(new long[] { 1, 3 }, ListQueryEvaluator.Query(Definition(ListFieldType.Timestamp), records, query, new ListLimits()).Records.Select(record => record.Number));
    }

    [Fact]
    public void Default_uuid_order_and_empty_results_have_no_cursor()
    {
        var records = new[] { Record(3, Values("\"C\"")), Record(1, new()), Record(2, Values("null")) };
        Assert.Equal(new long[] { 1, 2, 3 }, ListQueryEvaluator.Query(Definition(), records, new(), new ListLimits()).Records.Select(record => record.Number));
        var page = ListQueryEvaluator.Query(Definition(), records,
            new(Filters: [new(Field, FilterOperator.Equal, JsonValue.Create("absent"))]), new ListLimits());
        Assert.Empty(page.Records);
        Assert.Null(page.NextRecordId);
    }
}
