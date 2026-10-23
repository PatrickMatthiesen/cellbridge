namespace CellBridge.Lists.SqlServer;

public sealed partial class SqlServerListEngine
{
    // Keep unavailable features explicit. These methods never open a connection,
    // acquire authority, prune receipts or publish any state.
    public Task<ListDefinition> PublishSchemaAsync(ListActor actor, Guid workspaceId, Guid listId,
        PublishSchemaRequest request, CancellationToken ct = default) => Task.FromException<ListDefinition>(Unsupported());
    public Task<ListRecord> UpdateRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        UpdateRecordRequest request, CancellationToken ct = default) => Task.FromException<ListRecord>(Unsupported());
    public Task DeleteRecordAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        long expectedRevision, CancellationToken ct = default) => Task.FromException(Unsupported());
    public Task<RecordPage> QueryAsync(ListActor actor, Guid workspaceId, Guid listId,
        ListQuery query, CancellationToken ct = default) => Task.FromException<RecordPage>(Unsupported());
    public Task<MergePreview> PreviewMergeAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        MergeRequest request, CancellationToken ct = default) => Task.FromException<MergePreview>(Unsupported());
    public Task<IReadOnlyList<SavedListView>> GetViewsAsync(ListActor actor, Guid workspaceId, Guid listId,
        CancellationToken ct = default) => Task.FromException<IReadOnlyList<SavedListView>>(Unsupported());
    public Task<SavedListView> SaveViewAsync(ListActor actor, Guid workspaceId, Guid listId,
        SaveViewRequest request, CancellationToken ct = default) => Task.FromException<SavedListView>(Unsupported());
    public Task<ReferenceLabel> ResolveReferenceAsync(ListActor actor, Guid workspaceId, Guid listId, Guid recordId,
        Guid fieldId, CancellationToken ct = default) => Task.FromException<ReferenceLabel>(Unsupported());
    public Task SetMembershipAsync(ListActor actor, Guid workspaceId, string subject, bool member,
        bool canCreateLists, bool canManageGrants, CancellationToken ct = default) => Task.FromException(Unsupported());
    public Task SetListGrantAsync(ListActor actor, Guid workspaceId, Guid listId, string subject,
        ListAccess access, CancellationToken ct = default) => Task.FromException(Unsupported());
    public Task<IReadOnlyList<AuditEvent>> GetAuditAsync(ListActor actor, Guid workspaceId, int limit = 100,
        CancellationToken ct = default) => Task.FromException<IReadOnlyList<AuditEvent>>(Unsupported());
}
