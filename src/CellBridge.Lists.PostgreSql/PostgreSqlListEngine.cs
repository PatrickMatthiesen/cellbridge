using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CellBridge.Lists.PostgreSql;

/// <summary>Lists persistence using the shared EF operation implementation.</summary>
public sealed class PostgreSqlListEngine : IListEngine, IPagedListEngine
{
    private readonly EfListEngine _engine;
    public PostgreSqlListEngine(NpgsqlDataSource runtimeSource, ListLimits? limits = null)
        : this(runtimeSource, limits, continuationKey: null)
    {
    }

    /// <summary>Creates a native engine with an optional shared 32-byte continuation key.</summary>
    public PostgreSqlListEngine(NpgsqlDataSource runtimeSource, ListLimits? limits, byte[]? continuationKey)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlListDbContext>()
            .UseNpgsql(runtimeSource ?? throw new ArgumentNullException(nameof(runtimeSource))).Options;
        _engine = new(options, limits, continuationKey, authorizationProvider: null, useWriteReservations: true);
    }

    /// <summary>Creates a list engine with consumer-supplied PostgreSQL EF provider configuration.</summary>
    public PostgreSqlListEngine(DbContextOptions<PostgreSqlListDbContext> options, ListLimits? limits = null,
        byte[]? continuationKey = null)
    { _engine = new(options, limits, continuationKey); }

    public PostgreSqlListEngine(NpgsqlDataSource runtimeSource, ListLimits? limits, IListAuthorizationProvider authorizationProvider,
        byte[]? continuationKey = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlListDbContext>()
            .UseNpgsql(runtimeSource ?? throw new ArgumentNullException(nameof(runtimeSource))).Options;
        _engine = new(options, limits, continuationKey,
            authorizationProvider ?? throw new ArgumentNullException(nameof(authorizationProvider)), useWriteReservations: true);
    }
    public PostgreSqlListEngine(DbContextOptions<PostgreSqlListDbContext> options, ListLimits? limits,
        IListAuthorizationProvider authorizationProvider, byte[]? continuationKey = null)
    { _engine = new(options, limits, continuationKey, authorizationProvider ?? throw new ArgumentNullException(nameof(authorizationProvider))); }

    public Task CheckHealthAsync(CancellationToken cancellationToken = default) => _engine.CheckHealthAsync(cancellationToken);
    public Task<IReadOnlyList<ListDefinition>> GetListsAsync(ListActor actor, Guid workspaceId,
        CancellationToken ct = default) => _engine.GetListsAsync(actor, workspaceId, ct);
    public Task<ListDefinitionPage> GetListsPageAsync(ListActor actor, Guid workspaceId,
        string? continuation = null, int candidateLimit = 256, CancellationToken ct = default) => _engine.GetListsPageAsync(actor, workspaceId, continuation, candidateLimit, ct);
    public Task<ListDefinition> CreateListAsync(ListActor actor, Guid workspaceId,
        CreateListRequest request, CancellationToken ct = default) => _engine.CreateListAsync(actor, workspaceId, request, ct);
    public Task<ListDefinition> GetListAsync(ListActor actor, Guid workspaceId, Guid listId,
        CancellationToken ct = default) => _engine.GetListAsync(actor, workspaceId, listId, ct);
    public Task<ListDefinition> PublishSchemaAsync(ListActor actor, Guid workspaceId, Guid listId,
        PublishSchemaRequest request, CancellationToken ct = default) => _engine.PublishSchemaAsync(actor, workspaceId, listId, request, ct);
    public Task<IReadOnlyList<SavedListView>> GetViewsAsync(ListActor actor, Guid workspaceId,
        Guid listId, CancellationToken ct = default) => _engine.GetViewsAsync(actor, workspaceId, listId, ct);
    public Task<SavedListView> SaveViewAsync(ListActor actor, Guid workspaceId, Guid listId,
        SaveViewRequest request, CancellationToken ct = default) => _engine.SaveViewAsync(actor, workspaceId, listId, request, ct);
    public Task<CreateRecordResult> CreateRecordAsync(ListActor actor, Guid workspaceId, Guid listId,
        CreateRecordRequest request, CancellationToken ct = default) => _engine.CreateRecordAsync(actor, workspaceId, listId, request, ct);
    public Task<ListRecord> GetRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        CancellationToken ct = default) => _engine.GetRecordAsync(actor, workspaceId, listId, recordId, ct);
    public Task<ListRecord> UpdateRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        UpdateRecordRequest request, CancellationToken ct = default) => _engine.UpdateRecordAsync(actor, workspaceId, listId, recordId, request, ct);
    public Task DeleteRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        long expectedRevision, CancellationToken ct = default) => _engine.DeleteRecordAsync(actor, workspaceId, listId, recordId, expectedRevision, ct);
    public Task<MergePreview> PreviewMergeAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        MergeRequest request, CancellationToken ct = default) => _engine.PreviewMergeAsync(actor, workspaceId, listId, recordId, request, ct);
    public Task<ReferenceLabel> ResolveReferenceAsync(ListActor actor, Guid workspaceId, Guid listId,
        Guid recordId, Guid fieldId, CancellationToken ct = default) => _engine.ResolveReferenceAsync(actor, workspaceId, listId, recordId, fieldId, ct);
    public Task<RecordPage> QueryAsync(ListActor actor, Guid workspaceId, Guid listId, ListQuery query,
        CancellationToken ct = default) => _engine.QueryAsync(actor, workspaceId, listId, query, ct);
    public Task SetMembershipAsync(ListActor actor, Guid workspaceId, string subject, bool member,
        bool canCreateLists, bool canManageGrants, CancellationToken ct = default) => _engine.SetMembershipAsync(actor, workspaceId, subject, member, canCreateLists, canManageGrants, ct);
    public Task SetListGrantAsync(ListActor actor, Guid workspaceId, Guid listId, string subject,
        ListAccess access, CancellationToken ct = default) => _engine.SetListGrantAsync(actor, workspaceId, listId, subject, access, ct);
    public Task<IReadOnlyList<AuditEvent>> GetAuditAsync(ListActor actor, Guid workspaceId, int limit = 100,
        CancellationToken ct = default) => _engine.GetAuditAsync(actor, workspaceId, limit, ct);
}
