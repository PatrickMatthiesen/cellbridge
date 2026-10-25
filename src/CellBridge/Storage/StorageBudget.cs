namespace CellBridge.Storage.Abstractions;

/// <summary>Atomic process-local accounting used by volatile providers.</summary>
public sealed class StorageBudget
{
    public StorageLimits Limits { get; }
    public object SyncRoot { get; } = new();
    public long StoredBytes { get; private set; }
    public long DocumentCount { get; private set; }
    public StorageBudget(StorageLimits? limits = null)
    {
        Limits = limits ?? new StorageLimits();
        Limits.Validate();
    }
    public void Adjust(long bytes, long documents = 0)
    {
        lock (SyncRoot)
        {
            long nextBytes = checked(StoredBytes + bytes);
            long nextDocuments = checked(DocumentCount + documents);
            StorageLimits.Check("total stored bytes", nextBytes, bytes > 0 ? Limits.MaxStoredBytes : long.MaxValue);
            StorageLimits.Check("document count", nextDocuments, documents > 0 ? Limits.MaxDocuments : long.MaxValue);
            StoredBytes = nextBytes;
            DocumentCount = nextDocuments;
        }
    }
}

/// <summary>
/// Coordinates object/reservation bytes, state metadata and document-count admission
/// atomically in one scope. Temporary input buffers are outside this accounting.
/// Paired stores must expose the same scope object before the provider admits writes.
/// </summary>
public interface IStorageBudgetParticipant
{
    object BudgetScope { get; }
}

public interface ILocalStorageBudgetParticipant : IStorageBudgetParticipant
{
    StorageBudget Budget { get; }
    object IStorageBudgetParticipant.BudgetScope => Budget;
    /// <summary>Compose empty stores before admitting concurrent operations.</summary>
    void UseBudget(StorageBudget budget);
}
