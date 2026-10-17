using System.Text;
using System.Text.Json.Nodes;
using Npgsql;

namespace CellBridge.Lists.PostgreSql;

/// <summary>
/// PostgreSQL list storage. Record writes coordinate through list, record, counter and quota rows.
/// </summary>
public sealed partial class PostgreSqlListEngine : IListEngine, IPagedListEngine
{
    private readonly NpgsqlDataSource _source;
    private readonly ListLimits _limits;
    private readonly SemaphoreSlim _healthGate = new(1, 1);
    private bool _healthy;

    public PostgreSqlListEngine(NpgsqlDataSource runtimeSource, ListLimits? limits = null)
        : this(runtimeSource, limits, continuationKey: null)
    {
    }

    /// <summary>Creates a native engine with an optional shared 32-byte continuation key.</summary>
    public PostgreSqlListEngine(NpgsqlDataSource runtimeSource, ListLimits? limits, byte[]? continuationKey)
    {
        _source = runtimeSource ?? throw new ArgumentNullException(nameof(runtimeSource));
        _limits = limits ?? new ListLimits();
        _limits.Validate();
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
        if (_authorizationProvider is null)
            return await GetListsBuiltInAsync(actor, workspaceId, ct);

        var lists = new List<ListDefinition>();
        string? continuation = null;
        do
        {
            var page = await GetListsPageAsync(actor, workspaceId, continuation,
                Math.Min(256, _limits.MaxLists), ct);
            lists.AddRange(page.Lists);
            if (lists.Count > _limits.MaxLists)
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
            return await InTransactionAsync(workspaceId, false, null, async (connection, transaction) =>
            {
                await AuthorizeMemberAsync(connection, transaction, workspaceId, actor.Subject, ct, null);
                await using var command = new NpgsqlCommand("""
                    SELECT l.list_id,s.definition::text
                    FROM cellbridge_lists.lists l
                    JOIN cellbridge_lists.list_schemas s ON s.workspace_id=l.workspace_id AND s.list_id=l.list_id AND s.schema_version=l.schema_version
                    JOIN cellbridge_lists.list_grants g ON g.workspace_id=l.workspace_id AND g.list_id=l.list_id
                    WHERE l.workspace_id=$1 AND g.subject=$2 AND g.access<>0 AND ($3::uuid IS NULL OR l.list_id>$3)
                    ORDER BY l.list_id LIMIT $4
                    """, connection, transaction);
                command.Parameters.AddWithValue(workspaceId);
                command.Parameters.AddWithValue(actor.Subject);
                command.Parameters.AddWithValue(after.HasValue ? after.Value : DBNull.Value);
                command.Parameters.AddWithValue(candidateLimit + 1);
                await using var reader = await command.ExecuteReaderAsync(ct);
                var rows = new List<(Guid Id, ListDefinition Definition)>();
                while (await reader.ReadAsync(ct)) rows.Add((reader.GetGuid(0), PostgreSqlListJson.Deserialize<ListDefinition>(reader.GetString(1))));
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
            if (decisions.Count != requests.Length) throw AuthorizationUnavailable();
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
            await using var connection = await _source.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await ConfigureAsync(connection, transaction, workspaceId, ct);
            await RequireAnchorAsync(connection, transaction, workspaceId, authorization, true, ct);
            await AuthorizeMemberAsync(connection, transaction, workspaceId, subject, ct, authorization);
            await using var command = new NpgsqlCommand("""
                SELECT list_id FROM cellbridge_lists.lists
                WHERE workspace_id=$1 AND ($2::uuid IS NULL OR list_id>$2)
                ORDER BY list_id LIMIT $3
                """, connection, transaction);
            command.Parameters.AddWithValue(workspaceId);
            command.Parameters.AddWithValue(after.HasValue ? after.Value : DBNull.Value);
            command.Parameters.AddWithValue(limit);
            var result = new List<Guid>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) result.Add(reader.GetGuid(0));
            await reader.DisposeAsync();
            await transaction.CommitAsync(ct);
            return result;
        });

    private Task<IReadOnlyList<ListDefinition>> ReadAuthorizedDefinitionsAsync(string subject, Guid workspaceId,
        IReadOnlySet<Guid> allowed, OperationAuthorization authorization, CancellationToken ct) =>
        InTransactionAsync(workspaceId, false, authorization, async (connection, transaction) =>
        {
            await AuthorizeMemberAsync(connection, transaction, workspaceId, subject, ct, authorization);
            if (allowed.Count == 0) return (IReadOnlyList<ListDefinition>)[];
            await using var command = new NpgsqlCommand("""
                SELECT s.definition::text FROM cellbridge_lists.lists l
                JOIN cellbridge_lists.list_schemas s ON s.workspace_id=l.workspace_id AND s.list_id=l.list_id AND s.schema_version=l.schema_version
                WHERE l.workspace_id=$1 AND l.list_id=ANY($2) ORDER BY l.list_id
                """, connection, transaction);
            command.Parameters.AddWithValue(workspaceId);
            command.Parameters.AddWithValue(allowed.ToArray());
            await using var reader = await command.ExecuteReaderAsync(ct);
            var result = new List<ListDefinition>();
            while (await reader.ReadAsync(ct)) result.Add(PostgreSqlListJson.Deserialize<ListDefinition>(reader.GetString(0)));
            return result;
        }, ct);

    private async Task<IReadOnlyList<ListDefinition>> GetListsBuiltInAsync(ListActor actor, Guid workspaceId,
        CancellationToken ct)
    {
        return await ReadAsync(actor, workspaceId, async (connection, transaction, member, authorization) =>
        {
        await using var command = new NpgsqlCommand("""
            SELECT s.definition::text
            FROM cellbridge_lists.lists l
            JOIN cellbridge_lists.list_schemas s ON s.workspace_id=l.workspace_id AND s.list_id=l.list_id AND s.schema_version=l.schema_version
            JOIN cellbridge_lists.list_grants g ON g.workspace_id=l.workspace_id AND g.list_id=l.list_id
            WHERE l.workspace_id=$1 AND g.subject=$2 AND g.access<>0
            ORDER BY l.name COLLATE "C", l.list_id
            """, connection, transaction);
        command.Parameters.AddWithValue(workspaceId);
        command.Parameters.AddWithValue(actor.Subject);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ListDefinition>();
        while (await reader.ReadAsync(ct)) result.Add(PostgreSqlListJson.Deserialize<ListDefinition>(reader.GetString(0)));
        return result;
        }, [], ct);
    }

    public async Task<ListDefinition> CreateListAsync(ListActor actor, Guid workspaceId,
        CreateListRequest request, CancellationToken ct = default)
    {
        ValidateActor(actor);
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
        return await InTransactionAsync(workspaceId, false, authorization, async (connection, transaction) =>
        {
            var member = await AuthorizeMemberAsync(connection, transaction, workspaceId, actor.Subject, ct, authorization);
            if (!member.CanCreateLists) throw Forbidden();

            ListDefinition definition;
            if (request.TemplateId is { } templateId)
            {
                if (request.TemplateVersion is not { } templateVersion) throw Validation("A template version is required.");
                var template = await GetTemplateAsync(connection, transaction, templateId, templateVersion, ct);
                definition = ListTemplates.Instantiate(template, workspaceId, request.Id, request.Name,
                    request.Fields, request.NumberPrefix, _limits);
                if (preflightDefinition is not null &&
                    !StringComparer.Ordinal.Equals(PostgreSqlListJson.Serialize(preflightDefinition),
                        PostgreSqlListJson.Serialize(definition)))
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
            await ValidateReferenceDefinitionsAsync(connection, transaction, workspaceId, actor.Subject, definition, ct, authorization: authorization);

            await LockQuotaAsync(connection, transaction, workspaceId, ct);
            var listCount = await ScalarAsync<long>(connection, transaction,
                "SELECT count(*) FROM cellbridge_lists.lists WHERE workspace_id=$1", ct, workspaceId);
            if (listCount >= _limits.MaxLists) throw Limit("The workspace list limit has been reached.");
            var json = PostgreSqlListJson.Serialize(definition);
            await ReserveStorageAsync(connection, transaction, workspaceId,
                await CanonicalJsonBytesAsync(connection, transaction, json, ct) + 256 +
                (authorization is null ? 80 + Encoding.UTF8.GetByteCount(actor.Subject) : 0), ct);

            try
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO cellbridge_lists.lists(workspace_id,list_id,name,schema_version,template_id,template_version,number_prefix) VALUES ($1,$2,$3,1,$4,$5,$6)", ct,
                    workspaceId, request.Id, request.Name, request.TemplateId, request.TemplateVersion, request.NumberPrefix);
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO cellbridge_lists.list_schemas(workspace_id,list_id,schema_version,definition) VALUES ($1,$2,1,$3::jsonb)", ct,
                    workspaceId, request.Id, json);
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO cellbridge_lists.list_counters(workspace_id,list_id) VALUES ($1,$2)", ct,
                    workspaceId, request.Id);
                if (authorization is null)
                    await ExecuteAsync(connection, transaction,
                        "INSERT INTO cellbridge_lists.list_grants(workspace_id,list_id,subject,access) VALUES ($1,$2,$3,$4)", ct,
                        workspaceId, request.Id, actor.Subject, (int)(ListAccess.Read | ListAccess.Write | ListAccess.Design));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            { throw Conflict("That list ID already exists."); }
            await AuditAsync(connection, transaction, workspaceId, actor.Subject, "list.create", request.Id, null, 1,
                definition.Fields.Select(x => x.Id), false, ct);
            return definition;
        }, ct);
    }

    public async Task<ListDefinition> GetListAsync(ListActor actor, Guid workspaceId, Guid listId,
        CancellationToken ct = default)
    {
        ValidateActor(actor);
        var authorization = await AuthorizeOperationAsync(actor, workspaceId,
            [(listId, ListAuthorizationAction.Read)], ct);
        return await InRecordTransactionAsync(workspaceId, authorization, async (connection, transaction) =>
        {
            var context = await ReadWriteContextAsync(connection, transaction, workspaceId, listId,
                Guid.Empty, actor.Subject, ListAccess.Read, deleteLock: false, ct, authorization);
            RequireWriteContext(context, ListAccess.Read);
            return context.Definition!;
        }, ct);
    }

    public async Task<ListDefinition> PublishSchemaAsync(ListActor actor, Guid workspaceId, Guid listId,
        PublishSchemaRequest request, CancellationToken ct = default)
    {
        ValidateActor(actor);
        var sourceRequirements = new (Guid?, ListAuthorizationAction)[] { (listId, ListAuthorizationAction.Design) };
        var authorization = await AuthorizeOperationAsync(actor, workspaceId, sourceRequirements, ct);
        if (_authorizationProvider is not null)
        {
            var current = await ReadDefinitionPreflightAsync(actor.Subject, workspaceId, listId, authorization!, ct);
            var existing = current.Fields.Select(x => x.Id).ToHashSet();
            var requirements = request.Fields
                .Where(x => x.Type == ListFieldType.Reference && !existing.Contains(x.Id))
                .Select(x => ((Guid?)x.ReferenceListId, ListAuthorizationAction.Read))
                .Distinct()
                .ToArray();
            authorization = await AuthorizeAdditionalAsync(actor, authorization!, requirements, ct);
        }
        return await InTransactionAsync(workspaceId, false, authorization, async (connection, transaction) =>
        {
            await AuthorizeMemberAsync(connection, transaction, workspaceId, actor.Subject, ct, authorization);
            await RequireAccessAsync(connection, transaction, workspaceId, listId, actor.Subject, ListAccess.Design, ct, authorization);
            await LockWorkspaceMutationAsync(connection, transaction, workspaceId, ct);
            var previous = await GetDefinitionAsync(connection, transaction, workspaceId, listId, true, ct);
            if (previous.SchemaVersion != request.ExpectedVersion) throw Conflict("The list schema changed.");
            var next = previous with { Name = request.Name, SchemaVersion = checked(previous.SchemaVersion + 1), Fields = request.Fields };
            ListValidation.ValidateEvolution(previous, next, _limits);
            var previousFieldIds = previous.Fields.Select(x => x.Id).ToHashSet();
            var addedReferenceFieldIds = next.Fields
                .Where(x => x.Type == ListFieldType.Reference && !previousFieldIds.Contains(x.Id))
                .Select(x => x.Id)
                .ToHashSet();
            await ValidateReferenceDefinitionsAsync(connection, transaction, workspaceId, actor.Subject, next, ct,
                addedReferenceFieldIds, authorization);
            await LockQuotaAsync(connection, transaction, workspaceId, ct);
            var versions = await ScalarAsync<long>(connection, transaction,
                "SELECT count(*) FROM cellbridge_lists.list_schemas WHERE workspace_id=$1 AND list_id=$2", ct, workspaceId, listId);
            if (versions >= _limits.MaxSchemaVersions) throw Limit("The list schema version limit has been reached.");
            var json = PostgreSqlListJson.Serialize(next);
            await ReserveStorageAsync(connection, transaction, workspaceId,
                await CanonicalJsonBytesAsync(connection, transaction, json, ct) + 96, ct);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO cellbridge_lists.list_schemas(workspace_id,list_id,schema_version,definition) VALUES ($1,$2,$3,$4::jsonb)", ct,
                workspaceId, listId, next.SchemaVersion, json);
            await ExecuteAsync(connection, transaction,
                "UPDATE cellbridge_lists.lists SET name=$3,schema_version=$4 WHERE workspace_id=$1 AND list_id=$2", ct,
                workspaceId, listId, next.Name, next.SchemaVersion);
            await AuditAsync(connection, transaction, workspaceId, actor.Subject, "schema.publish", listId, null,
                next.SchemaVersion, next.Fields.Select(x => x.Id), false, ct);
            return next;
        }, ct);
    }

    public async Task<IReadOnlyList<SavedListView>> GetViewsAsync(ListActor actor, Guid workspaceId,
        Guid listId, CancellationToken ct = default) => await ReadAsync(actor, workspaceId, async (connection, transaction, member, authorization) =>
    {
        await RequireAccessAsync(connection, transaction, workspaceId, listId, actor.Subject, ListAccess.Read, ct, authorization);
        await using var command = Command(connection, transaction,
            "SELECT definition::text FROM cellbridge_lists.saved_views WHERE workspace_id=$1 AND list_id=$2 ORDER BY view_id", workspaceId, listId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var views = new List<SavedListView>();
        while (await reader.ReadAsync(ct)) views.Add(PostgreSqlListJson.Deserialize<SavedListView>(reader.GetString(0)));
        return views;
    }, [(listId, ListAuthorizationAction.Read)], ct);

    public async Task<SavedListView> SaveViewAsync(ListActor actor, Guid workspaceId, Guid listId,
        SaveViewRequest request, CancellationToken ct = default)
    {
        ValidateActor(actor);
        var authorization = await AuthorizeOperationAsync(actor, workspaceId,
            [(listId, ListAuthorizationAction.Design)], ct);
        return await InTransactionAsync(workspaceId, false, authorization, async (connection, transaction) =>
        {
            await AuthorizeMemberAsync(connection, transaction, workspaceId, actor.Subject, ct, authorization);
            await RequireAccessAsync(connection, transaction, workspaceId, listId, actor.Subject, ListAccess.Design, ct, authorization);
            var definition = await GetDefinitionAsync(connection, transaction, workspaceId, listId, true, ct);
            ListValidation.ValidateQuery(definition, request.Query, _limits);
            if (request.Id == Guid.Empty || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200 ||
                !ListValidation.IsWellFormedText(request.Name))
                throw Validation("A view ID and valid name are required.");
            if (request.VisibleFields.Distinct().Count() != request.VisibleFields.Count ||
                request.VisibleFields.Any(id => definition.Fields.All(x => x.Id != id)))
                throw Validation("A view contains an unknown or duplicate field.");
            await LockQuotaAsync(connection, transaction, workspaceId, ct);
            var count = await ScalarAsync<long>(connection, transaction,
                "SELECT count(*) FROM cellbridge_lists.saved_views WHERE workspace_id=$1 AND list_id=$2 AND view_id<>$3", ct,
                workspaceId, listId, request.Id);
            var previousBytes = await ScalarAsync<long?>(connection, transaction,
                "SELECT octet_length(definition::text)::bigint FROM cellbridge_lists.saved_views WHERE workspace_id=$1 AND list_id=$2 AND view_id=$3", ct,
                workspaceId, listId, request.Id);
            if (previousBytes is null && count >= _limits.MaxViewsPerList) throw Limit("The saved view limit has been reached.");
            var view = new SavedListView(request.Id, listId, request.Name, PostgreSqlListJson.NormalizeQuery(request.Query), request.VisibleFields);
            var json = PostgreSqlListJson.Serialize(view);
            var nextBytes = await CanonicalJsonBytesAsync(connection, transaction, json, ct);
            await ReserveStorageAsync(connection, transaction, workspaceId,
                nextBytes - (previousBytes ?? -64), ct);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO cellbridge_lists.saved_views(workspace_id,list_id,view_id,definition) VALUES ($1,$2,$3,$4::jsonb) ON CONFLICT (workspace_id,list_id,view_id) DO UPDATE SET definition=EXCLUDED.definition", ct,
                workspaceId, listId, request.Id, json);
            await AuditAsync(connection, transaction, workspaceId, actor.Subject, "view.save", listId, null, null,
                request.VisibleFields, false, ct);
            return view;
        }, ct);
    }

    private async Task<T> ReadAsync<T>(ListActor actor, Guid workspaceId,
        Func<NpgsqlConnection, NpgsqlTransaction, Membership, OperationAuthorization?, Task<T>> operation,
        IReadOnlyList<(Guid? ListId, ListAuthorizationAction Action)> requirements, CancellationToken ct)
    {
        ValidateActor(actor);
        var authorization = await AuthorizeOperationAsync(actor, workspaceId, requirements, ct);
        return await InTransactionAsync(workspaceId, false, authorization, async (connection, transaction) =>
        {
            var member = await AuthorizeMemberAsync(connection, transaction, workspaceId, actor.Subject, ct, authorization);
            return await operation(connection, transaction, member, authorization);
        }, ct);
    }

    private Task<T> InTransactionAsync<T>(Guid workspaceId, bool exclusiveAuthorization,
        OperationAuthorization? authorization, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> operation,
        CancellationToken ct) =>
        MapStoreFailuresAsync(async () =>
        {
            if (workspaceId == Guid.Empty) throw Validation("A workspace ID is required.");
            await EnsureHealthyAsync(ct);
            await using var connection = await _source.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            await ConfigureAsync(connection, transaction, workspaceId, ct);
            await AcquireAuthorizationLockAsync(connection, transaction, workspaceId,
                exclusiveAuthorization, authorization, ct);
            var result = await operation(connection, transaction);
            await transaction.CommitAsync(CancellationToken.None);
            return result;
        });

    // Keep native transactions and host preflights on the same typed store-failure contract.
    // Cancellation, domain failures and programming errors propagate unchanged.
    private static async Task<T> MapStoreFailuresAsync<T>(Func<Task<T>> operation)
    {
        try { return await operation(); }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.QueryCanceled or PostgresErrorCodes.LockNotAvailable)
        { throw new ListException(ListFailure.Unavailable, "The list operation timed out."); }
        catch (NpgsqlException ex)
        { throw new ListException(ListFailure.Unavailable, $"The list store is unavailable: {ex.Message}"); }
    }

    private static async Task AcquireAuthorizationLockAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid workspaceId, bool exclusive,
        OperationAuthorization? authorization, CancellationToken ct)
    {
        var anchorPredicate = authorization is null
            ? "AND w.authorization_mode='builtin'"
            : "AND w.authorization_mode='host' AND w.authorization_domain=$2 AND w.authorization_scope=$3";
        await using var command = new NpgsqlCommand($$"""
            SELECT {{(exclusive ? "pg_advisory_xact_lock" : "pg_advisory_xact_lock_shared")}}
              (hashtextextended($1::text,391813))
            FROM cellbridge_lists.workspaces w
            WHERE w.workspace_id=$1::uuid {{anchorPredicate}}
            """, connection, transaction);
        command.Parameters.AddWithValue(workspaceId);
        if (authorization is not null)
        {
            command.Parameters.AddWithValue(authorization.Anchor.Domain!);
            command.Parameters.AddWithValue(authorization.Anchor.Scope!);
        }
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw NotFound();
    }

    private async Task EnsureHealthyAsync(CancellationToken ct)
    {
        if (_healthy) return;
        await _healthGate.WaitAsync(ct);
        try
        {
            if (_healthy) return;
            await using var connection = await _source.OpenConnectionAsync(ct);
            await using var command = new NpgsqlCommand("""
                WITH expected(table_name, allowed) AS (VALUES
                  ('schema_version', ARRAY['SELECT']),
                  ('templates', ARRAY['SELECT']),
                  ('workspaces', ARRAY['SELECT','UPDATE']),
                  ('memberships', ARRAY['SELECT','INSERT','UPDATE','DELETE']),
                  ('lists', ARRAY['SELECT','INSERT','UPDATE']),
                  ('list_schemas', ARRAY['SELECT','INSERT']),
                  ('list_counters', ARRAY['SELECT','INSERT','UPDATE']),
                  ('list_grants', ARRAY['SELECT','INSERT','UPDATE','DELETE']),
                  ('records', ARRAY['SELECT','INSERT','UPDATE','DELETE']),
                  ('record_tombstones', ARRAY['INSERT']),
                  ('create_receipts', ARRAY['SELECT','INSERT','DELETE']),
                  ('record_references', ARRAY['SELECT','INSERT','UPDATE','DELETE']),
                  ('saved_views', ARRAY['SELECT','INSERT','UPDATE','DELETE']),
                  ('audit_events', ARRAY['SELECT','INSERT'])
                )
                SELECT v.version,
                  EXISTS (SELECT 1 FROM pg_roles inherited WHERE pg_has_role(current_user,inherited.oid,'MEMBER') AND (inherited.rolsuper OR inherited.rolbypassrls)),
                  EXISTS (SELECT 1 FROM pg_roles inherited WHERE pg_has_role(current_user,inherited.oid,'MEMBER') AND
                    has_schema_privilege(inherited.oid,'cellbridge_lists','CREATE')),
                  EXISTS (SELECT 1 FROM pg_class owned JOIN pg_namespace ns ON ns.oid=owned.relnamespace
                    WHERE ns.nspname='cellbridge_lists' AND pg_has_role(current_user,owned.relowner,'MEMBER')),
                  pg_has_role(current_user,n.nspowner,'MEMBER'),
                  (SELECT bool_and(COALESCE(has_table_privilege(current_user,
                    to_regclass('cellbridge_lists.' || e.table_name), privilege), false))
                    FROM expected e CROSS JOIN LATERAL unnest(e.allowed) privilege) AND
                  has_sequence_privilege(current_user,'cellbridge_lists.audit_events_id_seq','USAGE') AND
                  has_sequence_privilege(current_user,'cellbridge_lists.audit_events_id_seq','SELECT'),
                  EXISTS (
                    SELECT 1 FROM expected e
                    JOIN pg_class tbl ON tbl.oid=to_regclass('cellbridge_lists.' || e.table_name)
                    CROSS JOIN LATERAL aclexplode(acldefault('r', tbl.relowner)) acl
                    WHERE NOT (acl.privilege_type=ANY(e.allowed)) AND EXISTS (
                      SELECT 1 FROM pg_roles inherited
                      WHERE pg_has_role(current_user,inherited.oid,'MEMBER') AND
                        (has_table_privilege(inherited.oid,tbl.oid,acl.privilege_type) OR
                          CASE WHEN acl.privilege_type IN ('SELECT','INSERT','UPDATE','REFERENCES')
                            THEN has_any_column_privilege(inherited.oid,tbl.oid,acl.privilege_type)
                            ELSE false END)
                    )
                  ) OR EXISTS (
                    SELECT 1 FROM pg_roles inherited
                    WHERE pg_has_role(current_user,inherited.oid,'MEMBER') AND
                      has_sequence_privilege(inherited.oid,'cellbridge_lists.audit_events_id_seq','UPDATE')
                  ),
                  EXISTS (SELECT 1 FROM pg_trigger t
                    WHERE t.tgrelid='cellbridge_lists.workspaces'::regclass
                      AND t.tgname='immutable_workspace_authorization_anchor' AND t.tgenabled='O'
                      AND t.tgfoid='cellbridge_lists.reject_authorization_anchor_rebinding()'::regprocedure)
                FROM cellbridge_lists.schema_version v
                CROSS JOIN pg_roles r
                CROSS JOIN pg_class c
                JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE v.singleton AND r.rolname=current_user AND n.nspname='cellbridge_lists' AND c.relname='records'
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.GetInt32(0) != 3)
                throw new ListException(ListFailure.Unavailable, "The list schema version is missing or unsupported.");
            if (reader.GetBoolean(1) || reader.GetBoolean(2) || reader.GetBoolean(3) || reader.GetBoolean(4) ||
                !reader.GetBoolean(5) || reader.GetBoolean(6) || !reader.GetBoolean(7))
                throw new ListException(ListFailure.Unavailable, "The list runtime role has unsafe database privileges.");
            _healthy = true;
        }
        finally { _healthGate.Release(); }
    }

    private async Task ConfigureAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, CancellationToken ct)
    {
        await PostgreSqlListSetup.SetContextAsync(connection, transaction, workspaceId, ct);
        await ExecuteAsync(connection, transaction, "SET LOCAL synchronous_commit=on", ct);
        var milliseconds = Math.Clamp((long)_limits.StatementTimeout.TotalMilliseconds, 1, int.MaxValue);
        await ExecuteAsync(connection, transaction, $"SET LOCAL statement_timeout={milliseconds}; SET LOCAL lock_timeout={milliseconds}", ct);
    }

    private async Task<Membership> AuthorizeMemberAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid workspaceId, string subject, CancellationToken ct,
        OperationAuthorization? authorization)
    {
        if (authorization is not null)
        {
            if (!authorization.Allows(null, ListAuthorizationAction.UseLists)) throw Forbidden();
            var count = await ScalarAsync<int>(connection, transaction,
                "SELECT security_audit_count FROM cellbridge_lists.workspaces WHERE workspace_id=$1", ct, workspaceId);
            if (count >= _limits.MaxSecurityAuditEvents)
                throw new ListException(ListFailure.Unavailable, "The workspace security audit reserve is exhausted.");
            return new Membership(
                authorization.Decisions.Any(x => x.First.Action == ListAuthorizationAction.CreateList && x.Second.Allowed),
                false);
        }
        await using var command = Command(connection, transaction,
            "SELECT m.can_create_lists,m.can_manage_grants,w.security_audit_count FROM cellbridge_lists.memberships m JOIN cellbridge_lists.workspaces w USING(workspace_id) WHERE m.workspace_id=$1 AND m.subject=$2 AND w.authorization_mode='builtin'",
            workspaceId, subject);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw Forbidden();
        if (reader.GetInt32(2) >= _limits.MaxSecurityAuditEvents)
            throw new ListException(ListFailure.Unavailable, "The workspace security audit reserve is exhausted.");
        return new Membership(reader.GetBoolean(0), reader.GetBoolean(1));
    }

    private static async Task RequireAccessAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, Guid listId, string subject, ListAccess required, CancellationToken ct,
        OperationAuthorization? authorization)
    {
        if (authorization is not null)
        {
            if (!authorization.Allows(listId, required)) throw NotFound();
            return;
        }
        var access = await ScalarAsync<int?>(connection, transaction,
            "SELECT access FROM cellbridge_lists.list_grants WHERE workspace_id=$1 AND list_id=$2 AND subject=$3", ct,
            workspaceId, listId, subject);
        if (access is null || ((ListAccess)access.Value & required) != required) throw NotFound();
    }

    private static async Task<ListDefinition> GetDefinitionAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid workspaceId, Guid listId, bool forUpdate, CancellationToken ct)
    {
        var sql = "SELECT s.definition::text FROM cellbridge_lists.lists l JOIN cellbridge_lists.list_schemas s ON s.workspace_id=l.workspace_id AND s.list_id=l.list_id AND s.schema_version=l.schema_version WHERE l.workspace_id=$1 AND l.list_id=$2" +
            (forUpdate ? " FOR UPDATE OF l" : "");
        var json = await ScalarAsync<string?>(connection, transaction, sql, ct, workspaceId, listId);
        return json is null ? throw NotFound() : PostgreSqlListJson.Deserialize<ListDefinition>(json);
    }

    private static async Task<ListTemplate> GetTemplateAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid templateId, long version, CancellationToken ct)
    {
        var json = await ScalarAsync<string?>(connection, transaction,
            "SELECT definition::text FROM cellbridge_lists.templates WHERE template_id=$1 AND version=$2", ct, templateId, version);
        return json is null ? throw Validation("The template version does not exist.") : PostgreSqlListJson.Deserialize<ListTemplate>(json);
    }

    private Task<ListTemplate> ReadTemplatePreflightAsync(string subject, Guid workspaceId,
        OperationAuthorization authorization, Guid templateId, long version, CancellationToken ct) =>
        InTransactionAsync(workspaceId, false, authorization, async (connection, transaction) =>
        {
            await AuthorizeMemberAsync(connection, transaction, workspaceId, subject, ct, authorization);
            return await GetTemplateAsync(connection, transaction, templateId, version, ct);
        }, ct);

    private static async Task ValidateReferenceDefinitionsAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid workspaceId, string subject, ListDefinition definition, CancellationToken ct,
        IReadOnlySet<Guid>? fieldIds = null, OperationAuthorization? authorization = null)
    {
        foreach (var field in definition.Fields.Where(x => x.Type == ListFieldType.Reference &&
                     (fieldIds is null || fieldIds.Contains(x.Id))))
        {
            var targetListId = field.ReferenceListId!.Value;
            await RequireAccessAsync(connection, transaction, workspaceId, targetListId, subject, ListAccess.Read, ct, authorization);
            var target = await GetDefinitionAsync(connection, transaction, workspaceId, targetListId, false, ct);
            if (field.ReferenceDisplayFieldId is { } displayId && target.Fields.All(x => x.Id != displayId))
                throw Validation("The reference display field does not exist in the target list.");
        }
    }

    private static async Task LockQuotaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, CancellationToken ct)
    {
        if (await ScalarAsync<Guid?>(connection, transaction,
            "SELECT workspace_id FROM cellbridge_lists.workspaces WHERE workspace_id=$1 FOR UPDATE", ct, workspaceId) is null)
            throw NotFound();
    }

    private async Task ReserveStorageAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, long bytes, CancellationToken ct)
    {
        if (bytes < 0)
        {
            await ExecuteAsync(connection, transaction,
                "UPDATE cellbridge_lists.workspaces SET stored_bytes=GREATEST(0,stored_bytes+$2) WHERE workspace_id=$1", ct,
                workspaceId, bytes);
            return;
        }
        await using var command = Command(connection, transaction,
            "UPDATE cellbridge_lists.workspaces SET stored_bytes=stored_bytes+$2 WHERE workspace_id=$1 AND ($3::bigint IS NULL OR stored_bytes+$2<=$3) RETURNING workspace_id",
            workspaceId, bytes, _limits.MaxWorkspaceBytes);
        if (await command.ExecuteScalarAsync(ct) is null)
            throw Limit("The workspace storage limit has been reached.");
    }

    private async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid workspaceId,
        string actor, string action, Guid? listId, Guid? recordId, long? revision, IEnumerable<Guid> fieldIds,
        bool security, CancellationToken ct, string? targetSubject = null)
    {
        if (security)
        {
            await using var reserve = Command(connection, transaction,
                "UPDATE cellbridge_lists.workspaces SET security_audit_count=security_audit_count+1 WHERE workspace_id=$1 AND security_audit_count<$2 RETURNING workspace_id",
                workspaceId, _limits.MaxSecurityAuditEvents);
            if (await reserve.ExecuteScalarAsync(ct) is null) throw Limit("The security audit reserve is exhausted.");
        }
        else
        {
            var bytes = 96 + Encoding.UTF8.GetByteCount(actor) + Encoding.UTF8.GetByteCount(action) +
                (targetSubject is null ? 0 : Encoding.UTF8.GetByteCount(targetSubject)) + fieldIds.Count() * 16;
            await using var reserve = Command(connection, transaction,
                "UPDATE cellbridge_lists.workspaces SET stored_bytes=stored_bytes+$2,audit_count=audit_count+1 WHERE workspace_id=$1 AND ($3::bigint IS NULL OR stored_bytes+$2<=$3) AND ($4::bigint IS NULL OR audit_count<$4) RETURNING workspace_id",
                workspaceId, bytes, _limits.MaxWorkspaceBytes, _limits.MaxAuditEvents);
            if (await reserve.ExecuteScalarAsync(ct) is null) throw Limit("The workspace storage or audit limit has been reached.");
        }
        await ExecuteAsync(connection, transaction,
            "INSERT INTO cellbridge_lists.audit_events(workspace_id,utc,actor,action,list_id,record_id,revision,field_ids,security,target_subject) VALUES ($1,clock_timestamp(),$2,$3,$4,$5,$6,$7,$8,$9)", ct,
            workspaceId, actor, action, listId, recordId, revision, fieldIds.ToArray(), security, targetSubject);
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, params object?[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        return command;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken ct, params object?[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken ct, params object?[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        var result = await command.ExecuteScalarAsync(ct);
        if (result is null or DBNull) return default!;
        return (T)result;
    }

    private static Task<long> CanonicalJsonBytesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string json, CancellationToken ct) => ScalarAsync<long>(connection, transaction,
            "SELECT octet_length(($1::jsonb)::text)::bigint", ct, json);

    private static void ValidateActor(ListActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.Subject) || actor.Subject.Length > 1024 || !ListValidation.IsWellFormedText(actor.Subject) ||
            actor.Subject.Any(char.IsControl) || actor.TenantId is { } tenant && (string.IsNullOrWhiteSpace(tenant) || tenant.Length > 1024 || !ListValidation.IsWellFormedText(tenant) || tenant.Any(char.IsControl)))
            throw new ListException(ListFailure.Forbidden, "The actor is missing or invalid.");
    }

    private static ListException Validation(string message) => new(ListFailure.Validation, message);
    private static ListException Forbidden() => new(ListFailure.Forbidden, "Access is denied.");
    private static ListException NotFound() => new(ListFailure.NotFound, "The list resource was not found.");
    private static ListException Conflict(string message) => new(ListFailure.Conflict, message);
    private static ListException Limit(string message) => new(ListFailure.Limit, message);
    private sealed record Membership(bool CanCreateLists, bool CanManageGrants);
}
