using System.Linq.Expressions;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Lists.SqlServer;

public sealed partial class SqlServerListEngine
{
    private const string OrdinalCollation = "Latin1_General_100_BIN2";

    /// <summary>SQL applies all predicates and exact key ordering before taking one bounded page.</summary>
    public Task<RecordPage> QueryAsync(ListActor actor, Guid workspaceId, Guid listId, ListQuery query,
        CancellationToken ct = default) => OperateAsync(actor, workspaceId, false, async (db, workspace, member, authorization) =>
        {
            await RequireAccessAsync(db, workspaceId, listId, actor.Subject, ListAccess.Read, ct, authorization);
            var definition = await GetDefinitionAsync(db, workspaceId, listId, ct);
            ListValidation.ValidateQuery(definition, query, limits);
            var fields = definition.Fields.ToDictionary(field => field.Id);
            IQueryable<RecordRow> records = db.Records.AsNoTracking().Where(row => row.WorkspaceId == workspaceId && row.ListId == listId);
            foreach (var filter in query.Filters ?? []) records = FilterRecords(records, fields[filter.FieldId], filter);
            var sort = query.SortFieldId is { } id ? fields[id] : null;
            var key = sort?.Id.ToString("D") ?? "";
            var type = (int)(sort?.Type ?? ListFieldType.Text);
            if (query.AfterRecordId is { } cursor)
            {
                // The current filtered anchor is read under the same Shared
                // workspace lock as its page. A deleted/nonmatching anchor fails;
                // a changed sort value becomes the current anchor, as in the domain evaluator.
                var anchor = await records.Where(row => row.RecordId == cursor).Select(row => row.ValuesJson).SingleOrDefaultAsync(ct)
                    ?? throw Validation("Page cursor is no longer present in this filtered list.");
                var cursorKey = cursor.ToString("D");
                if (sort is null)
                    records = query.Descending
                        ? records.Where(row => SqlServerListDbContext.KeyCompare(SqlServerListDbContext.UuidKey(row.RecordId), cursorKey) < 0)
                        : records.Where(row => SqlServerListDbContext.KeyCompare(SqlServerListDbContext.UuidKey(row.RecordId), cursorKey) > 0);
                else if (query.Descending)
                    records = records.Where(row =>
                        SqlServerListDbContext.ScalarKey(row.ValuesJson, key, type) == null && SqlServerListDbContext.ScalarKey(anchor, key, type) != null ||
                        SqlServerListDbContext.KeyCompare(SqlServerListDbContext.ScalarKey(row.ValuesJson, key, type), SqlServerListDbContext.ScalarKey(anchor, key, type)) < 0 ||
                        (SqlServerListDbContext.ScalarKey(row.ValuesJson, key, type) == null && SqlServerListDbContext.ScalarKey(anchor, key, type) == null ||
                            SqlServerListDbContext.KeyCompare(SqlServerListDbContext.ScalarKey(row.ValuesJson, key, type), SqlServerListDbContext.ScalarKey(anchor, key, type)) == 0) &&
                        SqlServerListDbContext.KeyCompare(SqlServerListDbContext.UuidKey(row.RecordId), cursorKey) < 0);
                else
                    records = records.Where(row =>
                        SqlServerListDbContext.ScalarKey(row.ValuesJson, key, type) != null && SqlServerListDbContext.ScalarKey(anchor, key, type) == null ||
                        SqlServerListDbContext.KeyCompare(SqlServerListDbContext.ScalarKey(row.ValuesJson, key, type), SqlServerListDbContext.ScalarKey(anchor, key, type)) > 0 ||
                        (SqlServerListDbContext.ScalarKey(row.ValuesJson, key, type) == null && SqlServerListDbContext.ScalarKey(anchor, key, type) == null ||
                            SqlServerListDbContext.KeyCompare(SqlServerListDbContext.ScalarKey(row.ValuesJson, key, type), SqlServerListDbContext.ScalarKey(anchor, key, type)) == 0) &&
                        SqlServerListDbContext.KeyCompare(SqlServerListDbContext.UuidKey(row.RecordId), cursorKey) > 0);
            }
            IOrderedQueryable<RecordRow> ordered = sort is null
                ? query.Descending ? records.OrderByDescending(row => EF.Functions.Collate(SqlServerListDbContext.UuidKey(row.RecordId), OrdinalCollation))
                    : records.OrderBy(row => EF.Functions.Collate(SqlServerListDbContext.UuidKey(row.RecordId), OrdinalCollation))
                : query.Descending
                    ? records.OrderByDescending(row => EF.Functions.Collate(SqlServerListDbContext.ScalarKey(row.ValuesJson, key, type)!, OrdinalCollation))
                        .ThenByDescending(row => EF.Functions.Collate(SqlServerListDbContext.UuidKey(row.RecordId), OrdinalCollation))
                    : records.OrderBy(row => EF.Functions.Collate(SqlServerListDbContext.ScalarKey(row.ValuesJson, key, type)!, OrdinalCollation))
                        .ThenBy(row => EF.Functions.Collate(SqlServerListDbContext.UuidKey(row.RecordId), OrdinalCollation));
            // SQL Server puts NULL first ascending and last descending.
            var rows = await ordered.Take(query.Limit < int.MaxValue ? query.Limit + 1 : int.MaxValue).ToListAsync(ct);
            var more = rows.Count > query.Limit;
            if (more) rows.RemoveAt(rows.Count - 1);
            var result = rows.Select(row => ToRecord(row, ReadValues(row.ValuesJson))).ToArray();
            return new RecordPage(result, more ? result[^1].Id : null, definition.SchemaVersion);
        }, ct, [(listId, ListAuthorizationAction.Read)]);

    private static IQueryable<RecordRow> FilterRecords(IQueryable<RecordRow> records, ListField field, RecordFilter filter)
    {
        var key = field.Id.ToString("D");
        var type = (int)field.Type;
        if (filter.Value is null)
            return filter.Operator == FilterOperator.Equal
                ? records.Where(row => SqlServerListDbContext.ScalarKey(row.ValuesJson, key, type) == null)
                : records.Where(row => SqlServerListDbContext.ScalarKey(row.ValuesJson, key, type) != null);
        var value = new JsonObject { [key] = filter.Value.DeepClone() }.ToJsonString();
        Expression<Func<RecordRow, int?>> comparison = row => SqlServerListDbContext.KeyCompare(
            SqlServerListDbContext.ScalarKey(row.ValuesJson, key, type), SqlServerListDbContext.ScalarKey(value, key, type));
        var zero = Expression.Constant((int?)0, typeof(int?));
        var predicate = filter.Operator switch
        {
            FilterOperator.Equal => Expression.Equal(comparison.Body, zero),
            FilterOperator.NotEqual => Expression.NotEqual(comparison.Body, zero),
            FilterOperator.GreaterThan => Expression.GreaterThan(comparison.Body, zero),
            FilterOperator.GreaterOrEqual => Expression.GreaterThanOrEqual(comparison.Body, zero),
            FilterOperator.LessThan => Expression.LessThan(comparison.Body, zero),
            FilterOperator.LessOrEqual => Expression.LessThanOrEqual(comparison.Body, zero),
            _ => throw Validation("Unknown query operator.")
        };
        return records.Where(Expression.Lambda<Func<RecordRow, bool>>(predicate, comparison.Parameters));
    }
}
