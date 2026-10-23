using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.SqlServer;

public sealed partial class SqlServerStateStore
{
    // SQL Server's lock-based READ COMMITTED is not a coherent statement snapshot.
    // Serializable reads protect pointer/index joins without changing database-wide
    // snapshot settings owned by the consumer. Deadlocks/timeouts remain explicit errors.
    private async Task<T> ReadAsync<T>(Func<SqlServerDocumentDbContext, Task<T>> action, CancellationToken ct)
    {
        try
        {
            await using var db = new SqlServerDocumentDbContext(options);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await SqlServerDocumentDatabase.ConfigureTransactionAsync(db, ct);
            var result = await action(db);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (Exception ex) when (SqlServerDocumentDatabase.IsDatabaseException(ex))
        { throw new StorageUnavailableException("The document view could not be read.", ex); }
    }

    public async ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return await ReadAsync<IReadOnlyList<DocumentSummary>>(async db =>
        {
            var rows = await ReadRows(db, null, null).OrderBy(row => row.Pointer.PathKey).Skip(offset).Take(limit).ToListAsync(cancellationToken);
            return rows.Select(row => ValidateMetadata(row).ToSummary(DateTime.SpecifyKind(row.Utc, DateTimeKind.Utc))).ToArray();
        }, cancellationToken);
    }

    public ValueTask<DocumentReadMetadata?> FindReadMetadataByResourceIdAsync(Guid resourceId, CancellationToken cancellationToken = default) =>
        FindMetadataAsync(resourceId, null, cancellationToken);
    public ValueTask<DocumentReadMetadata?> FindReadMetadataByPathKeyAsync(string pathKey, CancellationToken cancellationToken = default) =>
        FindMetadataAsync(null, pathKey, cancellationToken);

    private async ValueTask<DocumentReadMetadata?> FindMetadataAsync(Guid? id, string? key, CancellationToken ct) =>
        await ReadAsync(async db =>
        {
            var row = await ReadRows(db, id, key).SingleOrDefaultAsync(ct);
            return row is null ? null : ValidateMetadata(row);
        }, ct);

    public async ValueTask<DocumentRevisionReadPage?> ReadRevisionSummariesAsync(Guid resourceId, int offset, int limit,
        CancellationToken cancellationToken = default)
    {
        DocumentReadModels.ValidatePage(offset, limit);
        return await ReadAsync(async db =>
        {
            var rows = await (from current in ReadRows(db, resourceId, null)
                from revision in db.Revisions.AsNoTracking().Where(row => row.ResourceId == resourceId &&
                    row.LifecycleGeneration == current.Metadata!.LifecycleGeneration)
                    .OrderByDescending(row => row.RevisionNumber).Skip(offset).Take(limit).DefaultIfEmpty()
                select new { Current = current, Revision = revision }).ToListAsync(cancellationToken);
            if (rows.Count == 0) return null;
            var metadata = ValidateMetadata(rows[0].Current);
            var revisions = rows.Where(row => row.Revision is not null)
                .Select(row => ValidateRevision(row.Revision!, metadata)).ToImmutableArray();
            if (revisions.Length != Math.Min(limit, Math.Max(0, metadata.RevisionCount - offset)))
                throw new StorageCorruptionException("Revision projection page is incomplete.");
            var next = (long)offset + revisions.Length;
            return new DocumentRevisionReadPage(metadata, revisions, next < metadata.RevisionCount ? (int)next : null);
        }, cancellationToken);
    }

    public async ValueTask<DocumentRevisionRead?> ReadRevisionSummaryAsync(Guid resourceId, ulong revisionNumber,
        CancellationToken cancellationToken = default) => await ReadAsync(async db =>
    {
        var row = await (from current in ReadRows(db, resourceId, null)
            from revision in db.Revisions.AsNoTracking().Where(candidate => candidate.ResourceId == resourceId &&
                candidate.LifecycleGeneration == current.Metadata!.LifecycleGeneration && candidate.RevisionNumber == (decimal)revisionNumber).DefaultIfEmpty()
            select new { Current = current, Revision = revision }).SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        var metadata = ValidateMetadata(row.Current);
        return new DocumentRevisionRead(metadata, row.Revision is null ? null : ValidateRevision(row.Revision, metadata));
    }, cancellationToken);

    private sealed class ReadRow
    {
        public required DocumentPointer Pointer { get; init; }
        public DocumentMetadata? Metadata { get; init; }
        public int TotalCount { get; init; }
        public int CurrentCount { get; init; }
        public decimal? Latest { get; init; }
        public DateTime Utc { get; init; }
    }

    private static IQueryable<ReadRow> ReadRows(SqlServerDocumentDbContext db, Guid? id, string? key)
    {
        var hash = key is null ? null : PathHash(key);
        return from pointer in db.Documents.AsNoTracking()
            where !pointer.IsDeleted && (id == null || pointer.ResourceId == id) &&
                (key == null || pointer.PathHash == hash && pointer.PathKey == key)
            join metadata in db.Metadata.AsNoTracking() on new { pointer.ResourceId, pointer.StateVersion }
                equals new { metadata.ResourceId, metadata.StateVersion } into heads
            from metadata in heads.DefaultIfEmpty()
            select new ReadRow
            {
                Pointer = pointer, Metadata = metadata,
                TotalCount = db.Revisions.Count(row => row.ResourceId == pointer.ResourceId),
                CurrentCount = db.Revisions.Count(row => row.ResourceId == pointer.ResourceId && row.LifecycleGeneration == metadata!.LifecycleGeneration),
                Latest = db.Revisions.Where(row => row.ResourceId == pointer.ResourceId && row.LifecycleGeneration == metadata!.LifecycleGeneration)
                    .Max(row => (decimal?)row.RevisionNumber),
                Utc = SqlServerDocumentDbContext.ClockUtc()
            };
    }

    private static DocumentReadMetadata ValidateMetadata(ReadRow row)
    {
        var head = row.Metadata ?? throw new StorageCorruptionException("Current metadata is missing or stale.");
        var metadata = RecoveryJson.Read<DocumentReadMetadata>(head.Data);
        if (metadata.ResourceId != row.Pointer.ResourceId || metadata.StateVersion != row.Pointer.StateVersion ||
            metadata.PathKey != row.Pointer.PathKey || metadata.IsDeleted != row.Pointer.IsDeleted ||
            metadata.LifecycleGeneration != head.LifecycleGeneration || metadata.RevisionCount != head.RevisionCount ||
            metadata.RevisionCount < 1 || row.TotalCount != metadata.RevisionCount || row.CurrentCount != metadata.RevisionCount ||
            row.Latest != (decimal)metadata.LatestRevisionNumber || metadata.ContentVersion == 0 || metadata.LifecycleGeneration < 1 ||
            metadata.Security?.Grants is null || metadata.EditorExpiries.IsDefault || metadata.PendingDeliveryCount < 0 ||
            string.IsNullOrWhiteSpace(metadata.Path) || metadata.CreatedUtc.Kind != DateTimeKind.Utc || metadata.ModifiedUtc.Kind != DateTimeKind.Utc)
            throw new StorageCorruptionException("Metadata disagrees with its pointer or history.");
        ValidateContent(metadata.Content);
        return metadata;
    }

    private static DocumentRevisionSummary ValidateRevision(RevisionMetadata row, DocumentReadMetadata current)
    {
        var revision = RecoveryJson.Read<DocumentRevisionSummary>(row.Data);
        if (revision.ResourceId != current.ResourceId || revision.LifecycleGeneration != current.LifecycleGeneration ||
            (decimal)revision.RevisionNumber != row.RevisionNumber || revision.RevisionNumber == 0 ||
            revision.RevisionNumber > current.LatestRevisionNumber || revision.ContentVersion == 0 ||
            revision.CreatedUtc.Kind != DateTimeKind.Utc || string.IsNullOrWhiteSpace(revision.Author?.Subject))
            throw new StorageCorruptionException("Revision metadata disagrees with its current history.");
        ValidateContent(revision.Content);
        return revision;
    }

    private static void ValidateContent(ContentHandle? content)
    {
        if (content is null || string.IsNullOrWhiteSpace(content.Key) || content.Length < 0 || content.Sha256 is null ||
            content.Sha256.Length != 64 || content.Sha256.Any(c => !char.IsAsciiHexDigit(c)))
            throw new StorageCorruptionException("Invalid content metadata in read projection.");
    }

    private static async Task WriteIndexesAsync(SqlServerDocumentDbContext db, DocumentState state, CancellationToken ct)
    {
        var metadata = DocumentReadModels.Metadata(state);
        var head = await db.Metadata.SingleOrDefaultAsync(row => row.ResourceId == state.ResourceId, ct);
        if (head is null)
        {
            head = new() { ResourceId = state.ResourceId, Data = [] };
            db.Metadata.Add(head);
        }
        head.StateVersion = state.StateVersion; head.LifecycleGeneration = state.LifecycleGeneration;
        head.RevisionCount = metadata.RevisionCount; head.Data = JsonSerializer.SerializeToUtf8Bytes(metadata);
        var desired = DocumentReadModels.Revisions(state).ToDictionary(row => (decimal)row.RevisionNumber);
        var existing = await db.Revisions.Where(row => row.ResourceId == state.ResourceId).ToListAsync(ct);
        foreach (var row in existing)
        {
            if (row.LifecycleGeneration != state.LifecycleGeneration || !desired.Remove(row.RevisionNumber, out var summary))
                db.Revisions.Remove(row);
            else
            {
                var data = JsonSerializer.SerializeToUtf8Bytes(summary);
                if (!row.Data.AsSpan().SequenceEqual(data)) row.Data = data;
            }
        }
        foreach (var summary in desired.Values)
            db.Revisions.Add(new() { ResourceId = state.ResourceId, LifecycleGeneration = state.LifecycleGeneration,
                RevisionNumber = summary.RevisionNumber, Data = JsonSerializer.SerializeToUtf8Bytes(summary) });
        var reservations = await db.Reservations.Where(row => row.ResourceId == state.ResourceId).ToListAsync(ct);
        foreach (var row in reservations)
            if (row.Ordinal >= state.RetiredPathKeys.Length) db.Reservations.Remove(row);
        for (var ordinal = 0; ordinal < state.RetiredPathKeys.Length; ordinal++)
        {
            var key = state.RetiredPathKeys[ordinal];
            var row = reservations.SingleOrDefault(candidate => candidate.Ordinal == ordinal);
            if (row is null) db.Reservations.Add(new() { ResourceId = state.ResourceId, Ordinal = ordinal, PathKey = key, PathHash = PathHash(key) });
            else if (row.PathKey != key) { row.PathKey = key; row.PathHash = PathHash(key); }
        }
    }
}
