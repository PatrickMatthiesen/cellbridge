using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace CellBridge.Lists.PostgreSql;

public sealed partial class PostgreSqlListEngine
{
    public async Task<CreateRecordResult> CreateRecordAsync(ListActor actor, Guid workspaceId, Guid listId,
        CreateRecordRequest request, CancellationToken ct = default)
    {
        ValidateActor(actor);
        var digest = ListValidation.CreateDigest(request);
        var result = await InRecordTransactionAsync(workspaceId, async (connection, transaction) =>
        {
            var context = await ReadWriteContextAsync(connection, transaction, workspaceId, listId,
                request.Id, actor.Subject, ListAccess.Write, deleteLock: false, ct);
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
                    await ValidateReferenceAssignmentsAsync(connection, transaction, workspaceId, actor.Subject, references, ct);
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
                    digest, request.Values.ToJsonString(), references,
                    request.Values.Select(x => Guid.Parse(x.Key)).ToArray(), ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation &&
                                               ex.ConstraintName == "record_references_target_fk")
            { throw Validation("The reference target does not exist."); }
        }, ct);
        if (result.Revision == 0) throw Conflict("That record ID was used by an expired create receipt.");
        return result;
    }

    public Task<ListRecord> GetRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        CancellationToken ct = default) => ReadAsync(actor, workspaceId, async (connection, transaction, member) =>
    {
        await RequireAccessAsync(connection, transaction, workspaceId, listId, actor.Subject, ListAccess.Read, ct);
        return await ReadRecordAsync(connection, transaction, workspaceId, listId, recordId, false, ct) ?? throw NotFound();
    }, ct);

    public async Task<ListRecord> UpdateRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        UpdateRecordRequest request, CancellationToken ct = default)
    {
        ValidateActor(actor);
        return await InRecordTransactionAsync(workspaceId, async (connection, transaction) =>
        {
            var context = await ReadWriteContextAsync(connection, transaction, workspaceId, listId,
                recordId, actor.Subject, ListAccess.Read | ListAccess.Write, deleteLock: false, ct);
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
                await ValidateReferenceAssignmentsAsync(connection, transaction, workspaceId, actor.Subject, references, ct);
            try
            {
                return await PersistUpdateAsync(connection, transaction, workspaceId, listId, current,
                    definition.SchemaVersion, values.ToJsonString(), actor.Subject, request.Changes,
                    changedReferenceIds, references, context.RecordBytes, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation &&
                                               ex.ConstraintName == "record_references_target_fk")
            { throw Validation("The reference target does not exist."); }
        }, ct);
    }

    public async Task DeleteRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        long expectedRevision, CancellationToken ct = default)
    {
        ValidateActor(actor);
        await InRecordTransactionAsync(workspaceId, async (connection, transaction) =>
        {
            var context = await ReadWriteContextAsync(connection, transaction, workspaceId, listId,
                recordId, actor.Subject, ListAccess.Write, deleteLock: true, ct);
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
            return true;
        }, ct);
    }

    public Task<MergePreview> PreviewMergeAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        MergeRequest request, CancellationToken ct = default) => ReadAsync(actor, workspaceId, async (connection, transaction, member) =>
    {
        await RequireAccessAsync(connection, transaction, workspaceId, listId, actor.Subject, ListAccess.Read | ListAccess.Write, ct);
        await LockListSchemaSharedAsync(connection, transaction, workspaceId, listId, ct);
        var definition = await GetDefinitionAsync(connection, transaction, workspaceId, listId, false, ct);
        var current = await ReadRecordAsync(connection, transaction, workspaceId, listId, recordId, false, ct) ?? throw NotFound();
        return ListMerge.Preview(definition, current, request, _limits);
    }, ct);

    public Task<ReferenceLabel> ResolveReferenceAsync(ListActor actor, Guid workspaceId, Guid listId,
        Guid recordId, Guid fieldId, CancellationToken ct = default) => ReadAsync(actor, workspaceId, async (connection, transaction, member) =>
    {
        await RequireAccessAsync(connection, transaction, workspaceId, listId, actor.Subject, ListAccess.Read, ct);
        var source = await GetDefinitionAsync(connection, transaction, workspaceId, listId, false, ct);
        var field = source.Fields.SingleOrDefault(x => x.Id == fieldId && x.Type == ListFieldType.Reference)
            ?? throw Validation("The reference field does not exist.");
        var sourceRecord = await ReadRecordAsync(connection, transaction, workspaceId, listId, recordId, false, ct) ?? throw NotFound();
        if (sourceRecord.Values[fieldId.ToString("D")] is not JsonValue referenceValue) throw NotFound();
        var targetRecordId = Guid.Parse(referenceValue.GetValue<string>());
        await RequireAccessAsync(connection, transaction, workspaceId, field.ReferenceListId!.Value, actor.Subject, ListAccess.Read, ct);
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

    private async Task<T> InRecordTransactionAsync<T>(Guid workspaceId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> operation, CancellationToken ct)
    {
        if (workspaceId == Guid.Empty) throw Validation("A workspace ID is required.");
        await EnsureHealthyAsync(ct);
        try
        {
            await using var connection = await _source.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            var result = await operation(connection, transaction);
            await transaction.CommitAsync(CancellationToken.None);
            return result;
        }
        catch (ListException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.QueryCanceled or PostgresErrorCodes.LockNotAvailable)
        { throw new ListException(ListFailure.Unavailable, "The list operation timed out."); }
        catch (NpgsqlException ex)
        { throw new ListException(ListFailure.Unavailable, $"The list store is unavailable: {ex.Message}"); }
    }

    private async Task<RecordWriteContext> ReadWriteContextAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid workspaceId, Guid listId, Guid recordId, string subject,
        ListAccess requiredAccess, bool deleteLock, CancellationToken ct)
    {
        var milliseconds = Math.Clamp((long)_limits.StatementTimeout.TotalMilliseconds, 1, int.MaxValue);
        await using var batch = new NpgsqlBatch(connection, transaction);
        var configure = new NpgsqlBatchCommand("""
            SELECT set_config('cellbridge.workspace_id',@workspace::text,true),
              set_config('synchronous_commit','on',true),
              set_config('statement_timeout',@timeout::text,true),
              set_config('lock_timeout',@timeout::text,true)
            """);
        configure.Parameters.AddWithValue("workspace", workspaceId);
        configure.Parameters.AddWithValue("timeout", milliseconds);
        batch.BatchCommands.Add(configure);
        var authorize = new NpgsqlBatchCommand("SELECT pg_advisory_xact_lock_shared(hashtextextended(@workspace::text,391813))");
        authorize.Parameters.AddWithValue("workspace", workspaceId);
        batch.BatchCommands.Add(authorize);

        var lockSource = new NpgsqlBatchCommand("""
            SELECT list_id FROM cellbridge_lists.lists
            WHERE workspace_id=@workspace AND list_id=@list FOR SHARE
            """);
        lockSource.Parameters.AddWithValue("workspace", workspaceId);
        lockSource.Parameters.AddWithValue("list", listId);
        batch.BatchCommands.Add(lockSource);

        var recordLock = deleteLock ? "FOR UPDATE" : "FOR NO KEY UPDATE";
        var read = new NpgsqlBatchCommand($$"""
            SELECT w.security_audit_count,g.access,
              source.definition,
              record.record_id,record.schema_version,record.revision,record.number,record.values_json,
              record.created_utc,record.modified_utc,record.created_by,record.modified_by,
              receipt.actor,receipt.schema_version,receipt.request_digest,receipt.revision,receipt.number,receipt.live,
              original.definition
            FROM cellbridge_lists.workspaces w
            JOIN cellbridge_lists.memberships m ON m.workspace_id=w.workspace_id AND m.subject=@subject
            LEFT JOIN cellbridge_lists.list_grants g ON g.workspace_id=w.workspace_id
              AND g.list_id=@list AND g.subject=@subject
            LEFT JOIN LATERAL (
              SELECT s.definition::text AS definition FROM cellbridge_lists.lists l
              JOIN cellbridge_lists.list_schemas s ON s.workspace_id=l.workspace_id AND s.list_id=l.list_id AND s.schema_version=l.schema_version
              WHERE l.workspace_id=@workspace AND l.list_id=@list
                AND (g.access & @required_access)=@required_access
            ) source ON true
            LEFT JOIN LATERAL (
              SELECT r.record_id,r.schema_version,r.revision,r.number,r.values_json::text AS values_json,
                r.created_utc,r.modified_utc,r.created_by,r.modified_by
              FROM cellbridge_lists.records r WHERE r.workspace_id=@workspace AND r.list_id=@list AND r.record_id=@record
                AND source.definition IS NOT NULL
              {{recordLock}}
            ) record ON true
            LEFT JOIN LATERAL (
              SELECT c.actor,c.schema_version,c.request_digest,c.revision,c.number,c.expires_utc>clock_timestamp() AS live
              FROM cellbridge_lists.create_receipts c WHERE c.workspace_id=@workspace AND c.list_id=@list AND c.record_id=@record
                AND source.definition IS NOT NULL
            ) receipt ON true
            LEFT JOIN LATERAL (
              SELECT s.definition::text AS definition FROM cellbridge_lists.list_schemas s
              WHERE s.workspace_id=@workspace AND s.list_id=@list AND s.schema_version=receipt.schema_version
            ) original ON receipt.schema_version IS NOT NULL
            WHERE w.workspace_id=@workspace
            """);
        read.Parameters.AddWithValue("workspace", workspaceId);
        read.Parameters.AddWithValue("list", listId);
        read.Parameters.AddWithValue("record", recordId);
        read.Parameters.AddWithValue("subject", subject);
        read.Parameters.AddWithValue("required_access", (int)requiredAccess);
        batch.BatchCommands.Add(read);
        await using var reader = await batch.ExecuteReaderAsync(ct);
        await reader.NextResultAsync(ct);
        await reader.NextResultAsync(ct);
        await reader.NextResultAsync(ct);
        if (!await reader.ReadAsync(ct)) throw Forbidden();
        if (Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture) >= _limits.MaxSecurityAuditEvents)
            throw new ListException(ListFailure.Unavailable, "The workspace security audit reserve is exhausted.");
        var access = reader.IsDBNull(1) ? ListAccess.None : (ListAccess)reader.GetInt32(1);
        var definition = reader.IsDBNull(2) ? null : PostgreSqlListJson.Deserialize<ListDefinition>(reader.GetString(2));
        ListRecord? record = null;
        long recordBytes = 0;
        if (!reader.IsDBNull(3))
        {
            var json = reader.GetString(7);
            recordBytes = Encoding.UTF8.GetByteCount(json);
            record = new ListRecord(reader.GetGuid(3), workspaceId, listId, reader.GetInt64(4), reader.GetInt64(5),
                reader.GetInt64(6), PostgreSqlListJson.Object(json),
                new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero), new DateTimeOffset(reader.GetDateTime(9), TimeSpan.Zero),
                reader.GetString(10), reader.GetString(11));
        }
        var receipt = reader.IsDBNull(12) ? null : new Receipt(reader.GetString(12), reader.GetInt64(13),
            reader.GetString(14), reader.GetInt64(15), reader.GetInt64(16), reader.GetBoolean(17));
        var receiptDefinition = reader.IsDBNull(18) ? null : PostgreSqlListJson.Deserialize<ListDefinition>(reader.GetString(18));
        return new RecordWriteContext(access, definition, record, recordBytes, receipt, receiptDefinition);
    }

    private static void RequireWriteContext(RecordWriteContext context, ListAccess required)
    {
        if (context.Definition is null || (context.Access & required) != required) throw NotFound();
    }

    private async Task ValidateReferenceAssignmentsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, string subject, IReadOnlyList<ReferenceAssignment> references, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            WITH requested AS (
              SELECT * FROM unnest(@field_ids::uuid[],@list_ids::uuid[],@record_ids::uuid[],@display_ids::uuid[])
                AS value(field_id,list_id,record_id,display_id)
            )
            SELECT requested.field_id,
              (SELECT g.access FROM cellbridge_lists.list_grants g WHERE g.workspace_id=@workspace AND g.list_id=requested.list_id AND g.subject=@subject),
              target_list.definition,target.record_id
            FROM requested
            LEFT JOIN LATERAL (
              SELECT s.definition::text AS definition FROM cellbridge_lists.lists l
              JOIN cellbridge_lists.list_schemas s ON s.workspace_id=l.workspace_id AND s.list_id=l.list_id AND s.schema_version=l.schema_version
              WHERE l.workspace_id=@workspace AND l.list_id=requested.list_id
            ) target_list ON true
            LEFT JOIN LATERAL (
              SELECT r.record_id FROM cellbridge_lists.records r
              WHERE r.workspace_id=@workspace AND r.list_id=requested.list_id AND r.record_id=requested.record_id
              FOR KEY SHARE
            ) target ON true ORDER BY requested.field_id
            """, connection, transaction);
        command.Parameters.AddWithValue("workspace", workspaceId);
        command.Parameters.AddWithValue("subject", subject);
        command.Parameters.AddWithValue("field_ids", references.Select(x => x.FieldId).ToArray());
        command.Parameters.AddWithValue("list_ids", references.Select(x => x.TargetListId).ToArray());
        command.Parameters.AddWithValue("record_ids", references.Select(x => x.TargetRecordId).ToArray());
        command.Parameters.AddWithValue("display_ids", references.Select(x => x.DisplayFieldId ?? Guid.Empty).ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct);
        var checkedCount = 0;
        while (await reader.ReadAsync(ct))
        {
            var assignment = references.Single(x => x.FieldId == reader.GetGuid(0));
            if (reader.IsDBNull(1) || (((ListAccess)reader.GetInt32(1)) & ListAccess.Read) == 0) throw NotFound();
            if (reader.IsDBNull(2)) throw NotFound();
            var targetDefinition = PostgreSqlListJson.Deserialize<ListDefinition>(reader.GetString(2));
            if (assignment.DisplayFieldId is { } displayId && targetDefinition.Fields.All(x => x.Id != displayId))
                throw Validation("The reference display field does not exist.");
            if (reader.IsDBNull(3)) throw Validation("The reference target does not exist.");
            checkedCount++;
        }
        if (checkedCount != references.Count)
            throw new ListException(ListFailure.Unavailable, "Reference validation returned incomplete results.");
    }

    private async Task<CreateRecordResult> InsertRecordAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, Guid listId, CreateRecordRequest request, string actor, string digest, string valuesJson,
        IReadOnlyList<ReferenceAssignment> references, Guid[] auditFields, CancellationToken ct)
    {
        var receiptBytes = Encoding.UTF8.GetByteCount(digest) + Encoding.UTF8.GetByteCount(actor) + 192L;
        var auditBytes = AuditStorageBytes(actor, "record.create", auditFields.Length);
        await using var command = new NpgsqlCommand("""
            WITH input AS (
              SELECT @values::jsonb AS values_json,octet_length((@values::jsonb)::text)::bigint AS value_bytes
            ), claimed AS (
              INSERT INTO cellbridge_lists.record_tombstones(workspace_id,list_id,record_id)
              VALUES (@workspace,@list,@record) ON CONFLICT DO NOTHING RETURNING 1
            ), allocated AS (
              UPDATE cellbridge_lists.list_counters SET identity_count=identity_count+1,next_number=next_number+1
              WHERE workspace_id=@workspace AND list_id=@list AND EXISTS (SELECT 1 FROM claimed)
                AND (@max_records::bigint IS NULL OR identity_count<@max_records)
              RETURNING next_number-1 AS number
            ), quota AS (
              UPDATE cellbridge_lists.workspaces w
              SET stored_bytes=w.stored_bytes+input.value_bytes+@receipt_bytes+128+@audit_bytes,audit_count=w.audit_count+1
              FROM input,allocated WHERE w.workspace_id=@workspace
                AND (@max_bytes::bigint IS NULL OR w.stored_bytes+input.value_bytes+@receipt_bytes+128+@audit_bytes<=@max_bytes)
                AND (@max_audits::bigint IS NULL OR w.audit_count<@max_audits) RETURNING 1
            ), created AS (
              INSERT INTO cellbridge_lists.records(workspace_id,list_id,record_id,schema_version,revision,number,
                values_json,created_utc,modified_utc,created_by,modified_by)
              SELECT @workspace,@list,@record,@schema,1,allocated.number,input.values_json,
                clock_timestamp(),clock_timestamp(),@actor,@actor FROM input,allocated,quota RETURNING record_id
            ), inserted_references AS (
              INSERT INTO cellbridge_lists.record_references(workspace_id,source_list_id,source_record_id,field_id,target_list_id,target_record_id)
              SELECT @workspace,@list,@record,refs.field_id,refs.list_id,refs.record_id
              FROM created CROSS JOIN unnest(@field_ids::uuid[],@list_ids::uuid[],@record_ids::uuid[])
                AS refs(field_id,list_id,record_id) RETURNING 1
            ), receipt AS (
              INSERT INTO cellbridge_lists.create_receipts(workspace_id,list_id,record_id,actor,schema_version,
                request_digest,revision,number,created_utc,expires_utc)
              SELECT @workspace,@list,@record,@actor,@schema,@digest,1,allocated.number,
                clock_timestamp(),clock_timestamp()+@lifetime::interval FROM allocated,quota,created RETURNING 1
            ), audit AS (
              INSERT INTO cellbridge_lists.audit_events(workspace_id,utc,actor,action,list_id,record_id,revision,field_ids,security)
              SELECT @workspace,clock_timestamp(),@actor,'record.create',@list,@record,1,@audit_fields,false
              FROM quota,created RETURNING 1
            )
            SELECT EXISTS(SELECT 1 FROM claimed),EXISTS(SELECT 1 FROM allocated),EXISTS(SELECT 1 FROM quota),
              (SELECT number FROM allocated),input.value_bytes FROM input
            """, connection, transaction);
        command.Parameters.AddWithValue("workspace", workspaceId);
        command.Parameters.AddWithValue("list", listId);
        command.Parameters.AddWithValue("record", request.Id);
        command.Parameters.AddWithValue("schema", request.SchemaVersion);
        command.Parameters.AddWithValue("values", valuesJson);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("digest", digest);
        command.Parameters.AddWithValue("lifetime", _limits.ReceiptLifetime);
        command.Parameters.AddWithValue("receipt_bytes", receiptBytes);
        command.Parameters.AddWithValue("audit_bytes", auditBytes);
        command.Parameters.AddWithValue("field_ids", references.Select(x => x.FieldId).ToArray());
        command.Parameters.AddWithValue("list_ids", references.Select(x => x.TargetListId).ToArray());
        command.Parameters.AddWithValue("record_ids", references.Select(x => x.TargetRecordId).ToArray());
        command.Parameters.AddWithValue("audit_fields", auditFields);
        AddNullableBigint(command, "max_records", _limits.MaxRecordsPerList);
        AddNullableBigint(command, "max_bytes", _limits.MaxWorkspaceBytes);
        AddNullableBigint(command, "max_audits", _limits.MaxAuditEvents);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        if (reader.GetInt64(4) > _limits.MaxRecordBytes) throw Limit("The normalized record exceeds the configured byte limit.");
        if (!reader.GetBoolean(0))
        {
            await reader.DisposeAsync();
            return await ReplayConcurrentCreateAsync(connection, transaction, workspaceId, listId, request,
                actor, digest, ct);
        }
        if (!reader.GetBoolean(1)) throw Limit("The record identity limit has been reached.");
        if (!reader.GetBoolean(2)) throw Limit("The workspace storage or audit limit has been reached.");
        return new CreateRecordResult(request.Id, 1, reader.GetInt64(3));
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
        await using var command = new NpgsqlCommand("""
            SELECT c.actor,c.schema_version,c.request_digest,c.revision,c.number,
              c.expires_utc>clock_timestamp(),s.definition::text
            FROM cellbridge_lists.create_receipts c
            LEFT JOIN cellbridge_lists.list_schemas s ON s.workspace_id=c.workspace_id
              AND s.list_id=c.list_id AND s.schema_version=c.schema_version
            WHERE c.workspace_id=@workspace AND c.list_id=@list AND c.record_id=@record
            """, connection, transaction);
        command.Parameters.AddWithValue("workspace", workspaceId);
        command.Parameters.AddWithValue("list", listId);
        command.Parameters.AddWithValue("record", request.Id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || !reader.GetBoolean(5)) return null;
        if (reader.GetString(0) != actor || reader.GetInt64(1) != request.SchemaVersion ||
            reader.GetString(2) != digest) return null;
        if (reader.IsDBNull(6))
            throw new ListException(ListFailure.Unavailable, "The receipt schema version is missing.");
        var definition = PostgreSqlListJson.Deserialize<ListDefinition>(reader.GetString(6));
        ListValidation.ValidateValues(definition, request.Values, _limits);
        return new CreateRecordResult(request.Id, reader.GetInt64(3), reader.GetInt64(4), true);
    }

    private async Task<ListRecord> PersistUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, Guid listId, ListRecord current, long schemaVersion, string valuesJson, string actor,
        IReadOnlyList<FieldChange> changes, IReadOnlySet<Guid> changedReferenceIds,
        IReadOnlyList<ReferenceAssignment> references, long previousBytes, CancellationToken ct)
    {
        var auditFields = changes.Select(x => x.FieldId).ToArray();
        var auditBytes = AuditStorageBytes(actor, "record.update", auditFields.Length);
        var revision = checked(current.Revision + 1);
        await using var command = new NpgsqlCommand("""
            WITH input AS (
              SELECT @values::jsonb AS values_json,octet_length((@values::jsonb)::text)::bigint AS value_bytes
            ), quota AS (
              UPDATE cellbridge_lists.workspaces w
              SET stored_bytes=w.stored_bytes+input.value_bytes-@previous_bytes+@audit_bytes,audit_count=w.audit_count+1
              FROM input WHERE w.workspace_id=@workspace
                AND (@max_bytes::bigint IS NULL OR w.stored_bytes+input.value_bytes-@previous_bytes+@audit_bytes<=@max_bytes)
                AND (@max_audits::bigint IS NULL OR w.audit_count<@max_audits) RETURNING 1
            ), updated AS (
              UPDATE cellbridge_lists.records r SET schema_version=@schema,revision=@revision,
                values_json=input.values_json,modified_utc=clock_timestamp(),modified_by=@actor
              FROM input,quota WHERE r.workspace_id=@workspace AND r.list_id=@list AND r.record_id=@record
                AND r.revision=@expected_revision
              RETURNING r.record_id,r.schema_version,r.revision,r.number,r.values_json::text,
                r.created_utc,r.modified_utc,r.created_by,r.modified_by
            ), removed AS (
              DELETE FROM cellbridge_lists.record_references refs USING updated
              WHERE refs.workspace_id=@workspace AND refs.source_list_id=@list AND refs.source_record_id=@record
                AND refs.field_id=ANY(@removed_fields::uuid[]) RETURNING 1
            ), inserted AS (
              INSERT INTO cellbridge_lists.record_references(workspace_id,source_list_id,source_record_id,field_id,target_list_id,target_record_id)
              SELECT @workspace,@list,@record,refs.field_id,refs.list_id,refs.record_id
              FROM updated CROSS JOIN unnest(@field_ids::uuid[],@list_ids::uuid[],@record_ids::uuid[])
                AS refs(field_id,list_id,record_id)
              ON CONFLICT (workspace_id,source_list_id,source_record_id,field_id) DO UPDATE SET
                target_list_id=EXCLUDED.target_list_id,target_record_id=EXCLUDED.target_record_id
              RETURNING 1
            ), audit AS (
              INSERT INTO cellbridge_lists.audit_events(workspace_id,utc,actor,action,list_id,record_id,revision,field_ids,security)
              SELECT @workspace,clock_timestamp(),@actor,'record.update',@list,@record,@revision,@audit_fields,false
              FROM quota,updated RETURNING 1
            )
            SELECT updated.*,input.value_bytes,EXISTS(SELECT 1 FROM quota) FROM input LEFT JOIN updated ON true
            """, connection, transaction);
        command.Parameters.AddWithValue("workspace", workspaceId);
        command.Parameters.AddWithValue("list", listId);
        command.Parameters.AddWithValue("record", current.Id);
        command.Parameters.AddWithValue("schema", schemaVersion);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("expected_revision", current.Revision);
        command.Parameters.AddWithValue("values", valuesJson);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("previous_bytes", previousBytes);
        command.Parameters.AddWithValue("audit_bytes", auditBytes);
        command.Parameters.AddWithValue("removed_fields", changedReferenceIds
            .Except(references.Select(x => x.FieldId)).ToArray());
        command.Parameters.AddWithValue("field_ids", references.Select(x => x.FieldId).ToArray());
        command.Parameters.AddWithValue("list_ids", references.Select(x => x.TargetListId).ToArray());
        command.Parameters.AddWithValue("record_ids", references.Select(x => x.TargetRecordId).ToArray());
        command.Parameters.AddWithValue("audit_fields", auditFields);
        AddNullableBigint(command, "max_bytes", _limits.MaxWorkspaceBytes);
        AddNullableBigint(command, "max_audits", _limits.MaxAuditEvents);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        if (reader.GetInt64(9) > _limits.MaxRecordBytes) throw Limit("The normalized record exceeds the configured byte limit.");
        if (!reader.GetBoolean(10)) throw Limit("The workspace storage or audit limit has been reached.");
        if (reader.IsDBNull(0)) throw Conflict("The record revision changed.");
        return ReadRecord(reader, workspaceId, listId);
    }

    private async Task PersistDeleteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, Guid listId, ListRecord current, string actor, long previousBytes, CancellationToken ct)
    {
        var auditBytes = AuditStorageBytes(actor, "record.delete", 0);
        await using var command = new NpgsqlCommand("""
            WITH quota AS (
              UPDATE cellbridge_lists.workspaces w
              SET stored_bytes=GREATEST(0,w.stored_bytes-@previous_bytes+@audit_bytes),audit_count=w.audit_count+1
              WHERE w.workspace_id=@workspace
                AND (@max_bytes::bigint IS NULL OR GREATEST(0,w.stored_bytes-@previous_bytes+@audit_bytes)<=@max_bytes)
                AND (@max_audits::bigint IS NULL OR w.audit_count<@max_audits) RETURNING 1
            ), deleted AS (
              DELETE FROM cellbridge_lists.records r USING quota
              WHERE r.workspace_id=@workspace AND r.list_id=@list AND r.record_id=@record AND r.revision=@expected_revision
              RETURNING r.revision
            ), audit AS (
              INSERT INTO cellbridge_lists.audit_events(workspace_id,utc,actor,action,list_id,record_id,revision,field_ids,security)
              SELECT @workspace,clock_timestamp(),@actor,'record.delete',@list,@record,deleted.revision,'{}'::uuid[],false
              FROM quota,deleted RETURNING 1
            ) SELECT EXISTS(SELECT 1 FROM quota),EXISTS(SELECT 1 FROM deleted)
            """, connection, transaction);
        command.Parameters.AddWithValue("workspace", workspaceId);
        command.Parameters.AddWithValue("list", listId);
        command.Parameters.AddWithValue("record", current.Id);
        command.Parameters.AddWithValue("expected_revision", current.Revision);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("previous_bytes", previousBytes);
        command.Parameters.AddWithValue("audit_bytes", auditBytes);
        AddNullableBigint(command, "max_bytes", _limits.MaxWorkspaceBytes);
        AddNullableBigint(command, "max_audits", _limits.MaxAuditEvents);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        if (!reader.GetBoolean(0)) throw Limit("The workspace storage or audit limit has been reached.");
        if (!reader.GetBoolean(1)) throw Conflict("The record revision changed.");
    }

    private static async Task DeleteExpiredReceiptAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, Guid listId, Guid recordId, Receipt receipt, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetByteCount(receipt.Digest) + Encoding.UTF8.GetByteCount(receipt.Actor) + 192L;
        await using var command = new NpgsqlCommand("""
            WITH removed AS (
              DELETE FROM cellbridge_lists.create_receipts WHERE workspace_id=@workspace AND list_id=@list AND record_id=@record RETURNING 1
            ) UPDATE cellbridge_lists.workspaces SET stored_bytes=GREATEST(0,stored_bytes-@bytes)
            WHERE workspace_id=@workspace AND EXISTS(SELECT 1 FROM removed)
            """, connection, transaction);
        command.Parameters.AddWithValue("workspace", workspaceId);
        command.Parameters.AddWithValue("list", listId);
        command.Parameters.AddWithValue("record", recordId);
        command.Parameters.AddWithValue("bytes", bytes);
        await command.ExecuteNonQueryAsync(ct);
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

    private static long AuditStorageBytes(string actor, string action, int fieldCount) =>
        96L + Encoding.UTF8.GetByteCount(actor) + Encoding.UTF8.GetByteCount(action) + fieldCount * 16L;

    private static void AddNullableBigint(NpgsqlCommand command, string name, long? value)
    {
        var parameter = command.Parameters.Add(name, NpgsqlDbType.Bigint);
        parameter.Value = value is { } actual ? actual : DBNull.Value;
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

    private static async Task<ListRecord?> ReadRecordAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, Guid listId, Guid recordId, bool forUpdate, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT record_id,schema_version,revision,number,values_json::text,created_utc,modified_utc,created_by,modified_by
            FROM cellbridge_lists.records WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3
            """ + (forUpdate ? " FOR UPDATE" : ""), workspaceId, listId, recordId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRecord(reader, workspaceId, listId) : null;
    }

    private static ListRecord ReadRecord(NpgsqlDataReader reader, Guid workspaceId, Guid listId) => new(
        reader.GetGuid(0), workspaceId, listId, reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
        PostgreSqlListJson.Object(reader.GetString(4)),
        new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero), new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero),
        reader.GetString(7), reader.GetString(8));

    private sealed record RecordWriteContext(ListAccess Access, ListDefinition? Definition, ListRecord? Record,
        long RecordBytes, Receipt? Receipt, ListDefinition? ReceiptDefinition);
    private sealed record ReferenceAssignment(Guid FieldId, Guid TargetListId, Guid TargetRecordId, Guid? DisplayFieldId);
    private sealed record Receipt(string Actor, long SchemaVersion, string Digest, long Revision, long Number, bool Live);
}
