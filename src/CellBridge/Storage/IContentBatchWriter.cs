namespace CellBridge.Storage.Abstractions;

/// <summary>Optionally writes a bounded set of immutable content objects in one transaction.</summary>
public interface IContentBatchWriter
{
    public const int MaxBatchObjects = 64;
    public const long MaxBatchBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Returns handles in input order, including duplicates. Input buffers must
    /// remain unchanged until completion. Batches admit at most 64 inputs and
    /// 512 MiB in total; the provider also enforces its per-object limit.
    /// </summary>
    ValueTask<IReadOnlyList<ContentHandle>> WriteManyAsync(IReadOnlyList<ReadOnlyMemory<byte>> contents,
        CancellationToken cancellationToken = default);
}
