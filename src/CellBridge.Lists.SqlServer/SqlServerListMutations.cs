using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Lists.SqlServer;

public sealed partial class SqlServerListEngine
{
    public Task<ListDefinition> PublishSchemaAsync(ListActor actor, Guid workspaceId, Guid listId,
        PublishSchemaRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Fields is null || request.Fields.Any(field => field is null)) throw Validation("Fields are required and cannot contain null.");
        return OperateAsync(actor, workspaceId, true, async (db, workspace, member, authorization) =>
        {
            await RequireAccessAsync(db, workspaceId, listId, actor.Subject, ListAccess.Design, ct, authorization);
            var previous = await GetDefinitionAsync(db, workspaceId, listId, ct);
            if (previous.SchemaVersion != request.ExpectedVersion) throw Conflict("The list schema changed.");
            var next = previous with { Name = request.Name, SchemaVersion = checked(previous.SchemaVersion + 1), Fields = request.Fields };
            ListValidation.ValidateEvolution(previous, next, limits);
            var existingFields = previous.Fields.Select(field => field.Id).ToHashSet();
            var added = next.Fields.Where(field => !existingFields.Contains(field.Id)).Select(field => field.Id).ToHashSet();
            await ValidateReferenceDefinitionsAsync(db, workspaceId, actor.Subject, next, authorization, ct, added);
            if (await db.ListSchemas.LongCountAsync(row => row.WorkspaceId == workspaceId && row.ListId == listId, ct) >= limits.MaxSchemaVersions)
                throw Limit("The list schema version limit has been reached.");
            var row = await db.Lists.SingleAsync(row => row.WorkspaceId == workspaceId && row.ListId == listId, ct);
            row.Name = next.Name;
            row.SchemaVersion = next.SchemaVersion;
            var schema = new ListSchemaRow { WorkspaceId = workspaceId, ListId = listId, SchemaVersion = next.SchemaVersion,
                Definition = JsonSerializer.Serialize(next, JsonOptions) };
            db.ListSchemas.Add(schema);
            await SqlServerListDatabase.SaveRequiredAsync(db, ct);
            await db.Entry(schema).ReloadAsync(ct);
            await db.Entry(row).ReloadAsync(ct);
            var persisted = ReadDefinition(schema.Definition);
            ListValidation.ValidateEvolution(previous, persisted, limits);
            if (persisted.Name != row.Name || persisted.SchemaVersion != row.SchemaVersion) throw InvalidStored();
            added = persisted.Fields.Where(field => !existingFields.Contains(field.Id)).Select(field => field.Id).ToHashSet();
            await ValidateReferenceDefinitionsAsync(db, workspaceId, actor.Subject, persisted, authorization, ct, added);
            var json = schema.Definition;
            var fields = persisted.Fields.Select(field => field.Id).ToArray();
            Reserve(workspace, checked(JsonBytes(json) + 96), actor.Subject, "schema.publish", fields.Length);
            db.AuditEvents.Add(Audit(workspaceId, await SqlServerListDatabase.ClockAsync(db, ct), actor.Subject,
                "schema.publish", listId, null, persisted.SchemaVersion, fields));
            await SqlServerListDatabase.SaveRequiredAsync(db, ct);
            await db.Entry(schema).ReloadAsync(ct);
            if (!StringComparer.Ordinal.Equals(json, schema.Definition)) throw InvalidStored();
            return persisted;
        }, ct, [(listId, ListAuthorizationAction.Design)], async db =>
        {
            var old = await GetDefinitionAsync(db, workspaceId, listId, ct);
            var existing = old.Fields.Select(field => field.Id).ToHashSet();
            return request.Fields.Where(field => field.Type == ListFieldType.Reference && !existing.Contains(field.Id))
                .Select(field => field.ReferenceListId!.Value).Distinct().ToArray();
        });
    }

    public Task<ListRecord> UpdateRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        UpdateRecordRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Changes is null || request.Changes.Any(change => change is null)) throw Validation("Changes are required and cannot contain null.");
        var changes = request.Changes.Select(change => change with { Value = change.Value?.DeepClone() }).ToArray();
        return OperateAsync(actor, workspaceId, true, async (db, workspace, member, authorization) =>
        {
            await RequireAccessAsync(db, workspaceId, listId, actor.Subject, ListAccess.Read | ListAccess.Write, ct, authorization);
            await SqlServerListDatabase.LockAsync(db, RecordLock(workspaceId, listId, recordId), true, limits, ct);
            var definition = await GetDefinitionAsync(db, workspaceId, listId, ct);
            if (request.SchemaVersion != definition.SchemaVersion) throw Conflict("The current schema version is required.");
            var record = await db.Records.SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId && row.ListId == listId && row.RecordId == recordId, ct)
                ?? throw NotFound();
            if (record.Revision != request.ExpectedRevision) throw Conflict("The record revision changed.");
            var original = ToRecord(record, ReadValues(record.ValuesJson));
            var values = ListValidation.ApplyChanges(definition, original.Values, changes, limits);
            var changed = changes.Select(change => change.FieldId).ToHashSet();
            await ValidateReferenceValuesAsync(db, workspaceId, actor.Subject, definition, values, authorization, ct, changed);
            var previousBytes = JsonBytes(record.ValuesJson);
            var now = await SqlServerListDatabase.ClockAsync(db, ct);
            record.ValuesJson = values.ToJsonString();
            record.SchemaVersion = definition.SchemaVersion;
            record.Revision = checked(record.Revision + 1);
            record.ModifiedUtc = now;
            record.ModifiedBy = actor.Subject;
            await SqlServerListDatabase.SaveRequiredAsync(db, ct);
            await db.Entry(record).ReloadAsync(ct);
            ValidateMetadata();
            values = ReadValues(record.ValuesJson);
            ListValidation.ValidateValues(definition, values, limits);
            var actualChanged = ChangedFieldIds(original.Values, values);
            await ValidateReferenceValuesAsync(db, workspaceId, actor.Subject, definition, values, authorization, ct, actualChanged);
            await ReconcileReferencesAsync(db, workspaceId, listId, recordId, definition, values, ct);
            var json = record.ValuesJson;
            var fields = changed.Concat(actualChanged).Distinct().Order().ToArray();
            Reserve(workspace, checked(JsonBytes(json) - previousBytes), actor.Subject, "record.update", fields.Length);
            db.AuditEvents.Add(Audit(workspaceId, now, actor.Subject, "record.update", listId, recordId, record.Revision, fields));
            await SqlServerListDatabase.SaveRequiredAsync(db, ct);
            await db.Entry(record).ReloadAsync(ct);
            ValidateMetadata();
            if (!StringComparer.Ordinal.Equals(record.ValuesJson, json)) throw InvalidStored();
            return ToRecord(record, ReadValues(record.ValuesJson));

            void ValidateMetadata()
            {
                if (db.Entry(record).State == EntityState.Detached || record.WorkspaceId != workspaceId || record.ListId != listId ||
                    record.RecordId != recordId || record.SchemaVersion != definition.SchemaVersion || record.Revision != checked(original.Revision + 1) ||
                    record.Number != original.Number || record.CreatedUtc != original.CreatedUtc || record.CreatedBy != original.CreatedBy ||
                    record.ModifiedUtc != now || record.ModifiedBy != actor.Subject) throw InvalidStored();
            }
        }, ct, [(listId, ListAuthorizationAction.Read), (listId, ListAuthorizationAction.Write)], db =>
            ReferenceTargetsAsync(db, workspaceId, listId, changes.Where(change => !change.Remove && change.Value is not null)
                .Select(change => change.FieldId).ToHashSet(), ct));
    }

    public Task DeleteRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        long expectedRevision, CancellationToken ct = default) =>
        OperateAsync(actor, workspaceId, true, async (db, workspace, member, authorization) =>
        {
            await RequireAccessAsync(db, workspaceId, listId, actor.Subject, ListAccess.Write, ct, authorization);
            await SqlServerListDatabase.LockAsync(db, RecordLock(workspaceId, listId, recordId), true, limits, ct);
            var record = await db.Records.SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId && row.ListId == listId && row.RecordId == recordId, ct)
                ?? throw NotFound();
            if (record.Revision != expectedRevision) throw Conflict("The record revision changed.");
            if (await db.RecordReferences.AnyAsync(row => row.WorkspaceId == workspaceId && row.TargetListId == listId &&
                row.TargetRecordId == recordId && (row.SourceListId != listId || row.SourceRecordId != recordId), ct))
                throw Conflict("The record is referenced by another record.");
            // Delete outgoing edges explicitly before the NoAction source FK.
            db.RecordReferences.RemoveRange(await db.RecordReferences.Where(row => row.WorkspaceId == workspaceId &&
                row.SourceListId == listId && row.SourceRecordId == recordId).ToListAsync(ct));
            await SqlServerListDatabase.SaveRequiredAsync(db, ct);
            db.Records.Remove(record);
            Reserve(workspace, -JsonBytes(record.ValuesJson), actor.Subject, "record.delete", 0);
            db.AuditEvents.Add(Audit(workspaceId, await SqlServerListDatabase.ClockAsync(db, ct), actor.Subject,
                "record.delete", listId, recordId, record.Revision, []));
            await SqlServerListDatabase.SaveRequiredAsync(db, ct);
            return true;
        }, ct, [(listId, ListAuthorizationAction.Write)]);

    public Task<MergePreview> PreviewMergeAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        MergeRequest request, CancellationToken ct = default) =>
        OperateAsync(actor, workspaceId, false, async (db, workspace, member, authorization) =>
        {
            await RequireAccessAsync(db, workspaceId, listId, actor.Subject, ListAccess.Read | ListAccess.Write, ct, authorization);
            var definition = await GetDefinitionAsync(db, workspaceId, listId, ct);
            var record = await db.Records.AsNoTracking().SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId &&
                row.ListId == listId && row.RecordId == recordId, ct) ?? throw NotFound();
            return ListMerge.Preview(definition, ToRecord(record, ReadValues(record.ValuesJson)), request, limits);
        }, ct, [(listId, ListAuthorizationAction.Read), (listId, ListAuthorizationAction.Write)]);

    private static HashSet<Guid> ChangedFieldIds(JsonObject previous, JsonObject current) => previous.Select(pair => pair.Key)
        .Concat(current.Select(pair => pair.Key)).Distinct().Where(key => previous.ContainsKey(key) != current.ContainsKey(key) ||
            !JsonNode.DeepEquals(previous[key], current[key])).Select(Guid.Parse).ToHashSet();

}
