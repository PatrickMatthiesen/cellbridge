namespace CellBridge.Client.PowerPoint;

/// <summary>Reads PowerPoint revisions and applies edits through a caller-owned document session.</summary>
public sealed class PowerPointPresentationSession
{
    private readonly OfficeRevisionSession _session;

    /// <summary>Opens the current PowerPoint package and limits the bytes retained for revision history and a proposed save.</summary>
    public PowerPointPresentationSession(CellBridgeDocumentSession documentSession,
        long maxSessionBytes = OfficeRevisionSession.DefaultMaxSessionBytes)
    {
        _session = new(documentSession, maxSessionBytes, package => PowerPointPresentation.Open(package));
    }

    public string Revision => _session.Revision;
    public bool HasPendingCommit => _session.HasPendingCommit;

    public Task<string> SyncAsync(CancellationToken cancellationToken = default) =>
        _session.SyncAsync(cancellationToken);

    public IReadOnlyList<PowerPointSlide> ListSlides() =>
        _session.Read(package => PowerPointPresentation.Open(package).ListSlides());

    public PowerPointSlide ReadSlide(uint slideId) =>
        _session.Read(package => PowerPointPresentation.Open(package).ReadSlide(slideId));

    /// <summary>Compares two retained revisions. Throws if either revision is unavailable.</summary>
    public IReadOnlyList<PowerPointSlideChange> ComputeDiff(string beforeRevision, string afterRevision,
        int maxChanges = 1000) =>
        _session.Diff(beforeRevision, afterRevision, maxChanges, (before, after) =>
            PowerPointPresentation.Open(before).Diff(PowerPointPresentation.Open(after)).Take(maxChanges).ToArray());

    public Task<OfficeWriteResult> ApplyAsync(PowerPointEdit edit, string baseRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return _session.ApplyAsync(baseRevision, (original, latest) =>
        {
            var baseline = PowerPointPresentation.Open(original);
            var current = ReferenceEquals(original, latest) ? baseline : PowerPointPresentation.Open(latest);
            var result = PowerPointPresentation.Merge(baseline, current, edit);
            return result.Succeeded
                ? new(result.Presentation!.ToArray(), [], result.AddedSlideId)
                : new(null, [result.Conflict!]);
        }, cancellationToken);
    }

    /// <summary>Replays the unresolved proposal with its original base revision and package.</summary>
    public Task<OfficeWriteResult> RetryPendingCommitAsync(CancellationToken cancellationToken = default) =>
        _session.RetryPendingCommitAsync(cancellationToken);
}
