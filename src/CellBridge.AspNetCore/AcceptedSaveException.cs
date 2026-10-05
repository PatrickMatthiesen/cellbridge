using System.Collections.Immutable;

namespace CellBridge.AspNetCore;

/// <summary>
/// A later operation failed after known successful saves. The failing operation may still
/// have an unknown commit outcome; these receipts are not a complete transaction journal.
/// Cancellation remains cancellation and does not use this exception.
/// </summary>
public sealed class AcceptedSaveException : IOException
{
    public ImmutableArray<AcceptedSave> AcceptedSaves { get; }

    internal AcceptedSaveException(ImmutableArray<AcceptedSave> acceptedSaves, Exception innerException)
        : base("An operation failed after one or more saves were accepted.", innerException)
        => AcceptedSaves = acceptedSaves;

    internal static bool CanWrap(Exception error) => error is IOException or InvalidOperationException or KeyNotFoundException;

    internal static AcceptedSaveException Wrap(ImmutableArray<AcceptedSave> previous, Exception error)
        => new(error is AcceptedSaveException partial ? previous.AddRange(partial.AcceptedSaves) : previous, error);
}
