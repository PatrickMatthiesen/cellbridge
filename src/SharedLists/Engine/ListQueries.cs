using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

#if CELLBRIDGE_SQLSERVER
namespace CellBridge.Lists.SqlServer;
#else
namespace CellBridge.Lists.PostgreSql;
#endif

internal sealed partial class EfListEngine
{
    /// <summary>Filters, exact typed ordering and keyset paging execute in the database. Only a page is materialized.</summary>
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
                var definition = await ReadListDefinitionAsync(db, connection, transaction, workspaceId, listId,
                    actor.Subject, ListAccess.Read, ct, authorization);
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
                    var ordered = ApplyOrder(records, sortField, query.Descending);
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

}
