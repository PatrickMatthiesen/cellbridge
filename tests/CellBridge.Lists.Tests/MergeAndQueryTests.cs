using System.Text.Json.Nodes;
using CellBridge.Lists;
using static CellBridge.Lists.Tests.ValidationTests;

namespace CellBridge.Lists.Tests;

public class MergeAndQueryTests
{
    private static ListRecord Record(int id, JsonObject values, long revision = 1) => new(
        Guid.Parse($"{id:x8}-0000-0000-0000-000000000000"), Workspace, List, 1, revision, id, values,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "issuer:editor", "issuer:editor");

    [Fact]
    public void Alice_title_edit_reveals_and_preserves_Bobs_deadline_edit()
    {
        var other = Guid.NewGuid();
        var definition = Definition() with { Fields = [Definition().Fields[0] with { Name = "Title" }, new ListField(other, "Deadline", ListFieldType.Date)] };
        var original = Values("\"old\""); original[other.ToString("D")] = "2026-10-01";
        var now = Values("\"old\""); now[other.ToString("D")] = "2026-11-01";
        var preview = ListMerge.Preview(definition, Record(1, now, 5), new MergeRequest(1, original, [new(Field, JsonValue.Create("proposed"))]), new ListLimits());
        Assert.Equal(5, preview.CurrentRevision);
        Assert.Equal(2, preview.Fields.Count);
        Assert.All(preview.Fields, field => Assert.False(field.Conflict));
        var concurrentField = Assert.Single(preview.Fields, field => field.FieldId == other);
        Assert.Equal("2026-10-01", concurrentField.OriginalValue!.GetValue<string>());
        Assert.Equal("2026-11-01", concurrentField.CurrentValue!.GetValue<string>());
        Assert.Equal("2026-10-01", concurrentField.ProposedValue!.GetValue<string>());
        Assert.Single(preview.SuggestedChanges);
        var merged = ListValidation.ApplyChanges(definition, now, preview.SuggestedChanges, new ListLimits());
        Assert.Equal("2026-11-01", merged[other.ToString("D")]!.GetValue<string>());
        Assert.Equal("proposed", merged[Field.ToString("D")]!.GetValue<string>());
        Assert.Equal("old", now[Field.ToString("D")]!.GetValue<string>());
    }

    [Theory]
    [InlineData("\"original\"", "\"concurrent\"", "\"proposed\"", true, 0)]
    [InlineData("\"original\"", "\"proposed\"", "\"proposed\"", false, 0)]
    [InlineData("\"original\"", "\"concurrent\"", "\"original\"", false, 0)]
    [InlineData("\"original\"", "\"original\"", "null", false, 1)]
    public void Merge_detects_conflicts_and_does_not_revert_unchanged_proposals(string original, string current, string proposed, bool conflict, int suggested)
    {
        var preview = ListMerge.Preview(Definition(), Record(1, Values(current)), new(1, Values(original), [new(Field, JsonNode.Parse(proposed))]), new ListLimits());
        Assert.Equal(conflict, Assert.Single(preview.Fields).Conflict);
        Assert.Equal(suggested, preview.SuggestedChanges.Count);
    }

    [Fact]
    public void Merge_distinguishes_missing_null_and_removal()
    {
        var missingToNull = ListMerge.Preview(Definition(), Record(1, Values("null")), new(1, new(), [new(Field, JsonValue.Create("value"))]), new ListLimits());
        var result = Assert.Single(missingToNull.Fields);
        Assert.False(result.OriginalPresent); Assert.True(result.CurrentPresent); Assert.Null(result.CurrentValue); Assert.True(result.Conflict);
        var removal = ListMerge.Preview(Definition(), Record(1, Values("null")), new(1, Values("null"), [new(Field, Remove: true)]), new ListLimits());
        Assert.False(Assert.Single(removal.Fields).ProposedPresent);
        Assert.True(Assert.Single(removal.SuggestedChanges).Remove);
        Reject(() => ListMerge.Preview(Definition(), Record(1, new()), new(2, new(), []), new ListLimits()), ListFailure.Conflict);
    }

    [Fact]
    public void Decimal_semantics_match_in_merge_and_query()
    {
        var definition = Definition(ListFieldType.Decimal);
        var preview = ListMerge.Preview(definition, Record(1, Values("1.00")), new(1, Values("1"), [new(Field, JsonNode.Parse("1e0"))]), new ListLimits());
        Assert.False(Assert.Single(preview.Fields).Conflict);
        Assert.Empty(preview.SuggestedChanges);
        var result = ListQueryEvaluator.Query(definition, [Record(1, Values("1.00")), Record(2, Values("2"))], new(Filters: [new(Field, FilterOperator.Equal, JsonNode.Parse("1e0"))]), new ListLimits());
        Assert.Equal(1, Assert.Single(result.Records).Number);
    }

    [Fact]
    public void Query_orders_null_then_ordinal_text_and_uses_id_tiebreaker()
    {
        var records = new[] { Record(4, Values("\"a\"")), Record(3, Values("\"A\"")), Record(2, Values("null")), Record(1, new()), Record(5, Values("\"a\"")) };
        var page = ListQueryEvaluator.Query(Definition(), records, new(SortFieldId: Field, Limit: 2), new ListLimits());
        Assert.Equal(new long[] { 1, 2 }, page.Records.Select(r => r.Number));
        Assert.Equal(records[2].Id, page.NextRecordId);
        var second = ListQueryEvaluator.Query(Definition(), records, new(SortFieldId: Field, AfterRecordId: page.NextRecordId, Limit: 2), new ListLimits());
        Assert.Equal(new long[] { 3, 4 }, second.Records.Select(r => r.Number));
        var third = ListQueryEvaluator.Query(Definition(), records, new(SortFieldId: Field, AfterRecordId: second.NextRecordId, Limit: 2), new ListLimits());
        Assert.Equal(5, Assert.Single(third.Records).Number); Assert.Null(third.NextRecordId);
        var descending = ListQueryEvaluator.Query(Definition(), records, new(SortFieldId: Field, Descending: true), new ListLimits());
        Assert.Equal(new long[] { 5, 4, 3, 2, 1 }, descending.Records.Select(r => r.Number));
    }

    [Fact]
    public void Query_conjoins_comparisons_without_null_relational_matches()
    {
        var records = new[] { Record(1, Values("null")), Record(2, Values("-10")), Record(3, Values("2")), Record(4, Values("20")) };
        var query = new ListQuery(Filters: [new(Field, FilterOperator.GreaterOrEqual, JsonNode.Parse("2")), new(Field, FilterOperator.LessThan, JsonNode.Parse("20"))]);
        Assert.Equal(3, Assert.Single(ListQueryEvaluator.Query(Definition(ListFieldType.Integer), records, query, new ListLimits()).Records).Number);
        var nulls = ListQueryEvaluator.Query(Definition(ListFieldType.Integer), records, new(Filters: [new(Field, FilterOperator.Equal, null)]), new ListLimits());
        Assert.Equal(1, Assert.Single(nulls.Records).Number);
    }

    [Fact]
    public void Timestamps_sort_chronologically_even_when_fractional_string_order_differs()
    {
        var result = ListQueryEvaluator.Query(Definition(ListFieldType.Timestamp), [Record(1, Values("\"2025-01-01T00:00:00.1Z\"")), Record(2, Values("\"2025-01-01T00:00:00Z\""))], new(SortFieldId: Field), new ListLimits());
        Assert.Equal(new long[] { 2, 1 }, result.Records.Select(r => r.Number));
    }

    [Fact]
    public void Query_rejects_out_of_budget_operators_unknown_fields_and_invalid_cursors()
    {
        Reject(() => ListValidation.ValidateQuery(Definition(), new(Limit: 501), new ListLimits()), ListFailure.Limit);
        Reject(() => ListValidation.ValidateQuery(Definition(), new(Filters: Enumerable.Repeat(new RecordFilter(Field, FilterOperator.Equal, null), 13).ToArray()), new ListLimits()), ListFailure.Limit);
        Reject(() => ListValidation.ValidateQuery(Definition(ListFieldType.Boolean), new(Filters: [new(Field, FilterOperator.GreaterThan, JsonValue.Create(true))]), new ListLimits()));
        Reject(() => ListValidation.ValidateQuery(Definition(), new(SortFieldId: Guid.NewGuid()), new ListLimits()));
        Reject(() => ListValidation.ValidateQuery(Definition(), new(Filters: [new(Field, (FilterOperator)99, null)]), new ListLimits()));
        Reject(() => ListQueryEvaluator.Query(Definition(), [Record(1, new())], new(AfterRecordId: Guid.NewGuid()), new ListLimits()));
        Reject(() => ListQueryEvaluator.Query(Definition(), [Record(1, new()) with { WorkspaceId = Guid.NewGuid() }], new(), new ListLimits()));
        Reject(() => ListQueryEvaluator.Query(Definition(), [Record(1, new())], new(), new ListLimits { MaxRecordsPerList = 0 }), ListFailure.Limit);
    }
}
