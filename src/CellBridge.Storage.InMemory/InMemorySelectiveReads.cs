using System.Collections.Immutable;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.InMemory;

public sealed partial class InMemoryStateStore
{
    public ValueTask<DocumentReadMetadata?> FindReadMetadataByResourceIdAsync(Guid resourceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Budget.SyncRoot)
        {
            var state = _documents.GetValueOrDefault(resourceId);
            return ValueTask.FromResult(state is { IsDeleted: false } ? DocumentReadModels.Metadata(state) : null);
        }
    }

    public ValueTask<DocumentReadMetadata?> FindReadMetadataByPathKeyAsync(string pathKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pathKey);
        cancellationToken.ThrowIfCancellationRequested();
        lock (Budget.SyncRoot)
        lock (_paths)
        {
            var state = _paths.TryGetValue(pathKey, out var resourceId) ? _documents.GetValueOrDefault(resourceId) : null;
            return ValueTask.FromResult(state is { IsDeleted: false } ? DocumentReadModels.Metadata(state) : null);
        }
    }

    public ValueTask<DocumentRevisionReadPage?> ReadRevisionSummariesAsync(Guid resourceId, int offset, int limit,
        CancellationToken cancellationToken = default)
    {
        DocumentReadModels.ValidatePage(offset, limit);
        cancellationToken.ThrowIfCancellationRequested();
        lock (Budget.SyncRoot)
        {
            if (_documents.GetValueOrDefault(resourceId) is not { IsDeleted: false } state)
                return ValueTask.FromResult<DocumentRevisionReadPage?>(null);

            var metadata = DocumentReadModels.Metadata(state);
            var total = state.Revisions.IsEmpty ? 1 : state.Revisions.Length;
            var capacity = (int)Math.Min(limit, Math.Max(0L, (long)total - offset));
            var builder = ImmutableArray.CreateBuilder<DocumentRevisionSummary>(capacity);
            if (state.Revisions.IsEmpty)
            {
                if (offset == 0)
                    builder.Add(DocumentReadModels.Revisions(state).Single());
            }
            else
            {
                for (var index = state.Revisions.Length - 1 - offset; index >= 0 && builder.Count < limit; index--)
                    builder.Add(DocumentReadModels.Summary(state.Revisions[index]));
            }
            var revisions = builder.MoveToImmutable();
            var next = (long)offset + revisions.Length < total && revisions.Length != 0
                ? checked(offset + revisions.Length)
                : (int?)null;
            return ValueTask.FromResult<DocumentRevisionReadPage?>(new(metadata, revisions, next));
        }
    }

    public ValueTask<DocumentRevisionRead?> ReadRevisionSummaryAsync(Guid resourceId, ulong revisionNumber,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Budget.SyncRoot)
        {
            if (_documents.GetValueOrDefault(resourceId) is not { IsDeleted: false } state)
                return ValueTask.FromResult<DocumentRevisionRead?>(null);

            var metadata = DocumentReadModels.Metadata(state);
            DocumentRevisionSummary? revision;
            if (state.Revisions.IsEmpty)
            {
                var legacy = DocumentReadModels.Revisions(state).Single();
                revision = legacy.RevisionNumber == revisionNumber ? legacy : null;
            }
            else
            {
                var found = state.Revisions.SingleOrDefault(item => item.RevisionNumber == revisionNumber);
                revision = found is null ? null : DocumentReadModels.Summary(found);
            }
            return ValueTask.FromResult<DocumentRevisionRead?>(new(metadata, revision));
        }
    }
}
