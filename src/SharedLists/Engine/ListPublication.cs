using System.Data.Common;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;

#if CELLBRIDGE_SQLSERVER
namespace CellBridge.Lists.SqlServer;
#else
namespace CellBridge.Lists.PostgreSql;
#endif

internal sealed partial class EfListEngine
{
    private static ListException InvalidPublication() => new(ListFailure.Unavailable,
        "Persisted list state is invalid or changed during publication.");

    private void RequireWorkspaceQuota(WorkspaceRow workspace)
    {
        if (_limits.MaxWorkspaceBytes is { } maximum && workspace.StoredBytes > maximum ||
            _limits.MaxAuditEvents is { } audits && workspace.AuditCount > audits)
            throw Limit("The workspace storage or audit limit has been reached.");
    }

    private static bool HasPublicationInterceptors(ListDbContext db) =>
        db.GetService<IInterceptors>().Aggregate<ISaveChangesInterceptor>() is not null;

    private static void StagePublicationAudit(ListDbContext db, AuditEventRow audit)
    {
        // Preserve PostgreSQL's batched save when no save interceptor can alter
        // the publication. Intercepted publications validate before inserting
        // their immutable audit event.
        if (!RequiresPublicationReadback && !HasPublicationInterceptors(db))
            db.AuditEvents.Add(audit);
    }

    private void CheckPreparedPublication(WorkspaceRow workspace)
    {
        // PostgreSQL checks the locked ledger before its single batched save.
        // SQL Server accounts after reading the values accepted by interceptors.
        if (!RequiresPublicationReadback) RequireWorkspaceQuota(workspace);
    }

    private async Task ValidatePublishedRecordAsync(ListDbContext db, DbConnection connection,
        DbTransaction transaction, ListDefinition definition, RecordRow record, ListRecord expected,
        CreateReceiptRow? receipt, CreateReceiptRow? expectedReceipt, WorkspaceRow? workspace,
        AuditEventRow audit, long preparedBytes, JsonObject? previousValues,
        IReadOnlySet<Guid> preparedReferenceFieldIds, OperationAuthorization? authorization,
        CancellationToken ct, long previousBytes = 0)
    {
        var deferredQuota = workspace is null;
        var publishedBytes = preparedBytes;
        var preparedFieldCount = audit.FieldIds.Length;
        var entities = receipt is null ? new object[] { record } : [record, receipt];
        await ReloadPublicationAsync(db, ct, entities);
        ValidateMetadata();
        var changed = !StringComparer.Ordinal.Equals(record.ValuesJson, ListJson.RecordValues(expected.Values));
        var reconcile = RequiresPublicationReadback || changed ||
            db.Entry(audit).State == EntityState.Detached && (!deferredQuota || HasPublicationInterceptors(db));
        // PostgreSQL's initial save updates only the source record. Stage its
        // edges after that conditional update owns the source row lock.
        if (reconcile || deferredQuota && preparedReferenceFieldIds.Count != 0)
        {
            var values = ListJson.Object(record.ValuesJson);
            if (reconcile) ListValidation.ValidateValues(definition, values, _limits);
            var references = ReferenceAssignments(definition, values);
            var changedReferenceFieldIds = previousValues is null
                ? definition.Fields.Where(field => field.Type == ListFieldType.Reference)
                    .Select(field => field.Id).ToHashSet()
                : ChangedFields(previousValues, values)
                    .Where(fieldId => definition.Fields.Any(field =>
                        field.Id == fieldId && field.Type == ListFieldType.Reference)).ToHashSet();
            var referencesToValidate = references.Where(reference =>
                changedReferenceFieldIds.Contains(reference.FieldId)).ToArray();
            if (reconcile && referencesToValidate.Length != 0)
                await ValidateReferenceAssignmentsAsync(db, connection, transaction, record.WorkspaceId,
                    record.ModifiedBy, referencesToValidate, ct, authorization);
            var referenceFieldIdsToReconcile = preparedReferenceFieldIds
                .Concat(changedReferenceFieldIds).ToHashSet();
            var referencesToReconcile = references.Where(reference =>
                referenceFieldIdsToReconcile.Contains(reference.FieldId)).ToArray();
            var existing = await db.RecordReferences.Where(row => row.WorkspaceId == record.WorkspaceId &&
                row.SourceListId == record.ListId && row.SourceRecordId == record.RecordId &&
                referenceFieldIdsToReconcile.Contains(row.FieldId)).ToListAsync(ct);
            var replacements = referencesToReconcile.ToDictionary(reference => reference.FieldId);
            foreach (var edge in existing)
            {
                if (!replacements.Remove(edge.FieldId, out var replacement)) db.RecordReferences.Remove(edge);
                else { edge.TargetListId = replacement.TargetListId; edge.TargetRecordId = replacement.TargetRecordId; }
            }
            db.RecordReferences.AddRange(replacements.Values.Select(reference => new RecordReferenceRow {
                WorkspaceId = record.WorkspaceId, SourceListId = record.ListId, SourceRecordId = record.RecordId,
                FieldId = reference.FieldId, TargetListId = reference.TargetListId, TargetRecordId = reference.TargetRecordId }));
            if (reconcile)
            {
                publishedBytes = await CanonicalJsonBytesAsync(db, record.ValuesJson, ct);
                if (publishedBytes > _limits.MaxRecordBytes) throw Limit("The normalized record exceeds the configured byte limit.");
                var fields = previousValues is null ? values.Select(pair => Guid.Parse(pair.Key)).ToArray()
                    : audit.FieldIds.Concat(ChangedFields(previousValues, values)).Distinct().Order().ToArray();
                audit.FieldIds = fields;
            }
        }
        // Accepted values can introduce target locks. Acquire them above, before
        // quota, so a target delete cannot hold that target while waiting on us.
        if (workspace is null)
        {
            workspace = await _locks.LockedQuota(db, record.WorkspaceId).SingleOrDefaultAsync(ct)
                ?? throw NotFound();
            workspace.StoredBytes = checked(workspace.StoredBytes + publishedBytes - previousBytes +
                AuditStorageBytes(record.ModifiedBy, audit.Action, audit.FieldIds.Length));
            workspace.AuditCount = checked(workspace.AuditCount + 1);
        }
        else if (reconcile)
            workspace.StoredBytes = checked(workspace.StoredBytes + publishedBytes - preparedBytes +
                (audit.FieldIds.Length - preparedFieldCount) * 16L);
        RequireWorkspaceQuota(workspace);
        if (db.Entry(audit).State == EntityState.Detached) db.AuditEvents.Add(audit);
        if (reconcile || deferredQuota)
        {
            var publishedJson = record.ValuesJson;
            if (db.ChangeTracker.HasChanges()) await SaveRequiredAsync(db, ct);
            await ReloadPublicationAsync(db, ct, entities);
            ValidateMetadata();
            // A second transformation cannot trigger another round of target
            // validation now that quota is locked. Roll back instead.
            if (!StringComparer.Ordinal.Equals(record.ValuesJson, publishedJson)) throw InvalidPublication();
        }

        RequireWorkspaceQuota(workspace);

        void ValidateMetadata()
        {
            if (record.WorkspaceId != expected.WorkspaceId || record.ListId != expected.ListId || record.RecordId != expected.Id ||
                record.SchemaVersion != expected.SchemaVersion || record.Revision != expected.Revision || record.Number != expected.Number ||
                record.CreatedUtc != expected.CreatedUtc || record.ModifiedUtc != expected.ModifiedUtc ||
                !StringComparer.Ordinal.Equals(record.CreatedBy, expected.CreatedBy) || !StringComparer.Ordinal.Equals(record.ModifiedBy, expected.ModifiedBy))
                throw InvalidPublication();
            if (receipt is not null && expectedReceipt is not null &&
                (receipt.WorkspaceId != expectedReceipt.WorkspaceId || receipt.ListId != expectedReceipt.ListId || receipt.RecordId != expectedReceipt.RecordId ||
                receipt.SchemaVersion != expectedReceipt.SchemaVersion || receipt.Revision != expectedReceipt.Revision || receipt.Number != expectedReceipt.Number ||
                receipt.CreatedUtc != expectedReceipt.CreatedUtc || receipt.ExpiresUtc != expectedReceipt.ExpiresUtc ||
                !StringComparer.Ordinal.Equals(receipt.Actor, expectedReceipt.Actor) || !StringComparer.Ordinal.Equals(receipt.RequestDigest, expectedReceipt.RequestDigest)))
                throw InvalidPublication();
        }
    }

    private static IEnumerable<Guid> ChangedFields(JsonObject previous, JsonObject current) => previous.Select(pair => pair.Key)
        .Concat(current.Select(pair => pair.Key)).Distinct().Where(key => previous.ContainsKey(key) != current.ContainsKey(key) ||
            !JsonNode.DeepEquals(previous[key], current[key])).Select(Guid.Parse);

    private void ValidateView(ListDefinition definition, SavedListView view)
    {
        ListValidation.ValidateQuery(definition, view.Query, _limits);
        if (view.Id == Guid.Empty || view.ListId != definition.Id || string.IsNullOrWhiteSpace(view.Name) ||
            view.Name.Length > 200 || !ListValidation.IsWellFormedText(view.Name)) throw Validation("A view ID and valid name are required.");
        if (view.VisibleFields is null || view.VisibleFields.Distinct().Count() != view.VisibleFields.Count ||
            view.VisibleFields.Any(id => definition.Fields.All(field => field.Id != id))) throw Validation("A view contains an unknown or duplicate field.");
    }
}
