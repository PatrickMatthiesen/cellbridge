using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Lists.SqlServer;

public sealed partial class SqlServerListEngine
{
    public Task<IReadOnlyList<SavedListView>> GetViewsAsync(ListActor actor, Guid workspaceId, Guid listId,
        CancellationToken ct = default) => OperateAsync<IReadOnlyList<SavedListView>>(actor, workspaceId, false,
        async (db, workspace, member, authorization) =>
        {
            await RequireAccessAsync(db, workspaceId, listId, actor.Subject, ListAccess.Read, ct, authorization);
            var definition = await GetDefinitionAsync(db, workspaceId, listId, ct);
            var json = await db.SavedViews.AsNoTracking().Where(row => row.WorkspaceId == workspaceId && row.ListId == listId)
                .OrderBy(row => EF.Functions.Collate(SqlServerListDbContext.UuidKey(row.ViewId), OrdinalCollation))
                .Select(row => row.Definition).ToListAsync(ct);
            return json.Select(value =>
            {
                var view = JsonSerializer.Deserialize<SavedListView>(value, JsonOptions) ?? throw InvalidStored();
                ValidateView(definition, view);
                return view;
            }).ToArray();
        }, ct, [(listId, ListAuthorizationAction.Read)]);

    public Task<SavedListView> SaveViewAsync(ListActor actor, Guid workspaceId, Guid listId, SaveViewRequest request,
        CancellationToken ct = default) => OperateAsync(actor, workspaceId, true, async (db, workspace, member, authorization) =>
        {
            await RequireAccessAsync(db, workspaceId, listId, actor.Subject, ListAccess.Design, ct, authorization);
            var definition = await GetDefinitionAsync(db, workspaceId, listId, ct);
            ArgumentNullException.ThrowIfNull(request);
            var view = new SavedListView(request.Id, listId, request.Name, request.Query, request.VisibleFields);
            ValidateView(definition, view);
            var existing = await db.SavedViews.SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId && row.ListId == listId && row.ViewId == request.Id, ct);
            if (existing is null && await db.SavedViews.LongCountAsync(row => row.WorkspaceId == workspaceId && row.ListId == listId, ct) >= limits.MaxViewsPerList)
                throw Limit("The saved view limit has been reached.");
            var before = existing is null ? -64 : JsonBytes(existing.Definition);
            var row = existing ?? new SavedViewRow { WorkspaceId = workspaceId, ListId = listId, ViewId = request.Id };
            row.Definition = JsonSerializer.Serialize(view, JsonOptions);
            if (existing is null) db.SavedViews.Add(row);
            await SqlServerListDatabase.SaveRequiredAsync(db, ct);
            await db.Entry(row).ReloadAsync(ct);
            var persisted = JsonSerializer.Deserialize<SavedListView>(row.Definition, JsonOptions) ?? throw InvalidStored();
            ValidateView(definition, persisted);
            if (persisted.Id != request.Id || persisted.ListId != listId) throw InvalidStored();
            var json = row.Definition;
            Reserve(workspace, checked(JsonBytes(json) - before), actor.Subject, "view.save", persisted.VisibleFields.Count);
            db.AuditEvents.Add(Audit(workspaceId, await SqlServerListDatabase.ClockAsync(db, ct), actor.Subject,
                "view.save", listId, null, null, persisted.VisibleFields.ToArray()));
            await SqlServerListDatabase.SaveRequiredAsync(db, ct);
            await db.Entry(row).ReloadAsync(ct);
            if (!StringComparer.Ordinal.Equals(json, row.Definition)) throw InvalidStored();
            return persisted;
        }, ct, [(listId, ListAuthorizationAction.Design)]);

    private void ValidateView(ListDefinition definition, SavedListView view)
    {
        ListValidation.ValidateQuery(definition, view.Query, limits);
        if (view.Id == Guid.Empty || view.ListId != definition.Id || string.IsNullOrWhiteSpace(view.Name) ||
            view.Name.Length > 200 || !ListValidation.IsWellFormedText(view.Name)) throw Validation("A view ID and valid name are required.");
        if (view.VisibleFields is null || view.VisibleFields.Distinct().Count() != view.VisibleFields.Count ||
            view.VisibleFields.Any(id => definition.Fields.All(field => field.Id != id))) throw Validation("A view contains an unknown or duplicate field.");
    }

    public Task<IReadOnlyList<AuditEvent>> GetAuditAsync(ListActor actor, Guid workspaceId, int limit = 100,
        CancellationToken ct = default)
    {
        if (limit is <= 0 or > 1000) throw Validation("The audit page size is invalid.");
        return OperateAsync<IReadOnlyList<AuditEvent>>(actor, workspaceId, false, async (db, workspace, member, authorization) =>
        {
            if (authorization is null && !member.CanManageGrants) throw Forbidden();
            return await db.AuditEvents.AsNoTracking().Where(row => row.WorkspaceId == workspaceId)
                .OrderByDescending(row => row.Id).Take(limit)
                .Select(row => new AuditEvent(row.Id, row.Utc, row.Actor, row.Action, row.ListId, row.RecordId,
                    row.Revision, row.FieldIds, row.TargetSubject)).ToListAsync(ct);
        }, ct, [(null, ListAuthorizationAction.ReadAudit)]);
    }
}
