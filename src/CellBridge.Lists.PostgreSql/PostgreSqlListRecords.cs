using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
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
        var result = await InTransactionAsync(workspaceId, false, authorization, async (db, connection, transaction) =>
        {
            var context = await ReadCreateContextAsync(db, connection, transaction, workspaceId, listId,
                request.Id, actor.Subject, ListAccess.Write, deleteLock: false, ct, authorization);
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
                await DeleteExpiredReceiptAsync(db, workspaceId, listId, request.Id, expired, ct);
                return new CreateRecordResult(request.Id, 0, 0);
            }

            var definition = context.Definition;
            if (request.SchemaVersion != definition.SchemaVersion) throw Conflict("The current schema version is required.");
            ListValidation.ValidateValues(definition, request.Values, _limits);
            var references = ReferenceAssignments(definition, request.Values);
            if (references.Count != 0)
            {
                try
                {
                    await ValidateReferenceAssignmentsAsync(db, connection, transaction, workspaceId, actor.Subject, references, ct, authorization);
                }
                catch (ListException ex) when (ex.Failure is ListFailure.Validation or ListFailure.NotFound)
                {
                    var replay = await TryReplayConcurrentCreateAsync(db, workspaceId,
                        listId, request, actor.Subject, digest, ct);
                    if (replay is not null) return replay;
                    throw;
                }
            }
            try
            {
                return await InsertRecordAsync(db, connection, transaction, workspaceId, listId, request, actor.Subject,
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
        CancellationToken ct = default) => ReadAsync(actor, workspaceId, async (db, connection, transaction, member, authorization) =>
    {
        await RequireAccessAsync(db, workspaceId, listId, actor.Subject, ListAccess.Read, ct, authorization);
        return await ReadRecordAsync(db, connection, transaction, workspaceId, listId, recordId, false, ct) ?? throw NotFound();
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
        return await InTransactionAsync(workspaceId, false, authorization, async (db, connection, transaction) =>
        {
            var context = await ReadMutationContextAsync(db, connection, transaction, workspaceId, listId,
                recordId, actor.Subject, ListAccess.Read | ListAccess.Write, deleteLock: false, ct, authorization);
            var definition = context.Definition;
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
                await ValidateReferenceAssignmentsAsync(db, connection, transaction, workspaceId, actor.Subject, references, ct, authorization);
            try
            {
                return await PersistUpdateAsync(db, connection, transaction, workspaceId, listId, current,
                    context.RecordRow!,
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
        await InTransactionAsync(workspaceId, false, authorization, async (db, connection, transaction) =>
        {
            var context = await ReadMutationContextAsync(db, connection, transaction, workspaceId, listId,
                recordId, actor.Subject, ListAccess.Write, deleteLock: true, ct, authorization);
            var current = context.Record ?? throw NotFound();
            if (current.Revision != expectedRevision) throw Conflict("The record revision changed.");
            try
            {
                await PersistDeleteAsync(db, connection, transaction, workspaceId, listId, current,
                    context.RecordRow!,
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
        MergeRequest request, CancellationToken ct = default) => ReadAsync(actor, workspaceId, async (db, connection, transaction, member, authorization) =>
    {
        await RequireAccessAsync(db, workspaceId, listId, actor.Subject, ListAccess.Read | ListAccess.Write, ct, authorization);
        await LockListSchemaSharedAsync(connection, transaction, workspaceId, listId, ct);
        var definition = await GetDefinitionAsync(db, connection, transaction, workspaceId, listId, false, ct);
        var current = await ReadRecordAsync(db, connection, transaction, workspaceId, listId, recordId, false, ct) ?? throw NotFound();
        return ListMerge.Preview(definition, current, request, _limits);
    }, [(listId, ListAuthorizationAction.Read), (listId, ListAuthorizationAction.Write)], ct);

    public async Task<ReferenceLabel> ResolveReferenceAsync(ListActor actor, Guid workspaceId, Guid listId,
        Guid recordId, Guid fieldId, CancellationToken ct = default)
    {
        ValidateActor(actor);
        var authorization = await AuthorizeForListWithReferencesAsync(actor, workspaceId, listId,
            [ListAuthorizationAction.Read], new HashSet<Guid> { fieldId }, ct);
        return await InTransactionAsync(workspaceId, false, authorization, async (db, connection, transaction) =>
        {
            await AuthorizeMemberAsync(db, workspaceId, actor.Subject, ct, authorization);
            await RequireAccessAsync(db, workspaceId, listId, actor.Subject, ListAccess.Read, ct, authorization);
            var source = await GetDefinitionAsync(db, connection, transaction, workspaceId, listId, false, ct);
            var field = source.Fields.SingleOrDefault(x => x.Id == fieldId && x.Type == ListFieldType.Reference)
                ?? throw Validation("The reference field does not exist.");
            var sourceRecord = await ReadRecordAsync(db, connection, transaction, workspaceId, listId, recordId, false, ct) ?? throw NotFound();
            if (sourceRecord.Values[fieldId.ToString("D")] is not JsonValue referenceValue) throw NotFound();
            var targetRecordId = Guid.Parse(referenceValue.GetValue<string>());
            await RequireAccessAsync(db, workspaceId, field.ReferenceListId!.Value, actor.Subject, ListAccess.Read, ct, authorization);
            var targetDefinition = await GetDefinitionAsync(db, connection, transaction, workspaceId, field.ReferenceListId.Value, false, ct);
            var target = await ReadRecordAsync(db, connection, transaction, workspaceId, targetDefinition.Id, targetRecordId, false, ct) ?? throw NotFound();
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
        InTransactionAsync(workspaceId, false, authorization, async (db, connection, transaction) =>
        {
            await AuthorizeMemberAsync(db, workspaceId, subject, ct, authorization);
            return await GetDefinitionAsync(db, connection, transaction, workspaceId, listId, false, ct);
        }, ct);

    private async Task<ListDefinition> ReadListDefinitionAsync(PostgreSqlListDbContext db,
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid workspaceId, Guid listId,
        string subject, ListAccess requiredAccess, CancellationToken ct, OperationAuthorization? authorization)
    {
        if (authorization is not null && !authorization.Allows(listId, requiredAccess)) throw NotFound();
        await LockRecordContextAsync(connection, transaction, workspaceId, listId, Guid.Empty, false, ct);
        var projection = await (
            from workspace in db.Workspaces.AsNoTracking()
            from list in db.Lists.AsNoTracking().Where(row =>
                row.WorkspaceId == workspace.WorkspaceId && row.ListId == listId)
            from schema in db.ListSchemas.AsNoTracking().Where(row =>
                row.WorkspaceId == list.WorkspaceId && row.ListId == list.ListId &&
                row.SchemaVersion == list.SchemaVersion)
            where workspace.WorkspaceId == workspaceId
            select new ListDefinitionProjection
            {
                SecurityAuditCount = workspace.SecurityAuditCount,
                IsMember = authorization != null || db.Memberships.Any(row =>
                    row.WorkspaceId == workspaceId && row.Subject == subject),
                Access = authorization != null
                    ? (int)requiredAccess
                    : db.ListGrants.Where(row => row.WorkspaceId == workspaceId &&
                            row.ListId == listId && row.Subject == subject)
                        .Select(row => (int?)row.Access).SingleOrDefault() ?? 0,
                DefinitionJson = schema.Definition
            }).SingleOrDefaultAsync(ct) ?? throw Forbidden();
        RequireProjectionAccess(projection.SecurityAuditCount, projection.IsMember, projection.Access,
            requiredAccess);
        return PostgreSqlListJson.Deserialize<ListDefinition>(projection.DefinitionJson);
    }

    private async Task<CreateRecordContext> ReadCreateContextAsync(PostgreSqlListDbContext db,
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid workspaceId, Guid listId,
        Guid recordId, string subject, ListAccess requiredAccess, bool deleteLock,
        CancellationToken ct, OperationAuthorization? authorization)
    {
        if (authorization is not null && !authorization.Allows(listId, requiredAccess)) throw NotFound();
        await LockRecordContextAsync(connection, transaction, workspaceId, listId, recordId, deleteLock, ct);
        var projection = await (
            from workspace in db.Workspaces.AsNoTracking()
            from list in db.Lists.AsNoTracking().Where(row =>
                row.WorkspaceId == workspace.WorkspaceId && row.ListId == listId)
            from schema in db.ListSchemas.AsNoTracking().Where(row =>
                row.WorkspaceId == list.WorkspaceId && row.ListId == list.ListId &&
                row.SchemaVersion == list.SchemaVersion)
            from receipt in db.CreateReceipts.AsNoTracking().Where(row => recordId != Guid.Empty &&
                row.WorkspaceId == workspace.WorkspaceId && row.ListId == listId &&
                row.RecordId == recordId).DefaultIfEmpty()
            from receiptSchema in db.ListSchemas.AsNoTracking().Where(row => receipt != null &&
                row.WorkspaceId == workspace.WorkspaceId && row.ListId == listId &&
                row.SchemaVersion == receipt.SchemaVersion).DefaultIfEmpty()
            where workspace.WorkspaceId == workspaceId
            select new CreateContextProjection
            {
                SecurityAuditCount = workspace.SecurityAuditCount,
                IsMember = authorization != null || db.Memberships.Any(row =>
                    row.WorkspaceId == workspaceId && row.Subject == subject),
                Access = authorization != null
                    ? (int)requiredAccess
                    : db.ListGrants.Where(row => row.WorkspaceId == workspaceId &&
                            row.ListId == listId && row.Subject == subject)
                        .Select(row => (int?)row.Access).SingleOrDefault() ?? 0,
                DefinitionJson = schema.Definition,
                Receipt = receipt,
                ReceiptLive = receipt != null && receipt.ExpiresUtc > PostgreSqlListDbContext.ClockTimestamp(),
                ReceiptDefinitionJson = receiptSchema == null ? null : receiptSchema.Definition
            }).SingleOrDefaultAsync(ct) ?? throw Forbidden();
        RequireProjectionAccess(projection.SecurityAuditCount, projection.IsMember, projection.Access,
            requiredAccess);
        var definition = PostgreSqlListJson.Deserialize<ListDefinition>(projection.DefinitionJson);
        var receiptRow = projection.Receipt;
        var currentReceipt = receiptRow is null ? null : new Receipt(receiptRow.Actor, receiptRow.SchemaVersion,
            receiptRow.RequestDigest, receiptRow.Revision, receiptRow.Number, projection.ReceiptLive);
        var receiptDefinition = projection.ReceiptDefinitionJson is null
            ? null : PostgreSqlListJson.Deserialize<ListDefinition>(projection.ReceiptDefinitionJson);
        return new CreateRecordContext(definition, currentReceipt, receiptDefinition);
    }

    private async Task<RecordMutationContext> ReadMutationContextAsync(PostgreSqlListDbContext db,
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid workspaceId, Guid listId,
        Guid recordId, string subject, ListAccess requiredAccess, bool deleteLock,
        CancellationToken ct, OperationAuthorization? authorization)
    {
        if (authorization is not null && !authorization.Allows(listId, requiredAccess)) throw NotFound();
        await LockRecordContextAsync(connection, transaction, workspaceId, listId, recordId, deleteLock, ct);
        var projection = await (
            from workspace in db.Workspaces.AsNoTracking()
            from list in db.Lists.AsNoTracking().Where(row =>
                row.WorkspaceId == workspace.WorkspaceId && row.ListId == listId)
            from schema in db.ListSchemas.AsNoTracking().Where(row =>
                row.WorkspaceId == list.WorkspaceId && row.ListId == list.ListId &&
                row.SchemaVersion == list.SchemaVersion)
            from record in db.Records.AsNoTracking().Where(row => recordId != Guid.Empty &&
                row.WorkspaceId == workspace.WorkspaceId && row.ListId == listId &&
                row.RecordId == recordId).DefaultIfEmpty()
            where workspace.WorkspaceId == workspaceId
            select new MutationContextProjection
            {
                SecurityAuditCount = workspace.SecurityAuditCount,
                IsMember = authorization != null || db.Memberships.Any(row =>
                    row.WorkspaceId == workspaceId && row.Subject == subject),
                Access = authorization != null
                    ? (int)requiredAccess
                    : db.ListGrants.Where(row => row.WorkspaceId == workspaceId &&
                            row.ListId == listId && row.Subject == subject)
                        .Select(row => (int?)row.Access).SingleOrDefault() ?? 0,
                DefinitionJson = schema.Definition,
                Record = record
            }).SingleOrDefaultAsync(ct) ?? throw Forbidden();
        RequireProjectionAccess(projection.SecurityAuditCount, projection.IsMember, projection.Access,
            requiredAccess);
        var recordRow = projection.Record;
        return new RecordMutationContext(
            PostgreSqlListJson.Deserialize<ListDefinition>(projection.DefinitionJson),
            recordRow is null ? null : ToListRecord(recordRow), recordRow,
            recordRow is null ? 0 : Encoding.UTF8.GetByteCount(recordRow.ValuesJson));
    }

    private void RequireProjectionAccess(int securityAuditCount, bool isMember, int access,
        ListAccess requiredAccess)
    {
        if (securityAuditCount >= _limits.MaxSecurityAuditEvents)
            throw new ListException(ListFailure.Unavailable, "The workspace security audit reserve is exhausted.");
        if (!isMember) throw Forbidden();
        if (((ListAccess)access & requiredAccess) != requiredAccess) throw NotFound();
    }

    private static async Task LockRecordContextAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid workspaceId, Guid listId, Guid recordId,
        bool deleteLock, CancellationToken ct)
    {
        await using var batch = new NpgsqlBatch(connection, transaction);
        var list = new NpgsqlBatchCommand("""
            SELECT list_id FROM cellbridge_lists.lists
            WHERE workspace_id=$1 AND list_id=$2 FOR SHARE
            """);
        list.Parameters.AddWithValue(workspaceId);
        list.Parameters.AddWithValue(listId);
        batch.BatchCommands.Add(list);
        if (recordId != Guid.Empty)
        {
            var record = new NpgsqlBatchCommand($$"""
                SELECT record_id FROM cellbridge_lists.records
                WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3
                {{(deleteLock ? "FOR UPDATE" : "FOR NO KEY UPDATE")}}
                """);
            record.Parameters.AddWithValue(workspaceId);
            record.Parameters.AddWithValue(listId);
            record.Parameters.AddWithValue(recordId);
            batch.BatchCommands.Add(record);
        }
        await using var reader = await batch.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw NotFound();
        if (recordId != Guid.Empty) await reader.NextResultAsync(ct);
    }

    private async Task ValidateReferenceAssignmentsAsync(PostgreSqlListDbContext db,
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid workspaceId, string subject,
        IReadOnlyList<ReferenceAssignment> references, CancellationToken ct,
        OperationAuthorization? authorization)
    {
        if (authorization is not null && references
            .Select(reference => reference.TargetListId)
            .Distinct()
            .Any(target => !authorization.Allows(target, ListAuthorizationAction.Read)))
            throw NotFound();
        var targetIds = references.Select(reference => reference.TargetListId).Distinct().ToArray();
        var definitions = authorization is null
            ? await (from list in db.Lists.AsNoTracking()
                join schema in db.ListSchemas.AsNoTracking()
                    on new { list.WorkspaceId, list.ListId, list.SchemaVersion }
                    equals new { schema.WorkspaceId, schema.ListId, schema.SchemaVersion }
                join grant in db.ListGrants.AsNoTracking()
                    on new { list.WorkspaceId, list.ListId } equals new { grant.WorkspaceId, grant.ListId }
                where list.WorkspaceId == workspaceId && targetIds.Contains(list.ListId) &&
                      grant.Subject == subject && (grant.Access & (int)ListAccess.Read) != 0
                select new { list.ListId, schema.Definition }).ToDictionaryAsync(row => row.ListId,
                row => row.Definition, ct)
            : await (from list in db.Lists.AsNoTracking()
                join schema in db.ListSchemas.AsNoTracking()
                    on new { list.WorkspaceId, list.ListId, list.SchemaVersion }
                    equals new { schema.WorkspaceId, schema.ListId, schema.SchemaVersion }
                where list.WorkspaceId == workspaceId && targetIds.Contains(list.ListId)
                select new { list.ListId, schema.Definition }).ToDictionaryAsync(row => row.ListId,
                row => row.Definition, ct);
        var targetDefinitions = definitions.ToDictionary(pair => pair.Key,
            pair => PostgreSqlListJson.Deserialize<ListDefinition>(pair.Value));
        var ordered = references.OrderBy(reference => reference.FieldId).ToArray();
        // Filter locks without throwing so validation below retains field-ID error order.
        var lockableFieldIds = ordered.Where(assignment =>
                targetDefinitions.TryGetValue(assignment.TargetListId, out var targetDefinition) &&
                (assignment.DisplayFieldId is not { } displayId || targetDefinition.Fields.Any(x => x.Id == displayId)))
            .Select(assignment => assignment.FieldId).ToArray();
        await using var command = new NpgsqlCommand("""
            SELECT requested.field_id, target.record_id
            FROM unnest($1::uuid[],$2::uuid[],$3::uuid[]) AS requested(field_id,list_id,record_id)
            LEFT JOIN LATERAL (
              SELECT record_id FROM cellbridge_lists.records
              WHERE workspace_id=$4 AND list_id=requested.list_id AND record_id=requested.record_id
                AND requested.field_id=ANY($5::uuid[])
              FOR KEY SHARE
            ) target ON true
            ORDER BY requested.field_id
            """, connection, transaction);
        command.Parameters.AddWithValue(ordered.Select(reference => reference.FieldId).ToArray());
        command.Parameters.AddWithValue(ordered.Select(reference => reference.TargetListId).ToArray());
        command.Parameters.AddWithValue(ordered.Select(reference => reference.TargetRecordId).ToArray());
        command.Parameters.AddWithValue(workspaceId);
        command.Parameters.AddWithValue(lockableFieldIds);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var existingTargets = new HashSet<Guid>();
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(1)) existingTargets.Add(reader.GetGuid(0));
        }
        foreach (var assignment in ordered)
        {
            if (!targetDefinitions.TryGetValue(assignment.TargetListId, out var targetDefinition)) throw NotFound();
            if (assignment.DisplayFieldId is { } displayId && targetDefinition.Fields.All(x => x.Id != displayId))
                throw Validation("The reference display field does not exist.");
            if (!existingTargets.Contains(assignment.FieldId))
                throw Validation("The reference target does not exist.");
        }
    }

    private async Task<CreateRecordResult> InsertRecordAsync(PostgreSqlListDbContext db,
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid workspaceId, Guid listId,
        CreateRecordRequest request, string actor, string digest, string valuesJson,
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
            return await ReplayConcurrentCreateAsync(db, workspaceId, listId, request,
                actor, digest, ct);

        await LockCreateStateAsync(connection, transaction, workspaceId, listId, ct);
        var state = await (from workspaceRow in db.Workspaces
            join counterRow in db.ListCounters
                on workspaceRow.WorkspaceId equals counterRow.WorkspaceId
            where workspaceRow.WorkspaceId == workspaceId && counterRow.ListId == listId
            select new
            {
                Workspace = workspaceRow,
                Counter = counterRow,
                Now = PostgreSqlListDbContext.ClockTimestamp()
            }).SingleAsync(ct);
        var counter = state.Counter;
        if (_limits.MaxRecordsPerList is { } maxRecords && counter.IdentityCount >= maxRecords)
            throw Limit("The record identity limit has been reached.");
        var number = counter.NextNumber;
        counter.IdentityCount = checked(counter.IdentityCount + 1);
        counter.NextNumber = checked(counter.NextNumber + 1);

        var workspace = state.Workspace;
        var storageDelta = checked(valueBytes + receiptBytes + 128 + auditBytes);
        if (_limits.MaxWorkspaceBytes is { } maxBytes && workspace.StoredBytes + storageDelta > maxBytes ||
            _limits.MaxAuditEvents is { } maxAudits && workspace.AuditCount >= maxAudits)
            throw Limit("The workspace storage or audit limit has been reached.");
        workspace.StoredBytes = checked(workspace.StoredBytes + storageDelta);
        workspace.AuditCount = checked(workspace.AuditCount + 1);
        var now = state.Now;
        db.Records.Add(new RecordRow { WorkspaceId = workspaceId, ListId = listId, RecordId = request.Id,
            SchemaVersion = request.SchemaVersion, Revision = 1, Number = number, ValuesJson = valuesJson,
            CreatedUtc = now, ModifiedUtc = now, CreatedBy = actor, ModifiedBy = actor });
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

    private static async Task LockCreateStateAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid workspaceId, Guid listId, CancellationToken ct)
    {
        await using var batch = new NpgsqlBatch(connection, transaction);
        var workspace = new NpgsqlBatchCommand("""
            SELECT workspace_id FROM cellbridge_lists.workspaces
            WHERE workspace_id=$1 FOR UPDATE
            """);
        workspace.Parameters.AddWithValue(workspaceId);
        batch.BatchCommands.Add(workspace);
        var counter = new NpgsqlBatchCommand("""
            SELECT list_id FROM cellbridge_lists.list_counters
            WHERE workspace_id=$1 AND list_id=$2 FOR UPDATE
            """);
        counter.Parameters.AddWithValue(workspaceId);
        counter.Parameters.AddWithValue(listId);
        batch.BatchCommands.Add(counter);
        await batch.ExecuteNonQueryAsync(ct);
    }

    private async Task<CreateRecordResult> ReplayConcurrentCreateAsync(PostgreSqlListDbContext db,
        Guid workspaceId, Guid listId, CreateRecordRequest request, string actor, string digest,
        CancellationToken ct)
        => await TryReplayConcurrentCreateAsync(db, workspaceId, listId, request,
               actor, digest, ct)
           ?? throw Conflict("That record ID was already used.");

    private async Task<CreateRecordResult?> TryReplayConcurrentCreateAsync(PostgreSqlListDbContext db,
        Guid workspaceId, Guid listId, CreateRecordRequest request, string actor, string digest,
        CancellationToken ct)
    {
        var replay = await (from receipt in db.CreateReceipts.AsNoTracking()
            from schema in db.ListSchemas.AsNoTracking().Where(row =>
                row.WorkspaceId == receipt.WorkspaceId && row.ListId == receipt.ListId &&
                row.SchemaVersion == receipt.SchemaVersion).DefaultIfEmpty()
            where receipt.WorkspaceId == workspaceId && receipt.ListId == listId &&
                  receipt.RecordId == request.Id
            select new ReceiptReplayProjection
            {
                Actor = receipt.Actor,
                SchemaVersion = receipt.SchemaVersion,
                Digest = receipt.RequestDigest,
                Revision = receipt.Revision,
                Number = receipt.Number,
                Live = receipt.ExpiresUtc > PostgreSqlListDbContext.ClockTimestamp(),
                DefinitionJson = schema == null ? null : schema.Definition
            }).SingleOrDefaultAsync(ct);
        if (replay is null || !replay.Live) return null;
        if (replay.Actor != actor || replay.SchemaVersion != request.SchemaVersion ||
            replay.Digest != digest) return null;
        if (replay.DefinitionJson is null)
            throw new ListException(ListFailure.Unavailable, "The receipt schema version is missing.");
        var definition = PostgreSqlListJson.Deserialize<ListDefinition>(replay.DefinitionJson);
        ListValidation.ValidateValues(definition, request.Values, _limits);
        return new CreateRecordResult(request.Id, replay.Revision, replay.Number, true);
    }

    private async Task<ListRecord> PersistUpdateAsync(PostgreSqlListDbContext db,
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, Guid listId, ListRecord current, RecordRow record, long schemaVersion,
        string valuesJson, string actor,
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
        var state = await db.Workspaces.Where(row => row.WorkspaceId == workspaceId)
            .Select(row => new
            {
                Workspace = row,
                Now = PostgreSqlListDbContext.ClockTimestamp()
            }).SingleAsync(ct);
        var workspace = state.Workspace;
        var storageDelta = checked(valueBytes - previousBytes + auditBytes);
        var nextBytes = checked(workspace.StoredBytes + storageDelta);
        if (_limits.MaxWorkspaceBytes is { } maxBytes && nextBytes > maxBytes ||
            _limits.MaxAuditEvents is { } maxAudits && workspace.AuditCount >= maxAudits)
            throw Limit("The workspace storage or audit limit has been reached.");
        db.Records.Attach(record);
        workspace.StoredBytes = nextBytes;
        workspace.AuditCount = checked(workspace.AuditCount + 1);
        record.SchemaVersion = schemaVersion;
        record.Revision = revision;
        record.ValuesJson = valuesJson;
        record.ModifiedUtc = state.Now;
        record.ModifiedBy = actor;

        var changed = changedReferenceIds.ToArray();
        List<RecordReferenceRow> existingReferences = changed.Length == 0
            ? []
            : await db.RecordReferences.Where(row => row.WorkspaceId == workspaceId &&
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

    private async Task PersistDeleteAsync(PostgreSqlListDbContext db,
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, Guid listId, ListRecord current, RecordRow record, string actor,
        long previousBytes, CancellationToken ct)
    {
        var auditBytes = AuditStorageBytes(actor, "record.delete", 0);
        await LockQuotaAsync(connection, transaction, workspaceId, ct);
        var state = await db.Workspaces.Where(row => row.WorkspaceId == workspaceId)
            .Select(row => new
            {
                Workspace = row,
                Now = PostgreSqlListDbContext.ClockTimestamp()
            }).SingleAsync(ct);
        var workspace = state.Workspace;
        var nextBytes = Math.Max(0, checked(workspace.StoredBytes - previousBytes + auditBytes));
        if (_limits.MaxWorkspaceBytes is { } maxBytes && nextBytes > maxBytes ||
            _limits.MaxAuditEvents is { } maxAudits && workspace.AuditCount >= maxAudits)
            throw Limit("The workspace storage or audit limit has been reached.");
        db.Records.Attach(record);
        workspace.StoredBytes = nextBytes;
        workspace.AuditCount = checked(workspace.AuditCount + 1);
        db.Records.Remove(record);
        db.AuditEvents.Add(new AuditEventRow { WorkspaceId = workspaceId,
            Utc = state.Now, Actor = actor, Action = "record.delete",
            ListId = listId, RecordId = current.Id, Revision = current.Revision, FieldIds = [] });
        await db.SaveChangesAsync(ct);
    }

    private async Task DeleteExpiredReceiptAsync(PostgreSqlListDbContext db, Guid workspaceId,
        Guid listId, Guid recordId, Receipt receipt, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetByteCount(receipt.Digest) + Encoding.UTF8.GetByteCount(receipt.Actor) + 192L;
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

    private async Task<ListRecord?> ReadRecordAsync(PostgreSqlListDbContext db,
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid workspaceId, Guid listId,
        Guid recordId, bool forUpdate, CancellationToken ct)
    {
        if (forUpdate)
        {
            await using var command = Command(connection, transaction,
                "SELECT record_id FROM cellbridge_lists.records WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3 FOR UPDATE",
                workspaceId, listId, recordId);
            await command.ExecuteNonQueryAsync(ct);
        }
        var row = await db.Records.AsNoTracking().SingleOrDefaultAsync(value => value.WorkspaceId == workspaceId &&
            value.ListId == listId && value.RecordId == recordId, ct);
        return row is null ? null : ToListRecord(row);
    }

    private static ListRecord ToListRecord(RecordRow row) => new(row.RecordId, row.WorkspaceId, row.ListId,
        row.SchemaVersion, row.Revision, row.Number, PostgreSqlListJson.Object(row.ValuesJson),
        row.CreatedUtc, row.ModifiedUtc, row.CreatedBy, row.ModifiedBy);

    private sealed record CreateRecordContext(ListDefinition Definition, Receipt? Receipt,
        ListDefinition? ReceiptDefinition);
    private sealed record RecordMutationContext(ListDefinition Definition, ListRecord? Record,
        RecordRow? RecordRow, long RecordBytes);
    private sealed record ReferenceAssignment(Guid FieldId, Guid TargetListId, Guid TargetRecordId, Guid? DisplayFieldId);
    private sealed record Receipt(string Actor, long SchemaVersion, string Digest, long Revision, long Number, bool Live);
    private sealed class ListDefinitionProjection
    {
        public int SecurityAuditCount { get; init; }
        public bool IsMember { get; init; }
        public int Access { get; init; }
        public required string DefinitionJson { get; init; }
    }

    private sealed class CreateContextProjection
    {
        public int SecurityAuditCount { get; init; }
        public bool IsMember { get; init; }
        public int Access { get; init; }
        public required string DefinitionJson { get; init; }
        public CreateReceiptRow? Receipt { get; init; }
        public bool ReceiptLive { get; init; }
        public string? ReceiptDefinitionJson { get; init; }
    }

    private sealed class MutationContextProjection
    {
        public int SecurityAuditCount { get; init; }
        public bool IsMember { get; init; }
        public int Access { get; init; }
        public required string DefinitionJson { get; init; }
        public RecordRow? Record { get; init; }
    }

    private sealed class ReceiptReplayProjection
    {
        public required string Actor { get; init; }
        public long SchemaVersion { get; init; }
        public required string Digest { get; init; }
        public long Revision { get; init; }
        public long Number { get; init; }
        public bool Live { get; init; }
        public string? DefinitionJson { get; init; }
    }
}
