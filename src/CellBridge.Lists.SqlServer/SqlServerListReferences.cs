using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Lists.SqlServer;

public sealed partial class SqlServerListEngine
{
    private static async Task<Guid[]> ReferenceTargetsAsync(SqlServerListDbContext db, Guid workspaceId,
        Guid listId, IReadOnlySet<Guid> fields, CancellationToken ct) =>
        (await GetDefinitionAsync(db, workspaceId, listId, ct)).Fields
            .Where(field => field.Type == ListFieldType.Reference && fields.Contains(field.Id))
            .Select(field => field.ReferenceListId!.Value).Distinct().ToArray();

    private static async Task ValidateReferenceDefinitionsAsync(SqlServerListDbContext db, Guid workspaceId,
        string subject, ListDefinition definition, OperationAuthorization? authorization, CancellationToken ct,
        IReadOnlySet<Guid>? fields = null)
    {
        foreach (var field in definition.Fields.Where(field => field.Type == ListFieldType.Reference &&
            (fields is null || fields.Contains(field.Id))).OrderBy(field => field.Id))
        {
            var targetListId = field.ReferenceListId!.Value;
            await RequireAccessAsync(db, workspaceId, targetListId, subject, ListAccess.Read, ct, authorization);
            var target = await GetDefinitionAsync(db, workspaceId, targetListId, ct);
            if (field.ReferenceDisplayFieldId is { } display && target.Fields.All(candidate => candidate.Id != display))
                throw Validation("The reference display field does not exist in the target list.");
        }
    }

    private async Task ValidateReferenceValuesAsync(SqlServerListDbContext db, Guid workspaceId, string subject,
        ListDefinition definition, JsonObject values, OperationAuthorization? authorization, CancellationToken ct,
        IReadOnlySet<Guid>? fields = null)
    {
        var assigned = definition.Fields.Where(field => field.Type == ListFieldType.Reference &&
            (fields is null || fields.Contains(field.Id)) && values[field.Id.ToString("D")] is not null).ToArray();
        await ValidateReferenceDefinitionsAsync(db, workspaceId, subject, definition, authorization, ct,
            assigned.Select(field => field.Id).ToHashSet());
        var targets = assigned.Select(field => (List: field.ReferenceListId!.Value,
            Record: Guid.Parse(values[field.Id.ToString("D")]!.GetValue<string>()))).Distinct()
            .OrderBy(target => target.List).ThenBy(target => target.Record);
        // Workspace authority/mutation lock precedes the source identity lock,
        // then target identity locks in a stable order. All locks are transactional.
        foreach (var target in targets)
        {
            await SqlServerListDatabase.LockAsync(db, RecordLock(workspaceId, target.List, target.Record), false, limits, ct);
            if (!await db.Records.AnyAsync(row => row.WorkspaceId == workspaceId && row.ListId == target.List &&
                row.RecordId == target.Record, ct)) throw Validation("The reference target does not exist.");
        }
    }

    private static async Task ReconcileReferencesAsync(SqlServerListDbContext db, Guid workspaceId, Guid listId,
        Guid recordId, ListDefinition definition, JsonObject values, CancellationToken ct)
    {
        var remaining = definition.Fields.Where(field => field.Type == ListFieldType.Reference && values[field.Id.ToString("D")] is not null)
            .ToDictionary(field => field.Id, field => (List: field.ReferenceListId!.Value,
                Record: Guid.Parse(values[field.Id.ToString("D")]!.GetValue<string>())));
        var existing = await db.RecordReferences.Where(row => row.WorkspaceId == workspaceId && row.SourceListId == listId &&
            row.SourceRecordId == recordId).ToListAsync(ct);
        foreach (var row in existing)
        {
            if (!remaining.Remove(row.FieldId, out var target)) db.RecordReferences.Remove(row);
            else { row.TargetListId = target.List; row.TargetRecordId = target.Record; }
        }
        foreach (var (fieldId, target) in remaining)
            db.RecordReferences.Add(new RecordReferenceRow { WorkspaceId = workspaceId, SourceListId = listId,
                SourceRecordId = recordId, FieldId = fieldId, TargetListId = target.List, TargetRecordId = target.Record });
    }

    public Task<ReferenceLabel> ResolveReferenceAsync(ListActor actor, Guid workspaceId, Guid listId,
        Guid recordId, Guid fieldId, CancellationToken ct = default) =>
        OperateAsync(actor, workspaceId, false, async (db, workspace, member, authorization) =>
        {
            await RequireAccessAsync(db, workspaceId, listId, actor.Subject, ListAccess.Read, ct, authorization);
            var definition = await GetDefinitionAsync(db, workspaceId, listId, ct);
            var field = definition.Fields.SingleOrDefault(field => field.Id == fieldId && field.Type == ListFieldType.Reference)
                ?? throw Validation("The reference field does not exist.");
            var source = await db.Records.AsNoTracking().SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId &&
                row.ListId == listId && row.RecordId == recordId, ct) ?? throw NotFound();
            var values = ReadValues(source.ValuesJson);
            if (values[fieldId.ToString("D")] is not JsonValue value) throw NotFound();
            var targetList = field.ReferenceListId!.Value;
            await RequireAccessAsync(db, workspaceId, targetList, actor.Subject, ListAccess.Read, ct, authorization);
            var targetId = Guid.Parse(value.GetValue<string>());
            var target = await db.Records.AsNoTracking().SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId &&
                row.ListId == targetList && row.RecordId == targetId, ct) ?? throw NotFound();
            var targetDefinition = await GetDefinitionAsync(db, workspaceId, targetList, ct);
            JsonNode? label = null;
            if (field.ReferenceDisplayFieldId is { } display)
            {
                if (targetDefinition.Fields.All(candidate => candidate.Id != display)) throw Validation("The reference display field does not exist.");
                ReadValues(target.ValuesJson).TryGetPropertyValue(display.ToString("D"), out label);
            }
            return new ReferenceLabel(targetId, target.Number, label?.DeepClone());
        }, ct, [(listId, ListAuthorizationAction.Read)], db => ReferenceTargetsAsync(db, workspaceId, listId, new HashSet<Guid> { fieldId }, ct));

    private static string RecordLock(Guid workspaceId, Guid listId, Guid recordId) =>
        $"CellBridge.Lists.Record:{workspaceId:N}:{listId:N}:{recordId:N}";
}
