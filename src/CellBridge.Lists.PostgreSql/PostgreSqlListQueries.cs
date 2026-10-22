using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Lists.PostgreSql;

public sealed partial class PostgreSqlListEngine
{
    /// <summary>Filters, exact typed ordering and keyset paging execute in PostgreSQL. Only a page is materialized.</summary>
    public Task<RecordPage> QueryAsync(ListActor actor, Guid workspaceId, Guid listId, ListQuery query,
        CancellationToken ct = default)
    {
        ValidateActor(actor);
        return QueryAuthorizedAsync();

        async Task<RecordPage> QueryAuthorizedAsync()
        {
            var authorization = await AuthorizeOperationAsync(actor, workspaceId,
                [(listId, ListAuthorizationAction.Read)], ct);
            return await InTransactionAsync(workspaceId, false, authorization, async (db, connection, transaction) =>
            {
                var context = await ReadWriteContextAsync(db, connection, transaction, workspaceId, listId,
                    Guid.Empty, actor.Subject, ListAccess.Read, deleteLock: false, ct, authorization);
                RequireWriteContext(context, ListAccess.Read);
                var definition = context.Definition!;
                ListValidation.ValidateQuery(definition, query, _limits);
                IQueryable<RecordRow> records = db.Records.AsNoTracking()
                    .Where(row => row.WorkspaceId == workspaceId && row.ListId == listId);
                var fields = definition.Fields.ToDictionary(field => field.Id);
                foreach (var filter in query.Filters ?? [])
                    records = ApplyFilter(records, fields[filter.FieldId], filter);

                var sortField = query.SortFieldId is { } selected ? fields[selected] : null;
                IQueryable<RecordRow?> page;
                if (query.AfterRecordId is { } cursor)
                {
                    var anchor = records.Where(row => row.RecordId == cursor);
                    page = sortField is null
                        ? PageAfterRecordId(records, anchor, query.Descending, query.Limit + 1)
                        : PageAfterSort(records, anchor, sortField, query.Descending, query.Limit + 1);
                }
                else
                {
                    var ordered = sortField is null
                        ? query.Descending
                            ? records.OrderByDescending(row => row.RecordId)
                            : records.OrderBy(row => row.RecordId)
                        : ApplyOrder(records, sortField, query.Descending);
                    page = ordered.Take(query.Limit + 1);
                }

                var materialized = query.AfterRecordId is null
                    ? await page.ToListAsync(ct)
                    : await ApplyPageOrder(page, sortField, query.Descending).ToListAsync(ct);
                if (query.AfterRecordId is not null && materialized.Count == 0)
                    throw Validation("Page cursor is no longer present in this filtered list.");
                var rows = materialized.OfType<RecordRow>().ToList();
                var hasMore = rows.Count > query.Limit;
                if (hasMore) rows.RemoveAt(rows.Count - 1);
                var result = rows.Select(ToListRecord).ToArray();
                return new RecordPage(result, hasMore ? result[^1].Id : null, definition.SchemaVersion);
            }, ct);
        }
    }

    private static IQueryable<RecordRow> ApplyFilter(IQueryable<RecordRow> query, ListField field,
        RecordFilter filter)
    {
        var key = field.Id.ToString("D");
        if (filter.Value is null)
            return filter.Operator == FilterOperator.NotEqual
                ? query.Where(row => PostgreSqlListDbContext.JsonText(row.ValuesJson, key) != null)
                : query.Where(row => PostgreSqlListDbContext.JsonText(row.ValuesJson, key) == null);

        using var document = JsonDocument.Parse(filter.Value.ToJsonString());
        var json = document.RootElement;
        var stringValue = json.ValueKind == JsonValueKind.String ? json.GetString() : null;
        return field.Type switch
        {
            ListFieldType.Integer => Compare(query,
                row => (long?)Convert.ToInt64(PostgreSqlListDbContext.JsonText(row.ValuesJson, key)),
                filter.Operator, (long?)json.GetInt64()),
            ListFieldType.Decimal => Compare(query,
                row => (decimal?)Convert.ToDecimal(PostgreSqlListDbContext.JsonText(row.ValuesJson, key)),
                filter.Operator, (decimal?)json.GetDecimal()),
            ListFieldType.Boolean when filter.Operator is FilterOperator.Equal or FilterOperator.NotEqual =>
                Compare(query,
                    row => (bool?)Convert.ToBoolean(PostgreSqlListDbContext.JsonText(row.ValuesJson, key)),
                    filter.Operator, (bool?)json.GetBoolean()),
            ListFieldType.Text or ListFieldType.Choice when filter.Operator is not FilterOperator.Equal and not FilterOperator.NotEqual =>
                Compare(query, row => PostgreSqlListDbContext.Utf16Compare(
                    PostgreSqlListDbContext.JsonText(row.ValuesJson, key), stringValue), filter.Operator, 0),
            ListFieldType.Timestamp when filter.Operator is not FilterOperator.Equal and not FilterOperator.NotEqual =>
                Compare(query, row => PostgreSqlListDbContext.TimestampCompare(
                    PostgreSqlListDbContext.JsonText(row.ValuesJson, key), stringValue), filter.Operator, 0),
            ListFieldType.Timestamp => Compare(query, row => EF.Functions.Collate(
                    PostgreSqlListDbContext.TimestampKey(
                        PostgreSqlListDbContext.JsonText(row.ValuesJson, key))!, "C"),
                filter.Operator, QueryTimestampKey(stringValue!)),
            _ when filter.Operator is FilterOperator.Equal or FilterOperator.NotEqual =>
                Compare(query, row => EF.Functions.Collate(
                    PostgreSqlListDbContext.JsonText(row.ValuesJson, key)!, "C"), filter.Operator,
                    stringValue),
            _ => Compare(query, row => PostgreSqlListDbContext.CCompare(
                PostgreSqlListDbContext.JsonText(row.ValuesJson, key), stringValue), filter.Operator, 0)
        };
    }

    private static IQueryable<RecordRow> ApplyOrder(IQueryable<RecordRow> query, ListField field, bool descending)
    {
        var key = field.Id.ToString("D");
        return field.Type switch
        {
            ListFieldType.Integer => Order(query,
                row => (long?)Convert.ToInt64(PostgreSqlListDbContext.JsonText(row.ValuesJson, key)), descending),
            ListFieldType.Decimal => Order(query,
                row => (decimal?)Convert.ToDecimal(PostgreSqlListDbContext.JsonText(row.ValuesJson, key)), descending),
            ListFieldType.Boolean => Order(query,
                row => (bool?)Convert.ToBoolean(PostgreSqlListDbContext.JsonText(row.ValuesJson, key)), descending),
            ListFieldType.Text or ListFieldType.Choice => Order(query,
                row => PostgreSqlListDbContext.Utf16Key(
                    PostgreSqlListDbContext.JsonText(row.ValuesJson, key)), descending),
            ListFieldType.Timestamp => Order(query,
                row => EF.Functions.Collate(PostgreSqlListDbContext.TimestampKey(
                    PostgreSqlListDbContext.JsonText(row.ValuesJson, key))!, "C"), descending),
            _ => Order(query, row => EF.Functions.Collate(
                PostgreSqlListDbContext.JsonText(row.ValuesJson, key)!, "C"), descending)
        };
    }

    private static IQueryable<RecordRow?> PageAfterRecordId(IQueryable<RecordRow> query,
        IQueryable<RecordRow> anchor, bool descending, int limit) => descending
        ? anchor.SelectMany(anchorRow => query
                .Where(row => row.RecordId.CompareTo(anchorRow.RecordId) < 0)
                .OrderByDescending(row => row.RecordId).Take(limit).DefaultIfEmpty(),
            (_, row) => row)
        : anchor.SelectMany(anchorRow => query
                .Where(row => row.RecordId.CompareTo(anchorRow.RecordId) > 0)
                .OrderBy(row => row.RecordId).Take(limit).DefaultIfEmpty(),
            (_, row) => row);

    private static IQueryable<RecordRow?> PageAfterSort(IQueryable<RecordRow> query,
        IQueryable<RecordRow> anchor, ListField field, bool descending, int limit)
    {
        var key = field.Id.ToString("D");
        return field.Type switch
        {
            ListFieldType.Integer => PageAfterLong(query, anchor, key, descending, limit),
            ListFieldType.Decimal => PageAfterDecimal(query, anchor, key, descending, limit),
            ListFieldType.Boolean => PageAfterBoolean(query, anchor, key, descending, limit),
            ListFieldType.Text or ListFieldType.Choice => PageAfterUtf16(query, anchor, key, descending, limit),
            ListFieldType.Timestamp => PageAfterTimestamp(query, anchor, key, descending, limit),
            _ => PageAfterOrdinal(query, anchor, key, descending, limit)
        };
    }

    private static IQueryable<RecordRow?> PageAfterLong(IQueryable<RecordRow> query,
        IQueryable<RecordRow> anchor, string key, bool descending, int limit)
    {
        var candidates = query.Select(row => new LongSortRow
        {
            Row = row,
            Value = Convert.ToInt64(PostgreSqlListDbContext.JsonText(row.ValuesJson, key))
        });
        var anchors = anchor.Select(row => new LongSortRow
        {
            Row = row,
            Value = Convert.ToInt64(PostgreSqlListDbContext.JsonText(row.ValuesJson, key))
        });
        return descending
            ? anchors.SelectMany(anchorRow => candidates.Where(candidate =>
                        candidate.Value == null && anchorRow.Value != null ||
                        candidate.Value < anchorRow.Value ||
                        candidate.Value == anchorRow.Value &&
                        candidate.Row.RecordId.CompareTo(anchorRow.Row.RecordId) < 0)
                    .OrderBy(candidate => candidate.Value == null).ThenByDescending(candidate => candidate.Value)
                    .ThenByDescending(candidate => candidate.Row.RecordId)
                    .Take(limit).DefaultIfEmpty(), (_, candidate) => candidate == null ? null : candidate.Row)
            : anchors.SelectMany(anchorRow => candidates.Where(candidate =>
                        candidate.Value != null && anchorRow.Value == null ||
                        candidate.Value > anchorRow.Value ||
                        candidate.Value == anchorRow.Value &&
                        candidate.Row.RecordId.CompareTo(anchorRow.Row.RecordId) > 0)
                    .OrderByDescending(candidate => candidate.Value == null).ThenBy(candidate => candidate.Value)
                    .ThenBy(candidate => candidate.Row.RecordId)
                    .Take(limit).DefaultIfEmpty(), (_, candidate) => candidate == null ? null : candidate.Row);
    }

    private static IQueryable<RecordRow?> PageAfterDecimal(IQueryable<RecordRow> query,
        IQueryable<RecordRow> anchor, string key, bool descending, int limit)
    {
        var candidates = query.Select(row => new DecimalSortRow
        {
            Row = row,
            Value = Convert.ToDecimal(PostgreSqlListDbContext.JsonText(row.ValuesJson, key))
        });
        var anchors = anchor.Select(row => new DecimalSortRow
        {
            Row = row,
            Value = Convert.ToDecimal(PostgreSqlListDbContext.JsonText(row.ValuesJson, key))
        });
        return descending
            ? anchors.SelectMany(anchorRow => candidates.Where(candidate =>
                        candidate.Value == null && anchorRow.Value != null ||
                        candidate.Value < anchorRow.Value ||
                        candidate.Value == anchorRow.Value &&
                        candidate.Row.RecordId.CompareTo(anchorRow.Row.RecordId) < 0)
                    .OrderBy(candidate => candidate.Value == null).ThenByDescending(candidate => candidate.Value)
                    .ThenByDescending(candidate => candidate.Row.RecordId)
                    .Take(limit).DefaultIfEmpty(), (_, candidate) => candidate == null ? null : candidate.Row)
            : anchors.SelectMany(anchorRow => candidates.Where(candidate =>
                        candidate.Value != null && anchorRow.Value == null ||
                        candidate.Value > anchorRow.Value ||
                        candidate.Value == anchorRow.Value &&
                        candidate.Row.RecordId.CompareTo(anchorRow.Row.RecordId) > 0)
                    .OrderByDescending(candidate => candidate.Value == null).ThenBy(candidate => candidate.Value)
                    .ThenBy(candidate => candidate.Row.RecordId)
                    .Take(limit).DefaultIfEmpty(), (_, candidate) => candidate == null ? null : candidate.Row);
    }

    private static IQueryable<RecordRow?> PageAfterBoolean(IQueryable<RecordRow> query,
        IQueryable<RecordRow> anchor, string key, bool descending, int limit)
    {
        var candidates = query.Select(row => new BooleanSortRow
        {
            Row = row,
            Raw = PostgreSqlListDbContext.JsonText(row.ValuesJson, key),
            Value = Convert.ToBoolean(PostgreSqlListDbContext.JsonText(row.ValuesJson, key))
        });
        var anchors = anchor.Select(row => new BooleanSortRow
        {
            Row = row,
            Raw = PostgreSqlListDbContext.JsonText(row.ValuesJson, key),
            Value = Convert.ToBoolean(PostgreSqlListDbContext.JsonText(row.ValuesJson, key))
        });
        return descending
            ? anchors.SelectMany(anchorRow => candidates.Where(candidate =>
                        candidate.Raw == null && anchorRow.Raw != null ||
                        PostgreSqlListDbContext.CCompare(candidate.Raw, anchorRow.Raw) < 0 ||
                        (candidate.Raw == null && anchorRow.Raw == null ||
                         candidate.Raw != null && anchorRow.Raw != null &&
                         PostgreSqlListDbContext.CCompare(candidate.Raw, anchorRow.Raw) == 0) &&
                        candidate.Row.RecordId.CompareTo(anchorRow.Row.RecordId) < 0)
                    .OrderBy(candidate => candidate.Value == null).ThenByDescending(candidate => candidate.Value)
                    .ThenByDescending(candidate => candidate.Row.RecordId)
                    .Take(limit).DefaultIfEmpty(), (_, candidate) => candidate == null ? null : candidate.Row)
            : anchors.SelectMany(anchorRow => candidates.Where(candidate =>
                        candidate.Raw != null && anchorRow.Raw == null ||
                        PostgreSqlListDbContext.CCompare(candidate.Raw, anchorRow.Raw) > 0 ||
                        (candidate.Raw == null && anchorRow.Raw == null ||
                         candidate.Raw != null && anchorRow.Raw != null &&
                         PostgreSqlListDbContext.CCompare(candidate.Raw, anchorRow.Raw) == 0) &&
                        candidate.Row.RecordId.CompareTo(anchorRow.Row.RecordId) > 0)
                    .OrderByDescending(candidate => candidate.Value == null).ThenBy(candidate => candidate.Value)
                    .ThenBy(candidate => candidate.Row.RecordId)
                    .Take(limit).DefaultIfEmpty(), (_, candidate) => candidate == null ? null : candidate.Row);
    }

    private static IQueryable<RecordRow?> PageAfterUtf16(IQueryable<RecordRow> query,
        IQueryable<RecordRow> anchor, string key, bool descending, int limit)
    {
        var candidates = query.Select(row => new BytesSortRow
        {
            Row = row,
            Raw = PostgreSqlListDbContext.JsonText(row.ValuesJson, key),
            Value = PostgreSqlListDbContext.Utf16Key(
                PostgreSqlListDbContext.JsonText(row.ValuesJson, key))
        });
        var anchors = anchor.Select(row => new BytesSortRow
        {
            Row = row,
            Raw = PostgreSqlListDbContext.JsonText(row.ValuesJson, key),
            Value = PostgreSqlListDbContext.Utf16Key(
                PostgreSqlListDbContext.JsonText(row.ValuesJson, key))
        });
        return descending
            ? anchors.SelectMany(anchorRow => candidates.Where(candidate =>
                        candidate.Raw == null && anchorRow.Raw != null ||
                        PostgreSqlListDbContext.Utf16Compare(candidate.Raw, anchorRow.Raw) < 0 ||
                        (candidate.Raw == null && anchorRow.Raw == null ||
                         candidate.Raw != null && anchorRow.Raw != null &&
                         PostgreSqlListDbContext.Utf16Compare(candidate.Raw, anchorRow.Raw) == 0) &&
                        candidate.Row.RecordId.CompareTo(anchorRow.Row.RecordId) < 0)
                    .OrderBy(candidate => candidate.Value == null).ThenByDescending(candidate => candidate.Value)
                    .ThenByDescending(candidate => candidate.Row.RecordId)
                    .Take(limit).DefaultIfEmpty(), (_, candidate) => candidate == null ? null : candidate.Row)
            : anchors.SelectMany(anchorRow => candidates.Where(candidate =>
                        candidate.Raw != null && anchorRow.Raw == null ||
                        PostgreSqlListDbContext.Utf16Compare(candidate.Raw, anchorRow.Raw) > 0 ||
                        (candidate.Raw == null && anchorRow.Raw == null ||
                         candidate.Raw != null && anchorRow.Raw != null &&
                         PostgreSqlListDbContext.Utf16Compare(candidate.Raw, anchorRow.Raw) == 0) &&
                        candidate.Row.RecordId.CompareTo(anchorRow.Row.RecordId) > 0)
                    .OrderByDescending(candidate => candidate.Value == null).ThenBy(candidate => candidate.Value)
                    .ThenBy(candidate => candidate.Row.RecordId)
                    .Take(limit).DefaultIfEmpty(), (_, candidate) => candidate == null ? null : candidate.Row);
    }

    private static IQueryable<RecordRow?> PageAfterTimestamp(IQueryable<RecordRow> query,
        IQueryable<RecordRow> anchor, string key, bool descending, int limit)
    {
        var candidates = query.Select(row => new StringSortRow
        {
            Row = row,
            Raw = PostgreSqlListDbContext.JsonText(row.ValuesJson, key),
            Value = PostgreSqlListDbContext.TimestampKey(
                PostgreSqlListDbContext.JsonText(row.ValuesJson, key))
        });
        var anchors = anchor.Select(row => new StringSortRow
        {
            Row = row,
            Raw = PostgreSqlListDbContext.JsonText(row.ValuesJson, key),
            Value = PostgreSqlListDbContext.TimestampKey(
                PostgreSqlListDbContext.JsonText(row.ValuesJson, key))
        });
        return descending
            ? anchors.SelectMany(anchorRow => candidates.Where(candidate =>
                        candidate.Raw == null && anchorRow.Raw != null ||
                        PostgreSqlListDbContext.TimestampCompare(candidate.Raw, anchorRow.Raw) < 0 ||
                        (candidate.Raw == null && anchorRow.Raw == null ||
                         candidate.Raw != null && anchorRow.Raw != null &&
                         PostgreSqlListDbContext.TimestampCompare(candidate.Raw, anchorRow.Raw) == 0) &&
                        candidate.Row.RecordId.CompareTo(anchorRow.Row.RecordId) < 0)
                    .OrderBy(candidate => candidate.Value == null)
                    .ThenByDescending(candidate => EF.Functions.Collate(candidate.Value!, "C"))
                    .ThenByDescending(candidate => candidate.Row.RecordId)
                    .Take(limit).DefaultIfEmpty(), (_, candidate) => candidate == null ? null : candidate.Row)
            : anchors.SelectMany(anchorRow => candidates.Where(candidate =>
                        candidate.Raw != null && anchorRow.Raw == null ||
                        PostgreSqlListDbContext.TimestampCompare(candidate.Raw, anchorRow.Raw) > 0 ||
                        (candidate.Raw == null && anchorRow.Raw == null ||
                         candidate.Raw != null && anchorRow.Raw != null &&
                         PostgreSqlListDbContext.TimestampCompare(candidate.Raw, anchorRow.Raw) == 0) &&
                        candidate.Row.RecordId.CompareTo(anchorRow.Row.RecordId) > 0)
                    .OrderByDescending(candidate => candidate.Value == null)
                    .ThenBy(candidate => EF.Functions.Collate(candidate.Value!, "C"))
                    .ThenBy(candidate => candidate.Row.RecordId)
                    .Take(limit).DefaultIfEmpty(), (_, candidate) => candidate == null ? null : candidate.Row);
    }

    private static IQueryable<RecordRow?> PageAfterOrdinal(IQueryable<RecordRow> query,
        IQueryable<RecordRow> anchor, string key, bool descending, int limit)
    {
        var candidates = query.Select(row => new StringSortRow
        {
            Row = row,
            Raw = PostgreSqlListDbContext.JsonText(row.ValuesJson, key),
            Value = PostgreSqlListDbContext.JsonText(row.ValuesJson, key)
        });
        var anchors = anchor.Select(row => new StringSortRow
        {
            Row = row,
            Raw = PostgreSqlListDbContext.JsonText(row.ValuesJson, key),
            Value = PostgreSqlListDbContext.JsonText(row.ValuesJson, key)
        });
        return descending
            ? anchors.SelectMany(anchorRow => candidates.Where(candidate =>
                        candidate.Raw == null && anchorRow.Raw != null ||
                        PostgreSqlListDbContext.CCompare(candidate.Raw, anchorRow.Raw) < 0 ||
                        (candidate.Raw == null && anchorRow.Raw == null ||
                         candidate.Raw != null && anchorRow.Raw != null &&
                         PostgreSqlListDbContext.CCompare(candidate.Raw, anchorRow.Raw) == 0) &&
                        candidate.Row.RecordId.CompareTo(anchorRow.Row.RecordId) < 0)
                    .OrderBy(candidate => candidate.Value == null)
                    .ThenByDescending(candidate => EF.Functions.Collate(candidate.Value!, "C"))
                    .ThenByDescending(candidate => candidate.Row.RecordId)
                    .Take(limit).DefaultIfEmpty(), (_, candidate) => candidate == null ? null : candidate.Row)
            : anchors.SelectMany(anchorRow => candidates.Where(candidate =>
                        candidate.Raw != null && anchorRow.Raw == null ||
                        PostgreSqlListDbContext.CCompare(candidate.Raw, anchorRow.Raw) > 0 ||
                        (candidate.Raw == null && anchorRow.Raw == null ||
                         candidate.Raw != null && anchorRow.Raw != null &&
                         PostgreSqlListDbContext.CCompare(candidate.Raw, anchorRow.Raw) == 0) &&
                        candidate.Row.RecordId.CompareTo(anchorRow.Row.RecordId) > 0)
                    .OrderByDescending(candidate => candidate.Value == null)
                    .ThenBy(candidate => EF.Functions.Collate(candidate.Value!, "C"))
                    .ThenBy(candidate => candidate.Row.RecordId)
                    .Take(limit).DefaultIfEmpty(), (_, candidate) => candidate == null ? null : candidate.Row);
    }

    private static IQueryable<RecordRow> Compare<T>(IQueryable<RecordRow> query,
        Expression<Func<RecordRow, T>> selector, FilterOperator operation, T value)
    {
        Expression<Func<T>> capturedValue = () => value;
        var right = capturedValue.Body;
        var comparison = operation switch
        {
            FilterOperator.Equal => Expression.Equal(selector.Body, right),
            FilterOperator.NotEqual => Expression.NotEqual(selector.Body, right),
            FilterOperator.GreaterThan => Expression.GreaterThan(selector.Body, right),
            FilterOperator.GreaterOrEqual => Expression.GreaterThanOrEqual(selector.Body, right),
            FilterOperator.LessThan => Expression.LessThan(selector.Body, right),
            FilterOperator.LessOrEqual => Expression.LessThanOrEqual(selector.Body, right),
            _ => throw Validation("Unknown query operator.")
        };
        return query.Where(Expression.Lambda<Func<RecordRow, bool>>(comparison, selector.Parameters));
    }

    private static IOrderedQueryable<RecordRow> Order<T>(IQueryable<RecordRow> query,
        Expression<Func<RecordRow, T>> selector, bool descending)
    {
        var nullValue = Expression.Constant(null, typeof(T));
        var isNull = Expression.Lambda<Func<RecordRow, bool>>(
            Expression.Equal(selector.Body, nullValue), selector.Parameters);
        return descending
            ? query.OrderBy(isNull).ThenByDescending(selector).ThenByDescending(row => row.RecordId)
            : query.OrderByDescending(isNull).ThenBy(selector).ThenBy(row => row.RecordId);
    }

    private static string QueryTimestampKey(string value) => value[..19] +
        (value.Length > 20 ? value[20..^1] : "").PadRight(7, '0');

    private static IOrderedQueryable<RecordRow?> ApplyPageOrder(IQueryable<RecordRow?> query,
        ListField? field, bool descending)
    {
        if (field is null)
            return descending
                ? query.OrderByDescending(row => row!.RecordId)
                : query.OrderBy(row => row!.RecordId);
        var key = field.Id.ToString("D");
        return field.Type switch
        {
            ListFieldType.Integer => OrderPage(query,
                row => (long?)Convert.ToInt64(PostgreSqlListDbContext.JsonText(row!.ValuesJson, key)), descending),
            ListFieldType.Decimal => OrderPage(query,
                row => (decimal?)Convert.ToDecimal(PostgreSqlListDbContext.JsonText(row!.ValuesJson, key)), descending),
            ListFieldType.Boolean => OrderPage(query,
                row => (bool?)Convert.ToBoolean(PostgreSqlListDbContext.JsonText(row!.ValuesJson, key)), descending),
            ListFieldType.Text or ListFieldType.Choice => OrderPage(query,
                row => PostgreSqlListDbContext.Utf16Key(
                    PostgreSqlListDbContext.JsonText(row!.ValuesJson, key)), descending),
            ListFieldType.Timestamp => OrderPage(query,
                row => EF.Functions.Collate(PostgreSqlListDbContext.TimestampKey(
                    PostgreSqlListDbContext.JsonText(row!.ValuesJson, key))!, "C"), descending),
            _ => OrderPage(query, row => EF.Functions.Collate(
                PostgreSqlListDbContext.JsonText(row!.ValuesJson, key)!, "C"), descending)
        };
    }

    private static IOrderedQueryable<RecordRow?> OrderPage<T>(IQueryable<RecordRow?> query,
        Expression<Func<RecordRow?, T>> selector, bool descending)
    {
        var nullValue = Expression.Constant(null, typeof(T));
        var isNull = Expression.Lambda<Func<RecordRow?, bool>>(
            Expression.Equal(selector.Body, nullValue), selector.Parameters);
        return descending
            ? query.OrderBy(isNull).ThenByDescending(selector).ThenByDescending(row => row!.RecordId)
            : query.OrderByDescending(isNull).ThenBy(selector).ThenBy(row => row!.RecordId);
    }

    private sealed class LongSortRow
    {
        public required RecordRow Row { get; init; }
        public long? Value { get; init; }
    }

    private sealed class DecimalSortRow
    {
        public required RecordRow Row { get; init; }
        public decimal? Value { get; init; }
    }

    private sealed class BooleanSortRow
    {
        public required RecordRow Row { get; init; }
        public string? Raw { get; init; }
        public bool? Value { get; init; }
    }

    private sealed class BytesSortRow
    {
        public required RecordRow Row { get; init; }
        public string? Raw { get; init; }
        public byte[]? Value { get; init; }
    }

    private sealed class StringSortRow
    {
        public required RecordRow Row { get; init; }
        public string? Raw { get; init; }
        public string? Value { get; init; }
    }

}
