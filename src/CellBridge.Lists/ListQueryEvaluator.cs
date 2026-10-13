using System.Globalization;
using System.Text.Json.Nodes;

namespace CellBridge.Lists;

/// <summary>Bounded conjunctions, ordinal text comparisons, and a stable UUID tie breaker. Pages are not snapshots.</summary>
public static class ListQueryEvaluator
{
    public static RecordPage Query(ListDefinition definition, IReadOnlyList<ListRecord> records, ListQuery query, ListLimits limits)
    {
        ListValidation.ValidateQuery(definition, query, limits);
        ListValidation.Budget(records.Count <= limits.MaxRecordsPerList, "Query candidate count exceeds the configured limit.");
        var fields = definition.Fields.ToDictionary(f => f.Id);
        foreach (var record in records)
            ListValidation.Require(record.WorkspaceId == definition.WorkspaceId && record.ListId == definition.Id, "Query candidates must belong to the list.");

        var columns = new List<QueryColumn>();
        var columnIndices = new Dictionary<Guid, int>();
        int Column(Guid id)
        {
            if (columnIndices.TryGetValue(id, out var index)) return index;
            index = columns.Count;
            columns.Add(new QueryColumn(fields[id], id.ToString("D")));
            columnIndices.Add(id, index);
            return index;
        }
        var filters = (query.Filters ?? []).Select(filter => new QueryFilter(Column(filter.FieldId),
            filter.Operator, Normalize(fields[filter.FieldId], filter.Value))).ToArray();
        var sortColumn = query.SortFieldId is { } sort ? Column(sort) : -1;
        // Reuse these slots between records. A repeated predicate or the sort field
        // reads the same normalized scalar without another JSON conversion.
        var normalized = new object?[columns.Count];
        var loaded = new bool[columns.Count];
        var candidates = new List<QueryCandidate>(records.Count);
        foreach (var record in records)
        {
            Array.Clear(loaded);
            var matches = true;
            foreach (var filter in filters)
            {
                if (!loaded[filter.Column])
                {
                    var column = columns[filter.Column];
                    normalized[filter.Column] = Normalize(column.Field, record.Values[column.Key]);
                    loaded[filter.Column] = true;
                }
                if (!Matches(normalized[filter.Column], filter.Value, filter.Operator))
                {
                    matches = false;
                    break;
                }
            }
            if (!matches) continue;
            object? sortValue = null;
            if (sortColumn >= 0)
            {
                var column = columns[sortColumn];
                sortValue = loaded[sortColumn] ? normalized[sortColumn] : Normalize(column.Field, record.Values[column.Key]);
            }
            candidates.Add(new QueryCandidate(record, record.Id.ToString("D"), sortValue));
        }
        candidates.Sort((left, right) =>
        {
            var comparison = sortColumn >= 0 ? Compare(left.SortValue, right.SortValue) : 0;
            if (comparison == 0) comparison = StringComparer.Ordinal.Compare(left.CanonicalId, right.CanonicalId);
            return query.Descending ? -comparison : comparison;
        });
        var offset = 0;
        if (query.AfterRecordId is { } after)
        {
            var index = candidates.FindIndex(candidate => candidate.Record.Id == after);
            ListValidation.Require(index >= 0, "Page cursor is no longer present in this filtered list.");
            offset = index + 1;
        }
        var count = Math.Min(query.Limit, candidates.Count - offset);
        var page = new ListRecord[count];
        for (var index = 0; index < count; index++) page[index] = candidates[offset + index].Record;
        var hasMore = candidates.Count - offset > count;
        return new RecordPage(page, hasMore ? page[^1].Id : null, definition.SchemaVersion);
    }

    private static bool Matches(object? left, object? right, FilterOperator op)
    {
        // Missing and explicit null both compare as null in queries, unlike merge snapshots.
        if (op is not FilterOperator.Equal and not FilterOperator.NotEqual && (left is null || right is null)) return false;
        var comparison = Compare(left, right);
        return op switch
        {
            FilterOperator.Equal => comparison == 0,
            FilterOperator.NotEqual => comparison != 0,
            FilterOperator.GreaterThan => comparison > 0,
            FilterOperator.GreaterOrEqual => comparison >= 0,
            FilterOperator.LessThan => comparison < 0,
            FilterOperator.LessOrEqual => comparison <= 0,
            _ => false
        };
    }

    private static object? Normalize(ListField field, JsonNode? node)
    {
        if (node is null) return null;
        // Parsed JSON and common CLR-backed JsonValues expose their scalar directly.
        // Preserve the validator's conversion for other valid CLR numeric shapes.
        if (node is JsonValue value)
        {
            switch (field.Type)
            {
                case ListFieldType.Integer:
                    if (value.TryGetValue<long>(out var integer)) return integer;
                    if (value.TryGetValue<int>(out var smallInteger)) return (long)smallInteger;
                    break;
                case ListFieldType.Decimal:
                    if (value.TryGetValue<decimal>(out var number)) return number;
                    if (value.TryGetValue<long>(out var longNumber)) return (decimal)longNumber;
                    if (value.TryGetValue<int>(out var intNumber)) return (decimal)intNumber;
                    break;
                case ListFieldType.Boolean:
                    if (value.TryGetValue<bool>(out var boolean)) return boolean;
                    break;
                default:
                    if (value.TryGetValue<string>(out var text))
                        return field.Type == ListFieldType.Timestamp ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture) : text;
                    break;
            }
        }
        var scalar = ListValidation.Scalar(field, node);
        return field.Type == ListFieldType.Timestamp && scalar is string timestamp
            ? DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture) : scalar;
    }

    private static int Compare(object? left, object? right)
    {
        if (left is null) return right is null ? 0 : -1;
        if (right is null) return 1;
        if (left is string text) return StringComparer.Ordinal.Compare(text, (string)right);
        return ((IComparable)left).CompareTo(right);
    }

    private readonly record struct QueryColumn(ListField Field, string Key);
    private readonly record struct QueryFilter(int Column, FilterOperator Operator, object? Value);
    private readonly record struct QueryCandidate(ListRecord Record, string CanonicalId, object? SortValue);
}
