using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.PostgreSql;

/// <summary>EF document persistence with required cross-host coordination.</summary>
public sealed class PostgreSqlStateStore : IDocumentStateStore, IDocumentLifecycleStore,
    IAtomicDocumentRenameStore, IDocumentReadStore, IProviderRecoveryStore, IStorageBudgetParticipant
{
    private readonly DocumentOperations<CellBridgeDbContext> operations;
    private readonly DbContextOptions<CellBridgeDbContext> options;
    private readonly StorageLimits? limits;
    public PostgreSqlStateStore(Npgsql.NpgsqlDataSource dataSource, StorageLimits? limits = null)
        : this(PostgreSqlDatabase.CreateOptions(dataSource), limits, dataSource) { }

    public PostgreSqlStateStore(DbContextOptions<CellBridgeDbContext> options, StorageLimits? limits = null)
        : this(options, limits, options) { }

    private PostgreSqlStateStore(DbContextOptions<CellBridgeDbContext> options, StorageLimits? limits, object budgetScope)
    {
        options = PostgreSqlDatabase.NormalizeOptions(options);
        this.options = options;
        this.limits = limits;
        operations = new(new PostgreSqlDocumentDatabase(options), limits, budgetScope);
    }

    public bool Durable => operations.Durable;
    public bool Shared => operations.Shared;
    public object BudgetScope => operations.BudgetScope;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => new PostgreSqlStorageMigrator(options, limits).MigrateAsync(cancellationToken);
    internal static Task ConfigureTransactionAsync(CellBridgeDbContext context, CancellationToken cancellationToken) => PostgreSqlDocumentDatabase.ConfigureTransactionAsync(context, cancellationToken);

    public ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken cancellationToken = default) => operations.FindByResourceIdAsync(id, cancellationToken);
    public ValueTask<DocumentState?> FindByPathKeyAsync(string key, CancellationToken cancellationToken = default) => operations.FindByPathKeyAsync(key, cancellationToken);
    public ValueTask<DocumentState?> FindLifecycleAsync(Guid id, CancellationToken cancellationToken = default) => operations.FindLifecycleAsync(id, cancellationToken);
    public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken cancellationToken = default) => operations.ListAsync(offset, limit, cancellationToken);
    public ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken cancellationToken = default) => operations.TryCreateAsync(state, cancellationToken);
    public ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition, CancellationToken cancellationToken = default) => operations.TransitionAsync(id, transition, cancellationToken);
    public ValueTask<T> RenameAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition, CancellationToken cancellationToken = default) => operations.RenameAsync(id, transition, cancellationToken);
    public ValueTask<bool> TryDeleteAsync(Guid id, long expectedGeneration, long expectedStateVersion, CancellationToken cancellationToken = default) => operations.TryDeleteAsync(id, expectedGeneration, expectedStateVersion, cancellationToken);
    public ValueTask<bool> TryRecreateAsync(Guid id, long expectedGeneration, long expectedStateVersion, DocumentState replacement, CancellationToken cancellationToken = default) => operations.TryRecreateAsync(id, expectedGeneration, expectedStateVersion, replacement, cancellationToken);
    public ValueTask CheckHealthAsync(CancellationToken cancellationToken = default) => operations.CheckHealthAsync(cancellationToken);
    public ValueTask<DocumentReadMetadata?> FindReadMetadataByResourceIdAsync(Guid id, CancellationToken cancellationToken = default) => operations.FindReadMetadataByResourceIdAsync(id, cancellationToken);
    public ValueTask<DocumentReadMetadata?> FindReadMetadataByPathKeyAsync(string key, CancellationToken cancellationToken = default) => operations.FindReadMetadataByPathKeyAsync(key, cancellationToken);
    public ValueTask<DocumentRevisionReadPage?> ReadRevisionSummariesAsync(Guid id, int offset, int limit, CancellationToken cancellationToken = default) => operations.ReadRevisionSummariesAsync(id, offset, limit, cancellationToken);
    public ValueTask<DocumentRevisionRead?> ReadRevisionSummaryAsync(Guid id, ulong revisionNumber, CancellationToken cancellationToken = default) => operations.ReadRevisionSummaryAsync(id, revisionNumber, cancellationToken);
    public ValueTask CheckRecoveryAsync(CancellationToken cancellationToken = default) => operations.CheckRecoveryAsync(cancellationToken);
    public ValueTask<ProviderSnapshot> CaptureRecoveryAsync(CancellationToken cancellationToken = default) => operations.CaptureRecoveryAsync(cancellationToken);
    public ValueTask<RecoveryReceipt?> FindRecoveryReceiptAsync(Guid operationId, CancellationToken cancellationToken = default) => operations.FindRecoveryReceiptAsync(operationId, cancellationToken);
    public ValueTask<RecoveryReceipt> ImportRecoveryAsync(ProviderSnapshot snapshot, RecoveryReceipt receipt, CancellationToken cancellationToken = default) => operations.ImportRecoveryAsync(snapshot, receipt, cancellationToken);
}
