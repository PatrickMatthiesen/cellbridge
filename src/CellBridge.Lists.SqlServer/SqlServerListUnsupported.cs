namespace CellBridge.Lists.SqlServer;

public sealed partial class SqlServerListEngine
{
    // Keep unavailable features explicit. These methods never open a connection,
    // acquire authority, prune receipts or publish any state.
    public Task<RecordPage> QueryAsync(ListActor actor, Guid workspaceId, Guid listId,
        ListQuery query, CancellationToken ct = default) => Task.FromException<RecordPage>(Unsupported());
    public Task<IReadOnlyList<SavedListView>> GetViewsAsync(ListActor actor, Guid workspaceId, Guid listId,
        CancellationToken ct = default) => Task.FromException<IReadOnlyList<SavedListView>>(Unsupported());
    public Task<SavedListView> SaveViewAsync(ListActor actor, Guid workspaceId, Guid listId,
        SaveViewRequest request, CancellationToken ct = default) => Task.FromException<SavedListView>(Unsupported());
    public Task<IReadOnlyList<AuditEvent>> GetAuditAsync(ListActor actor, Guid workspaceId, int limit = 100,
        CancellationToken ct = default) => Task.FromException<IReadOnlyList<AuditEvent>>(Unsupported());
}
