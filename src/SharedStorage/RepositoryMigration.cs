using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed partial class RelationalDocumentRepository<TContext> where TContext : DbContext
{
    private static async Task<long> ResetImportPartitionAsync(TContext db, DocumentHead head, PreparedPartitionPublication publication, CancellationToken ct)
    {
        var kind = publication.After.Kind;
        var bindings = await db.Set<RepositoryBindingRow>().Where(row => row.ResourceId == head.ResourceId && row.Lifecycle == head.LifecycleGeneration && row.Partition == kind).ToArrayAsync(ct);
        long removed = bindings.Sum(row => (long)Encoding.UTF8.GetByteCount(row.Json));
        db.Set<RepositoryBindingRow>().RemoveRange(bindings);
        var visibility = await db.Set<RepositoryVisibilityRow>().Where(row => row.ResourceId == head.ResourceId && row.Lifecycle == head.LifecycleGeneration && row.Partition == kind && row.EndSequence == null).ToArrayAsync(ct);
        foreach (var interval in visibility) interval.EndSequence = publication.After.PublicationSequence;
        var mappings = await db.Set<RepositoryMappingRow>().Where(row => row.ResourceId == head.ResourceId && row.Lifecycle == head.LifecycleGeneration && row.Partition == kind).ToArrayAsync(ct);
        removed = checked(removed + mappings.Sum(row => (long)Encoding.UTF8.GetByteCount(row.Json)));
        db.Set<RepositoryMappingRow>().RemoveRange(mappings);
        var intervals = await db.Set<RepositoryMappingIntervalRow>().Where(row => row.ResourceId == head.ResourceId && row.Lifecycle == head.LifecycleGeneration && row.Partition == kind && row.EndSequence == null).ToArrayAsync(ct);
        foreach (var interval in intervals) interval.EndSequence = publication.After.PublicationSequence;
        await db.SaveChangesAsync(ct);
        return -removed;
    }

    // Initialization only. Ordinary operations never open the legacy snapshot tables.
    internal async Task BackfillAsync(TContext db, IContentStore? content, CancellationToken ct)
    {
        var reader = new RepositoryContentReader<TContext>(db, content, limits.MaxObjectBytes, database);
        var pointers = await db.Set<DocumentRow>().AsNoTracking().OrderBy(row => row.ResourceId).ToArrayAsync(ct);
        var pending = new List<(DocumentRow Pointer, long Lifecycle)>();
        foreach (var pointer in pointers)
        {
            if (await db.Set<RepositoryHeadRow>().AnyAsync(row => row.ResourceId == pointer.ResourceId, ct)) continue;
            var json = await db.Set<StateRow>().AsNoTracking().Where(row => row.ResourceId == pointer.ResourceId && row.StateVersion == pointer.StateVersion)
                .Select(row => row.StateJson).SingleOrDefaultAsync(ct) ?? throw new StorageCorruptionException("A legacy current snapshot is missing.");
            using var metadata = JsonDocument.Parse(json);
            var lifecycle = metadata.RootElement.TryGetProperty("LifecycleGeneration", out var value) ? value.GetInt64() : 1;
            pending.Add((pointer, lifecycle));
        }
        // Hold one document's graph/history at a time. The small first pass orders
        // recreated path owners after their retired ancestors.
        foreach (var item in pending.OrderBy(value => value.Lifecycle))
        {
            var pointer = item.Pointer;
            var json = await db.Set<StateRow>().AsNoTracking().Where(row => row.ResourceId == pointer.ResourceId && row.StateVersion == pointer.StateVersion)
                .Select(row => row.StateJson).SingleAsync(ct);
            var state = DocumentStateJson.Deserialize(json, strict: true, limits);
            if (state.ResourceId != pointer.ResourceId || state.StateVersion != pointer.StateVersion || state.PathKey != pointer.PathKey || state.IsDeleted != pointer.IsDeleted)
                throw new StorageCorruptionException("A legacy snapshot disagrees with its authoritative pointer.");
            var bundle = await LegacyDocumentProjection.PrepareAsync(state, reader, limits, ct);
            var head = bundle.Current.Head;
            var row = new RepositoryHeadRow { ResourceId = head.ResourceId, PathKey = head.PathKey, PathHash = Hash(head.PathKey), Json = "" };
            db.Set<RepositoryHeadRow>().Add(row);
            var metadata = await WriteControlAsync(db, new(head, head.ModifiedUtc, bundle.Current.Coordination, bundle.Current.Editors,
                bundle.Current.Partitions.Select(partition => partition.After).ToImmutableArray()), ct);
            int elementCount = 0;
            long elementBytes = 0;
            foreach (var revision in bundle.Revisions)
            {
                foreach (var partition in revision.Partitions)
                {
                    metadata = checked(metadata + await ResetImportPartitionAsync(db, head, partition, ct));
                    var written = await WritePartitionAsync(db, head, partition, true, ct);
                    metadata = checked(metadata + written.MetadataBytes); elementCount = checked(elementCount + written.AddedElements);
                    elementBytes = checked(elementBytes + written.AddedElementBytes);
                    await db.SaveChangesAsync(ct);
                }
                metadata = checked(metadata + await AddRevisionAsync(db, head, revision.Revision, ct));
            }
            foreach (var partition in bundle.Current.Partitions)
            {
                metadata = checked(metadata + await ResetImportPartitionAsync(db, head, partition, ct));
                var written = await WritePartitionAsync(db, head, partition, true, ct);
                metadata = checked(metadata + written.MetadataBytes); elementCount = checked(elementCount + written.AddedElements);
                elementBytes = checked(elementBytes + written.AddedElementBytes);
                await db.SaveChangesAsync(ct);
            }
            foreach (var receipt in bundle.Receipts) metadata = checked(metadata + await AddReceiptAsync(db, head.ResourceId, receipt, ct, allowOwnerless: true));
            foreach (var delivery in bundle.Deliveries)
            {
                var deliveryJson = Encode(delivery);
                db.Set<RepositoryDeliveryRow>().Add(new() { ResourceId = head.ResourceId, OperationId = delivery.OperationId, Sequence = delivery.Sequence, Json = deliveryJson });
                metadata = checked(metadata + Encoding.UTF8.GetByteCount(deliveryJson));
            }
            var contentBytes = await AddReferencesAsync(db, head.ResourceId, bundle.PhysicalReferences, ct);
            // Legacy package objects remain in the old immutable inventory. They
            // are not normal download roots or canonical graph charges.
            foreach (var key in bundle.RetiredPathKeys.Append(head.PathKey).Distinct(StringComparer.Ordinal))
            {
                var claim = await db.Set<RepositoryPathRow>().FindAsync([Hash(key)], ct);
                if (claim is null) db.Set<RepositoryPathRow>().Add(new() { ResourceId = head.ResourceId, PathHash = Hash(key), PathKey = key });
                else if (claim.PathKey != key) throw new StorageCorruptionException("A legacy path hash collision was detected.");
                else if (claim.ResourceId != head.ResourceId)
                {
                    var previous = await HeadAsync(db, claim.ResourceId, ct);
                    if (key != head.PathKey || previous is null || !previous.IsDeleted || previous.ReplacedBy != head.ResourceId ||
                        previous.LifecycleGeneration >= head.LifecycleGeneration)
                        throw new StorageCorruptionException("Legacy filename claims conflict across document identities.");
                    var previousRow = await HeadRowAsync(db, claim.ResourceId, ct) ?? throw new StorageCorruptionException("A legacy claim owner is absent.");
                    SetHead(previousRow, previous with { Counters = previous.Counters with
                        { StoredBytes = checked(previous.Counters.StoredBytes - Encoding.UTF8.GetByteCount(key)) } });
                    claim.ResourceId = head.ResourceId;
                }
                metadata = checked(metadata + Encoding.UTF8.GetByteCount(key));
            }
            head = head with { Counters = head.Counters with { StoredBytes = checked(contentBytes + metadata),
                GraphElements = elementCount, GraphBytes = elementBytes } };
            CheckCounters(head.Counters); head = SetHead(row, head);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }
}

internal sealed class RepositoryContentReader<TContext>(TContext db, IContentStore? external, long maximum,
    IRepositoryDatabase<TContext> database) : IContentStore, IContentBatchReader where TContext : DbContext
{
    public bool Durable => true;
    public bool Shared => true;
    public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Migration verifies immutable existing content and does not rewrite it.");
    public async ValueTask<IReadOnlyDictionary<string, byte[]>> ReadManyAsync(IReadOnlyCollection<ContentHandle> handles,
        CancellationToken cancellationToken = default)
    {
        var unique = DocumentContentBatchReader.ValidateHandles(handles, maximum, cancellationToken);
        if (unique.Length == 0) return new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            if (external is null)
            {
                await db.Database.OpenConnectionAsync(cancellationToken);
                try { return await DocumentContentBatchReader.ReadAsync(db, keys => database.ObjectsAtKeys(db, keys), unique, cancellationToken); }
                finally { await db.Database.CloseConnectionAsync(); }
            }
            var result = new Dictionary<string, byte[]>(unique.Length, StringComparer.Ordinal);
            foreach (var batch in unique.Chunk(64))
            {
                // EF releases a library-opened header connection after this query,
                // before the selected content reader acquires its own pool lease.
                // An already-open caller connection/transaction remains untouched.
                await DocumentContentBatchReader.ValidateMetadataAsync(keys => database.ObjectsAtKeys(db, keys), batch, cancellationToken);
                var payloads = await external.ReadManyVerifiedAsync(batch, maximum, cancellationToken);
                foreach (var handle in batch) result.Add(handle.Key, payloads[handle.Key]);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (database.IsDatabaseException(error))
        { throw new StorageUnavailableException("Repository content batch read failed.", error); }
    }
    public async ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
    {
        StorageLimits.Check("object bytes", handle.Length, maximum);
        var metadata = await db.Set<ObjectRow>().AsNoTracking().SingleOrDefaultAsync(row => row.ObjectKey == handle.Key, cancellationToken);
        if (metadata is null || metadata.Length != handle.Length || metadata.Sha256 != handle.Sha256)
            throw new StorageCorruptionException("Content metadata disagrees with its immutable handle.");
        if (external is not null) return await external.OpenReadAsync(handle, cancellationToken);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose, BufferSize = 65536 };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(Path.Combine(Path.GetTempPath(), "cellbridge-migration-" + Guid.NewGuid().ToString("N")), options);
        try
        {
            int ordinal = 0;
            await foreach (var chunk in db.Set<ChunkRow>().AsNoTracking().Where(row => row.ObjectKey == handle.Key).OrderBy(row => row.Ordinal)
                .AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                if (chunk.Ordinal != ordinal++ || chunk.Bytes.Length == 0 || chunk.Bytes.Length > 65536 || chunk.Bytes.Length > handle.Length - stream.Length)
                    throw new StorageCorruptionException("Invalid immutable content chunk sequence.");
                await stream.WriteAsync(chunk.Bytes, cancellationToken);
            }
            if (stream.Length != handle.Length)
                throw new StorageUnavailableException("The database payload is incomplete. Check content integrity or supply the matching filesystem content reader and Storage:ContentRoot.");
            stream.Position = 0;
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)) != handle.Sha256)
                throw new StorageCorruptionException("Immutable content digest is corrupt.");
            stream.Position = 0; return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }
}
