using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Lists.SqlServer;

/// <summary>
/// SQL Server Lists authority, schema and record mutations. Mutations serialize
/// per workspace. Queries, views, audit retrieval and host list paging are pending.
/// </summary>
/// <remarks>
/// This non-packable provider does not establish SQL Server performance support.
/// Run SqlServerListMigrator and SqlServerListSetup with administrator options;
/// construct this engine with a separate restricted runtime principal. Health
/// requires SELECT on migration history, VIEW DEFINITION on the Lists schema,
/// and the minimum table permissions declared in SqlServerListHealth.
/// A supplied external connection must be reserved for one operation at a time.
/// Cancellation before commit rolls back; commit itself does not accept caller
/// cancellation. A store failure during commit may leave an uncertain outcome.
/// Repeat an original create to resolve that outcome while its receipt is live.
/// Logical storage counts compact System.Text.Json output in UTF-8, with its
/// default escaping and persisted scalar spellings, plus fixed row charges.
/// It does not count SQL Server nvarchar allocation or PostgreSQL normalization.
/// Expired receipts are retained in this phase; lifetime identity claims never
/// expire. Receipt pruning and its corresponding quota reclamation are pending.
/// </remarks>
public sealed partial class SqlServerListEngine : IListEngine
{
    private readonly DbContextOptions<SqlServerListDbContext> options;
    private readonly ListLimits limits;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SqlServerListEngine(DbContextOptions<SqlServerListDbContext> options, ListLimits? limits = null)
    {
        this.options = SqlServerListDatabase.Normalize(options);
        this.limits = limits ?? new ListLimits();
        this.limits.Validate();
    }

    /// <summary>Checks explicit migration, durability, RLS and restricted runtime permissions. Does not run DDL.</summary>
    public Task CheckHealthAsync(CancellationToken ct = default) => SqlServerListDatabase.MapFailuresAsync(async () =>
    {
        await using var scope = await SqlServerListWorkspaceTransaction.OpenAsync(options, Guid.NewGuid(), ct,
            limits.StatementTimeout);
        await CheckConfigurationAsync(scope.Context, ct);
        await scope.CommitAsync(ct);
        return true;
    }, ct);

    public Task<ListDefinition> CreateListAsync(ListActor actor, Guid workspaceId, CreateListRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OperateAsync(actor, workspaceId, true, async (db, workspace, member, authorization) =>
        {
            if (!member.CanCreateLists) throw Forbidden();
            var definition = await InstantiateAsync(db, workspaceId, request, ct);
            await ValidateReferenceDefinitionsAsync(db, workspaceId, actor.Subject, definition, authorization, ct);
            if (await db.Lists.AnyAsync(row => row.WorkspaceId == workspaceId && row.ListId == request.Id, ct))
                throw Conflict("That list ID already exists.");
            if (await db.Lists.LongCountAsync(row => row.WorkspaceId == workspaceId, ct) >= limits.MaxLists)
                throw Limit("The workspace list limit has been reached.");
            var list = new ListRow { WorkspaceId = workspaceId, ListId = request.Id, Name = definition.Name,
                SchemaVersion = 1, NumberPrefix = definition.NumberPrefix, TemplateId = definition.TemplateId,
                TemplateVersion = definition.TemplateVersion };
            var schema = new ListSchemaRow { WorkspaceId = workspaceId, ListId = request.Id,
                SchemaVersion = 1, Definition = JsonSerializer.Serialize(definition, JsonOptions) };
            db.Lists.Add(list);
            db.ListSchemas.Add(schema);
            db.ListCounters.Add(new ListCounterRow { WorkspaceId = workspaceId, ListId = request.Id });
            if (authorization is null)
                db.ListGrants.Add(new ListGrantRow { WorkspaceId = workspaceId, ListId = request.Id,
                    Subject = actor.Subject, Access = (int)(ListAccess.Read | ListAccess.Write | ListAccess.Design) });
            await SqlServerListDatabase.SaveRequiredAsync(db, ct);
            await db.Entry(schema).ReloadAsync(ct);
            await db.Entry(list).ReloadAsync(ct);
            var persisted = ReadDefinition(schema.Definition);
            ListValidation.ValidateDefinition(persisted, limits);
            if (persisted.Id != definition.Id || persisted.WorkspaceId != workspaceId || persisted.SchemaVersion != 1 ||
                !StringComparer.Ordinal.Equals(persisted.Name, list.Name) || persisted.TemplateId != definition.TemplateId ||
                persisted.TemplateVersion != definition.TemplateVersion || persisted.TemplateId != list.TemplateId ||
                persisted.TemplateVersion != list.TemplateVersion || persisted.NumberPrefix != list.NumberPrefix)
                throw InvalidStored();
            // Interceptors may change display/ordinary fields, but cannot invent
            // template provenance or replace pinned protected field definitions.
            var protectedFields = definition.Fields.Where(field => field.Protected).ToArray();
            if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(protectedFields, JsonOptions),
                JsonSerializer.SerializeToNode(persisted.Fields.Where(field => field.Protected).ToArray(), JsonOptions))) throw InvalidStored();
            await ValidateReferenceDefinitionsAsync(db, workspaceId, actor.Subject, persisted, authorization, ct);
            var json = schema.Definition;
            var fields = persisted.Fields.Select(field => field.Id).ToArray();
            Reserve(workspace, checked(JsonBytes(json) + 256 + (authorization is null ? 80 + Encoding.UTF8.GetByteCount(actor.Subject) : 0)),
                actor.Subject, "list.create", fields.Length);
            db.AuditEvents.Add(Audit(workspaceId, await SqlServerListDatabase.ClockAsync(db, ct), actor.Subject,
                "list.create", request.Id, null, 1, fields));
            await SqlServerListDatabase.SaveRequiredAsync(db, ct);
            await db.Entry(schema).ReloadAsync(ct);
            if (!StringComparer.Ordinal.Equals(schema.Definition, json)) throw InvalidStored();
            return persisted;
        }, ct, [(null, ListAuthorizationAction.CreateList)], async db =>
            (await InstantiateAsync(db, workspaceId, request, ct)).Fields.Where(field => field.Type == ListFieldType.Reference)
                .Select(field => field.ReferenceListId!.Value).Distinct().ToArray());
    }

    private async Task<ListDefinition> InstantiateAsync(SqlServerListDbContext db, Guid workspaceId,
        CreateListRequest request, CancellationToken ct)
    {
        ListDefinition definition;
        if (request.TemplateId is { } templateId && request.TemplateVersion is { } version)
        {
            var row = await db.Templates.AsNoTracking().SingleOrDefaultAsync(row => row.TemplateId == templateId && row.Version == version, ct)
                ?? throw NotFound();
            var template = JsonSerializer.Deserialize<ListTemplate>(row.Definition, JsonOptions) ?? throw InvalidStored();
            if (template.Id != templateId || template.Version != version) throw InvalidStored();
            definition = ListTemplates.Instantiate(template, workspaceId, request.Id, request.Name, request.Fields, request.NumberPrefix, limits);
        }
        else
        {
            if (request.TemplateId is not null || request.TemplateVersion is not null) throw Validation("Template ID and version must be supplied together.");
            definition = new ListDefinition(request.Id, workspaceId, request.Name, 1, request.Fields, NumberPrefix: request.NumberPrefix);
            ListValidation.ValidateDefinition(definition, limits);
            definition = definition with { Fields = definition.Fields.Select(field => field with { Protected = false }).ToArray() };
        }
        return definition;
    }

    public Task<IReadOnlyList<ListDefinition>> GetListsAsync(ListActor actor, Guid workspaceId,
        CancellationToken ct = default) => authorizationProvider is not null
        ? Task.FromException<IReadOnlyList<ListDefinition>>(Unsupported())
        : OperateAsync<IReadOnlyList<ListDefinition>>(actor, workspaceId, false,
        async (db, workspace, member, authorization) =>
        {
            var key = SqlServerListSetup.SubjectKey(actor.Subject);
            var rows = await (from list in db.Lists.AsNoTracking()
                join schema in db.ListSchemas.AsNoTracking()
                    on new { list.WorkspaceId, list.ListId, list.SchemaVersion }
                    equals new { schema.WorkspaceId, schema.ListId, schema.SchemaVersion }
                join grant in db.ListGrants.AsNoTracking()
                    on new { list.WorkspaceId, list.ListId } equals new { grant.WorkspaceId, grant.ListId }
                where list.WorkspaceId == workspaceId && EF.Property<byte[]>(grant, "SubjectKey") == key && grant.Access != 0
                select new { grant.Subject, schema.Definition }).ToListAsync(ct);
            return rows.Where(row => StringComparer.Ordinal.Equals(row.Subject, actor.Subject))
                .Select(row => ReadDefinition(row.Definition)).OrderBy(row => row.Name, StringComparer.Ordinal)
                .ThenBy(row => row.Id).ToArray();
        }, ct);

    public Task<ListDefinition> GetListAsync(ListActor actor, Guid workspaceId, Guid listId,
        CancellationToken ct = default) => OperateAsync(actor, workspaceId, false, async (db, workspace, member, authorization) =>
        {
            await RequireAccessAsync(db, workspaceId, listId, actor.Subject, ListAccess.Read, ct, authorization);
            return await GetDefinitionAsync(db, workspaceId, listId, ct);
        }, ct, [(listId, ListAuthorizationAction.Read)]);

    public Task<ListRecord> GetRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        CancellationToken ct = default) => OperateAsync(actor, workspaceId, false, async (db, workspace, member, authorization) =>
        {
            await RequireAccessAsync(db, workspaceId, listId, actor.Subject, ListAccess.Read, ct, authorization);
            var definition = await GetDefinitionAsync(db, workspaceId, listId, ct);
            var row = await db.Records.AsNoTracking().SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId &&
                row.ListId == listId && row.RecordId == recordId, ct) ?? throw NotFound();
            var values = ReadValues(row.ValuesJson);
            ListValidation.ValidateValues(definition, values, limits);
            return ToRecord(row, values);
        }, ct, [(listId, ListAuthorizationAction.Read)]);

    public Task<CreateRecordResult> CreateRecordAsync(ListActor actor, Guid workspaceId, Guid listId,
        CreateRecordRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Values);
        // Own the request snapshot across awaits. Interceptors may replace stored JSON,
        // but receipts always identify the original validated request.
        var snapshot = request with { Values = (JsonObject)request.Values.DeepClone() };
        var digest = ListValidation.CreateDigest(snapshot);
        return OperateAsync(actor, workspaceId, true, async (db, workspace, member, authorization) =>
        {
            // Current membership and Write permission precede all receipt inspection.
            await RequireAccessAsync(db, workspaceId, listId, actor.Subject, ListAccess.Write, ct, authorization);
            var definition = await GetDefinitionAsync(db, workspaceId, listId, ct);
            await SqlServerListDatabase.LockAsync(db,
                $"CellBridge.Lists.Record:{workspaceId:N}:{listId:N}:{snapshot.Id:N}", true, limits, ct);
            var now = await SqlServerListDatabase.ClockAsync(db, ct);
            var receipt = await db.CreateReceipts.AsNoTracking().SingleOrDefaultAsync(row =>
                row.WorkspaceId == workspaceId && row.ListId == listId && row.RecordId == snapshot.Id, ct);
            if (receipt is not null)
            {
                if (receipt.ExpiresUtc <= now || !StringComparer.Ordinal.Equals(receipt.Actor, actor.Subject) ||
                    receipt.SchemaVersion != snapshot.SchemaVersion || !StringComparer.Ordinal.Equals(receipt.RequestDigest, digest))
                    throw Conflict("That record ID has a different or expired create receipt.");
                var originalSchema = await db.ListSchemas.AsNoTracking().SingleOrDefaultAsync(row =>
                    row.WorkspaceId == workspaceId && row.ListId == listId && row.SchemaVersion == receipt.SchemaVersion, ct)
                    ?? throw InvalidStored();
                ListValidation.ValidateValues(ReadDefinition(originalSchema.Definition), snapshot.Values, limits);
                return new CreateRecordResult(snapshot.Id, receipt.Revision, receipt.Number, true);
            }
            if (await db.RecordTombstones.AnyAsync(row => row.WorkspaceId == workspaceId &&
                    row.ListId == listId && row.RecordId == snapshot.Id, ct) ||
                await db.Records.AnyAsync(row => row.WorkspaceId == workspaceId &&
                    row.ListId == listId && row.RecordId == snapshot.Id, ct))
                throw Conflict("That record ID was already used.");
            if (snapshot.SchemaVersion != definition.SchemaVersion) throw Conflict("The current schema version is required.");
            ListValidation.ValidateValues(definition, snapshot.Values, limits);
            await ValidateReferenceValuesAsync(db, workspaceId, actor.Subject, definition, snapshot.Values, authorization, ct);
            var counter = await db.ListCounters.SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId &&
                row.ListId == listId, ct) ?? throw InvalidStored();
            if (limits.MaxRecordsPerList is { } maximum && counter.IdentityCount >= maximum)
                throw Limit("The lifetime record identity limit has been reached.");
            var number = counter.NextNumber;
            counter.IdentityCount = checked(counter.IdentityCount + 1);
            counter.NextNumber = checked(number + 1);
            var record = new RecordRow { WorkspaceId = workspaceId, ListId = listId, RecordId = snapshot.Id,
                SchemaVersion = snapshot.SchemaVersion, Revision = 1, Number = number, ValuesJson = snapshot.Values.ToJsonString(),
                CreatedUtc = now, ModifiedUtc = now, CreatedBy = actor.Subject, ModifiedBy = actor.Subject };
            var createdReceipt = new CreateReceiptRow { WorkspaceId = workspaceId, ListId = listId, RecordId = snapshot.Id,
                Actor = actor.Subject, SchemaVersion = snapshot.SchemaVersion, RequestDigest = digest, Revision = 1,
                Number = number, CreatedUtc = now, ExpiresUtc = now.Add(limits.ReceiptLifetime) };
            db.RecordTombstones.Add(new RecordTombstoneRow { WorkspaceId = workspaceId, ListId = listId, RecordId = snapshot.Id });
            db.Records.Add(record);
            db.CreateReceipts.Add(createdReceipt);
            await SqlServerListDatabase.SaveRequiredAsync(db, ct);
            await db.Entry(record).ReloadAsync(ct);
            await db.Entry(createdReceipt).ReloadAsync(ct);
            ValidatePublication();
            var values = ReadValues(record.ValuesJson);
            ListValidation.ValidateValues(definition, values, limits);
            await ValidateReferenceValuesAsync(db, workspaceId, actor.Subject, definition, values, authorization, ct);
            await ReconcileReferencesAsync(db, workspaceId, listId, snapshot.Id, definition, values, ct);
            var persistedJson = record.ValuesJson;
            var fields = values.Select(value => Guid.Parse(value.Key)).ToArray();
            var receiptBytes = checked(192L + Encoding.UTF8.GetByteCount(digest) + Encoding.UTF8.GetByteCount(actor.Subject));
            Reserve(workspace, checked(JsonBytes(persistedJson) + receiptBytes + 128), actor.Subject, "record.create", fields.Length);
            db.AuditEvents.Add(Audit(workspaceId, now, actor.Subject, "record.create", listId, snapshot.Id, 1, fields));
            await SqlServerListDatabase.SaveRequiredAsync(db, ct);
            await db.Entry(record).ReloadAsync(ct);
            await db.Entry(createdReceipt).ReloadAsync(ct);
            ValidatePublication();
            if (!StringComparer.Ordinal.Equals(record.ValuesJson, persistedJson)) throw InvalidStored();
            return new CreateRecordResult(snapshot.Id, 1, number);

            void ValidatePublication()
            {
                if (db.Entry(record).State == EntityState.Detached || db.Entry(createdReceipt).State == EntityState.Detached ||
                    record.WorkspaceId != workspaceId || record.ListId != listId || record.RecordId != snapshot.Id ||
                    record.SchemaVersion != snapshot.SchemaVersion || record.Revision != 1 || record.Number != number ||
                    record.CreatedUtc != now || record.ModifiedUtc != now ||
                    !StringComparer.Ordinal.Equals(record.CreatedBy, actor.Subject) || !StringComparer.Ordinal.Equals(record.ModifiedBy, actor.Subject) ||
                    createdReceipt.WorkspaceId != workspaceId || createdReceipt.ListId != listId || createdReceipt.RecordId != snapshot.Id ||
                    createdReceipt.SchemaVersion != snapshot.SchemaVersion || createdReceipt.Revision != 1 || createdReceipt.Number != number ||
                    !StringComparer.Ordinal.Equals(createdReceipt.Actor, actor.Subject) ||
                    !StringComparer.Ordinal.Equals(createdReceipt.RequestDigest, digest) || createdReceipt.CreatedUtc != now ||
                    createdReceipt.ExpiresUtc != now.Add(limits.ReceiptLifetime)) throw InvalidStored();
            }
        }, ct, [(listId, ListAuthorizationAction.Write)], db => ReferenceTargetsAsync(db, workspaceId, listId,
            snapshot.Values.Where(value => value.Value is not null).Select(value => Guid.Parse(value.Key)).ToHashSet(), ct));
    }

    private Task<T> OperateAsync<T>(ListActor actor, Guid workspaceId, bool mutation,
        Func<SqlServerListDbContext, WorkspaceRow, MembershipRow, OperationAuthorization?, Task<T>> operation,
        CancellationToken ct, IReadOnlyList<(Guid? ListId, ListAuthorizationAction Action)>? requirements = null,
        Func<SqlServerListDbContext, Task<Guid[]>>? referenceTargets = null)
    {
        ListAuthorizationValidation.ValidateActor(actor);
        if (workspaceId == Guid.Empty) throw Validation("A workspace ID is required.");
        return SqlServerListDatabase.MapFailuresAsync(async () =>
        {
            var authorization = await AuthorizeOperationAsync(actor, workspaceId, requirements ?? [], referenceTargets, ct);
            await using var scope = await SqlServerListWorkspaceTransaction.OpenAsync(options, workspaceId, ct, limits.StatementTimeout);
            var db = scope.Context;
            await CheckConfigurationAsync(db, ct);
            await SqlServerListDatabase.LockAsync(db, SqlServerListDatabase.WorkspaceLock(workspaceId), mutation, limits, ct);
            var workspace = await db.Workspaces.SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId, ct) ?? throw Forbidden();
            MembershipRow member;
            if (authorization is null)
            {
                if (!StringComparer.Ordinal.Equals(workspace.AuthorizationMode, "builtin")) throw NotFound();
                member = await FindMemberAsync(db, workspaceId, actor.Subject, ct) ?? throw Forbidden();
            }
            else
            {
                RequireAnchor(workspace, authorization, actor);
                member = new MembershipRow { Subject = actor.Subject,
                    CanCreateLists = authorization.Allows(null, ListAuthorizationAction.CreateList) };
                authorization.ValidateFreshness();
            }
            RequireReserve(workspace);
            var result = await operation(db, workspace, member, authorization);
            authorization?.ValidateFreshness();
            await scope.CommitAsync(ct);
            return result;
        }, ct);
    }

    private static async Task<MembershipRow?> FindMemberAsync(SqlServerListDbContext db, Guid workspaceId,
        string subject, CancellationToken ct, bool tracked = false)
    {
        var key = SqlServerListSetup.SubjectKey(subject);
        var query = tracked ? db.Memberships.AsTracking() : db.Memberships.AsNoTracking();
        var member = await query.SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId &&
            EF.Property<byte[]>(row, "SubjectKey") == key, ct);
        if (member is not null && !StringComparer.Ordinal.Equals(member.Subject, subject)) throw Forbidden();
        return member;
    }

    private static async Task RequireAccessAsync(SqlServerListDbContext db, Guid workspaceId, Guid listId,
        string subject, ListAccess required, CancellationToken ct, OperationAuthorization? authorization = null)
    {
        if (authorization is not null)
        {
            if (!authorization.Allows(listId, required)) throw NotFound();
            return;
        }
        var key = SqlServerListSetup.SubjectKey(subject);
        var grant = await db.ListGrants.AsNoTracking().SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId &&
            row.ListId == listId && EF.Property<byte[]>(row, "SubjectKey") == key, ct);
        if (grant is null || !StringComparer.Ordinal.Equals(grant.Subject, subject) ||
            ((ListAccess)grant.Access & required) != required) throw NotFound();
    }

    private void RequireReserve(WorkspaceRow workspace)
    {
        if (workspace.SecurityAuditCount >= limits.MaxSecurityAuditEvents)
            throw new ListException(ListFailure.Unavailable, "The workspace security audit reserve is exhausted.");
    }

    private static async Task<ListDefinition> GetDefinitionAsync(SqlServerListDbContext db, Guid workspaceId,
        Guid listId, CancellationToken ct)
    {
        var json = await (from list in db.Lists.AsNoTracking()
            join schema in db.ListSchemas.AsNoTracking()
                on new { list.WorkspaceId, list.ListId, list.SchemaVersion }
                equals new { schema.WorkspaceId, schema.ListId, schema.SchemaVersion }
            where list.WorkspaceId == workspaceId && list.ListId == listId
            select schema.Definition).SingleOrDefaultAsync(ct) ?? throw NotFound();
        var definition = ReadDefinition(json);
        if (definition.WorkspaceId != workspaceId || definition.Id != listId) throw InvalidStored();
        return definition;
    }

    private void Reserve(WorkspaceRow workspace, long bytes, string actor, string action, int fieldCount)
    {
        var total = checked(workspace.StoredBytes + bytes + 96L + Encoding.UTF8.GetByteCount(actor) +
            Encoding.UTF8.GetByteCount(action) + fieldCount * 16L);
        if (limits.MaxWorkspaceBytes is { } maximum && total > maximum ||
            limits.MaxAuditEvents is { } audits && workspace.AuditCount >= audits)
            throw Limit("The workspace storage or audit limit has been reached.");
        workspace.StoredBytes = total;
        workspace.AuditCount = checked(workspace.AuditCount + 1);
    }

    private static AuditEventRow Audit(Guid workspaceId, DateTimeOffset now, string actor, string action,
        Guid? listId, Guid? recordId, long? revision, Guid[] fields) => new() { WorkspaceId = workspaceId,
            Utc = now, Actor = actor, Action = action, ListId = listId, RecordId = recordId, Revision = revision, FieldIds = fields };

    // Logical JSON bytes are UTF-8 bytes of compact System.Text.Json output after
    // parsing persisted text. This is independent of nvarchar's UTF-16 allocation.
    private static long JsonBytes(string json) => Encoding.UTF8.GetByteCount(JsonNode.Parse(json)!.ToJsonString());
    private static ListDefinition ReadDefinition(string json) => JsonSerializer.Deserialize<ListDefinition>(json, JsonOptions) ?? throw InvalidStored();
    private static JsonObject ReadValues(string json) => JsonNode.Parse(json) as JsonObject ?? throw InvalidStored();
    private static ListRecord ToRecord(RecordRow row, JsonObject values) => new(row.RecordId, row.WorkspaceId, row.ListId,
        row.SchemaVersion, row.Revision, row.Number, values, row.CreatedUtc, row.ModifiedUtc, row.CreatedBy, row.ModifiedBy);
    private static ListException Validation(string message) => new(ListFailure.Validation, message);
    private static ListException Forbidden() => new(ListFailure.Forbidden, "Workspace membership and permission are required.");
    private static ListException NotFound() => new(ListFailure.NotFound, "The list or record was not found.");
    private static ListException Conflict(string message) => new(ListFailure.Conflict, message);
    private static ListException Limit(string message) => new(ListFailure.Limit, message);
    private static ListException InvalidStored() => new(ListFailure.Unavailable, "Persisted list state is invalid or changed during publication.");
    private static ListException Unsupported() => new(ListFailure.NotSupported, "This operation is not yet supported by the SQL Server Lists runtime.");
}
