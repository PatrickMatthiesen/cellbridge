using System.Text.Json;

namespace CellBridge.Storage.Abstractions;

/// <summary>Admission limits, not a claim about supported Office file sizes.</summary>
public sealed record StorageLimits
{
    public long MaxDocumentBytes { get; init; } = 128L * 1024 * 1024;
    public long MaxGraphBytes { get; init; } = 512L * 1024 * 1024;
    public int MaxGraphElements { get; init; } = 100_000;
    public long MaxDocumentStoredBytes { get; init; } = 1024L * 1024 * 1024;
    public long MaxStoredBytes { get; init; } = 10L * 1024 * 1024 * 1024;
    public int MaxDocuments { get; init; } = 10_000;
    public long MaxObjectBytes { get; init; } = 512L * 1024 * 1024;
    public int MaxSaveReceipts { get; init; } = 10_000;
    public int MaxPendingExternalRevisions { get; init; } = 128;
    public long MaxPendingExternalBytes { get; init; } = 1024L * 1024 * 1024;
    public int MaxRetainedStateSnapshots { get; init; } = 64;
    public int MaxHistoryRevisions { get; init; } = 1_000;
    public int MaxRestoreReceipts { get; init; } = 1_000;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDocumentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxGraphBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxGraphElements);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDocumentStoredBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxStoredBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDocuments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxObjectBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSaveReceipts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRetainedStateSnapshots);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPendingExternalRevisions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPendingExternalBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxHistoryRevisions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRestoreReceipts);
    }

    public void CheckDocument(DocumentState state)
    {
        Check("document bytes", state.Content.Length, MaxDocumentBytes);
        long graphBytes = 0;
        long elements = 0;
        foreach (var partition in state.Partitions)
            foreach (var element in partition.Elements)
            {
                graphBytes = checked(graphBytes + element.Payload.Length);
                elements++;
            }
        Check("graph bytes", graphBytes, MaxGraphBytes);
        Check("graph elements", elements, MaxGraphElements);
        Check("save receipts", state.Receipts.Length, MaxSaveReceipts);
        if (state.Publication is { } publication)
        {
            Check("pending external revisions", publication.Pending.Length, MaxPendingExternalRevisions);
            Check("pending external bytes", publication.Pending.Sum(r => r.Content.Length), MaxPendingExternalBytes);
        }
        Check("history revisions", state.Revisions.Length, MaxHistoryRevisions);
        Check("restore receipts", state.RestoreReceipts.Length, MaxRestoreReceipts);
        long stored = JsonSerializer.SerializeToUtf8Bytes(state).LongLength;
        foreach (var handle in StorageReferences.Handles(state).DistinctBy(h => h.Key))
            stored = checked(stored + handle.Length);
        Check("document stored bytes", stored, MaxDocumentStoredBytes);
    }

    public static void Check(string budget, long used, long limit)
    {
        if (used < 0 || used > limit) throw new StorageQuotaExceededException(budget, used, limit);
    }
}

public sealed class StorageQuotaExceededException(string budget, long used, long limit)
    : IOException($"The {budget} budget is exceeded: {used} bytes/items, limit {limit}.")
{
    public string Budget { get; } = budget;
    public long Used { get; } = used;
    public long Limit { get; } = limit;
}

public static class StorageReferences
{
    public static IEnumerable<ContentHandle> Handles(DocumentState state)
    {
        yield return state.Content;
        if (state.Publication is { } publication)
            foreach (var revision in publication.Pending) yield return revision.Content;
        foreach (var partition in state.Partitions)
        {
            yield return partition.Content;
            foreach (var element in partition.Elements) yield return element.Payload;
        }
        foreach (var receipt in state.Receipts)
            if (receipt.Response is not null) yield return receipt.Response;
        foreach (var revision in state.Revisions)
        {
            yield return revision.Content;
            foreach (var partition in revision.Partitions)
            {
                yield return partition.Content;
                foreach (var element in partition.Elements) yield return element.Payload;
            }
        }
    }
}
