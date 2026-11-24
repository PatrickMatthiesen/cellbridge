namespace CellBridge.Client;

public enum OfficeWriteStatus
{
    Success,
    Conflict,
    BaseRevisionUnavailable,
    RetryExhausted,
    /// <summary>The server could have accepted the save. Check the session's pending commit state before continuing.</summary>
    OutcomeUnknown,
}

public sealed record OfficeWriteResult(OfficeWriteStatus Status, string Revision, IReadOnlyList<string> Conflicts,
    uint? AddedSlideId = null, string? Detail = null);
