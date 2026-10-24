using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;

#if CELLBRIDGE_SQLSERVER
namespace CellBridge.Lists.SqlServer;
#else
namespace CellBridge.Lists.PostgreSql;
#endif

/// <summary>
/// Shared EF list operations. Provider helpers implement database locks, mapping and session security.
/// </summary>
internal sealed partial class EfListEngine : IListEngine, IPagedListEngine
{
    private readonly DbContextOptions<ListDbContext> _dbOptions;
    private readonly PooledDbContextFactory<ListDbContext>? _contextFactory;
    private readonly ListLimits _limits;
    private readonly IListDatabaseLocks _locks;

    internal EfListEngine(DbContextOptions<ListDbContext> options, ListLimits? limits,
        byte[]? continuationKey, IListAuthorizationProvider? authorizationProvider = null)
    {
        _dbOptions = NormalizeOptions(options ?? throw new ArgumentNullException(nameof(options)));
        _contextFactory = CreateContextFactory(_dbOptions);
        _limits = limits ?? new ListLimits();
        _limits.Validate();
        _locks = new DatabaseLocks(this);
        _authorizationProvider = authorizationProvider;
        _authorizationDomain = authorizationProvider is null ? null : ValidateDomain(authorizationProvider.PolicyDomain);
        if (continuationKey is not null)
        {
            if (continuationKey.Length != 32) throw new ArgumentException("The continuation key must contain 32 bytes.", nameof(continuationKey));
            _continuationKey = continuationKey.ToArray();
        }
    }

    public Task CheckHealthAsync(CancellationToken cancellationToken = default) => EnsureHealthyAsync(cancellationToken);

    public async Task<IReadOnlyList<ListDefinition>> GetListsAsync(ListActor actor, Guid workspaceId,
        CancellationToken ct = default)
    {
        var lists = new List<ListDefinition>();
        string? continuation = null;
        do
        {
            var page = await GetListsPageAsync(actor, workspaceId, continuation,
                Math.Min(256, _limits.MaxLists), ct);
            lists.AddRange(page.Lists);
            if (lists.Count > ListEnumerationLimit)
                throw Limit("The workspace list limit has been exceeded.");
            continuation = page.Continuation;
        } while (continuation is not null);
        return lists.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id).ToArray();
    }

    public async Task<ListDefinitionPage> GetListsPageAsync(ListActor actor, Guid workspaceId,
        string? continuation = null, int candidateLimit = 256, CancellationToken ct = default)
    {
        ValidateActor(actor);
        if (workspaceId == Guid.Empty) throw Validation("A workspace ID is required.");
        if (candidateLimit is < 1 or > 256) throw Validation("The candidate page size must be between 1 and 256.");
        var after = continuation is null ? (Guid?)null : UnprotectContinuation(continuation, actor, workspaceId).AfterCandidateId;

        if (_authorizationProvider is null)
        {
            return await InTransactionAsync(workspaceId, false, null, async (db, connection, transaction) =>
            {
                await AuthorizeMemberAsync(db, workspaceId, actor.Subject, ct, null);
                var data = await (from list in db.Lists.AsNoTracking()
                    join schema in db.ListSchemas.AsNoTracking()
                        on new { list.WorkspaceId, list.ListId, list.SchemaVersion }
                        equals new { schema.WorkspaceId, schema.ListId, schema.SchemaVersion }
                    join grant in db.ListGrants.AsNoTracking()
                        on new { list.WorkspaceId, list.ListId } equals new { grant.WorkspaceId, grant.ListId }
                    where list.WorkspaceId == workspaceId && grant.Subject == actor.Subject && grant.Access != 0 &&
                          (!after.HasValue || ListDbContext.UuidKey(list.ListId).CompareTo(ListDbContext.UuidKey(after.Value)) > 0)
                    orderby ListDbContext.UuidKey(list.ListId)
                    select new { list.ListId, schema.Definition })
                    .Take(candidateLimit + 1).ToListAsync(ct);
                var rows = data.Select(row => (row.ListId,
                    ListJson.Deserialize<ListDefinition>(row.Definition))).ToList();
                return Page(rows, candidateLimit, actor, workspaceId);
            }, ct);
        }

        var authorization = await AuthorizeOperationAsync(actor, workspaceId, [], ct);
        var queryRequest = new ListAuthorizationRequest(authorization!.Anchor.Domain!, actor.Subject,
            authorization.Anchor.Scope,
            workspaceId, null, ListAuthorizationAction.Read);
        ListQueryAuthorizationDecision queryDecision;
        var hasQueryDecision = false;
        if (_authorizationProvider is IListQueryAuthorizationProvider queryProvider)
        {
            var callStartedUtc = DateTimeOffset.UtcNow;
            try { queryDecision = await queryProvider.AuthorizeQueryAsync(queryRequest, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { throw AuthorizationUnavailable(); }
            ListAuthorizationValidation.ValidateQueryDecision(
                queryDecision, queryRequest, callStartedUtc, DateTimeOffset.UtcNow);
            hasQueryDecision = true;
        }
        else
        {
            queryDecision = new(ListQueryAuthorizationScope.CheckCandidates, queryRequest.PolicyDomain,
                queryRequest.Subject, queryRequest.TenantId, queryRequest.WorkspaceId);
        }
        if (hasQueryDecision)
            authorization = new OperationAuthorization(authorization.Anchor,
                authorization.Decisions.Append((queryRequest,
                    new ListAuthorizationDecision(true, queryDecision.EvaluatedAtUtc,
                        queryDecision.FreshUntilUtc))).ToArray());

        if (queryDecision.Scope == ListQueryAuthorizationScope.None)
        {
            // The empty set still checks the anchor and audit reserve, without reading list data.
            var empty = await ReadAuthorizedDefinitionsAsync(actor.Subject, workspaceId,
                new HashSet<Guid>(), authorization!, ct);
            return new(empty, null);
        }

        var candidates = await ReadCandidateIdsAsync(actor.Subject, workspaceId, after, candidateLimit + 1, authorization!, ct);
        var scanned = candidates.Take(candidateLimit).ToArray();
        IReadOnlySet<Guid> allowed;
        if (queryDecision.Scope == ListQueryAuthorizationScope.All) allowed = scanned.ToHashSet();
        else
        {
            var requests = scanned.Select(id => new ListAuthorizationRequest(authorization.Anchor.Domain!, actor.Subject,
                authorization.Anchor.Scope, workspaceId, id, ListAuthorizationAction.Read)).ToArray();
            IReadOnlyList<ListAuthorizationDecision> decisions;
            var callStartedUtc = DateTimeOffset.UtcNow;
            try
            {
                decisions = _authorizationProvider is IListBatchAuthorizationProvider batch
                    ? await batch.AuthorizeBatchAsync(requests, ct)
                    : await AuthorizeSeparatelyAsync(_authorizationProvider, requests, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { throw AuthorizationUnavailable(); }
            if (decisions is null || decisions.Count != requests.Length) throw AuthorizationUnavailable();
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < decisions.Count; i++)
                ListAuthorizationValidation.ValidateDecision(decisions[i], callStartedUtc, now);
            allowed = scanned.Where((_, index) => decisions[index].Allowed).ToHashSet();
            authorization = new OperationAuthorization(authorization!.Anchor,
                authorization.Decisions.Concat(requests.Zip(decisions)).ToArray());
        }

        var definitions = await ReadAuthorizedDefinitionsAsync(actor.Subject, workspaceId, allowed, authorization!, ct);
        var next = candidates.Count > candidateLimit && scanned.Length != 0
            ? ProtectContinuation(actor, workspaceId, scanned[^1])
            : null;
        return new(definitions, next);
    }

    private ListDefinitionPage Page(List<(Guid Id, ListDefinition Definition)> rows, int limit,
        ListActor actor, Guid workspaceId)
    {
        var page = rows.Take(limit).ToArray();
        var next = rows.Count > limit && page.Length != 0
            ? ProtectContinuation(actor, workspaceId, page[^1].Id)
            : null;
        return new(page.Select(x => x.Definition).ToArray(), next);
    }

    private Task<IReadOnlyList<Guid>> ReadCandidateIdsAsync(string subject, Guid workspaceId, Guid? after, int limit,
        OperationAuthorization authorization, CancellationToken ct) =>
        MapStoreFailuresAsync<IReadOnlyList<Guid>>(async () =>
        {
            await using var session = await OpenSessionAsync(workspaceId, ct);
            var db = session.Context;
            var connection = db.Database.GetDbConnection();
            var transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            await ConfigureCandidateReadAsync(db, workspaceId, authorization, ct);
            authorization.ValidateFreshness();
            await RequireAnchorAsync(db, workspaceId, authorization, true, ct);
            await AuthorizeMemberAsync(db, workspaceId, subject, ct, authorization);
            var result = await db.Lists.AsNoTracking()
                .Where(row => row.WorkspaceId == workspaceId &&
                              (!after.HasValue || ListDbContext.UuidKey(row.ListId).CompareTo(ListDbContext.UuidKey(after.Value)) > 0))
                .OrderBy(row => ListDbContext.UuidKey(row.ListId)).Select(row => row.ListId).Take(limit).ToListAsync(ct);
            authorization.ValidateFreshness();
            await session.CommitAsync(ct);
            return result;
        }, ct);

    private Task<IReadOnlyList<ListDefinition>> ReadAuthorizedDefinitionsAsync(string subject, Guid workspaceId,
        IReadOnlySet<Guid> allowed, OperationAuthorization authorization, CancellationToken ct) =>
        InTransactionAsync(workspaceId, false, authorization, async (db, connection, transaction) =>
        {
            await AuthorizeMemberAsync(db, workspaceId, subject, ct, authorization);
            if (allowed.Count == 0) return (IReadOnlyList<ListDefinition>)[];
            var definitions = await (from list in db.Lists.AsNoTracking()
                join schema in db.ListSchemas.AsNoTracking()
                    on new { list.WorkspaceId, list.ListId, list.SchemaVersion }
                    equals new { schema.WorkspaceId, schema.ListId, schema.SchemaVersion }
                where list.WorkspaceId == workspaceId && allowed.Contains(list.ListId)
                orderby ListDbContext.UuidKey(list.ListId)
                select schema.Definition).ToListAsync(ct);
            return definitions.Select(ListJson.Deserialize<ListDefinition>).ToArray();
        }, ct);

    public async Task<ListDefinition> CreateListAsync(ListActor actor, Guid workspaceId,
        CreateListRequest request, CancellationToken ct = default)
    {
        ValidateActor(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Fields is null || request.Fields.Any(field => field is null)) throw Validation("Fields are required and cannot contain null.");
        request = request with { Fields = request.Fields.ToArray() };
        if (request.Id == Guid.Empty) throw Validation("A list ID is required.");
        var requirements = new List<(Guid?, ListAuthorizationAction)> { (null, ListAuthorizationAction.CreateList) };
        requirements.AddRange(request.Fields.Where(x => x.Type == ListFieldType.Reference)
            .Select(x => ((Guid?)x.ReferenceListId, ListAuthorizationAction.Read)));
        var authorization = await AuthorizeOperationAsync(actor, workspaceId, requirements, ct);
        ListDefinition? preflightDefinition = null;
        if (_authorizationProvider is not null && request.TemplateId is { } preflightTemplateId)
        {
            if (request.TemplateVersion is not { } preflightTemplateVersion)
                throw Validation("A template version is required.");
            var template = await ReadTemplatePreflightAsync(actor.Subject, workspaceId, authorization!,
                preflightTemplateId, preflightTemplateVersion, ct);
            preflightDefinition = ListTemplates.Instantiate(template, workspaceId, request.Id, request.Name,
                request.Fields, request.NumberPrefix, _limits);
            var templateRequirements = preflightDefinition.Fields
                .Where(field => field.Type == ListFieldType.Reference)
                .Select(field => ((Guid?)field.ReferenceListId, ListAuthorizationAction.Read))
                .Distinct()
                .ToArray();
            authorization = await AuthorizeAdditionalAsync(actor, authorization!, templateRequirements, ct);
        }
        return await InTransactionAsync(workspaceId, false, authorization, async (db, connection, transaction) =>
        {
            var member = await AuthorizeMemberAsync(db, workspaceId, actor.Subject, ct, authorization);
            if (!member.CanCreateLists) throw Forbidden();

            ListDefinition definition;
            if (request.TemplateId is { } templateId)
            {
                if (request.TemplateVersion is not { } templateVersion) throw Validation("A template version is required.");
                var template = await GetTemplateAsync(db, templateId, templateVersion, ct);
                definition = ListTemplates.Instantiate(template, workspaceId, request.Id, request.Name,
                    request.Fields, request.NumberPrefix, _limits);
                if (preflightDefinition is not null &&
                    !StringComparer.Ordinal.Equals(ListJson.Serialize(preflightDefinition),
                        ListJson.Serialize(definition)))
                    throw new ListException(ListFailure.Unavailable,
                        "The template changed after authorization.");
            }
            else
            {
                if (request.TemplateVersion is not null) throw Validation("A template ID is required.");
                definition = new ListDefinition(request.Id, workspaceId, request.Name, 1,
                    request.Fields.Select(x => x with { Protected = false }).ToArray(), NumberPrefix: request.NumberPrefix);
            }
            ListValidation.ValidateDefinition(definition, _limits);
            await ValidateReferenceDefinitionsAsync(db, workspaceId, actor.Subject, definition, ct, authorization: authorization);

            await _locks.QuotaAsync(db, workspaceId, ct);
            var listCount = await db.Lists.LongCountAsync(row => row.WorkspaceId == workspaceId, ct);
            if (listCount >= _limits.MaxLists) throw Limit("The workspace list limit has been reached.");
            var json = ListJson.Serialize(definition);
            var list = new ListRow { WorkspaceId = workspaceId, ListId = request.Id, Name = definition.Name,
                SchemaVersion = 1, TemplateId = definition.TemplateId, TemplateVersion = definition.TemplateVersion,
                NumberPrefix = definition.NumberPrefix };
            var schema = new ListSchemaRow { WorkspaceId = workspaceId, ListId = request.Id, SchemaVersion = 1, Definition = json };
            try
            {
                db.Lists.Add(list);
                db.ListSchemas.Add(schema);
                db.ListCounters.Add(new ListCounterRow { WorkspaceId = workspaceId, ListId = request.Id });
                if (authorization is null)
                    db.ListGrants.Add(new ListGrantRow { WorkspaceId = workspaceId, ListId = request.Id,
                        Subject = actor.Subject, Access = (int)(ListAccess.Read | ListAccess.Write | ListAccess.Design) });
                await SaveRequiredAsync(db, ct);
            }
            catch (Exception ex) when (IsUniqueViolation(ex))
            { throw Conflict("That list ID already exists."); }
            await ReloadPublicationAsync(db, ct, list, schema);
            var persisted = ListJson.Deserialize<ListDefinition>(schema.Definition);
            ListValidation.ValidateDefinition(persisted, _limits);
            if (list.WorkspaceId != workspaceId || list.ListId != request.Id || list.SchemaVersion != 1 ||
                persisted.Id != request.Id || persisted.WorkspaceId != workspaceId || persisted.SchemaVersion != 1 ||
                !StringComparer.Ordinal.Equals(persisted.Name, list.Name) || persisted.TemplateId != definition.TemplateId ||
                persisted.TemplateVersion != definition.TemplateVersion || persisted.TemplateId != list.TemplateId ||
                persisted.TemplateVersion != list.TemplateVersion || persisted.NumberPrefix != list.NumberPrefix ||
                !JsonNode.DeepEquals(System.Text.Json.JsonSerializer.SerializeToNode(definition.Fields.Where(field => field.Protected).ToArray()),
                    System.Text.Json.JsonSerializer.SerializeToNode(persisted.Fields.Where(field => field.Protected).ToArray())))
                throw InvalidPublication();
            await ValidateReferenceDefinitionsAsync(db, workspaceId, actor.Subject, persisted, ct, authorization: authorization);
            await ReserveStorageAsync(db, workspaceId,
                await CanonicalJsonBytesAsync(db, schema.Definition, ct) + 256 +
                (authorization is null ? 80 + Encoding.UTF8.GetByteCount(actor.Subject) : 0), ct);
            json = schema.Definition;
            definition = persisted;
            await AuditAsync(db, connection, transaction, workspaceId, actor.Subject, "list.create", request.Id, null, 1,
                definition.Fields.Select(x => x.Id), false, ct);
            await ReloadPublicationAsync(db, ct, list, schema);
            if (schema.Definition != json || list.Name != definition.Name || list.SchemaVersion != definition.SchemaVersion)
                throw InvalidPublication();
            return definition;
        }, ct, mutation: true);
    }

    public async Task<ListDefinition> GetListAsync(ListActor actor, Guid workspaceId, Guid listId,
        CancellationToken ct = default)
    {
        ValidateActor(actor);
        var authorization = await AuthorizeOperationAsync(actor, workspaceId,
            [(listId, ListAuthorizationAction.Read)], ct);
        return await InTransactionAsync(workspaceId, false, authorization, async (db, connection, transaction) =>
        {
            return await ReadListDefinitionAsync(db, connection, transaction, workspaceId, listId,
                actor.Subject, ListAccess.Read, ct, authorization);
        }, ct);
    }

    public async Task<ListDefinition> PublishSchemaAsync(ListActor actor, Guid workspaceId, Guid listId,
        PublishSchemaRequest request, CancellationToken ct = default)
    {
        ValidateActor(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Fields is null || request.Fields.Any(field => field is null)) throw Validation("Fields are required and cannot contain null.");
        request = request with { Fields = request.Fields.ToArray() };
        var sourceRequirements = new (Guid?, ListAuthorizationAction)[] { (listId, ListAuthorizationAction.Design) };
        var authorization = await AuthorizeOperationAsync(actor, workspaceId, sourceRequirements, ct);
        if (_authorizationProvider is not null)
        {
            var current = await ReadDefinitionPreflightAsync(actor.Subject, workspaceId, listId, authorization!, ct);
            if (current.SchemaVersion != request.ExpectedVersion) throw Conflict("The list schema changed.");
            ListValidation.ValidateEvolution(current, current with { Name = request.Name,
                SchemaVersion = checked(current.SchemaVersion + 1), Fields = request.Fields }, _limits);
            var existing = current.Fields.Select(x => x.Id).ToHashSet();
            var requirements = request.Fields
                .Where(x => x.Type == ListFieldType.Reference && !existing.Contains(x.Id))
                .Select(x => ((Guid?)x.ReferenceListId, ListAuthorizationAction.Read))
                .Distinct()
                .ToArray();
            authorization = await AuthorizeAdditionalAsync(actor, authorization!, requirements, ct);
        }
        return await InTransactionAsync(workspaceId, false, authorization, async (db, connection, transaction) =>
        {
            await AuthorizeMemberAsync(db, workspaceId, actor.Subject, ct, authorization);
            await RequireAccessAsync(db, workspaceId, listId, actor.Subject, ListAccess.Design, ct, authorization);
            await _locks.MutationAsync(db, workspaceId, ct);
            var previous = await GetDefinitionAsync(db, connection, transaction, workspaceId, listId, true, ct);
            if (previous.SchemaVersion != request.ExpectedVersion) throw Conflict("The list schema changed.");
            var next = previous with { Name = request.Name, SchemaVersion = checked(previous.SchemaVersion + 1), Fields = request.Fields };
            ListValidation.ValidateEvolution(previous, next, _limits);
            var previousFieldIds = previous.Fields.Select(x => x.Id).ToHashSet();
            var addedReferenceFieldIds = next.Fields
                .Where(x => x.Type == ListFieldType.Reference && !previousFieldIds.Contains(x.Id))
                .Select(x => x.Id)
                .ToHashSet();
            await ValidateReferenceDefinitionsAsync(db, workspaceId, actor.Subject, next, ct,
                addedReferenceFieldIds, authorization);
            await _locks.QuotaAsync(db, workspaceId, ct);
            var versions = await db.ListSchemas.LongCountAsync(
                row => row.WorkspaceId == workspaceId && row.ListId == listId, ct);
            if (versions >= _limits.MaxSchemaVersions) throw Limit("The list schema version limit has been reached.");
            var json = ListJson.Serialize(next);
            var schema = new ListSchemaRow { WorkspaceId = workspaceId, ListId = listId,
                SchemaVersion = next.SchemaVersion, Definition = json };
            db.ListSchemas.Add(schema);
            var list = await db.Lists.SingleAsync(
                row => row.WorkspaceId == workspaceId && row.ListId == listId, ct);
            list.Name = next.Name;
            list.SchemaVersion = next.SchemaVersion;
            await SaveRequiredAsync(db, ct);
            await ReloadPublicationAsync(db, ct, list, schema);
            var persisted = ListJson.Deserialize<ListDefinition>(schema.Definition);
            ListValidation.ValidateEvolution(previous, persisted, _limits);
            if (persisted.Id != listId || persisted.WorkspaceId != workspaceId ||
                persisted.Name != list.Name || persisted.SchemaVersion != list.SchemaVersion) throw InvalidPublication();
            var added = persisted.Fields.Where(field => field.Type == ListFieldType.Reference && !previousFieldIds.Contains(field.Id))
                .Select(field => field.Id).ToHashSet();
            await ValidateReferenceDefinitionsAsync(db, workspaceId, actor.Subject, persisted, ct, added, authorization);
            await ReserveStorageAsync(db, workspaceId, await CanonicalJsonBytesAsync(db, schema.Definition, ct) + 96, ct);
            json = schema.Definition;
            next = persisted;
            await AuditAsync(db, connection, transaction, workspaceId, actor.Subject, "schema.publish", listId, null,
                next.SchemaVersion, next.Fields.Select(x => x.Id), false, ct);
            await ReloadPublicationAsync(db, ct, schema);
            if (schema.Definition != json) throw InvalidPublication();
            return next;
        }, ct, mutation: true);
    }

    public async Task<IReadOnlyList<SavedListView>> GetViewsAsync(ListActor actor, Guid workspaceId,
        Guid listId, CancellationToken ct = default) => await ReadAsync(actor, workspaceId, async (db, connection, transaction, member, authorization) =>
    {
        await RequireAccessAsync(db, workspaceId, listId, actor.Subject, ListAccess.Read, ct, authorization);
        var definitions = await db.SavedViews.AsNoTracking()
            .Where(row => row.WorkspaceId == workspaceId && row.ListId == listId)
            .OrderBy(row => ListDbContext.UuidKey(row.ViewId)).Select(row => row.Definition).ToListAsync(ct);
        return definitions.Select(ListJson.Deserialize<SavedListView>).ToArray();
    }, [(listId, ListAuthorizationAction.Read)], ct);

    public async Task<SavedListView> SaveViewAsync(ListActor actor, Guid workspaceId, Guid listId,
        SaveViewRequest request, CancellationToken ct = default)
    {
        ValidateActor(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (request.VisibleFields is null) throw Validation("Visible fields are required.");
        request = request with { VisibleFields = request.VisibleFields.ToArray() };
        var authorization = await AuthorizeOperationAsync(actor, workspaceId,
            [(listId, ListAuthorizationAction.Design)], ct);
        return await InTransactionAsync(workspaceId, false, authorization, async (db, connection, transaction) =>
        {
            await AuthorizeMemberAsync(db, workspaceId, actor.Subject, ct, authorization);
            await RequireAccessAsync(db, workspaceId, listId, actor.Subject, ListAccess.Design, ct, authorization);
            var definition = await GetDefinitionAsync(db, connection, transaction, workspaceId, listId, true, ct);
            ListValidation.ValidateQuery(definition, request.Query, _limits);
            if (request.Id == Guid.Empty || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200 ||
                !ListValidation.IsWellFormedText(request.Name))
                throw Validation("A view ID and valid name are required.");
            if (request.VisibleFields.Distinct().Count() != request.VisibleFields.Count ||
                request.VisibleFields.Any(id => definition.Fields.All(x => x.Id != id)))
                throw Validation("A view contains an unknown or duplicate field.");
            await _locks.QuotaAsync(db, workspaceId, ct);
            var count = await db.SavedViews.LongCountAsync(row => row.WorkspaceId == workspaceId &&
                row.ListId == listId && row.ViewId != request.Id, ct);
            var existing = await db.SavedViews.SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId &&
                row.ListId == listId && row.ViewId == request.Id, ct);
            if (existing is null && count >= _limits.MaxViewsPerList) throw Limit("The saved view limit has been reached.");
            var view = new SavedListView(request.Id, listId, request.Name, ListJson.NormalizeQuery(request.Query), request.VisibleFields);
            var json = ListJson.Serialize(view);
            long? previousBytes = existing is null ? null : await CanonicalJsonBytesAsync(db, existing.Definition, ct);
            var row = existing ?? new SavedViewRow { WorkspaceId = workspaceId, ListId = listId, ViewId = request.Id };
            row.Definition = json;
            if (existing is null) db.SavedViews.Add(row);
            await SaveRequiredAsync(db, ct);
            await ReloadPublicationAsync(db, ct, row);
            var persisted = ListJson.Deserialize<SavedListView>(row.Definition);
            ValidateView(definition, persisted);
            if (persisted.Id != request.Id || persisted.ListId != listId) throw InvalidPublication();
            await ReserveStorageAsync(db, workspaceId,
                await CanonicalJsonBytesAsync(db, row.Definition, ct) - (previousBytes ?? -64), ct);
            json = row.Definition;
            await AuditAsync(db, connection, transaction, workspaceId, actor.Subject, "view.save", listId, null, null,
                persisted.VisibleFields, false, ct);
            await ReloadPublicationAsync(db, ct, row);
            if (row.Definition != json) throw InvalidPublication();
            return persisted;
        }, ct, mutation: true);
    }

    private async Task<T> ReadAsync<T>(ListActor actor, Guid workspaceId,
        Func<ListDbContext, DbConnection, DbTransaction, Membership, OperationAuthorization?, Task<T>> operation,
        IReadOnlyList<(Guid? ListId, ListAuthorizationAction Action)> requirements, CancellationToken ct)
    {
        ValidateActor(actor);
        var authorization = await AuthorizeOperationAsync(actor, workspaceId, requirements, ct);
        return await InTransactionAsync(workspaceId, false, authorization, async (db, connection, transaction) =>
        {
            var member = await AuthorizeMemberAsync(db, workspaceId, actor.Subject, ct, authorization);
            return await operation(db, connection, transaction, member, authorization);
        }, ct);
    }

    private Task<T> InTransactionAsync<T>(Guid workspaceId, bool exclusiveAuthorization,
        OperationAuthorization? authorization,
        Func<ListDbContext, DbConnection, DbTransaction, Task<T>> operation,
        CancellationToken ct, bool mutation = false) =>
        MapStoreFailuresAsync(async () =>
        {
            if (workspaceId == Guid.Empty) throw Validation("A workspace ID is required.");
            await EnsureHealthyAsync(ct);
            await using var session = await OpenSessionAsync(workspaceId, ct);
            var db = session.Context;
            var connection = db.Database.GetDbConnection();
            var transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            await _locks.AuthorizationAsync(db, workspaceId, exclusiveAuthorization, mutation, authorization, ct);
            authorization?.ValidateFreshness();
            var result = await operation(db, connection, transaction);
            authorization?.ValidateFreshness();
            ct.ThrowIfCancellationRequested();
            await session.CommitAsync(ct);
            return result;
        }, ct);

    private async Task<Membership> AuthorizeMemberAsync(ListDbContext db,
        Guid workspaceId, string subject, CancellationToken ct,
        OperationAuthorization? authorization)
    {
        if (authorization is not null)
        {
            if (!authorization.Allows(null, ListAuthorizationAction.UseLists)) throw Forbidden();
            var count = await db.Workspaces.Where(row => row.WorkspaceId == workspaceId)
                .Select(row => row.SecurityAuditCount).SingleAsync(ct);
            if (count >= _limits.MaxSecurityAuditEvents)
                throw new ListException(ListFailure.Unavailable, "The workspace security audit reserve is exhausted.");
            return new Membership(
                authorization.Decisions.Any(x => x.First.Action == ListAuthorizationAction.CreateList && x.Second.Allowed),
                false);
        }
        var membership = await (from member in db.Memberships.AsNoTracking()
            join workspace in db.Workspaces.AsNoTracking() on member.WorkspaceId equals workspace.WorkspaceId
            where member.WorkspaceId == workspaceId && member.Subject == subject && workspace.AuthorizationMode == "builtin"
            select new { member.CanCreateLists, member.CanManageGrants, workspace.SecurityAuditCount })
            .SingleOrDefaultAsync(ct);
        if (membership is null) throw Forbidden();
        if (membership.SecurityAuditCount >= _limits.MaxSecurityAuditEvents)
            throw new ListException(ListFailure.Unavailable, "The workspace security audit reserve is exhausted.");
        return new Membership(membership.CanCreateLists, membership.CanManageGrants);
    }

    private async Task RequireAccessAsync(ListDbContext db,
        Guid workspaceId, Guid listId, string subject, ListAccess required, CancellationToken ct,
        OperationAuthorization? authorization)
    {
        if (authorization is not null)
        {
            if (!authorization.Allows(listId, required)) throw NotFound();
            return;
        }
        var access = await db.ListGrants.AsNoTracking()
            .Where(row => row.WorkspaceId == workspaceId && row.ListId == listId && row.Subject == subject)
            .Select(row => (int?)row.Access).SingleOrDefaultAsync(ct);
        if (access is null || ((ListAccess)access.Value & required) != required) throw NotFound();
    }

    private async Task<ListDefinition> GetDefinitionAsync(ListDbContext db,
        DbConnection connection, DbTransaction transaction, Guid workspaceId, Guid listId,
        bool forUpdate, CancellationToken ct)
    {
        if (forUpdate)
            await _locks.ListExclusiveAsync(db, workspaceId, listId, ct);
        var json = await (from list in db.Lists.AsNoTracking()
            join schema in db.ListSchemas.AsNoTracking()
                on new { list.WorkspaceId, list.ListId, list.SchemaVersion }
                equals new { schema.WorkspaceId, schema.ListId, schema.SchemaVersion }
            where list.WorkspaceId == workspaceId && list.ListId == listId
            select schema.Definition).SingleOrDefaultAsync(ct);
        return json is null ? throw NotFound() : ListJson.Deserialize<ListDefinition>(json);
    }

    private async Task<ListTemplate> GetTemplateAsync(ListDbContext db, Guid templateId,
        long version, CancellationToken ct)
    {
        var json = await db.Templates.AsNoTracking()
            .Where(row => row.TemplateId == templateId && row.Version == version)
            .Select(row => row.Definition).SingleOrDefaultAsync(ct);
        return json is null ? throw Validation("The template version does not exist.") : ListJson.Deserialize<ListTemplate>(json);
    }

    private Task<ListTemplate> ReadTemplatePreflightAsync(string subject, Guid workspaceId,
        OperationAuthorization authorization, Guid templateId, long version, CancellationToken ct) =>
        InTransactionAsync(workspaceId, false, authorization, async (db, connection, transaction) =>
        {
            await AuthorizeMemberAsync(db, workspaceId, subject, ct, authorization);
            return await GetTemplateAsync(db, templateId, version, ct);
        }, ct);

    private async Task ValidateReferenceDefinitionsAsync(ListDbContext db, Guid workspaceId,
        string subject, ListDefinition definition, CancellationToken ct,
        IReadOnlySet<Guid>? fieldIds = null, OperationAuthorization? authorization = null)
    {
        var references = definition.Fields.Where(field => field.Type == ListFieldType.Reference &&
            (fieldIds is null || fieldIds.Contains(field.Id))).ToArray();
        if (references.Length == 0) return;
        var targetIds = references.Select(field => field.ReferenceListId!.Value).Distinct().ToArray();
        if (authorization is not null && targetIds.Any(target =>
                !authorization.Allows(target, ListAuthorizationAction.Read)))
            throw NotFound();

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

        foreach (var field in references)
        {
            var targetListId = field.ReferenceListId!.Value;
            if (!definitions.TryGetValue(targetListId, out var definitionJson)) throw NotFound();
            var target = ListJson.Deserialize<ListDefinition>(definitionJson);
            if (field.ReferenceDisplayFieldId is { } displayId && target.Fields.All(x => x.Id != displayId))
                throw Validation("The reference display field does not exist in the target list.");
        }
    }

    private async Task ReserveStorageAsync(ListDbContext db, Guid workspaceId, long bytes,
        CancellationToken ct)
    {
        if (bytes < 0)
        {
            await db.Workspaces.Where(row => row.WorkspaceId == workspaceId)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.StoredBytes,
                    row => row.StoredBytes + bytes > 0 ? row.StoredBytes + bytes : 0), ct);
            return;
        }
        var maxBytes = _limits.MaxWorkspaceBytes;
        var updated = await db.Workspaces.Where(row => row.WorkspaceId == workspaceId &&
                (!maxBytes.HasValue || row.StoredBytes + bytes <= maxBytes.Value))
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.StoredBytes,
                row => row.StoredBytes + bytes), ct);
        if (updated == 0)
            throw Limit("The workspace storage limit has been reached.");
    }

    private async Task AuditAsync(ListDbContext db, DbConnection connection,
        DbTransaction transaction, Guid workspaceId,
        string actor, string action, Guid? listId, Guid? recordId, long? revision, IEnumerable<Guid> fieldIds,
        bool security, CancellationToken ct, string? targetSubject = null)
    {
        var auditFields = fieldIds.ToArray();
        if (security)
        {
            var updated = await db.Workspaces.Where(row => row.WorkspaceId == workspaceId &&
                    row.SecurityAuditCount < _limits.MaxSecurityAuditEvents)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.SecurityAuditCount,
                    row => row.SecurityAuditCount + 1), ct);
            if (updated == 0) throw Limit("The security audit reserve is exhausted.");
        }
        else
        {
            var bytes = 96 + Encoding.UTF8.GetByteCount(actor) + Encoding.UTF8.GetByteCount(action) +
                (targetSubject is null ? 0 : Encoding.UTF8.GetByteCount(targetSubject)) + auditFields.Length * 16;
            var maxBytes = _limits.MaxWorkspaceBytes;
            var maxAudits = _limits.MaxAuditEvents;
            var updated = await db.Workspaces.Where(row => row.WorkspaceId == workspaceId &&
                    (!maxBytes.HasValue || row.StoredBytes + bytes <= maxBytes.Value) &&
                    (!maxAudits.HasValue || row.AuditCount < maxAudits.Value))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(row => row.StoredBytes, row => row.StoredBytes + bytes)
                    .SetProperty(row => row.AuditCount, row => row.AuditCount + 1), ct);
            if (updated == 0) throw Limit("The workspace storage or audit limit has been reached.");
        }
        db.AuditEvents.Add(new AuditEventRow { WorkspaceId = workspaceId,
            Utc = await ClockAsync(db, ct),
            Actor = actor, Action = action, ListId = listId, RecordId = recordId, Revision = revision,
            FieldIds = auditFields, Security = security, TargetSubject = targetSubject });
        await SaveRequiredAsync(db, ct);
    }

    private static ListException Validation(string message) => new(ListFailure.Validation, message);
    private static ListException Forbidden() => new(ListFailure.Forbidden, "Access is denied.");
    private static ListException NotFound() => new(ListFailure.NotFound, "The list resource was not found.");
    private static ListException Conflict(string message) => new(ListFailure.Conflict, message);
    private static ListException Limit(string message) => new(ListFailure.Limit, message);
    private sealed record Membership(bool CanCreateLists, bool CanManageGrants);
}
