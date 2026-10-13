using System.Text;
using System.Text.Json.Nodes;
using Npgsql;

namespace CellBridge.Lists.PostgreSql;

public sealed partial class PostgreSqlListEngine
{
    public async Task<CreateRecordResult> CreateRecordAsync(ListActor actor, Guid workspaceId, Guid listId,
        CreateRecordRequest request, CancellationToken ct = default)
    {
        ValidateActor(actor);
        var digest = ListValidation.CreateDigest(request);
        var result = await InTransactionAsync(workspaceId, false, async (connection, transaction) =>
        {
            await AuthorizeMemberAsync(connection, transaction, workspaceId, actor.Subject, ct);
            await RequireAccessAsync(connection, transaction, workspaceId, listId, actor.Subject, ListAccess.Write, ct);
            await LockWorkspaceMutationAsync(connection, transaction, workspaceId, ct);

            var receipt = await GetReceiptAsync(connection, transaction, workspaceId, listId, request.Id, ct);
            if (receipt is not null && receipt.ExpiresUtc > DateTimeOffset.UtcNow)
            {
                if (receipt.Actor != actor.Subject || receipt.Digest != digest || receipt.SchemaVersion != request.SchemaVersion)
                    throw Conflict("That record ID has a different create receipt.");
                return new CreateRecordResult(request.Id, receipt.Revision, receipt.Number, true);
            }

            var definition = await GetDefinitionAsync(connection, transaction, workspaceId, listId, true, ct);
            if (receipt is not null)
            {
                await LockQuotaAsync(connection, transaction, workspaceId, ct);
                await ExecuteAsync(connection, transaction,
                    "DELETE FROM cellbridge_lists.create_receipts WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3", ct,
                    workspaceId, listId, request.Id);
                await ReserveStorageAsync(connection, transaction, workspaceId,
                    -(Encoding.UTF8.GetByteCount(receipt.Digest) + Encoding.UTF8.GetByteCount(receipt.Actor) + 192), ct);
                return new CreateRecordResult(request.Id, 0, 0);
            }
            if (request.SchemaVersion != definition.SchemaVersion) throw Conflict("The current schema version is required.");
            ListValidation.ValidateValues(definition, request.Values, _limits);
            await ValidateReferencesAsync(connection, transaction, actor.Subject, definition, request.Values, ct);
            await LockQuotaAsync(connection, transaction, workspaceId, ct);

            if (await ScalarAsync<long>(connection, transaction,
                "SELECT count(*) FROM cellbridge_lists.record_tombstones WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3", ct,
                workspaceId, listId, request.Id) != 0) throw Conflict("That record ID was already used.");
            var count = await ScalarAsync<long>(connection, transaction,
                "SELECT count(*) FROM cellbridge_lists.record_tombstones WHERE workspace_id=$1 AND list_id=$2", ct, workspaceId, listId);
            if (count >= _limits.MaxRecordsPerList) throw Limit("The record identity limit has been reached.");

            var number = await ScalarAsync<long>(connection, transaction,
                "UPDATE cellbridge_lists.lists SET next_number=next_number+1 WHERE workspace_id=$1 AND list_id=$2 RETURNING next_number-1", ct,
                workspaceId, listId);
            var valuesJson = request.Values.ToJsonString();
            var valuesBytes = await CanonicalJsonBytesAsync(connection, transaction, valuesJson, ct);
            if (valuesBytes > _limits.MaxRecordBytes) throw Limit("The normalized record exceeds the configured byte limit.");
            var receiptBytes = Encoding.UTF8.GetByteCount(digest) + Encoding.UTF8.GetByteCount(actor.Subject) + 192;
            await ReserveStorageAsync(connection, transaction, workspaceId,
                valuesBytes + receiptBytes + 128, ct);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO cellbridge_lists.record_tombstones(workspace_id,list_id,record_id) VALUES ($1,$2,$3)", ct,
                workspaceId, listId, request.Id);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO cellbridge_lists.records(workspace_id,list_id,record_id,schema_version,revision,number,values_json,created_utc,modified_utc,created_by,modified_by)
                VALUES ($1,$2,$3,$4,1,$5,$6::jsonb,clock_timestamp(),clock_timestamp(),$7,$7)
                """, ct, workspaceId, listId, request.Id, definition.SchemaVersion, number, valuesJson, actor.Subject);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO cellbridge_lists.create_receipts(workspace_id,list_id,record_id,actor,schema_version,request_digest,revision,number,created_utc,expires_utc)
                VALUES ($1,$2,$3,$4,$5,$6,1,$7,clock_timestamp(),clock_timestamp()+$8::interval)
                """, ct, workspaceId, listId, request.Id, actor.Subject, request.SchemaVersion, digest, number,
                $"{_limits.ReceiptLifetime.TotalSeconds} seconds");
            await AuditAsync(connection, transaction, workspaceId, actor.Subject, "record.create", listId, request.Id, 1,
                request.Values.Select(x => Guid.Parse(x.Key)), false, ct);
            return new CreateRecordResult(request.Id, 1, number);
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
        return await InTransactionAsync(workspaceId, false, async (connection, transaction) =>
        {
            await AuthorizeMemberAsync(connection, transaction, workspaceId, actor.Subject, ct);
            await RequireAccessAsync(connection, transaction, workspaceId, listId, actor.Subject, ListAccess.Read | ListAccess.Write, ct);
            await LockWorkspaceMutationAsync(connection, transaction, workspaceId, ct);
            var definition = await GetDefinitionAsync(connection, transaction, workspaceId, listId, true, ct);
            if (request.SchemaVersion != definition.SchemaVersion) throw Conflict("The current schema version is required.");
            await LockQuotaAsync(connection, transaction, workspaceId, ct);
            var current = await ReadRecordAsync(connection, transaction, workspaceId, listId, recordId, true, ct) ?? throw NotFound();
            if (current.Revision != request.ExpectedRevision) throw Conflict("The record revision changed.");
            var values = ListValidation.ApplyChanges(definition, current.Values, request.Changes, _limits);
            await ValidateReferencesAsync(connection, transaction, actor.Subject, definition, values, ct);
            var json = values.ToJsonString();
            var nextBytes = await CanonicalJsonBytesAsync(connection, transaction, json, ct);
            if (nextBytes > _limits.MaxRecordBytes) throw Limit("The normalized record exceeds the configured byte limit.");
            var previousBytes = await CanonicalJsonBytesAsync(connection, transaction, current.Values.ToJsonString(), ct);
            await ReserveStorageAsync(connection, transaction, workspaceId,
                nextBytes - previousBytes, ct);
            var revision = checked(current.Revision + 1);
            await ExecuteAsync(connection, transaction, """
                UPDATE cellbridge_lists.records SET schema_version=$5,revision=$6,values_json=$7::jsonb,
                  modified_utc=clock_timestamp(),modified_by=$8
                WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3 AND revision=$4
                """, ct, workspaceId, listId, recordId, current.Revision, definition.SchemaVersion, revision, json, actor.Subject);
            await AuditAsync(connection, transaction, workspaceId, actor.Subject, "record.update", listId, recordId,
                revision, request.Changes.Select(x => x.FieldId), false, ct);
            return (await ReadRecordAsync(connection, transaction, workspaceId, listId, recordId, false, ct))!;
        }, ct);
    }

    public async Task DeleteRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        long expectedRevision, CancellationToken ct = default)
    {
        ValidateActor(actor);
        await InTransactionAsync(workspaceId, false, async (connection, transaction) =>
        {
            await AuthorizeMemberAsync(connection, transaction, workspaceId, actor.Subject, ct);
            await RequireAccessAsync(connection, transaction, workspaceId, listId, actor.Subject, ListAccess.Write, ct);
            await LockWorkspaceMutationAsync(connection, transaction, workspaceId, ct);
            await GetDefinitionAsync(connection, transaction, workspaceId, listId, true, ct);
            await LockQuotaAsync(connection, transaction, workspaceId, ct);
            var current = await ReadRecordAsync(connection, transaction, workspaceId, listId, recordId, true, ct) ?? throw NotFound();
            if (current.Revision != expectedRevision) throw Conflict("The record revision changed.");
            await EnsureNotReferencedAsync(connection, transaction, workspaceId, listId, recordId, ct);
            await ExecuteAsync(connection, transaction,
                "DELETE FROM cellbridge_lists.records WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3", ct,
                workspaceId, listId, recordId);
            await ReserveStorageAsync(connection, transaction, workspaceId,
                -await CanonicalJsonBytesAsync(connection, transaction, current.Values.ToJsonString(), ct), ct);
            await AuditAsync(connection, transaction, workspaceId, actor.Subject, "record.delete", listId, recordId,
                current.Revision, Array.Empty<Guid>(), false, ct);
            return true;
        }, ct);
    }

    public Task<RecordPage> QueryAsync(ListActor actor, Guid workspaceId, Guid listId, ListQuery query,
        CancellationToken ct = default) => ReadAsync(actor, workspaceId, async (connection, transaction, member) =>
    {
        await RequireAccessAsync(connection, transaction, workspaceId, listId, actor.Subject, ListAccess.Read, ct);
        await LockListSchemaSharedAsync(connection, transaction, workspaceId, listId, ct);
        var definition = await GetDefinitionAsync(connection, transaction, workspaceId, listId, false, ct);
        ListValidation.ValidateQuery(definition, query, _limits);
        await using var command = Command(connection, transaction, """
            SELECT record_id,schema_version,revision,number,values_json::text,created_utc,modified_utc,created_by,modified_by
            FROM cellbridge_lists.records WHERE workspace_id=$1 AND list_id=$2 ORDER BY record_id LIMIT $3
            """, workspaceId, listId, _limits.MaxRecordsPerList + 1);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var records = new List<ListRecord>();
        while (await reader.ReadAsync(ct)) records.Add(ReadRecord(reader, workspaceId, listId));
        if (records.Count > _limits.MaxRecordsPerList) throw Limit("The query candidate limit has been reached.");
        return ListQueryEvaluator.Query(definition, records, query, _limits);
    }, ct);

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
        var field = source.Fields.SingleOrDefault(x => x.Id == fieldId && x.Type == ListFieldType.Reference) ?? throw Validation("The reference field does not exist.");
        var sourceRecord = await ReadRecordAsync(connection, transaction, workspaceId, listId, recordId, false, ct) ?? throw NotFound();
        if (sourceRecord.Values[fieldId.ToString("D")] is not JsonValue referenceValue)
            throw NotFound();
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

    private async Task ValidateReferencesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string subject, ListDefinition definition, JsonObject values, CancellationToken ct)
    {
        foreach (var field in definition.Fields.Where(x => x.Type == ListFieldType.Reference))
        {
            if (values[field.Id.ToString("D")] is not JsonValue value) continue;
            var targetId = Guid.Parse(value.GetValue<string>());
            var targetListId = field.ReferenceListId!.Value;
            await RequireAccessAsync(connection, transaction, definition.WorkspaceId, targetListId, subject, ListAccess.Read, ct);
            var targetDefinition = await GetDefinitionAsync(connection, transaction, definition.WorkspaceId, targetListId, false, ct);
            if (field.ReferenceDisplayFieldId is { } displayId && targetDefinition.Fields.All(x => x.Id != displayId))
                throw Validation("The reference display field does not exist.");
            if (await ScalarAsync<long>(connection, transaction,
                "SELECT count(*) FROM cellbridge_lists.records WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3", ct,
                definition.WorkspaceId, targetListId, targetId) == 0) throw Validation("The reference target does not exist.");
        }
    }

    private async Task EnsureNotReferencedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, Guid targetListId, Guid targetRecordId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT s.definition::text,r.values_json::text
            FROM cellbridge_lists.records r
            JOIN cellbridge_lists.lists l ON l.workspace_id=r.workspace_id AND l.list_id=r.list_id
            JOIN cellbridge_lists.list_schemas s ON s.workspace_id=l.workspace_id AND s.list_id=l.list_id AND s.schema_version=l.schema_version
            WHERE r.workspace_id=$1
            """, workspaceId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var definition = PostgreSqlListJson.Deserialize<ListDefinition>(reader.GetString(0));
            var values = PostgreSqlListJson.Object(reader.GetString(1));
            foreach (var field in definition.Fields.Where(x => x.Type == ListFieldType.Reference && x.ReferenceListId == targetListId))
                if (values[field.Id.ToString("D")] is JsonValue value && value.GetValue<string>() == targetRecordId.ToString("D"))
                    throw Conflict("The record is referenced by another record.");
        }
    }

    private static async Task LockWorkspaceMutationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 391814))", workspaceId.ToString("D"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task LockListSchemaSharedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, Guid listId, CancellationToken ct)
    {
        if (await ScalarAsync<Guid?>(connection, transaction,
            "SELECT list_id FROM cellbridge_lists.lists WHERE workspace_id=$1 AND list_id=$2 FOR SHARE", ct,
            workspaceId, listId) is null) throw NotFound();
    }

    private static async Task<Receipt?> GetReceiptAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, Guid listId, Guid recordId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT actor,schema_version,request_digest,revision,number,expires_utc
            FROM cellbridge_lists.create_receipts WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3
            """, workspaceId, listId, recordId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new Receipt(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3),
                reader.GetInt64(4), new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero))
            : null;
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

    private sealed record Receipt(string Actor, long SchemaVersion, string Digest, long Revision, long Number,
        DateTimeOffset ExpiresUtc);
}
