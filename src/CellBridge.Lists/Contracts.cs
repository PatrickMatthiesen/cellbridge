using System.Text.Json.Nodes;

namespace CellBridge.Lists;

public sealed record ListActor(string Subject)
{
    public ListActor(string subject, string? tenantId) : this(subject) => TenantId = tenantId;
    public string? TenantId { get; init; }
}

[Flags]
public enum ListAccess { None = 0, Read = 1, Write = 2, Design = 4 }
public enum ListFieldType { Text, Boolean, Integer, Decimal, Date, Timestamp, Choice, Reference }
public enum FilterOperator { Equal, NotEqual, GreaterThan, GreaterOrEqual, LessThan, LessOrEqual }
public enum ListFailure { Validation, NotFound, Forbidden, Conflict, Limit, Unavailable, NotSupported }

public sealed class ListException(ListFailure failure, string message) : Exception(message)
{
    public ListFailure Failure { get; } = failure;
}

public sealed record FieldChoice(string Id, string Label);
public sealed record FieldDisplay(bool Hidden = false, bool ReadOnly = false, string? HelpText = null,
    int Order = 0, IReadOnlyList<string>? Tags = null);
public sealed record ListField(Guid Id, string Name, ListFieldType Type, bool Required = false,
    int MaxLength = 1024, IReadOnlyList<FieldChoice>? Choices = null, FieldDisplay? Display = null,
    bool Protected = false, Guid? ReferenceListId = null, Guid? ReferenceDisplayFieldId = null);
public sealed record ListTemplate(Guid Id, string Name, long Version, IReadOnlyList<ListField> Fields);
public sealed record ListDefinition(Guid Id, Guid WorkspaceId, string Name, long SchemaVersion,
    IReadOnlyList<ListField> Fields, Guid? TemplateId = null, long? TemplateVersion = null,
    string? NumberPrefix = null);
public sealed record CreateListRequest(Guid Id, string Name, IReadOnlyList<ListField> Fields,
    Guid? TemplateId = null, long? TemplateVersion = null, string? NumberPrefix = null);
public sealed record PublishSchemaRequest(long ExpectedVersion, string Name, IReadOnlyList<ListField> Fields);
public sealed record ListRecord(Guid Id, Guid WorkspaceId, Guid ListId, long SchemaVersion, long Revision,
    long Number, JsonObject Values, DateTimeOffset CreatedUtc, DateTimeOffset ModifiedUtc,
    string CreatedBy, string ModifiedBy);
public sealed record CreateRecordRequest(Guid Id, long SchemaVersion, JsonObject Values);
public sealed record CreateRecordResult(Guid Id, long Revision, long Number, bool Replayed = false);
public sealed record FieldChange(Guid FieldId, JsonNode? Value = null, bool Remove = false);
public sealed record UpdateRecordRequest(long ExpectedRevision, long SchemaVersion, IReadOnlyList<FieldChange> Changes);
public sealed record RecordFilter(Guid FieldId, FilterOperator Operator, JsonNode? Value = null);
public sealed record ListQuery(IReadOnlyList<RecordFilter>? Filters = null, Guid? SortFieldId = null,
    bool Descending = false, Guid? AfterRecordId = null, int Limit = 100);
public sealed record RecordPage(IReadOnlyList<ListRecord> Records, Guid? NextRecordId, long SchemaVersion);
public sealed record SavedListView(Guid Id, Guid ListId, string Name, ListQuery Query,
    IReadOnlyList<Guid> VisibleFields);
public sealed record SaveViewRequest(Guid Id, string Name, ListQuery Query, IReadOnlyList<Guid> VisibleFields);
public sealed record MergeRequest(long SchemaVersion, JsonObject OriginalValues, IReadOnlyList<FieldChange> Changes);
public sealed record FieldMerge(Guid FieldId, bool OriginalPresent, JsonNode? OriginalValue,
    bool CurrentPresent, JsonNode? CurrentValue, bool ProposedPresent, JsonNode? ProposedValue, bool Conflict);
public sealed record MergePreview(long CurrentRevision, long SchemaVersion, IReadOnlyList<FieldChange> SuggestedChanges,
    IReadOnlyList<FieldMerge> Fields);
public sealed record ReferenceLabel(Guid RecordId, long Number, JsonNode? Label);
public sealed record AuditEvent(long Id, DateTimeOffset Utc, string Actor, string Action, Guid? ListId,
    Guid? RecordId, long? Revision, IReadOnlyList<Guid> FieldIds, string? TargetSubject = null);
public sealed record ListDefinitionPage(IReadOnlyList<ListDefinition> Lists, string? Continuation);

public sealed class ListLimits
{
    public int MaxFields { get; init; } = 64;
    public int MaxLists { get; init; } = 100;
    public int MaxRecordBytes { get; init; } = 64 * 1024;
    /// <summary>Optional host quota for lifetime record identities. Null disables this quota.</summary>
    public long? MaxRecordsPerList { get; init; }
    /// <summary>Optional logical workspace storage quota. Null disables this quota.</summary>
    public long? MaxWorkspaceBytes { get; init; }
    public int MaxPageSize { get; init; } = 500;
    public int MaxFilters { get; init; } = 12;
    public int MaxViewsPerList { get; init; } = 32;
    public int MaxSchemaVersions { get; init; } = 128;
    /// <summary>Optional ordinary audit-event quota. Null disables this quota.</summary>
    public long? MaxAuditEvents { get; init; }
    public int MaxSecurityAuditEvents { get; init; } = 10000;
    public TimeSpan ReceiptLifetime { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan StatementTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public void Validate()
    {
        if (MaxFields <= 0 || MaxLists <= 0 || MaxRecordBytes <= 0 || MaxRecordsPerList is <= 0 ||
            MaxWorkspaceBytes is <= 0 || MaxPageSize <= 0 || MaxFilters <= 0 || MaxViewsPerList <= 0 ||
            MaxSchemaVersions <= 0 || MaxAuditEvents is <= 0 || MaxSecurityAuditEvents <= 0 ||
            ReceiptLifetime <= TimeSpan.Zero || StatementTimeout <= TimeSpan.Zero ||
            StatementTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentException("Configured list limits and retention must be positive and statement timeout must fit a millisecond integer.");
    }
}

/// <summary>
/// Host authentication supplies the stable actor. Implementations must authorize each
/// operation and preserve the documented revocation/transaction guarantees.
/// Display metadata and client merge snapshots never provide authority.
/// </summary>
public interface IListEngine
{
    Task<IReadOnlyList<ListDefinition>> GetListsAsync(ListActor actor, Guid workspaceId, CancellationToken ct = default);
    Task<ListDefinition> CreateListAsync(ListActor actor, Guid workspaceId, CreateListRequest request, CancellationToken ct = default);
    Task<ListDefinition> GetListAsync(ListActor actor, Guid workspaceId, Guid listId, CancellationToken ct = default);
    Task<ListDefinition> PublishSchemaAsync(ListActor actor, Guid workspaceId, Guid listId, PublishSchemaRequest request, CancellationToken ct = default);
    Task<CreateRecordResult> CreateRecordAsync(ListActor actor, Guid workspaceId, Guid listId, CreateRecordRequest request, CancellationToken ct = default);
    Task<ListRecord> GetRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId, CancellationToken ct = default);
    Task<ListRecord> UpdateRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId, UpdateRecordRequest request, CancellationToken ct = default);
    Task DeleteRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId, long expectedRevision, CancellationToken ct = default);
    Task<RecordPage> QueryAsync(ListActor actor, Guid workspaceId, Guid listId, ListQuery query, CancellationToken ct = default);
    Task<MergePreview> PreviewMergeAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId, MergeRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<SavedListView>> GetViewsAsync(ListActor actor, Guid workspaceId, Guid listId, CancellationToken ct = default);
    Task<SavedListView> SaveViewAsync(ListActor actor, Guid workspaceId, Guid listId, SaveViewRequest request, CancellationToken ct = default);
    Task<ReferenceLabel> ResolveReferenceAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId, Guid fieldId, CancellationToken ct = default);
    Task SetMembershipAsync(ListActor actor, Guid workspaceId, string subject, bool member, bool canCreateLists, bool canManageGrants, CancellationToken ct = default);
    Task SetListGrantAsync(ListActor actor, Guid workspaceId, Guid listId, string subject, ListAccess access, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEvent>> GetAuditAsync(ListActor actor, Guid workspaceId, int limit = 100, CancellationToken ct = default);
}

/// <summary>
/// Exposes bounded candidate paging for hosts that cannot authorize an entire workspace list set in one decision.
/// Continuations must be opaque and authenticated; adapters pass them through without interpreting candidate IDs.
/// </summary>
public interface IPagedListEngine
{
    Task<ListDefinitionPage> GetListsPageAsync(ListActor actor, Guid workspaceId, string? continuation = null,
        int candidateLimit = 256, CancellationToken ct = default);
}
