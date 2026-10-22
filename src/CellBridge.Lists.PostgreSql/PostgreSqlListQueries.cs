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
            return await InRecordTransactionAsync(workspaceId, authorization, async (connection, transaction) =>
            {
                var context = await ReadWriteContextAsync(connection, transaction, workspaceId, listId,
                    Guid.Empty, actor.Subject, ListAccess.Read, deleteLock: false, ct, authorization);
                RequireWriteContext(context, ListAccess.Read);
                var definition = context.Definition!;
                ListValidation.ValidateQuery(definition, query, _limits);
                await using var db = CreateContext(connection, transaction);
                IQueryable<RecordRow> records = db.Records.AsNoTracking()
                    .Where(row => row.WorkspaceId == workspaceId && row.ListId == listId);
                var fields = definition.Fields.ToDictionary(field => field.Id);
                foreach (var filter in query.Filters ?? [])
                    records = ApplyFilter(records, fields[filter.FieldId], filter);

                var filtered = records;
                if (query.AfterRecordId is { } cursor)
                {
                    var anchor = filtered.Where(row => row.RecordId == cursor);
                    records = query.SortFieldId is { } sortId
                        ? ApplyAfterAnchor(records, anchor, fields[sortId], query.Descending)
                        : query.Descending
                            ? records.Where(row => row.RecordId.CompareTo(cursor) < 0 && anchor.Any())
                            : records.Where(row => row.RecordId.CompareTo(cursor) > 0 && anchor.Any());
                }

                records = query.SortFieldId is { } fieldId
                    ? ApplyOrder(records, fields[fieldId], query.Descending)
                    : query.Descending
                        ? records.OrderByDescending(row => row.RecordId)
                        : records.OrderBy(row => row.RecordId);
                var page = records.Take(query.Limit + 1);
                var sortField = query.SortFieldId is { } selected ? fields[selected] : null;
                var sortKey = sortField?.Id.ToString("D") ?? string.Empty;
                var projected = page.Select(row => new QueryEnvelope
                    {
                        RecordId = row.RecordId,
                        SchemaVersion = row.SchemaVersion,
                        Revision = row.Revision,
                        Number = row.Number,
                        ValuesJson = row.ValuesJson,
                        CreatedUtc = row.CreatedUtc,
                        ModifiedUtc = row.ModifiedUtc,
                        CreatedBy = row.CreatedBy,
                        ModifiedBy = row.ModifiedBy,
                        LongSort = sortField != null && sortField.Type == ListFieldType.Integer
                            ? Convert.ToInt64(PostgreSqlListDbContext.JsonText(row.ValuesJson, sortKey)) : null,
                        DecimalSort = sortField != null && sortField.Type == ListFieldType.Decimal
                            ? Convert.ToDecimal(PostgreSqlListDbContext.JsonText(row.ValuesJson, sortKey)) : null,
                        BooleanSort = sortField != null && sortField.Type == ListFieldType.Boolean
                            ? Convert.ToBoolean(PostgreSqlListDbContext.JsonText(row.ValuesJson, sortKey)) : null,
                        BytesSort = sortField != null &&
                            (sortField.Type == ListFieldType.Text || sortField.Type == ListFieldType.Choice)
                            ? PostgreSqlListDbContext.Utf16Key(PostgreSqlListDbContext.JsonText(row.ValuesJson, sortKey)) : null,
                        StringSort = sortField != null && sortField.Type == ListFieldType.Timestamp
                            ? PostgreSqlListDbContext.TimestampKey(PostgreSqlListDbContext.JsonText(row.ValuesJson, sortKey))
                            : sortField != null ? PostgreSqlListDbContext.JsonText(row.ValuesJson, sortKey) : null
                    });
                var seeded = db.SchemaVersions.AsNoTracking().Take(1)
                    .SelectMany(_ => projected.DefaultIfEmpty(), (_, envelope) => new QueryEnvelope
                    {
                        HasRecord = envelope!.RecordId != null,
                        CursorValid = query.AfterRecordId == null ||
                            filtered.Any(candidate => candidate.RecordId == query.AfterRecordId),
                        RecordId = envelope.RecordId,
                        SchemaVersion = envelope.SchemaVersion,
                        Revision = envelope.Revision,
                        Number = envelope.Number,
                        ValuesJson = envelope.ValuesJson,
                        CreatedUtc = envelope.CreatedUtc,
                        ModifiedUtc = envelope.ModifiedUtc,
                        CreatedBy = envelope.CreatedBy,
                        ModifiedBy = envelope.ModifiedBy,
                        LongSort = envelope.LongSort,
                        DecimalSort = envelope.DecimalSort,
                        BooleanSort = envelope.BooleanSort,
                        BytesSort = envelope.BytesSort,
                        StringSort = envelope.StringSort
                    });
                var envelopes = await ApplyEnvelopeOrder(seeded, sortField, query.Descending).ToListAsync(ct);
                if (envelopes.Count == 0 || !envelopes[0].CursorValid)
                    throw Validation("Page cursor is no longer present in this filtered list.");
                var rows = envelopes.Where(envelope => envelope.HasRecord).Select(envelope => new RecordRow
                {
                    WorkspaceId = workspaceId,
                    ListId = listId,
                    RecordId = envelope.RecordId!.Value,
                    SchemaVersion = envelope.SchemaVersion!.Value,
                    Revision = envelope.Revision!.Value,
                    Number = envelope.Number!.Value,
                    ValuesJson = envelope.ValuesJson!,
                    CreatedUtc = envelope.CreatedUtc!.Value,
                    ModifiedUtc = envelope.ModifiedUtc!.Value,
                    CreatedBy = envelope.CreatedBy!,
                    ModifiedBy = envelope.ModifiedBy!
                }).ToList();
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

    private static IQueryable<RecordRow> ApplyAfterAnchor(IQueryable<RecordRow> query,
        IQueryable<RecordRow> anchor, ListField field, bool descending)
    {
        var key = field.Id.ToString("D");
        return field.Type switch
        {
            ListFieldType.Integer => AfterAnchor(query, anchor,
                row => (long?)Convert.ToInt64(PostgreSqlListDbContext.JsonText(row.ValuesJson, key)), descending),
            ListFieldType.Decimal => AfterAnchor(query, anchor,
                row => (decimal?)Convert.ToDecimal(PostgreSqlListDbContext.JsonText(row.ValuesJson, key)), descending),
            ListFieldType.Boolean => AfterAnchorByComparison(query, anchor,
                row => PostgreSqlListDbContext.JsonText(row.ValuesJson, key),
                nameof(PostgreSqlListDbContext.CCompare), descending),
            ListFieldType.Text or ListFieldType.Choice => AfterAnchorByComparison(query, anchor,
                row => PostgreSqlListDbContext.JsonText(row.ValuesJson, key),
                nameof(PostgreSqlListDbContext.Utf16Compare), descending),
            ListFieldType.Timestamp => AfterAnchorByComparison(query, anchor,
                row => PostgreSqlListDbContext.JsonText(row.ValuesJson, key),
                nameof(PostgreSqlListDbContext.TimestampCompare), descending),
            _ => AfterAnchorByComparison(query, anchor,
                row => PostgreSqlListDbContext.JsonText(row.ValuesJson, key),
                nameof(PostgreSqlListDbContext.CCompare), descending)
        };
    }

    private static IQueryable<RecordRow> Compare<T>(IQueryable<RecordRow> query,
        Expression<Func<RecordRow, T>> selector, FilterOperator operation, T value)
    {
        var right = Expression.Constant(value, typeof(T));
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

    private static IQueryable<RecordRow> AfterAnchor<T>(IQueryable<RecordRow> query,
        IQueryable<RecordRow> anchor, Expression<Func<RecordRow, T?>> selector, bool descending) where T : struct
    {
        var pairs = query.SelectMany(_ => anchor, (row, anchorRow) => new RecordPair { Row = row, Anchor = anchorRow });
        var parameter = Expression.Parameter(typeof(RecordPair), "pair");
        var row = Expression.Property(parameter, nameof(RecordPair.Row));
        var anchorRow = Expression.Property(parameter, nameof(RecordPair.Anchor));
        var rowValue = new ReplaceParameterVisitor(selector.Parameters[0], row).Visit(selector.Body)!;
        var anchorValue = new ReplaceParameterVisitor(selector.Parameters[0], anchorRow).Visit(selector.Body)!;
        var rowId = Expression.Property(row, nameof(RecordRow.RecordId));
        var anchorId = Expression.Property(anchorRow, nameof(RecordRow.RecordId));
        var compareId = Expression.Call(rowId, nameof(Guid.CompareTo), Type.EmptyTypes, anchorId);
        var tied = descending ? Expression.LessThan(compareId, Expression.Constant(0))
            : Expression.GreaterThan(compareId, Expression.Constant(0));
        var rowNull = Expression.Equal(rowValue, Expression.Constant(null, typeof(T?)));
        var anchorNull = Expression.Equal(anchorValue, Expression.Constant(null, typeof(T?)));
        var ordered = descending ? Expression.LessThan(rowValue, anchorValue)
            : Expression.GreaterThan(rowValue, anchorValue);
        var equal = Expression.Equal(rowValue, anchorValue);
        var nullOrder = descending
            ? Expression.AndAlso(rowNull, Expression.Not(anchorNull))
            : Expression.AndAlso(Expression.Not(rowNull), anchorNull);
        var body = Expression.OrElse(nullOrder, Expression.OrElse(ordered, Expression.AndAlso(equal, tied)));
        return pairs.Where(Expression.Lambda<Func<RecordPair, bool>>(body, parameter)).Select(pair => pair.Row);
    }

    private static IQueryable<RecordRow> AfterAnchorByComparison(IQueryable<RecordRow> query,
        IQueryable<RecordRow> anchor, Expression<Func<RecordRow, string?>> selector,
        string comparisonMethod, bool descending)
    {
        var pairs = query.SelectMany(_ => anchor, (row, anchorRow) => new RecordPair { Row = row, Anchor = anchorRow });
        var parameter = Expression.Parameter(typeof(RecordPair), "pair");
        var row = Expression.Property(parameter, nameof(RecordPair.Row));
        var anchorRow = Expression.Property(parameter, nameof(RecordPair.Anchor));
        var rowValue = new ReplaceParameterVisitor(selector.Parameters[0], row).Visit(selector.Body)!;
        var anchorValue = new ReplaceParameterVisitor(selector.Parameters[0], anchorRow).Visit(selector.Body)!;
        var method = typeof(PostgreSqlListDbContext).GetMethod(comparisonMethod,
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var comparison = Expression.Call(method, rowValue, anchorValue);
        var rowNull = Expression.Equal(rowValue, Expression.Constant(null, typeof(string)));
        var anchorNull = Expression.Equal(anchorValue, Expression.Constant(null, typeof(string)));
        var rowId = Expression.Property(row, nameof(RecordRow.RecordId));
        var anchorId = Expression.Property(anchorRow, nameof(RecordRow.RecordId));
        var compareId = Expression.Call(rowId, nameof(Guid.CompareTo), Type.EmptyTypes, anchorId);
        var tied = descending ? Expression.LessThan(compareId, Expression.Constant(0))
            : Expression.GreaterThan(compareId, Expression.Constant(0));
        var ordered = descending ? Expression.LessThan(comparison, Expression.Constant(0))
            : Expression.GreaterThan(comparison, Expression.Constant(0));
        var nullOrder = descending
            ? Expression.AndAlso(rowNull, Expression.Not(anchorNull))
            : Expression.AndAlso(Expression.Not(rowNull), anchorNull);
        var bothNull = Expression.AndAlso(rowNull, anchorNull);
        var equal = Expression.OrElse(bothNull,
            Expression.AndAlso(Expression.Not(rowNull), Expression.AndAlso(Expression.Not(anchorNull),
                Expression.Equal(comparison, Expression.Constant(0)))));
        var body = Expression.OrElse(nullOrder, Expression.OrElse(ordered, Expression.AndAlso(equal, tied)));
        return pairs.Where(Expression.Lambda<Func<RecordPair, bool>>(body, parameter)).Select(pair => pair.Row);
    }

    private static IOrderedQueryable<QueryEnvelope> ApplyEnvelopeOrder(IQueryable<QueryEnvelope> query,
        ListField? field, bool descending)
    {
        if (field is null)
            return descending ? query.OrderByDescending(row => row.RecordId) : query.OrderBy(row => row.RecordId);
        return field.Type switch
        {
            ListFieldType.Integer => descending
                ? query.OrderBy(row => row.LongSort == null).ThenByDescending(row => row.LongSort)
                    .ThenByDescending(row => row.RecordId)
                : query.OrderByDescending(row => row.LongSort == null).ThenBy(row => row.LongSort)
                    .ThenBy(row => row.RecordId),
            ListFieldType.Decimal => descending
                ? query.OrderBy(row => row.DecimalSort == null).ThenByDescending(row => row.DecimalSort)
                    .ThenByDescending(row => row.RecordId)
                : query.OrderByDescending(row => row.DecimalSort == null).ThenBy(row => row.DecimalSort)
                    .ThenBy(row => row.RecordId),
            ListFieldType.Boolean => descending
                ? query.OrderBy(row => row.BooleanSort == null).ThenByDescending(row => row.BooleanSort)
                    .ThenByDescending(row => row.RecordId)
                : query.OrderByDescending(row => row.BooleanSort == null).ThenBy(row => row.BooleanSort)
                    .ThenBy(row => row.RecordId),
            ListFieldType.Text or ListFieldType.Choice => descending
                ? query.OrderBy(row => row.BytesSort == null).ThenByDescending(row => row.BytesSort)
                    .ThenByDescending(row => row.RecordId)
                : query.OrderByDescending(row => row.BytesSort == null).ThenBy(row => row.BytesSort)
                    .ThenBy(row => row.RecordId),
            _ => descending
                ? query.OrderBy(row => row.StringSort == null)
                    .ThenByDescending(row => EF.Functions.Collate(row.StringSort!, "C"))
                    .ThenByDescending(row => row.RecordId)
                : query.OrderByDescending(row => row.StringSort == null)
                    .ThenBy(row => EF.Functions.Collate(row.StringSort!, "C"))
                    .ThenBy(row => row.RecordId)
        };
    }

    private sealed class ReplaceParameterVisitor(ParameterExpression source, Expression target)
        : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == source ? target : node;
    }

    private sealed class RecordPair { public required RecordRow Row { get; init; } public required RecordRow Anchor { get; init; } }
    private sealed class QueryEnvelope
    {
        public bool HasRecord { get; init; }
        public bool CursorValid { get; init; }
        public Guid? RecordId { get; init; }
        public long? SchemaVersion { get; init; }
        public long? Revision { get; init; }
        public long? Number { get; init; }
        public string? ValuesJson { get; init; }
        public DateTimeOffset? CreatedUtc { get; init; }
        public DateTimeOffset? ModifiedUtc { get; init; }
        public string? CreatedBy { get; init; }
        public string? ModifiedBy { get; init; }
        public long? LongSort { get; init; }
        public decimal? DecimalSort { get; init; }
        public bool? BooleanSort { get; init; }
        public byte[]? BytesSort { get; init; }
        public string? StringSort { get; init; }
    }
}
