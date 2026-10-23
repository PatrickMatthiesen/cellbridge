using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.PostgreSql;

public sealed partial class PostgreSqlStateStore
{
    public ValueTask<DocumentReadMetadata?> FindReadMetadataByResourceIdAsync(Guid resourceId,
        CancellationToken cancellationToken = default) => FindReadMetadataAsync(resourceId, null, cancellationToken);

    public ValueTask<DocumentReadMetadata?> FindReadMetadataByPathKeyAsync(string pathKey,
        CancellationToken cancellationToken = default) => FindReadMetadataAsync(null, pathKey, cancellationToken);

    private async ValueTask<DocumentReadMetadata?> FindReadMetadataAsync(Guid? resourceId, string? pathKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var context = CreateContext();
            var row = await CurrentReadRows(context, resourceId, pathKey, false)
                .SingleOrDefaultAsync(cancellationToken);
            return row is null ? null : DecodeReadMetadata(row);
        }
        catch (Exception ex) when (PostgreSqlDatabase.IsDatabaseException(ex))
        { throw new StorageUnavailableException("Document metadata could not be read.", ex); }
    }

    public async ValueTask<DocumentRevisionReadPage?> ReadRevisionSummariesAsync(Guid resourceId, int offset, int limit,
        CancellationToken cancellationToken = default)
    {
        DocumentReadModels.ValidatePage(offset, limit);
        try
        {
            await using var context = CreateContext();
            var rows = await (from current in CurrentReadRows(context, resourceId, null, false)
                              from revision in context.RevisionSummaries.AsNoTracking()
                                  .Where(candidate => candidate.ResourceId == current.ResourceId &&
                                      candidate.LifecycleGeneration == current.LifecycleGeneration)
                                  .OrderByDescending(candidate => candidate.RevisionNumber).Skip(offset).Take(limit)
                                  .DefaultIfEmpty()
                              select new RevisionReadProjection { Current = current, Revision = revision })
                .ToListAsync(cancellationToken);
            if (rows.Count == 0) return null;
            var metadata = DecodeReadMetadata(rows[0].Current);
            var revisions = rows.Where(row => row.Revision is not null)
                .Select(row => DecodeRevisionSummary(row.Revision!, metadata)).ToImmutableArray();
            var expected = Math.Min(limit, Math.Max(0, metadata.RevisionCount - offset));
            if (revisions.Length != expected)
                throw new StorageCorruptionException("Revision projection page is incomplete.");
            var next = (long)offset + revisions.Length;
            return new(metadata, revisions, next < metadata.RevisionCount ? checked((int)next) : null);
        }
        catch (Exception ex) when (PostgreSqlDatabase.IsDatabaseException(ex))
        { throw new StorageUnavailableException("Revision summaries could not be read.", ex); }
    }

    public async ValueTask<DocumentRevisionRead?> ReadRevisionSummaryAsync(Guid resourceId, ulong revisionNumber,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = CreateContext();
            var row = await (from current in CurrentReadRows(context, resourceId, null, false)
                             from revision in context.RevisionSummaries.AsNoTracking()
                                 .Where(candidate => candidate.ResourceId == current.ResourceId &&
                                     candidate.LifecycleGeneration == current.LifecycleGeneration &&
                                     candidate.RevisionNumber == (decimal)revisionNumber).DefaultIfEmpty()
                             select new RevisionReadProjection { Current = current, Revision = revision })
                .SingleOrDefaultAsync(cancellationToken);
            if (row is null) return null;
            var metadata = DecodeReadMetadata(row.Current);
            return new(metadata, row.Revision is null ? null : DecodeRevisionSummary(row.Revision, metadata));
        }
        catch (Exception ex) when (PostgreSqlDatabase.IsDatabaseException(ex))
        { throw new StorageUnavailableException("Revision summary could not be read.", ex); }
    }

    private static IQueryable<ReadProjection> CurrentReadRows(CellBridgeDbContext context, Guid? resourceId,
        string? pathKey, bool includeDeleted)
    {
        return from document in context.Documents.AsNoTracking()
               join matchingHead in context.ReadHeads.AsNoTracking()
                   on new { document.ResourceId, document.StateVersion }
                   equals new { matchingHead.ResourceId, matchingHead.StateVersion } into heads
               from head in heads.DefaultIfEmpty()
               where (resourceId == null || document.ResourceId == resourceId) &&
                     (pathKey == null || document.PathKey == pathKey) &&
                     (includeDeleted || !document.IsDeleted)
               select new ReadProjection
               {
                   ResourceId = document.ResourceId,
                   PathKey = document.PathKey,
                   StateVersion = document.StateVersion,
                   IsDeleted = document.IsDeleted,
                   HeadStateVersion = head == null ? null : head.StateVersion,
                   LifecycleGeneration = head == null ? null : head.LifecycleGeneration,
                   RevisionCount = head == null ? null : head.RevisionCount,
                   MetadataJson = head == null ? null : head.MetadataJson,
                   TotalRevisionCount = context.RevisionSummaries.Count(revision =>
                       revision.ResourceId == document.ResourceId),
                   CurrentRevisionCount = context.RevisionSummaries.Count(revision =>
                       revision.ResourceId == document.ResourceId &&
                       revision.LifecycleGeneration == head!.LifecycleGeneration),
                   LatestRevisionNumber = context.RevisionSummaries
                       .Where(revision => revision.ResourceId == document.ResourceId &&
                           revision.LifecycleGeneration == head!.LifecycleGeneration)
                       .Max(revision => (decimal?)revision.RevisionNumber),
                   DatabaseUtc = CellBridgeDbContext.ClockTimestamp(),
               };
    }

    private static DocumentReadMetadata DecodeReadMetadata(ReadProjection row)
    {
        if (row.HeadStateVersion is null || row.LifecycleGeneration is null || row.RevisionCount is null ||
            row.MetadataJson is null)
            throw new StorageCorruptionException("The current metadata projection is missing or stale.");
        var metadata = RecoveryJson.Read<DocumentReadMetadata>(Encoding.UTF8.GetBytes(row.MetadataJson));
        if (metadata.ResourceId != row.ResourceId || metadata.PathKey != row.PathKey ||
            metadata.StateVersion != row.StateVersion || metadata.StateVersion != row.HeadStateVersion.Value ||
            metadata.IsDeleted != row.IsDeleted || metadata.LifecycleGeneration != row.LifecycleGeneration.Value ||
            metadata.Security is null || metadata.Security.Grants is null || metadata.EditorExpiries.IsDefault ||
            metadata.PendingDeliveryCount < 0 || string.IsNullOrWhiteSpace(metadata.Path) ||
            metadata.ContentVersion == 0 || metadata.LifecycleGeneration < 1 ||
            metadata.CreatedUtc.Kind != DateTimeKind.Utc || metadata.ModifiedUtc.Kind != DateTimeKind.Utc ||
            metadata.RevisionCount != row.RevisionCount.Value || row.RevisionCount.Value < 1 ||
            row.TotalRevisionCount != row.RevisionCount.Value || row.CurrentRevisionCount != row.RevisionCount.Value ||
            row.LatestRevisionNumber != (decimal)metadata.LatestRevisionNumber)
            throw new StorageCorruptionException("The current metadata projection disagrees with its pointer or history.");
        ValidateReadContent(metadata.Content);
        return metadata;
    }

    private static DocumentRevisionSummary DecodeRevisionSummary(RevisionSummaryRow row,
        DocumentReadMetadata metadata)
    {
        var revision = RecoveryJson.Read<DocumentRevisionSummary>(Encoding.UTF8.GetBytes(row.SummaryJson));
        if (revision.ResourceId != metadata.ResourceId || revision.LifecycleGeneration != metadata.LifecycleGeneration ||
            (decimal)revision.RevisionNumber != row.RevisionNumber || revision.RevisionNumber == 0 ||
            revision.RevisionNumber > metadata.LatestRevisionNumber || revision.ContentVersion == 0 ||
            revision.CreatedUtc.Kind != DateTimeKind.Utc || revision.Author is null ||
            string.IsNullOrWhiteSpace(revision.Author.Subject))
            throw new StorageCorruptionException("The revision summary disagrees with its current history.");
        ValidateReadContent(revision.Content);
        return revision;
    }

    private static void ValidateReadContent(ContentHandle? content)
    {
        if (content is null || string.IsNullOrWhiteSpace(content.Key) || content.Length < 0 || content.Sha256 is null ||
            content.Sha256.Length != 64 || content.Sha256.Any(character => !char.IsAsciiHexDigit(character)))
            throw new StorageCorruptionException("Invalid content metadata in read projection.");
    }

    private static async Task WriteSelectiveReadsAsync(CellBridgeDbContext context, DocumentState state,
        CancellationToken cancellationToken)
    {
        var head = context.ReadHeads.Local.SingleOrDefault(row => row.ResourceId == state.ResourceId)
            ?? await context.ReadHeads.AsNoTracking()
                .SingleOrDefaultAsync(row => row.ResourceId == state.ResourceId, cancellationToken);
        if (head is not null && head.StateVersion > state.StateVersion) return;
        var metadata = DocumentReadModels.Metadata(state);
        var revisions = DocumentReadModels.Revisions(state).ToArray();
        if (revisions.Length == 0 || revisions.Length != metadata.RevisionCount ||
            revisions.Any(revision => revision.ResourceId != state.ResourceId ||
                revision.LifecycleGeneration != state.LifecycleGeneration || revision.RevisionNumber == 0) ||
            revisions.Select(revision => revision.RevisionNumber).Distinct().Count() != revisions.Length ||
            revisions.Max(revision => revision.RevisionNumber) != metadata.LatestRevisionNumber)
            throw new StorageCorruptionException("Cannot index inconsistent revision history.");

        var databaseRows = await context.RevisionSummaries.AsNoTracking()
            .Where(row => row.ResourceId == state.ResourceId)
            .Select(row => new RevisionSummaryProjection
            {
                LifecycleGeneration = row.LifecycleGeneration,
                RevisionNumber = row.RevisionNumber,
                SummaryJson = row.SummaryJson,
            }).ToDictionaryAsync(row => (row.LifecycleGeneration, row.RevisionNumber), cancellationToken);
        var tracked = context.ChangeTracker.Entries<RevisionSummaryRow>()
            .Where(entry => entry.Entity.ResourceId == state.ResourceId && entry.State != EntityState.Detached)
            .ToDictionary(entry => (entry.Entity.LifecycleGeneration, entry.Entity.RevisionNumber));
        var desiredKeys = new HashSet<(long LifecycleGeneration, decimal RevisionNumber)>(revisions.Length);

        foreach (var revision in revisions)
        {
            var key = (revision.LifecycleGeneration, RevisionNumber: (decimal)revision.RevisionNumber);
            desiredKeys.Add(key);
            var summaryJson = JsonSerializer.Serialize(revision);
            if (tracked.TryGetValue(key, out var entry))
            {
                if (entry.State == EntityState.Deleted) entry.State = EntityState.Unchanged;
                entry.Entity.SummaryJson = summaryJson;
            }
            else if (databaseRows.TryGetValue(key, out var databaseRow))
            {
                if (databaseRow.SummaryJson == summaryJson) continue;
                var row = CreateRevisionSummaryRow(state.ResourceId, databaseRow);
                context.RevisionSummaries.Attach(row);
                row.SummaryJson = summaryJson;
            }
            else
            {
                context.RevisionSummaries.Add(new RevisionSummaryRow
                {
                    ResourceId = state.ResourceId,
                    LifecycleGeneration = key.LifecycleGeneration,
                    RevisionNumber = key.RevisionNumber,
                    SummaryJson = summaryJson,
                });
            }
        }
        foreach (var pair in tracked)
        {
            if (!desiredKeys.Contains(pair.Key)) pair.Value.State = pair.Value.State == EntityState.Added
                ? EntityState.Detached
                : EntityState.Deleted;
        }
        foreach (var pair in databaseRows)
        {
            if (desiredKeys.Contains(pair.Key) || tracked.ContainsKey(pair.Key)) continue;
            context.RevisionSummaries.Remove(CreateRevisionSummaryRow(state.ResourceId, pair.Value));
        }

        var metadataJson = JsonSerializer.Serialize(metadata);
        if (head is null)
        {
            context.ReadHeads.Add(new ReadHeadRow
            {
                ResourceId = state.ResourceId,
                StateVersion = state.StateVersion,
                LifecycleGeneration = state.LifecycleGeneration,
                RevisionCount = revisions.Length,
                MetadataJson = metadataJson,
            });
        }
        else
        {
            if (context.Entry(head).State == EntityState.Detached) context.ReadHeads.Attach(head);
            head.StateVersion = state.StateVersion;
            head.LifecycleGeneration = state.LifecycleGeneration;
            head.RevisionCount = revisions.Length;
            head.MetadataJson = metadataJson;
        }
    }

    private static RevisionSummaryRow CreateRevisionSummaryRow(Guid resourceId,
        RevisionSummaryProjection projection) => new()
    {
        ResourceId = resourceId,
        LifecycleGeneration = projection.LifecycleGeneration,
        RevisionNumber = projection.RevisionNumber,
        SummaryJson = projection.SummaryJson,
    };

    private sealed class RevisionSummaryProjection
    {
        public long LifecycleGeneration { get; init; }
        public decimal RevisionNumber { get; init; }
        public required string SummaryJson { get; init; }
    }

    private sealed class ReadProjection
    {
        public Guid ResourceId { get; init; }
        public required string PathKey { get; init; }
        public long StateVersion { get; init; }
        public bool IsDeleted { get; init; }
        public long? HeadStateVersion { get; init; }
        public long? LifecycleGeneration { get; init; }
        public int? RevisionCount { get; init; }
        public string? MetadataJson { get; init; }
        public int TotalRevisionCount { get; init; }
        public int CurrentRevisionCount { get; init; }
        public decimal? LatestRevisionNumber { get; init; }
        public DateTime DatabaseUtc { get; init; }
    }

    private sealed class RevisionReadProjection
    {
        public required ReadProjection Current { get; init; }
        public RevisionSummaryRow? Revision { get; init; }
    }
}
