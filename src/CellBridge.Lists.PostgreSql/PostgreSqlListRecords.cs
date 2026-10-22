using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CellBridge.Lists.PostgreSql;

public sealed partial class PostgreSqlListEngine
{
    public async Task<CreateRecordResult> CreateRecordAsync(ListActor actor, Guid workspaceId, Guid listId,
        CreateRecordRequest request, CancellationToken ct = default)
    {
        ValidateActor(actor);
        var digest = ListValidation.CreateDigest(request);
        var assignedFieldIds = ParseAssignedFieldIds(request.Values);
        var authorization = await AuthorizeForListWithReferencesAsync(actor, workspaceId, listId,
            [ListAuthorizationAction.Write], assignedFieldIds, ct);
        var result = await InRecordTransactionAsync(workspaceId, authorization, async (connection, transaction) =>
        {
            var context = await ReadWriteContextAsync(connection, transaction, workspaceId, listId,
                request.Id, actor.Subject, ListAccess.Write, deleteLock: false, ct, authorization);
            RequireWriteContext(context, ListAccess.Write);
            if (context.Receipt is { Live: true } receipt)
            {
                if (receipt.Actor != actor.Subject || receipt.Digest != digest || receipt.SchemaVersion != request.SchemaVersion)
                    throw Conflict("That record ID has a different create receipt.");
                if (context.ReceiptDefinition is null)
                    throw new ListException(ListFailure.Unavailable, "The receipt schema version is missing.");
                ListValidation.ValidateValues(context.ReceiptDefinition, request.Values, _limits);
                return new CreateRecordResult(request.Id, receipt.Revision, receipt.Number, true);
            }
            if (context.Receipt is { } expired)
            {
                await DeleteExpiredReceiptAsync(connection, transaction, workspaceId, listId, request.Id, expired, ct);
                return new CreateRecordResult(request.Id, 0, 0);
            }

            var definition = context.Definition!;
            if (request.SchemaVersion != definition.SchemaVersion) throw Conflict("The current schema version is required.");
            ListValidation.ValidateValues(definition, request.Values, _limits);
            var references = ReferenceAssignments(definition, request.Values);
            if (references.Count != 0)
            {
                try
                {
                    await ValidateReferenceAssignmentsAsync(connection, transaction, workspaceId, actor.Subject, references, ct, authorization);
                }
                catch (ListException ex) when (ex.Failure is ListFailure.Validation or ListFailure.NotFound)
                {
                    var replay = await TryReplayConcurrentCreateAsync(connection, transaction, workspaceId,
                        listId, request, actor.Subject, digest, ct);
                    if (replay is not null) return replay;
                    throw;
                }
            }
            try
            {
                return await InsertRecordAsync(connection, transaction, workspaceId, listId, request, actor.Subject,
                    digest, PostgreSqlListJson.RecordValues(request.Values), references,
                    request.Values.Select(x => Guid.Parse(x.Key)).ToArray(), ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation &&
                                               ex.ConstraintName == "record_references_target_fk")
            { throw Validation("The reference target does not exist."); }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                       { SqlState: PostgresErrorCodes.ForeignKeyViolation, ConstraintName: "record_references_target_fk" })
            { throw Validation("The reference target does not exist."); }
        }, ct);
        if (result.Revision == 0) throw Conflict("That record ID was used by an expired create receipt.");
        return result;
    }

    public Task<ListRecord> GetRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        CancellationToken ct = default) => ReadAsync(actor, workspaceId, async (connection, transaction, member, authorization) =>
    {
        await RequireAccessAsync(connection, transaction, workspaceId, listId, actor.Subject, ListAccess.Read, ct, authorization);
        return await ReadRecordAsync(connection, transaction, workspaceId, listId, recordId, false, ct) ?? throw NotFound();
    }, [(listId, ListAuthorizationAction.Read)], ct);

    public async Task<ListRecord> UpdateRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        UpdateRecordRequest request, CancellationToken ct = default)
    {
        ValidateActor(actor);
        var assignedReferenceFields = request.Changes
            .Where(x => !x.Remove && x.Value is not null)
            .Select(x => x.FieldId).ToHashSet();
        var authorization = await AuthorizeForListWithReferencesAsync(actor, workspaceId, listId,
            [ListAuthorizationAction.Read, ListAuthorizationAction.Write], assignedReferenceFields, ct);
        return await InRecordTransactionAsync(workspaceId, authorization, async (connection, transaction) =>
        {
            var context = await ReadWriteContextAsync(connection, transaction, workspaceId, listId,
                recordId, actor.Subject, ListAccess.Read | ListAccess.Write, deleteLock: false, ct, authorization);
            RequireWriteContext(context, ListAccess.Read | ListAccess.Write);
            var definition = context.Definition!;
            if (request.SchemaVersion != definition.SchemaVersion) throw Conflict("The current schema version is required.");
            var current = context.Record ?? throw NotFound();
            if (current.Revision != request.ExpectedRevision) throw Conflict("The record revision changed.");

            var values = ListValidation.ApplyChanges(definition, current.Values, request.Changes, _limits);
            var changedReferenceIds = request.Changes
                .Where(change => definition.Fields.Any(field => field.Id == change.FieldId && field.Type == ListFieldType.Reference))
                .Select(change => change.FieldId).ToHashSet();
            var assignedReferenceIds = request.Changes.Where(x => !x.Remove).Select(x => x.FieldId).ToHashSet();
            assignedReferenceIds.IntersectWith(changedReferenceIds);
            var references = ReferenceAssignments(definition, values, assignedReferenceIds);
            if (references.Count != 0)
                await ValidateReferenceAssignmentsAsync(connection, transaction, workspaceId, actor.Subject, references, ct, authorization);
            try
            {
                return await PersistUpdateAsync(connection, transaction, workspaceId, listId, current,
                    definition.SchemaVersion, PostgreSqlListJson.RecordValues(values), actor.Subject, request.Changes,
                    changedReferenceIds, references, context.RecordBytes, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation &&
                                               ex.ConstraintName == "record_references_target_fk")
            { throw Validation("The reference target does not exist."); }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                       { SqlState: PostgresErrorCodes.ForeignKeyViolation, ConstraintName: "record_references_target_fk" })
            { throw Validation("The reference target does not exist."); }
        }, ct);
    }

    public async Task DeleteRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        long expectedRevision, CancellationToken ct = default)
    {
        ValidateActor(actor);
        var authorization = await AuthorizeOperationAsync(actor, workspaceId,
            [(listId, ListAuthorizationAction.Write)], ct);
        await InRecordTransactionAsync(workspaceId, authorization, async (connection, transaction) =>
        {
            var context = await ReadWriteContextAsync(connection, transaction, workspaceId, listId,
                recordId, actor.Subject, ListAccess.Write, deleteLock: true, ct, authorization);
            RequireWriteContext(context, ListAccess.Write);
            var current = context.Record ?? throw NotFound();
            if (current.Revision != expectedRevision) throw Conflict("The record revision changed.");
            try
            {
                await PersistDeleteAsync(connection, transaction, workspaceId, listId, current,
                    actor.Subject, context.RecordBytes, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation &&
                                               ex.ConstraintName == "record_references_target_fk")
            { throw Conflict("The record is referenced by another record."); }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                       { SqlState: PostgresErrorCodes.ForeignKeyViolation, ConstraintName: "record_references_target_fk" })
            { throw Conflict("The record is referenced by another record."); }
            return true;
        }, ct);
    }

    public Task<MergePreview> PreviewMergeAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        MergeRequest request, CancellationToken ct = default) => ReadAsync(actor, workspaceId, async (connection, transaction, member, authorization) =>
    {
        await RequireAccessAsync(connection, transaction, workspaceId, listId, actor.Subject, ListAccess.Read | ListAccess.Write, ct, authorization);
        await LockListSchemaSharedAsync(connection, transaction, workspaceId, listId, ct);
        var definition = await GetDefinitionAsync(connection, transaction, workspaceId, listId, false, ct);
        var current = await ReadRecordAsync(connection, transaction, workspaceId, listId, recordId, false, ct) ?? throw NotFound();
        return ListMerge.Preview(definition, current, request, _limits);
    }, [(listId, ListAuthorizationAction.Read), (listId, ListAuthorizationAction.Write)], ct);

    public async Task<ReferenceLabel> ResolveReferenceAsync(ListActor actor, Guid workspaceId, Guid listId,
        Guid recordId, Guid fieldId, CancellationToken ct = default)
    {
        ValidateActor(actor);
        var authorization = await AuthorizeForListWithReferencesAsync(actor, workspaceId, listId,
            [ListAuthorizationAction.Read], new HashSet<Guid> { fieldId }, ct);
        return await InTransactionAsync(workspaceId, false, authorization, async (connection, transaction) =>
        {
            await AuthorizeMemberAsync(connection, transaction, workspaceId, actor.Subject, ct, authorization);
            await RequireAccessAsync(connection, transaction, workspaceId, listId, actor.Subject, ListAccess.Read, ct, authorization);
            var source = await GetDefinitionAsync(connection, transaction, workspaceId, listId, false, ct);
            var field = source.Fields.SingleOrDefault(x => x.Id == fieldId && x.Type == ListFieldType.Reference)
                ?? throw Validation("The reference field does not exist.");
            var sourceRecord = await ReadRecordAsync(connection, transaction, workspaceId, listId, recordId, false, ct) ?? throw NotFound();
            if (sourceRecord.Values[fieldId.ToString("D")] is not JsonValue referenceValue) throw NotFound();
            var targetRecordId = Guid.Parse(referenceValue.GetValue<string>());
            await RequireAccessAsync(connection, transaction, workspaceId, field.ReferenceListId!.Value, actor.Subject, ListAccess.Read, ct, authorization);
            var targetDefinition = await GetDefinitionAsync(connection, transaction, workspaceId, field.ReferenceListId.Value, false, ct);
            var target = await ReadRecordAsync(connection, transaction, workspaceId, targetDefinition.Id, targetRecordId, false, ct) ?? throw NotFound();
            JsonNode? label = null;
            if (field.ReferenceDisplayFieldId is { } displayId)
            {
                if (targetDefinition.Fields.All(x => x.Id != displayId)) throw Validation("The reference display field does not exist.");
                target.Values.TryGetPropertyValue(displayId.ToString("D"), out label);
            }
            return new ReferenceLabel(targetRecordId, target.Number, label?.DeepClone());
        }, ct);
    }

    private async Task<OperationAuthorization?> AuthorizeForListWithReferencesAsync(ListActor actor,
        Guid workspaceId, Guid listId, IReadOnlyList<ListAuthorizationAction> sourceActions,
        IReadOnlySet<Guid> referenceFieldIds, CancellationToken ct)
    {
        var sourceRequirements = sourceActions.Select(x => ((Guid?)listId, x)).ToArray();
        var sourceAuthorization = await AuthorizeOperationAsync(actor, workspaceId, sourceRequirements, ct);
        if (_authorizationProvider is null) return sourceAuthorization;
        var definition = await ReadDefinitionPreflightAsync(actor.Subject, workspaceId, listId, sourceAuthorization!, ct);
        var requirements = definition.Fields.Where(x => x.Type == ListFieldType.Reference && referenceFieldIds.Contains(x.Id))
            .Select(x => ((Guid?)x.ReferenceListId, ListAuthorizationAction.Read));
        return await AuthorizeAdditionalAsync(actor, sourceAuthorization!, requirements.Distinct().ToArray(), ct);
    }

    private Task<ListDefinition> ReadDefinitionPreflightAsync(string subject, Guid workspaceId, Guid listId,
        OperationAuthorization authorization, CancellationToken ct) =>
        InTransactionAsync(workspaceId, false, authorization, async (connection, transaction) =>
        {
            await AuthorizeMemberAsync(connection, transaction, workspaceId, subject, ct, authorization);
            return await GetDefinitionAsync(connection, transaction, workspaceId, listId, false, ct);
        }, ct);

    private async Task<T> InRecordTransactionAsync<T>(Guid workspaceId, OperationAuthorization? authorization,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> operation, CancellationToken ct)
    {
        if (workspaceId == Guid.Empty) throw Validation("A workspace ID is required.");
        await EnsureHealthyAsync(ct);
        try
        {
            await using var db = CreateContext();
            await db.Database.OpenConnectionAsync(ct);
            await using var efTransaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var transaction = (NpgsqlTransaction)efTransaction.GetDbTransaction();
            var result = await operation(connection, transaction);
            await efTransaction.CommitAsync(CancellationToken.None);
            return result;
        }
        catch (ListException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.QueryCanceled or PostgresErrorCodes.LockNotAvailable)
        { throw new ListException(ListFailure.Unavailable, "The list operation timed out."); }
        catch (DbUpdateException ex) when (ex.InnerException is NpgsqlException npgsql)
        { throw new ListException(ListFailure.Unavailable, $"The list store is unavailable: {npgsql.Message}"); }
        catch (NpgsqlException ex)
        { throw new ListException(ListFailure.Unavailable, $"The list store is unavailable: {ex.Message}"); }
    }

    private async Task<RecordWriteContext> ReadWriteContextAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid workspaceId, Guid listId, Guid recordId, string subject,
        ListAccess requiredAccess, bool deleteLock, CancellationToken ct, OperationAuthorization? authorization)
    {
        if (authorization is not null && !authorization.Allows(listId, requiredAccess)) throw NotFound();
        await ConfigureAsync(connection, transaction, workspaceId, ct);
        await AcquireAuthorizationLockAsync(connection, transaction, workspaceId, false, authorization, ct);
        await LockListSchemaSharedAsync(connection, transaction, workspaceId, listId, ct);
        if (recordId != Guid.Empty)
            await ExecuteAsync(connection, transaction,
                $"SELECT record_id FROM cellbridge_lists.records WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3 {(deleteLock ? "FOR UPDATE" : "FOR NO KEY UPDATE")}",
                ct, workspaceId, listId, recordId);

        await using var db = CreateContext(connection, transaction);
        var workspace = await db.Workspaces.AsNoTracking()
            .SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId, ct) ?? throw Forbidden();
        if (workspace.SecurityAuditCount >= _limits.MaxSecurityAuditEvents)
            throw new ListException(ListFailure.Unavailable, "The workspace security audit reserve is exhausted.");
        ListAccess access;
        if (authorization is null)
        {
            if (!await db.Memberships.AsNoTracking().AnyAsync(
                    row => row.WorkspaceId == workspaceId && row.Subject == subject, ct))
                throw Forbidden();
            access = (ListAccess)(await db.ListGrants.AsNoTracking()
                .Where(row => row.WorkspaceId == workspaceId && row.ListId == listId && row.Subject == subject)
                .Select(row => (int?)row.Access).SingleOrDefaultAsync(ct) ?? 0);
        }
        else
            access = requiredAccess;

        var definitionJson = (access & requiredAccess) == requiredAccess
            ? await (from list in db.Lists.AsNoTracking()
                join schema in db.ListSchemas.AsNoTracking()
                    on new { list.WorkspaceId, list.ListId, list.SchemaVersion }
                    equals new { schema.WorkspaceId, schema.ListId, schema.SchemaVersion }
                where list.WorkspaceId == workspaceId && list.ListId == listId
                select schema.Definition).SingleOrDefaultAsync(ct)
            : null;
        var definition = definitionJson is null ? null : PostgreSqlListJson.Deserialize<ListDefinition>(definitionJson);
        var recordRow = definition is null || recordId == Guid.Empty ? null : await db.Records.AsNoTracking()
            .SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId && row.ListId == listId && row.RecordId == recordId, ct);
        var record = recordRow is null ? null : ToListRecord(recordRow);
        var recordBytes = recordRow is null ? 0 : Encoding.UTF8.GetByteCount(recordRow.ValuesJson);
        var receiptRow = definition is null || recordId == Guid.Empty ? null : await db.CreateReceipts.AsNoTracking()
            .SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId && row.ListId == listId && row.RecordId == recordId, ct);
        var receipt = receiptRow is null ? null : new Receipt(receiptRow.Actor, receiptRow.SchemaVersion,
            receiptRow.RequestDigest, receiptRow.Revision, receiptRow.Number,
            receiptRow.ExpiresUtc > await ClockAsync(connection, transaction, ct));
        var receiptDefinitionJson = receiptRow is null ? null : await db.ListSchemas.AsNoTracking()
            .Where(row => row.WorkspaceId == workspaceId && row.ListId == listId && row.SchemaVersion == receiptRow.SchemaVersion)
            .Select(row => row.Definition).SingleOrDefaultAsync(ct);
        var receiptDefinition = receiptDefinitionJson is null
            ? null : PostgreSqlListJson.Deserialize<ListDefinition>(receiptDefinitionJson);
        return new RecordWriteContext(access, definition, record, recordBytes, receipt, receiptDefinition);
    }

    private static void RequireWriteContext(RecordWriteContext context, ListAccess required)
    {
        if (context.Definition is null || (context.Access & required) != required) throw NotFound();
    }

    private async Task ValidateReferenceAssignmentsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, string subject, IReadOnlyList<ReferenceAssignment> references, CancellationToken ct,
        OperationAuthorization? authorization)
    {
        if (authorization is not null && references
            .Select(reference => reference.TargetListId)
            .Distinct()
            .Any(target => !authorization.Allows(target, ListAuthorizationAction.Read)))
            throw NotFound();
        await using var db = CreateContext(connection, transaction);
        foreach (var assignment in references.OrderBy(reference => reference.FieldId))
        {
            if (authorization is null)
            {
                var access = await db.ListGrants.AsNoTracking().Where(row => row.WorkspaceId == workspaceId &&
                        row.ListId == assignment.TargetListId && row.Subject == subject)
                    .Select(row => (int?)row.Access).SingleOrDefaultAsync(ct);
                if (access is null || ((ListAccess)access.Value & ListAccess.Read) == 0) throw NotFound();
            }
            var definitionJson = await (from list in db.Lists.AsNoTracking()
                join schema in db.ListSchemas.AsNoTracking()
                    on new { list.WorkspaceId, list.ListId, list.SchemaVersion }
                    equals new { schema.WorkspaceId, schema.ListId, schema.SchemaVersion }
                where list.WorkspaceId == workspaceId && list.ListId == assignment.TargetListId
                select schema.Definition).SingleOrDefaultAsync(ct);
            if (definitionJson is null) throw NotFound();
            var targetDefinition = PostgreSqlListJson.Deserialize<ListDefinition>(definitionJson);
            if (assignment.DisplayFieldId is { } displayId && targetDefinition.Fields.All(x => x.Id != displayId))
                throw Validation("The reference display field does not exist.");
            await using var command = Command(connection, transaction,
                "SELECT record_id FROM cellbridge_lists.records WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3 FOR KEY SHARE",
                workspaceId, assignment.TargetListId, assignment.TargetRecordId);
            if (await command.ExecuteScalarAsync(ct) is null)
                throw Validation("The reference target does not exist.");
        }
    }

    private async Task<CreateRecordResult> InsertRecordAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, Guid listId, CreateRecordRequest request, string actor, string digest, string valuesJson,
        IReadOnlyList<ReferenceAssignment> references, Guid[] auditFields, CancellationToken ct)
    {
        var receiptBytes = Encoding.UTF8.GetByteCount(digest) + Encoding.UTF8.GetByteCount(actor) + 192L;
        var auditBytes = AuditStorageBytes(actor, "record.create", auditFields.Length);
        var valueBytes = await CanonicalJsonBytesAsync(connection, transaction, valuesJson, ct);
        if (valueBytes > _limits.MaxRecordBytes)
            throw Limit("The normalized record exceeds the configured byte limit.");

        // PostgreSQL performs only the irreversible identity claim. All persistent list data below uses EF.
        await using var claim = Command(connection, transaction, """
            INSERT INTO cellbridge_lists.record_tombstones(workspace_id,list_id,record_id)
            VALUES ($1,$2,$3) ON CONFLICT DO NOTHING RETURNING 1
            """, workspaceId, listId, request.Id);
        if (await claim.ExecuteScalarAsync(ct) is null)
            return await ReplayConcurrentCreateAsync(connection, transaction, workspaceId, listId, request,
                actor, digest, ct);

        await LockQuotaAsync(connection, transaction, workspaceId, ct);
        await ExecuteAsync(connection, transaction,
            "SELECT list_id FROM cellbridge_lists.list_counters WHERE workspace_id=$1 AND list_id=$2 FOR UPDATE",
            ct, workspaceId, listId);
        await using var db = CreateContext(connection, transaction);
        var counter = await db.ListCounters.SingleAsync(
            row => row.WorkspaceId == workspaceId && row.ListId == listId, ct);
        if (_limits.MaxRecordsPerList is { } maxRecords && counter.IdentityCount >= maxRecords)
            throw Limit("The record identity limit has been reached.");
        var number = counter.NextNumber;
        counter.IdentityCount = checked(counter.IdentityCount + 1);
        counter.NextNumber = checked(counter.NextNumber + 1);

        var workspace = await db.Workspaces.SingleAsync(row => row.WorkspaceId == workspaceId, ct);
        var storageDelta = checked(valueBytes + receiptBytes + 128 + auditBytes);
        if (_limits.MaxWorkspaceBytes is { } maxBytes && workspace.StoredBytes + storageDelta > maxBytes ||
            _limits.MaxAuditEvents is { } maxAudits && workspace.AuditCount >= maxAudits)
            throw Limit("The workspace storage or audit limit has been reached.");
        workspace.StoredBytes = checked(workspace.StoredBytes + storageDelta);
        workspace.AuditCount = checked(workspace.AuditCount + 1);
        var now = await ClockAsync(connection, transaction, ct);
        db.Records.Add(new RecordRow { WorkspaceId = workspaceId, ListId = listId, RecordId = request.Id,
            SchemaVersion = request.SchemaVersion, Revision = 1, Number = number, ValuesJson = valuesJson,
            CreatedUtc = now, ModifiedUtc = now, CreatedBy = actor, ModifiedBy = actor });
        await db.SaveChangesAsync(ct);

        db.RecordReferences.AddRange(references.Select(reference => new RecordReferenceRow {
            WorkspaceId = workspaceId, SourceListId = listId, SourceRecordId = request.Id,
            FieldId = reference.FieldId, TargetListId = reference.TargetListId,
            TargetRecordId = reference.TargetRecordId }));
        db.CreateReceipts.Add(new CreateReceiptRow { WorkspaceId = workspaceId, ListId = listId,
            RecordId = request.Id, Actor = actor, SchemaVersion = request.SchemaVersion,
            RequestDigest = digest, Revision = 1, Number = number, CreatedUtc = now,
            ExpiresUtc = now.Add(_limits.ReceiptLifetime) });
        db.AuditEvents.Add(new AuditEventRow { WorkspaceId = workspaceId, Utc = now, Actor = actor,
            Action = "record.create", ListId = listId, RecordId = request.Id, Revision = 1,
            FieldIds = auditFields });
        await db.SaveChangesAsync(ct);
        return new CreateRecordResult(request.Id, 1, number);
    }

    private async Task<CreateRecordResult> ReplayConcurrentCreateAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid workspaceId, Guid listId, CreateRecordRequest request,
        string actor, string digest, CancellationToken ct)
        => await TryReplayConcurrentCreateAsync(connection, transaction, workspaceId, listId, request,
               actor, digest, ct)
           ?? throw Conflict("That record ID was already used.");

    private async Task<CreateRecordResult?> TryReplayConcurrentCreateAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid workspaceId, Guid listId, CreateRecordRequest request,
        string actor, string digest, CancellationToken ct)
    {
        await using var db = CreateContext(connection, transaction);
        var receipt = await db.CreateReceipts.AsNoTracking().SingleOrDefaultAsync(row =>
            row.WorkspaceId == workspaceId && row.ListId == listId && row.RecordId == request.Id, ct);
        if (receipt is null || receipt.ExpiresUtc <= await ClockAsync(connection, transaction, ct)) return null;
        if (receipt.Actor != actor || receipt.SchemaVersion != request.SchemaVersion ||
            receipt.RequestDigest != digest) return null;
        var definitionJson = await db.ListSchemas.AsNoTracking().Where(row =>
                row.WorkspaceId == workspaceId && row.ListId == listId && row.SchemaVersion == receipt.SchemaVersion)
            .Select(row => row.Definition).SingleOrDefaultAsync(ct);
        if (definitionJson is null)
            throw new ListException(ListFailure.Unavailable, "The receipt schema version is missing.");
        var definition = PostgreSqlListJson.Deserialize<ListDefinition>(definitionJson);
        ListValidation.ValidateValues(definition, request.Values, _limits);
        return new CreateRecordResult(request.Id, receipt.Revision, receipt.Number, true);
    }

    private async Task<ListRecord> PersistUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, Guid listId, ListRecord current, long schemaVersion, string valuesJson, string actor,
        IReadOnlyList<FieldChange> changes, IReadOnlySet<Guid> changedReferenceIds,
        IReadOnlyList<ReferenceAssignment> references, long previousBytes, CancellationToken ct)
    {
        var auditFields = changes.Select(x => x.FieldId).ToArray();
        var auditBytes = AuditStorageBytes(actor, "record.update", auditFields.Length);
        var revision = checked(current.Revision + 1);
        var valueBytes = await CanonicalJsonBytesAsync(connection, transaction, valuesJson, ct);
        if (valueBytes > _limits.MaxRecordBytes)
            throw Limit("The normalized record exceeds the configured byte limit.");
        await LockQuotaAsync(connection, transaction, workspaceId, ct);
        await using var db = CreateContext(connection, transaction);
        var workspace = await db.Workspaces.SingleAsync(row => row.WorkspaceId == workspaceId, ct);
        var storageDelta = checked(valueBytes - previousBytes + auditBytes);
        var nextBytes = checked(workspace.StoredBytes + storageDelta);
        if (_limits.MaxWorkspaceBytes is { } maxBytes && nextBytes > maxBytes ||
            _limits.MaxAuditEvents is { } maxAudits && workspace.AuditCount >= maxAudits)
            throw Limit("The workspace storage or audit limit has been reached.");
        var record = await db.Records.SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId &&
            row.ListId == listId && row.RecordId == current.Id, ct);
        if (record is null || record.Revision != current.Revision)
            throw Conflict("The record revision changed.");
        workspace.StoredBytes = nextBytes;
        workspace.AuditCount = checked(workspace.AuditCount + 1);
        record.SchemaVersion = schemaVersion;
        record.Revision = revision;
        record.ValuesJson = valuesJson;
        record.ModifiedUtc = await ClockAsync(connection, transaction, ct);
        record.ModifiedBy = actor;

        var changed = changedReferenceIds.ToArray();
        var existingReferences = await db.RecordReferences.Where(row => row.WorkspaceId == workspaceId &&
            row.SourceListId == listId && row.SourceRecordId == current.Id && changed.Contains(row.FieldId))
            .ToListAsync(ct);
        var replacements = references.ToDictionary(reference => reference.FieldId);
        foreach (var existing in existingReferences)
        {
            if (!replacements.Remove(existing.FieldId))
                db.RecordReferences.Remove(existing);
            else
            {
                var replacement = references.Single(reference => reference.FieldId == existing.FieldId);
                existing.TargetListId = replacement.TargetListId;
                existing.TargetRecordId = replacement.TargetRecordId;
            }
        }
        db.RecordReferences.AddRange(replacements.Values.Select(reference => new RecordReferenceRow {
            WorkspaceId = workspaceId, SourceListId = listId, SourceRecordId = current.Id,
            FieldId = reference.FieldId, TargetListId = reference.TargetListId,
            TargetRecordId = reference.TargetRecordId }));
        db.AuditEvents.Add(new AuditEventRow { WorkspaceId = workspaceId, Utc = record.ModifiedUtc,
            Actor = actor, Action = "record.update", ListId = listId, RecordId = current.Id,
            Revision = revision, FieldIds = auditFields });
        await db.SaveChangesAsync(ct);
        return ToListRecord(record);
    }

    private async Task PersistDeleteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, Guid listId, ListRecord current, string actor, long previousBytes, CancellationToken ct)
    {
        var auditBytes = AuditStorageBytes(actor, "record.delete", 0);
        await LockQuotaAsync(connection, transaction, workspaceId, ct);
        await using var db = CreateContext(connection, transaction);
        var workspace = await db.Workspaces.SingleAsync(row => row.WorkspaceId == workspaceId, ct);
        var nextBytes = Math.Max(0, checked(workspace.StoredBytes - previousBytes + auditBytes));
        if (_limits.MaxWorkspaceBytes is { } maxBytes && nextBytes > maxBytes ||
            _limits.MaxAuditEvents is { } maxAudits && workspace.AuditCount >= maxAudits)
            throw Limit("The workspace storage or audit limit has been reached.");
        var record = await db.Records.SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId &&
            row.ListId == listId && row.RecordId == current.Id, ct);
        if (record is null || record.Revision != current.Revision)
            throw Conflict("The record revision changed.");
        workspace.StoredBytes = nextBytes;
        workspace.AuditCount = checked(workspace.AuditCount + 1);
        db.Records.Remove(record);
        db.AuditEvents.Add(new AuditEventRow { WorkspaceId = workspaceId,
            Utc = await ClockAsync(connection, transaction, ct), Actor = actor, Action = "record.delete",
            ListId = listId, RecordId = current.Id, Revision = current.Revision, FieldIds = [] });
        await db.SaveChangesAsync(ct);
    }

    private async Task DeleteExpiredReceiptAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, Guid listId, Guid recordId, Receipt receipt, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetByteCount(receipt.Digest) + Encoding.UTF8.GetByteCount(receipt.Actor) + 192L;
        await using var db = CreateContext(connection, transaction);
        var removed = await db.CreateReceipts.Where(row => row.WorkspaceId == workspaceId &&
            row.ListId == listId && row.RecordId == recordId).ExecuteDeleteAsync(ct);
        if (removed != 0)
            await db.Workspaces.Where(row => row.WorkspaceId == workspaceId)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.StoredBytes,
                    row => row.StoredBytes > bytes ? row.StoredBytes - bytes : 0), ct);
    }

    private static List<ReferenceAssignment> ReferenceAssignments(ListDefinition definition, JsonObject values,
        IReadOnlySet<Guid>? includedFields = null)
    {
        var result = new List<ReferenceAssignment>();
        foreach (var field in definition.Fields.Where(x => x.Type == ListFieldType.Reference &&
                     (includedFields is null || includedFields.Contains(x.Id))))
        {
            if (values[field.Id.ToString("D")] is not JsonValue value) continue;
            result.Add(new ReferenceAssignment(field.Id, field.ReferenceListId!.Value,
                Guid.Parse(value.GetValue<string>()), field.ReferenceDisplayFieldId));
        }
        return result;
    }

    private static HashSet<Guid> ParseAssignedFieldIds(JsonObject values)
    {
        var result = new HashSet<Guid>();
        foreach (var value in values)
        {
            if (!Guid.TryParseExact(value.Key, "D", out var fieldId) ||
                !StringComparer.Ordinal.Equals(value.Key, fieldId.ToString("D")))
                throw Validation("Value keys must be canonical field UUIDs.");
            if (value.Value is not null) result.Add(fieldId);
        }
        return result;
    }

    private static long AuditStorageBytes(string actor, string action, int fieldCount) =>
        96L + Encoding.UTF8.GetByteCount(actor) + Encoding.UTF8.GetByteCount(action) + fieldCount * 16L;

    private static async Task<DateTimeOffset> ClockAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT clock_timestamp()");
        return await command.ExecuteScalarAsync(ct) switch
        {
            DateTimeOffset value => value,
            DateTime value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException("PostgreSQL did not return its current timestamp.")
        };
    }

    private static async Task LockWorkspaceMutationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1,391814))", workspaceId.ToString("D"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task LockListSchemaSharedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, Guid listId, CancellationToken ct)
    {
        if (await ScalarAsync<Guid?>(connection, transaction,
            "SELECT list_id FROM cellbridge_lists.lists WHERE workspace_id=$1 AND list_id=$2 FOR SHARE", ct,
            workspaceId, listId) is null) throw NotFound();
    }

    private async Task<ListRecord?> ReadRecordAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, Guid listId, Guid recordId, bool forUpdate, CancellationToken ct)
    {
        if (forUpdate)
        {
            await using var command = Command(connection, transaction,
                "SELECT record_id FROM cellbridge_lists.records WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3 FOR UPDATE",
                workspaceId, listId, recordId);
            await command.ExecuteNonQueryAsync(ct);
        }
        await using var db = CreateContext(connection, transaction);
        var row = await db.Records.AsNoTracking().SingleOrDefaultAsync(value => value.WorkspaceId == workspaceId &&
            value.ListId == listId && value.RecordId == recordId, ct);
        return row is null ? null : ToListRecord(row);
    }

    private static ListRecord ToListRecord(RecordRow row) => new(row.RecordId, row.WorkspaceId, row.ListId,
        row.SchemaVersion, row.Revision, row.Number, PostgreSqlListJson.Object(row.ValuesJson),
        row.CreatedUtc, row.ModifiedUtc, row.CreatedBy, row.ModifiedBy);

    private sealed record RecordWriteContext(ListAccess Access, ListDefinition? Definition, ListRecord? Record,
        long RecordBytes, Receipt? Receipt, ListDefinition? ReceiptDefinition);
    private sealed record ReferenceAssignment(Guid FieldId, Guid TargetListId, Guid TargetRecordId, Guid? DisplayFieldId);
    private sealed record Receipt(string Actor, long SchemaVersion, string Digest, long Revision, long Number, bool Live);
}
