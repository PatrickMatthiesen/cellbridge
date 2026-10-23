using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CellBridge.Lists.PostgreSql;

public sealed partial class PostgreSqlListEngine
{
    // Each engine owns its delegates, so their first execution uses this engine's
    // consumer options, services and model. A consumer model-cache key may still
    // select another model on a later lease; use ordinary queries for that model.
    private IModel? _compiledContextModel;

    private bool CanUseCompiledContextQueries(PostgreSqlListDbContext db)
    {
        var model = db.Model;
        return ReferenceEquals(Interlocked.CompareExchange(ref _compiledContextModel, model, null) ?? model, model);
    }

    private readonly Func<PostgreSqlListDbContext, Guid, Guid, string, int, bool, CancellationToken, Task<ListDefinitionProjection?>> _compiledDefinitionContext =
        EF.CompileAsyncQuery((PostgreSqlListDbContext db, Guid workspaceId, Guid listId, string subject, int requiredAccess, bool hostAuthorized, CancellationToken ct) => (
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
                IsMember = hostAuthorized || db.Memberships.Any(row =>
                    row.WorkspaceId == workspaceId && row.Subject == subject),
                Access = hostAuthorized
                    ? requiredAccess
                    : db.ListGrants.Where(row => row.WorkspaceId == workspaceId &&
                            row.ListId == listId && row.Subject == subject)
                        .Select(row => (int?)row.Access).SingleOrDefault() ?? 0,
                DefinitionJson = schema.Definition
            }
        ).SingleOrDefault());

    private readonly Func<PostgreSqlListDbContext, Guid, Guid, Guid, string, int, bool, CancellationToken, Task<CreateContextProjection?>> _compiledCreateContext =
        EF.CompileAsyncQuery((PostgreSqlListDbContext db, Guid workspaceId, Guid listId, Guid recordId, string subject, int requiredAccess, bool hostAuthorized, CancellationToken ct) => (
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
                IsMember = hostAuthorized || db.Memberships.Any(row =>
                    row.WorkspaceId == workspaceId && row.Subject == subject),
                Access = hostAuthorized
                    ? requiredAccess
                    : db.ListGrants.Where(row => row.WorkspaceId == workspaceId &&
                            row.ListId == listId && row.Subject == subject)
                        .Select(row => (int?)row.Access).SingleOrDefault() ?? 0,
                DefinitionJson = schema.Definition,
                Receipt = receipt,
                ReceiptLive = receipt != null && receipt.ExpiresUtc > PostgreSqlListDbContext.ClockTimestamp(),
                ReceiptDefinitionJson = receiptSchema == null ? null : receiptSchema.Definition
            }
        ).SingleOrDefault());

    private readonly Func<PostgreSqlListDbContext, Guid, Guid, Guid, string, int, bool, CancellationToken, Task<MutationContextProjection?>> _compiledMutationContext =
        EF.CompileAsyncQuery((PostgreSqlListDbContext db, Guid workspaceId, Guid listId, Guid recordId, string subject, int requiredAccess, bool hostAuthorized, CancellationToken ct) => (
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
                IsMember = hostAuthorized || db.Memberships.Any(row =>
                    row.WorkspaceId == workspaceId && row.Subject == subject),
                Access = hostAuthorized
                    ? requiredAccess
                    : db.ListGrants.Where(row => row.WorkspaceId == workspaceId &&
                            row.ListId == listId && row.Subject == subject)
                        .Select(row => (int?)row.Access).SingleOrDefault() ?? 0,
                DefinitionJson = schema.Definition,
                Record = record
            }
        ).SingleOrDefault());
    private sealed class CreateStateProjection
    {
        public WorkspaceRow Workspace { get; init; } = null!;
        public ListCounterRow Counter { get; init; } = null!;
        public DateTimeOffset Now { get; init; }
    }

    private readonly Func<PostgreSqlListDbContext, Guid, Guid, CancellationToken, Task<CreateStateProjection>> _compiledCreateState =
        EF.CompileAsyncQuery((PostgreSqlListDbContext db, Guid workspaceId, Guid listId, CancellationToken ct) => (
            from workspace in db.Workspaces
            join counter in db.ListCounters on workspace.WorkspaceId equals counter.WorkspaceId
            where workspace.WorkspaceId == workspaceId && counter.ListId == listId
            select new CreateStateProjection
            {
                Workspace = workspace,
                Counter = counter,
                Now = PostgreSqlListDbContext.ClockTimestamp()
            }).Single());

}
