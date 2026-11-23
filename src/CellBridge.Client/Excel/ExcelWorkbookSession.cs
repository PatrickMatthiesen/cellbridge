namespace CellBridge.Client.Excel;

public sealed record ExcelWorkbookSummary(string Revision, IReadOnlyList<ExcelSheet> Sheets, bool Date1904);

public sealed record ExcelRangeSnapshot(string Revision, IReadOnlyList<ExcelCell> Cells);

public sealed record ExcelWorkbookCellChange(uint SheetId, string Address, ExcelCellValue Before, ExcelCellValue After);

public sealed record ExcelWorkbookSyncResult(string Revision, IReadOnlyList<ExcelWorkbookCellChange> Changes, bool Truncated);

/// <summary>Applies revision-based Excel edits through a caller-owned document session.</summary>
public sealed class ExcelWorkbookSession
{
    private sealed record Proposal(string Revision, byte[] Package, uint? AddedSheetId);
    private readonly CellBridgeDocumentSession _document;
    private readonly ExcelWorkbookLimits _limits;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LinkedList<CellBridgeDocumentSnapshot> _history = [];
    private long _retainedBytes;
    private Proposal? _unresolved;
    private CellBridgeDocumentSnapshot? _syncBaseline;

    public ExcelWorkbookSession(CellBridgeDocumentSession documentSession, ExcelWorkbookLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(documentSession);
        _document = documentSession;
        _limits = limits ?? new ExcelWorkbookLimits();
        _limits.Validate();
        var initial = Remember(_document.GetSnapshot());
        if (_limits.TrackSyncChanges) _syncBaseline = initial;
    }

    public bool HasPendingCommit => Volatile.Read(ref _unresolved) is not null;

    public ExcelWorkbookSummary GetWorkbookSummary() => Read(snapshot =>
    {
        var (sheets, date1904) = ExcelWorkbook.ReadProperties(snapshot.Package, LimitsForRead(snapshot));
        return new ExcelWorkbookSummary(snapshot.Revision, sheets, date1904);
    });

    public IReadOnlyList<ExcelSheet> ListSheets() => GetWorkbookSummary().Sheets;

    public IReadOnlyList<ExcelCell> ReadRange(uint sheetId, string a1Range, int maxCells = 10_000) =>
        ReadRangeAtRevision(sheetId, a1Range, maxCells).Cells;

    /// <summary>Returns cells and the revision to supply when applying edits based on them.</summary>
    public ExcelRangeSnapshot ReadRangeAtRevision(uint sheetId, string a1Range, int maxCells = 10_000) =>
        Read(snapshot => new ExcelRangeSnapshot(snapshot.Revision,
            ExcelWorkbook.ReadRange(snapshot.Package, sheetId, a1Range, maxCells, LimitsForRead(snapshot))));

    public async Task<string> SyncAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            ThrowIfUnresolved();
            var latest = Remember(await _document.SyncAsync(cancellationToken));
            if (_syncBaseline is not null) _syncBaseline = latest;
            TrimHistory();
            return latest.Revision;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reports changes in cell values since opening or the last explicit sync.</summary>
    /// <remarks>Enable <see cref="ExcelWorkbookLimits.TrackSyncChanges"/> to retain the comparison revision.</remarks>
    public async Task<ExcelWorkbookSyncResult> SyncWithChangesAsync(int maxChanges,
        CancellationToken cancellationToken = default)
    {
        if (maxChanges < 1 || maxChanges > _limits.MaxCellsPerOperation)
            throw new ArgumentOutOfRangeException(nameof(maxChanges));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            ThrowIfUnresolved();
            var before = _syncBaseline ?? throw new InvalidOperationException("Enable workbook sync change tracking first.");
            var latest = Remember(await _document.SyncAsync(cancellationToken));
            var (changes, truncated) = before.Revision == latest.Revision
                ? (Array.Empty<ExcelWorkbookCellChange>(), false)
                : ExcelWorkbook.DiffValues(before.Package, latest.Package, maxChanges, _limits, Budget(), cancellationToken);
            _syncBaseline = latest;
            TrimHistory();
            return new(latest.Revision, changes, truncated);
        }
        finally { _gate.Release(); }
    }

    public Task<ExcelWriteResult> SetCellsAsync(uint sheetId, IEnumerable<ExcelCellEdit> edits, string baseRevision,
        IReadOnlyDictionary<string, ExcelCellValue>? expectedValues = null,
        ExcelConflictPolicy policy = ExcelConflictPolicy.FailWithConflict, CancellationToken cancellationToken = default) =>
        WriteAsync(baseRevision, (original, budget) =>
        {
            var changes = ExcelWorkbook.ParseEdits(edits, _limits.MaxCellsPerOperation, budget);
            var expected = expectedValues is null ? null : ExcelWorkbook.ParseEdits(
                expectedValues.Select(pair => new ExcelCellEdit(pair.Key, pair.Value)), _limits.MaxCellsPerOperation,
                budget, validateValues: false);
            return latest =>
            {
                var attemptBudget = Budget();
                attemptBudget.Charge(IntentBytes(changes, expected));
                return ExcelWorkbook.MergeCells(original.Package, latest.Package, original.Revision == latest.Revision,
                    sheetId, changes, expected, policy, _limits, attemptBudget);
            };
        }, cancellationToken);

    public Task<ExcelWriteResult> ClearRangeAsync(uint sheetId, string a1Range, string baseRevision,
        CancellationToken cancellationToken = default) =>
        SetCellsAsync(sheetId, CellRange.Parse(a1Range).Enumerate(_limits.MaxCellsPerOperation)
            .Select(address => new ExcelCellEdit(address.ToString(), ExcelCellValue.Blank)), baseRevision,
            cancellationToken: cancellationToken);

    public Task<ExcelWriteResult> AddSheetAsync(string sheetName, CancellationToken cancellationToken = default) =>
        WriteAsync(null, (original, _) => latest =>
        {
            if (latest.Revision != original.Revision &&
                ExcelWorkbook.ListSheets(latest.Package, LimitsForRead(latest)).Any(sheet =>
                    string.Equals(sheet.Name, sheetName, StringComparison.OrdinalIgnoreCase)))
                return new ExcelMergeResult(ExcelWriteStatus.StructuralConflict, null, [], "A sheet with that name was added.");
            var addition = ExcelWorkbook.AddSheet(latest.Package, sheetName, _limits, Budget());
            return new ExcelMergeResult(ExcelWriteStatus.Success, addition.Package, []) { AddedSheetId = addition.SheetId };
        }, cancellationToken);

    /// <summary>Resends an unresolved proposal with its original revision and package.</summary>
    public async Task<ExcelWriteResult> RetryPendingCommitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            var pending = _unresolved ?? throw new InvalidOperationException("There is no unresolved Excel proposal.");
            var result = await SendProposalAsync(pending, cancellationToken);
            if (result.Succeeded)
                return new ExcelWriteResult(ExcelWriteStatus.Success, result.Revision, []) { AddedSheetId = pending.AddedSheetId };
            if (result.Status == CellBridgeCommitStatus.OutcomeUnknown)
                return UnknownOutcome(result.Revision);
            var latest = Remember(await _document.SyncAsync(cancellationToken));
            return new ExcelWriteResult(ExcelWriteStatus.RetryExhausted, latest.Revision, [],
                "The recovered proposal had a confirmed revision conflict. Resubmit the original edits to rebase.");
        }
        finally { _gate.Release(); }
    }

    private async Task<ExcelWriteResult> WriteAsync(string? baseRevision,
        Func<CellBridgeDocumentSnapshot, ExcelMemoryBudget, Func<CellBridgeDocumentSnapshot, ExcelMergeResult>> createEdit,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            ThrowIfUnresolved();
            var current = Remember(_document.GetSnapshot(), baseRevision);
            var original = baseRevision is null ? current : _history.FirstOrDefault(snapshot => snapshot.Revision == baseRevision);
            if (original is null)
                return new ExcelWriteResult(ExcelWriteStatus.BaseRevisionUnavailable, current.Revision, []);

            var apply = createEdit(original, Budget());
            current = Remember(await _document.SyncAsync(cancellationToken), original.Revision);
            for (var attempt = 0; attempt < _limits.MaxCommitAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var edit = apply(current);
                if (edit.Status != ExcelWriteStatus.Success || edit.Package is null)
                    return new ExcelWriteResult(edit.Status, current.Revision, edit.Conflicts, edit.Detail);

                var proposal = new Proposal(current.Revision, edit.Package, edit.AddedSheetId);
                var result = await SendProposalAsync(proposal, cancellationToken, original.Revision);
                if (result.Succeeded)
                    return new ExcelWriteResult(ExcelWriteStatus.Success, result.Revision, []) { AddedSheetId = proposal.AddedSheetId };
                if (result.Status == CellBridgeCommitStatus.OutcomeUnknown)
                    return UnknownOutcome(result.Revision);
                if (attempt + 1 < _limits.MaxCommitAttempts)
                {
                    var ceiling = 20 * (1 << Math.Min(attempt, 5));
                    await Task.Delay(Random.Shared.Next(ceiling / 2, ceiling + 1), cancellationToken);
                }
                current = Remember(await _document.SyncAsync(cancellationToken), original.Revision);
            }

            return new ExcelWriteResult(ExcelWriteStatus.RetryExhausted, current.Revision, []);
        }
        finally
        {
            TrimHistory();
            _gate.Release();
        }
    }

    private async Task<CellBridgeCommitResult> SendProposalAsync(Proposal proposal, CancellationToken cancellationToken,
        string? keep = null)
    {
        if (_unresolved is null)
            Budget().Charge(proposal.Package.LongLength);

        CellBridgeCommitResult result;
        try
        {
            result = await _document.TryCommitAsync(proposal.Revision, proposal.Package, cancellationToken);
        }
        catch
        {
            _unresolved = _document.HasPendingCommit(proposal.Revision, proposal.Package) ? proposal : null;
            throw;
        }

        _unresolved = null;
        if (result.Succeeded)
            Remember(new CellBridgeDocumentSnapshot(result.Revision, proposal.Package), keep);
        return result;
    }

    private CellBridgeDocumentSnapshot Remember(CellBridgeDocumentSnapshot snapshot, string? keep = null)
    {
        var old = _history.FirstOrDefault(entry => entry.Revision == snapshot.Revision);
        if (old is not null)
            return old;
        if (snapshot.Package.LongLength > Math.Min(_limits.MaxPackageBytes, _limits.MaxSessionBytes))
            throw new InvalidDataException("The workbook exceeds the Excel session package limit.");
        var protectedBytes = _history.Where(entry => entry.Revision == keep || entry == _syncBaseline)
            .Sum(entry => entry.Package.LongLength);
        if (snapshot.Package.LongLength + protectedBytes + (_unresolved?.Package.LongLength ?? 0) > _limits.MaxSessionBytes)
            throw new InvalidDataException("The workbook exceeds the Excel session memory budget.");
        _history.AddLast(snapshot);
        _retainedBytes += snapshot.Package.LongLength;
        TrimHistory(keep);
        return snapshot;
    }

    private void TrimHistory(string? keep = null)
    {
        var latest = _history.Last?.Value;
        var retainedCount = _history.Count(entry => entry == latest || entry == _syncBaseline || entry.Revision == keep);
        while (_retainedBytes + (_unresolved?.Package.LongLength ?? 0) > _limits.MaxSessionBytes ||
               _history.Count > Math.Max(_limits.MaxRetainedRevisions, retainedCount))
        {
            var eviction = _history.First!;
            while (eviction.Value.Revision == keep || eviction.Value == _syncBaseline || eviction.Value == latest)
                eviction = eviction.Next!;
            _retainedBytes -= eviction.Value.Package.LongLength;
            _history.Remove(eviction);
        }
    }

    private ExcelMemoryBudget Budget() => new(_limits.MaxSessionBytes - _retainedBytes - (_unresolved?.Package.LongLength ?? 0));

    private ExcelWorkbookLimits LimitsForRead(CellBridgeDocumentSnapshot snapshot) =>
        _limits with { MaxSessionBytes = Math.Max(1,
            _limits.MaxSessionBytes - _retainedBytes - (_unresolved?.Package.LongLength ?? 0) + snapshot.Package.LongLength) };

    private T Read<T>(Func<CellBridgeDocumentSnapshot, T> read)
    {
        _gate.Wait();
        try { return read(Remember(_document.GetSnapshot())); }
        finally { _gate.Release(); }
    }

    private static long IntentBytes(Dictionary<CellAddress, ExcelCellValue> edits,
        Dictionary<CellAddress, ExcelCellValue>? expected) =>
        edits.Values.Sum(value => 200L + (value.RawValue?.Length ?? 0) * 8L) +
        (expected?.Values.Sum(value => 200L + (value.RawValue?.Length ?? 0) * 8L) ?? 0L);

    private static ExcelWriteResult UnknownOutcome(string revision) =>
        new(ExcelWriteStatus.OutcomeUnknown, revision, [],
            "The replay conflicted after a lost response. Sync and inspect the workbook before editing.");

    private void ThrowIfUnresolved()
    {
        if (_unresolved is not null)
            throw new InvalidOperationException("Retry the unresolved commit before syncing or editing this workbook.");
    }
}
