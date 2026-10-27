using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;

#if CELLBRIDGE_SQLSERVER
namespace CellBridge.Lists.SqlServer;
#else
namespace CellBridge.Lists.PostgreSql;
#endif

internal sealed partial class EfListEngine
{
    public async Task<CreateRecordResult> CreateRecordAsync(ListActor actor, Guid workspaceId, Guid listId,
        CreateRecordRequest request, CancellationToken ct = default)
    {
        ValidateActor(actor);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Values);
        request = request with { Values = (JsonObject)request.Values.DeepClone() };
        var digest = ListValidation.CreateDigest(request);
        var assignedFieldIds = ParseAssignedFieldIds(request.Values);
        var authorization = await AuthorizeOperationAsync(actor, workspaceId, [(listId, ListAuthorizationAction.Write)], ct);
        var receiptAdmitted = false;
        if (authorization is not null)
        {
            var preflight = await InTransactionAsync(workspaceId, false, authorization, async (db, connection, transaction) =>
            {
                await AuthorizeMemberAsync(db, workspaceId, actor.Subject, ct, authorization);
                await RequireAccessAsync(db, workspaceId, listId, actor.Subject, ListAccess.Write, ct, authorization);
                // Any established receipt, including an expired one, is decided
                // in the final transaction without new target-read decisions.
                if (await db.CreateReceipts.AsNoTracking().AnyAsync(row => row.WorkspaceId == workspaceId &&
                    row.ListId == listId && row.RecordId == request.Id, ct)) return (Receipt: true, Targets: Array.Empty<Guid>());
                var definition = await GetDefinitionAsync(db, connection, transaction, workspaceId, listId, false, ct);
                return (Receipt: false, Targets: definition.Fields.Where(field => field.Type == ListFieldType.Reference && assignedFieldIds.Contains(field.Id))
                    .Select(field => field.ReferenceListId!.Value).Distinct().ToArray());
            }, ct);
            receiptAdmitted = preflight.Receipt;
            authorization = await AuthorizeAdditionalAsync(actor, authorization,
                preflight.Targets.Select(target => ((Guid?)target, ListAuthorizationAction.Read)).ToArray(), ct);
        }
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

            if (receiptAdmitted) throw Conflict("That record ID was already used.");
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
                    digest, ListJson.RecordValues(request.Values), references,
                    request.Values.Select(x => Guid.Parse(x.Key)).ToArray(), definition, authorization, ct);
            }
            catch (Exception ex) when (IsReferenceViolation(ex))
            { throw Validation("The reference target does not exist."); }
        }, ct, mutation: true);
        if (result.Revision == 0) throw Conflict("That record ID was used by an expired create receipt.");
        return result;
    }

    public Task<ListRecord> GetRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        CancellationToken ct = default) => ReadAsync(actor, workspaceId, async (db, connection, transaction, member, authorization) =>
    {
        await RequireAccessAsync(db, workspaceId, listId, actor.Subject, ListAccess.Read, ct, authorization);
        var record = await ReadRecordAsync(db, connection, transaction, workspaceId, listId, recordId, false, ct) ?? throw NotFound();
        var definition = await GetDefinitionAsync(db, connection, transaction, workspaceId, listId, false, ct);
        ListValidation.ValidateValues(definition, record.Values, _limits);
        return record;
    }, [(listId, ListAuthorizationAction.Read)], ct);

    public async Task<ListRecord> UpdateRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        UpdateRecordRequest request, CancellationToken ct = default)
    {
        ValidateActor(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Changes is null || request.Changes.Any(change => change is null)) throw Validation("Changes are required and cannot contain null.");
        request = request with { Changes = request.Changes.Select(change => change with { Value = change.Value?.DeepClone() }).ToArray() };
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
            var normalizedValues = ListJson.NormalizeOwnedRecordValues(values);
            try
            {
                return await PersistUpdateAsync(db, connection, transaction, workspaceId, listId, current,
                    context.RecordRow!,
                    definition.SchemaVersion, normalizedValues.ToJsonString(), normalizedValues, actor.Subject, request.Changes,
                    changedReferenceIds, references, context.RecordBytes, definition, authorization, ct);
            }
            catch (Exception ex) when (IsReferenceViolation(ex))
            { throw Validation("The reference target does not exist."); }
        }, ct, mutation: true);
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
                await PersistDeleteAsync(db, workspaceId, listId, current,
                    context.RecordRow!,
                    actor.Subject, context.RecordBytes, ct);
            }
            catch (Exception ex) when (IsReferenceViolation(ex))
            { throw Conflict("The record is referenced by another record."); }
            return true;
        }, ct, mutation: true);
    }

    public Task<MergePreview> PreviewMergeAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        MergeRequest request, CancellationToken ct = default) => ReadAsync(actor, workspaceId, async (db, connection, transaction, member, authorization) =>
    {
        await RequireAccessAsync(db, workspaceId, listId, actor.Subject, ListAccess.Read | ListAccess.Write, ct, authorization);
        await _locks.ListSharedAsync(db, workspaceId, listId, ct);
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
            if (!authorization.Decisions.Any(pair => pair.First.ListId == listId && pair.Second.Allowed)) throw NotFound();
            return await GetDefinitionAsync(db, connection, transaction, workspaceId, listId, false, ct);
        }, ct);

    private async Task<ListDefinition> ReadListDefinitionAsync(ListDbContext db,
        DbConnection connection, DbTransaction transaction, Guid workspaceId, Guid listId,
        string subject, ListAccess requiredAccess, CancellationToken ct, OperationAuthorization? authorization)
    {
        if (authorization is not null && !authorization.Allows(listId, requiredAccess)) throw NotFound();
        await CheckContextAdmissionAsync(db, workspaceId, listId, subject, requiredAccess, authorization, ct);
        await _locks.RecordAsync(db, workspaceId, listId, Guid.Empty, false, ct);
        var projection = (CanUseCompiledContextQueries(db)
            ? await _compiledDefinitionContext(db, workspaceId, listId, subject, (int)requiredAccess,
                authorization is not null, ct)
            : await (
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
            }).SingleOrDefaultAsync(ct)) ?? throw MissingContext();
        RequireProjectionAccess(projection.SecurityAuditCount, projection.IsMember, projection.Access,
            requiredAccess);
        return ListJson.Deserialize<ListDefinition>(projection.DefinitionJson);
    }

    private async Task<CreateRecordContext> ReadCreateContextAsync(ListDbContext db,
        DbConnection connection, DbTransaction transaction, Guid workspaceId, Guid listId,
        Guid recordId, string subject, ListAccess requiredAccess, bool deleteLock,
        CancellationToken ct, OperationAuthorization? authorization)
    {
        if (authorization is not null && !authorization.Allows(listId, requiredAccess)) throw NotFound();
        await CheckContextAdmissionAsync(db, workspaceId, listId, subject, requiredAccess, authorization, ct);
        await _locks.RecordAsync(db, workspaceId, listId, recordId, deleteLock, ct);
        var projection = (CanUseCompiledContextQueries(db)
            ? await _compiledCreateContext(db, workspaceId, listId, recordId, subject,
                (int)requiredAccess, authorization is not null, ct)
            : await (
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
                ReceiptLive = receipt != null && receipt.ExpiresUtc > ListDbContext.ClockTimestamp(),
                ReceiptDefinitionJson = receiptSchema == null ? null : receiptSchema.Definition
            }).SingleOrDefaultAsync(ct)) ?? throw MissingContext();
        RequireProjectionAccess(projection.SecurityAuditCount, projection.IsMember, projection.Access,
            requiredAccess);
        var definition = ListJson.Deserialize<ListDefinition>(projection.DefinitionJson);
        var receiptRow = projection.Receipt;
        var currentReceipt = receiptRow is null ? null : new Receipt(receiptRow.Actor, receiptRow.SchemaVersion,
            receiptRow.RequestDigest, receiptRow.Revision, receiptRow.Number, projection.ReceiptLive);
        var receiptDefinition = projection.ReceiptDefinitionJson is null
            ? null : ListJson.Deserialize<ListDefinition>(projection.ReceiptDefinitionJson);
        return new CreateRecordContext(definition, currentReceipt, receiptDefinition);
    }

    private async Task<RecordMutationContext> ReadMutationContextAsync(ListDbContext db,
        DbConnection connection, DbTransaction transaction, Guid workspaceId, Guid listId,
        Guid recordId, string subject, ListAccess requiredAccess, bool deleteLock,
        CancellationToken ct, OperationAuthorization? authorization)
    {
        if (authorization is not null && !authorization.Allows(listId, requiredAccess)) throw NotFound();
        await CheckContextAdmissionAsync(db, workspaceId, listId, subject, requiredAccess, authorization, ct);
        await _locks.RecordAsync(db, workspaceId, listId, recordId, deleteLock, ct);
        var projection = (CanUseCompiledContextQueries(db)
            ? await _compiledMutationContext(db, workspaceId, listId, recordId, subject,
                (int)requiredAccess, authorization is not null, ct)
            : await (
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
            }).SingleOrDefaultAsync(ct)) ?? throw MissingContext();
        RequireProjectionAccess(projection.SecurityAuditCount, projection.IsMember, projection.Access,
            requiredAccess);
        var recordRow = projection.Record;
        return new RecordMutationContext(
            ListJson.Deserialize<ListDefinition>(projection.DefinitionJson),
            recordRow is null ? null : ToListRecord(recordRow), recordRow,
            recordRow is null ? 0 : StoredRecordBytes(recordRow.ValuesJson));
    }

    private void RequireProjectionAccess(int securityAuditCount, bool isMember, int access,
        ListAccess requiredAccess)
    {
        if (securityAuditCount >= _limits.MaxSecurityAuditEvents)
            throw new ListException(ListFailure.Unavailable, "The workspace security audit reserve is exhausted.");
        if (!isMember) throw Forbidden();
        if (((ListAccess)access & requiredAccess) != requiredAccess) throw NotFound();
    }

    private async Task ValidateReferenceAssignmentsAsync(ListDbContext db,
        DbConnection connection, DbTransaction transaction, Guid workspaceId, string subject,
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
            pair => ListJson.Deserialize<ListDefinition>(pair.Value));
        var ordered = references.OrderBy(reference => reference.FieldId).ToArray();
        // Filter locks without throwing so validation below retains field-ID error order.
        var lockableFieldIds = ordered.Where(assignment =>
                targetDefinitions.TryGetValue(assignment.TargetListId, out var targetDefinition) &&
                (assignment.DisplayFieldId is not { } displayId || targetDefinition.Fields.Any(x => x.Id == displayId)))
            .Select(assignment => assignment.FieldId).ToArray();
        var existingTargets = await LockReferenceTargetsAsync(db, workspaceId,
            ordered, lockableFieldIds, ct);
        foreach (var assignment in ordered)
        {
            if (!targetDefinitions.TryGetValue(assignment.TargetListId, out var targetDefinition)) throw NotFound();
            if (assignment.DisplayFieldId is { } displayId && targetDefinition.Fields.All(x => x.Id != displayId))
                throw Validation("The reference display field does not exist.");
            if (!existingTargets.Contains(assignment.FieldId))
                throw Validation("The reference target does not exist.");
        }
    }

    private async Task<CreateRecordResult> InsertRecordAsync(ListDbContext db,
        DbConnection connection, DbTransaction transaction, Guid workspaceId, Guid listId,
        CreateRecordRequest request, string actor, string digest, string valuesJson,
        IReadOnlyList<ReferenceAssignment> references, Guid[] auditFields, ListDefinition definition,
        OperationAuthorization? authorization, CancellationToken ct)
    {
        var receiptBytes = Encoding.UTF8.GetByteCount(digest) + Encoding.UTF8.GetByteCount(actor) + 192L;
        var auditBytes = AuditStorageBytes(actor, "record.create", auditFields.Length);
        var (valueBytes, claimed) = await ClaimIdentityAsync(db, workspaceId,
            listId, request.Id, valuesJson, ct);
        if (valueBytes > _limits.MaxRecordBytes)
            throw Limit("The normalized record exceeds the configured byte limit.");
        if (!claimed)
            return await ReplayConcurrentCreateAsync(db, workspaceId, listId, request,
                actor, digest, ct);

        await _locks.CreateStateAsync(db, workspaceId, listId, ct);
        var state = CanUseCompiledContextQueries(db)
            ? await _compiledCreateState(db, workspaceId, listId, ct)
            : await (from workspaceRow in db.Workspaces
            join counterRow in db.ListCounters
                on workspaceRow.WorkspaceId equals counterRow.WorkspaceId
            where workspaceRow.WorkspaceId == workspaceId && counterRow.ListId == listId
            select new CreateStateProjection
            {
                Workspace = workspaceRow,
                Counter = counterRow,
                Now = ListDbContext.ClockTimestamp()
            }).SingleAsync(ct);
        var counter = state.Counter;
        if (_limits.MaxRecordsPerList is { } maxRecords && counter.IdentityCount >= maxRecords)
            throw Limit("The record identity limit has been reached.");
        var number = counter.NextNumber;
        counter.IdentityCount = checked(counter.IdentityCount + 1);
        counter.NextNumber = checked(counter.NextNumber + 1);

        var workspace = state.Workspace;
        var storageDelta = checked(valueBytes + receiptBytes + 128 + auditBytes);
        workspace.StoredBytes = checked(workspace.StoredBytes + storageDelta);
        workspace.AuditCount = checked(workspace.AuditCount + 1);
        CheckPreparedPublication(workspace);
        var now = state.Now;
        var record = new RecordRow { WorkspaceId = workspaceId, ListId = listId, RecordId = request.Id,
            SchemaVersion = request.SchemaVersion, Revision = 1, Number = number, ValuesJson = valuesJson,
            CreatedUtc = now, ModifiedUtc = now, CreatedBy = actor, ModifiedBy = actor };
        db.Records.Add(record);
        var expected = ToListRecord(record);
        db.RecordReferences.AddRange(references.Select(reference => new RecordReferenceRow {
            WorkspaceId = workspaceId, SourceListId = listId, SourceRecordId = request.Id,
            FieldId = reference.FieldId, TargetListId = reference.TargetListId,
            TargetRecordId = reference.TargetRecordId }));
        var receipt = new CreateReceiptRow { WorkspaceId = workspaceId, ListId = listId,
            RecordId = request.Id, Actor = actor, SchemaVersion = request.SchemaVersion,
            RequestDigest = digest, Revision = 1, Number = number, CreatedUtc = now,
            ExpiresUtc = now.Add(_limits.ReceiptLifetime) };
        db.CreateReceipts.Add(receipt);
        var expectedReceipt = new CreateReceiptRow { WorkspaceId = workspaceId, ListId = listId, RecordId = request.Id,
            Actor = actor, SchemaVersion = request.SchemaVersion, RequestDigest = digest, Revision = 1, Number = number,
            CreatedUtc = now, ExpiresUtc = now.Add(_limits.ReceiptLifetime) };
        var audit = new AuditEventRow { WorkspaceId = workspaceId, Utc = now, Actor = actor,
            Action = "record.create", ListId = listId, RecordId = request.Id, Revision = 1,
            FieldIds = auditFields };
        StagePublicationAudit(db, audit);
        await SaveRequiredAsync(db, ct);
        await ValidatePublishedRecordAsync(db, connection, transaction, definition, record, expected, receipt, expectedReceipt,
            workspace, audit, valueBytes, null, references.Select(reference => reference.FieldId).ToHashSet(),
            authorization, ct);
        return new CreateRecordResult(request.Id, 1, number);
    }

    private async Task<CreateRecordResult> ReplayConcurrentCreateAsync(ListDbContext db,
        Guid workspaceId, Guid listId, CreateRecordRequest request, string actor, string digest,
        CancellationToken ct)
        => await TryReplayConcurrentCreateAsync(db, workspaceId, listId, request,
               actor, digest, ct)
           ?? throw Conflict("That record ID was already used.");

    private async Task<CreateRecordResult?> TryReplayConcurrentCreateAsync(ListDbContext db,
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
                Live = receipt.ExpiresUtc > ListDbContext.ClockTimestamp(),
                DefinitionJson = schema == null ? null : schema.Definition
            }).SingleOrDefaultAsync(ct);
        if (replay is null || !replay.Live) return null;
        if (replay.Actor != actor || replay.SchemaVersion != request.SchemaVersion ||
            replay.Digest != digest) return null;
        if (replay.DefinitionJson is null)
            throw new ListException(ListFailure.Unavailable, "The receipt schema version is missing.");
        var definition = ListJson.Deserialize<ListDefinition>(replay.DefinitionJson);
        ListValidation.ValidateValues(definition, request.Values, _limits);
        return new CreateRecordResult(request.Id, replay.Revision, replay.Number, true);
    }

    private async Task<ListRecord> PersistUpdateAsync(ListDbContext db,
        DbConnection connection, DbTransaction transaction,
        Guid workspaceId, Guid listId, ListRecord current, RecordRow record, long schemaVersion,
        string valuesJson, JsonObject responseValues, string actor,
        IReadOnlyList<FieldChange> changes, IReadOnlySet<Guid> changedReferenceIds,
        IReadOnlyList<ReferenceAssignment> references, long previousBytes, ListDefinition definition,
        OperationAuthorization? authorization, CancellationToken ct)
    {
        var auditFields = changes.Select(x => x.FieldId).ToArray();
        var revision = checked(current.Revision + 1);
#if CELLBRIDGE_SQLSERVER
        var auditBytes = AuditStorageBytes(actor, "record.update", auditFields.Length);
        var valueBytes = await CanonicalJsonBytesAsync(db, valuesJson, ct);
        if (valueBytes > _limits.MaxRecordBytes)
            throw Limit("The normalized record exceeds the configured byte limit.");
        var state = await _locks.LockedQuota(db, workspaceId)
            .Select(row => new { Workspace = row, Now = ListDbContext.ClockTimestamp() })
            .SingleOrDefaultAsync(ct) ?? throw NotFound();
        WorkspaceRow? workspace = state.Workspace;
        var now = state.Now;
        workspace.StoredBytes = checked(workspace.StoredBytes + valueBytes - previousBytes + auditBytes);
        workspace.AuditCount = checked(workspace.AuditCount + 1);
        CheckPreparedPublication(workspace);
#else
        // Consumer models must retain the token before this path drops the source lock.
        if (db.Model.FindEntityType(typeof(RecordRow))?.FindProperty(nameof(RecordRow.Revision))?.IsConcurrencyToken != true)
            throw new ListException(ListFailure.Unavailable, "Record revision concurrency checking is required.");
        var (valueBytes, now) = await ReadUpdatePublicationStateAsync(db, valuesJson, ct);
        if (valueBytes > _limits.MaxRecordBytes)
            throw Limit("The normalized record exceeds the configured byte limit.");
        WorkspaceRow? workspace = null;
#endif
        // Attach the original revision before assigning the next revision.
        db.Records.Attach(record);
        record.SchemaVersion = schemaVersion;
        record.Revision = revision;
        record.ValuesJson = valuesJson;
        record.ModifiedUtc = now;
        record.ModifiedBy = actor;
        var expected = ToListRecord(record, (JsonObject)responseValues.DeepClone());

#if CELLBRIDGE_SQLSERVER
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
#endif
        var audit = new AuditEventRow { WorkspaceId = workspaceId, Utc = record.ModifiedUtc,
            Actor = actor, Action = "record.update", ListId = listId, RecordId = current.Id,
            Revision = revision, FieldIds = auditFields };
#if CELLBRIDGE_SQLSERVER
        StagePublicationAudit(db, audit);
#endif
        // PostgreSQL defers the audit FK's workspace KEY SHARE until quota is held.
        await SaveRequiredAsync(db, ct);
        await ValidatePublishedRecordAsync(db, connection, transaction, definition, record, expected, null, null,
            workspace, audit, valueBytes, current.Values, changedReferenceIds, authorization, ct, previousBytes);
        // Save interceptors may replace the tracked JSON before persistence.
        return StringComparer.Ordinal.Equals(record.ValuesJson, valuesJson)
            ? ToListRecord(record, responseValues)
            : ToListRecord(record);
    }

    private async Task PersistDeleteAsync(ListDbContext db,
        Guid workspaceId, Guid listId, ListRecord current, RecordRow record, string actor,
        long previousBytes, CancellationToken ct)
    {
        var auditBytes = AuditStorageBytes(actor, "record.delete", 0);
        var state = await _locks.LockedQuota(db, workspaceId)
            .Select(row => new
            {
                Workspace = row,
                Now = ListDbContext.ClockTimestamp()
            }).SingleOrDefaultAsync(ct) ?? throw NotFound();
        var workspace = state.Workspace;
        var nextBytes = Math.Max(0, checked(workspace.StoredBytes - previousBytes + auditBytes));
        if (_limits.MaxWorkspaceBytes is { } maxBytes && nextBytes > maxBytes ||
            _limits.MaxAuditEvents is { } maxAudits && workspace.AuditCount >= maxAudits)
            throw Limit("The workspace storage or audit limit has been reached.");
        db.Records.Attach(record);
        workspace.StoredBytes = nextBytes;
        workspace.AuditCount = checked(workspace.AuditCount + 1);
        db.RecordReferences.RemoveRange(await db.RecordReferences.Where(row => row.WorkspaceId == workspaceId &&
            row.SourceListId == listId && row.SourceRecordId == current.Id).ToListAsync(ct));
        await SaveRequiredAsync(db, ct);
        db.Records.Remove(record);
        db.AuditEvents.Add(new AuditEventRow { WorkspaceId = workspaceId,
            Utc = state.Now, Actor = actor, Action = "record.delete",
            ListId = listId, RecordId = current.Id, Revision = current.Revision, FieldIds = [] });
        await SaveRequiredAsync(db, ct);
    }

    private async Task DeleteExpiredReceiptAsync(ListDbContext db, Guid workspaceId,
        Guid listId, Guid recordId, Receipt receipt, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetByteCount(receipt.Digest) + Encoding.UTF8.GetByteCount(receipt.Actor) + 192L;
        var row = await db.CreateReceipts.SingleOrDefaultAsync(value => value.WorkspaceId == workspaceId &&
            value.ListId == listId && value.RecordId == recordId, ct);
        if (row is null) return;
        await _locks.QuotaAsync(db, workspaceId, ct);
        var workspace = await db.Workspaces.SingleAsync(value => value.WorkspaceId == workspaceId, ct);
        workspace.StoredBytes = Math.Max(0, checked(workspace.StoredBytes - bytes));
        db.CreateReceipts.Remove(row);
        await SaveRequiredAsync(db, ct);
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

    private async Task<ListRecord?> ReadRecordAsync(ListDbContext db,
        DbConnection connection, DbTransaction transaction, Guid workspaceId, Guid listId,
        Guid recordId, bool forUpdate, CancellationToken ct)
    {
        if (forUpdate)
        {
            await _locks.RecordAsync(db, workspaceId, listId, recordId, true, ct);
        }
        var row = await db.Records.AsNoTracking().SingleOrDefaultAsync(value => value.WorkspaceId == workspaceId &&
            value.ListId == listId && value.RecordId == recordId, ct);
        return row is null ? null : ToListRecord(row);
    }

    private static ListRecord ToListRecord(RecordRow row) => ToListRecord(row, ListJson.Object(row.ValuesJson));

    private static ListRecord ToListRecord(RecordRow row, JsonObject values) => new(row.RecordId, row.WorkspaceId, row.ListId,
        row.SchemaVersion, row.Revision, row.Number, values,
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
