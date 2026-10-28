using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.SqlServer;

/// <summary>EF document persistence with required cross-host coordination.</summary>
public sealed class SqlServerStateStore : IDocumentStateStore, IDocumentLifecycleStore,
    IAtomicDocumentRenameStore, IDocumentReadStore, IProviderRecoveryStore, IStorageBudgetParticipant, IAtomicDocumentSaveStore, IDocumentPreparationStore
{
    private readonly DocumentOperations<SqlServerDocumentDbContext> operations;
    public SqlServerStateStore(DbContextOptions<SqlServerDocumentDbContext> options, StorageLimits? limits = null)
        : this(options, limits, options) { }

    private SqlServerStateStore(DbContextOptions<SqlServerDocumentDbContext> options, StorageLimits? limits, object budgetScope)
    {
        options = SqlServerDocumentDatabase.Normalize(options);
        operations = new(new SqlServerDocumentDatabase(options), limits, budgetScope);
    }

    public bool Durable => operations.Durable;
    public bool Shared => operations.Shared;
    public object BudgetScope => operations.BudgetScope;
    public bool SupportsContentStore(IContentStore content) => content is SqlServerContentStore writer &&
        ReferenceEquals(BudgetScope, writer.BudgetScope);
    public ValueTask<T> TransitionWithContentAsync<T>(Guid resourceId, IContentStore content,
        IReadOnlyList<ReadOnlyMemory<byte>> pending, Func<DocumentState, DateTime, StateTransition<T>> transition,
        CancellationToken cancellationToken = default)
    {
        if (content is not SqlServerContentStore writer || !SupportsContentStore(content))
            throw new ArgumentException("Atomic saves require the matching SQL Server content store and budget scope.", nameof(content));
        return operations.TransitionWithContentAsync(resourceId, writer.Writer, pending, transition, cancellationToken);
    }

    public ValueTask<DocumentPreparationRead?> ReadPreparationAsync(Guid resourceId, CancellationToken cancellationToken = default) =>
        operations.ReadPreparationAsync(resourceId, cancellationToken);
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
