namespace CellBridge.Client.Word;

/// <summary>Reads Word revisions and applies edits through a caller-owned document session.</summary>
public sealed class WordDocumentSession
{
    private readonly OfficeRevisionSession _session;

    /// <summary>Opens the current Word package and limits the bytes retained for revision history and a proposed save.</summary>
    public WordDocumentSession(CellBridgeDocumentSession documentSession,
        long maxSessionBytes = OfficeRevisionSession.DefaultMaxSessionBytes)
    {
        _session = new(documentSession, maxSessionBytes, package => WordDocument.Open(package));
    }

    public string Revision => _session.Revision;
    public bool HasPendingCommit => _session.HasPendingCommit;

    public Task<string> SyncAsync(CancellationToken cancellationToken = default) =>
        _session.SyncAsync(cancellationToken);

    public WordDocumentSummary GetSummary() => _session.Read(package => WordDocument.Open(package).GetSummary());

    public IReadOnlyList<WordParagraph> ReadParagraphs(int startIndex = 0, int maxParagraphs = 1000) =>
        _session.Read(package => WordDocument.Open(package).ReadParagraphs(startIndex, maxParagraphs));

    public IReadOnlyList<WordParagraph> ListHeadings() =>
        _session.Read(package => WordDocument.Open(package).ListHeadings());

    /// <summary>Compares two retained revisions. Throws if either revision is unavailable.</summary>
    public WordDiffResult ComputeDiff(string beforeRevision, string afterRevision, int maxChanges = 1000) =>
        _session.Diff(beforeRevision, afterRevision, maxChanges,
            (before, after) => WordDocument.Open(after).Diff(WordDocument.Open(before), maxChanges));

    public Task<OfficeWriteResult> ApplyAsync(WordEdit edit, string baseRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return _session.ApplyAsync(baseRevision, (original, latest) =>
        {
            var result = WordDocument.Merge(original, latest, [edit]);
            return new(result.Package, result.Conflicts.Select(conflict => conflict.Message).ToArray());
        }, cancellationToken);
    }

    /// <summary>Replays the unresolved proposal with its original base revision and package.</summary>
    public Task<OfficeWriteResult> RetryPendingCommitAsync(CancellationToken cancellationToken = default) =>
        _session.RetryPendingCommitAsync(cancellationToken);
}
