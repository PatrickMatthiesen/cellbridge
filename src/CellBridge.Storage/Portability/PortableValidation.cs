using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage;

public sealed partial class PortableArchive
{
    private async ValueTask ValidateStateAsync(PortableManifest manifest, IContentStore content, CancellationToken cancellationToken)
    {
        try
        {
            var immutable = new Dictionary<(Guid Resource, int Partition, ExtendedId Id), GraphElementState>();
            foreach (var state in manifest.State.Snapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckStateShape(state);
                foreach (var handle in ContentStoreReader.UniqueHandles(StorageReferences.Handles(state)))
                {
                    await using var stream = await content.OpenReadAsync(handle, cancellationToken);
                    await CopyVerifiedAsync(stream, Stream.Null, handle, cancellationToken);
                }
                await ValidatePartitionsAsync(state, state.Content, state.Partitions, content, immutable, cancellationToken);
                ulong previous = 0;
                foreach (var revision in state.Revisions)
                {
                    if (revision.ResourceId != state.ResourceId || revision.LifecycleGeneration != state.LifecycleGeneration ||
                        revision.RevisionNumber <= previous || revision.ContentVersion == 0 || revision.Author is null ||
                        string.IsNullOrWhiteSpace(revision.Author.Subject) || revision.CreatedUtc.Kind != DateTimeKind.Utc ||
                        revision.Partitions.Length != 2 || !revision.Partitions.Select(p => p.Kind).Order().SequenceEqual(new[] { 0, 1 }))
                        throw new StorageCorruptionException("Invalid revision history identity, ordering or partitions.");
                    previous = revision.RevisionNumber;
                    await ValidatePartitionsAsync(state, revision.Content, revision.Partitions, content, immutable, cancellationToken);
                }
                if (!state.Revisions.IsEmpty && (state.Revisions[^1].Content != state.Content ||
                    state.Revisions[^1].ContentVersion != state.ContentVersion ||
                    JsonSerializer.Serialize(state.Revisions[^1].Partitions) != JsonSerializer.Serialize(state.Partitions.Where(p => p.Kind != 2))))
                    throw new StorageCorruptionException("Current publication disagrees with revision history.");
                foreach (var receipt in state.Receipts)
                {
                    if (receipt.LifecycleGeneration != state.LifecycleGeneration || receipt.PartitionKind is < 0 or > 2 ||
                        string.IsNullOrWhiteSpace(receipt.OperationKey) || string.IsNullOrWhiteSpace(receipt.Digest) ||
                        receipt.ContentVersion > state.ContentVersion && receipt.PartitionKind == 0)
                        throw new StorageCorruptionException("Invalid save receipt identity.");
                    if (receipt.Response is null) continue;
                    var reader = new BinaryReaderEx(await content.ReadVerifiedAsync(receipt.Response, _storage.MaxObjectBytes, cancellationToken));
                    var response = FsshttpbResponse.Deserialize(reader);
                    if (reader.Remaining != 0 || response.SubResponses.Count != 1 || response.SubResponses[0].RequestType != RequestTypes.PutChanges ||
                        response.SubResponses[0].Status) throw new StorageCorruptionException("Invalid retained save response.");
                }
                if (state.Receipts.Select(r => (r.PartitionKind, r.OperationKey)).Distinct().Count() != state.Receipts.Length ||
                    state.RestoreReceipts.Select(r => (r.LifecycleGeneration, r.OwnerSubject, r.OperationKey)).Distinct().Count() != state.RestoreReceipts.Length ||
                    state.RestoreReceipts.Any(r => r.LifecycleGeneration != state.LifecycleGeneration || string.IsNullOrWhiteSpace(r.OwnerSubject) ||
                        string.IsNullOrWhiteSpace(r.OperationKey) || string.IsNullOrWhiteSpace(r.Digest) ||
                        !state.Revisions.Any(v => v.RevisionNumber == r.RevisionNumber)))
                    throw new StorageCorruptionException("Invalid or conflicting durable receipts.");
                if (state.Publication is { } publication)
                {
                    if (publication.BindingId == Guid.Empty || string.IsNullOrWhiteSpace(publication.Destination) ||
                        string.IsNullOrWhiteSpace(publication.ExpectedRevision) || publication.NextSequence < 1 ||
                        publication.Pending.IsDefault || publication.Pending.Select(r => r.OperationId).Distinct().Count() != publication.Pending.Length)
                        throw new StorageCorruptionException("Invalid external publication binding.");
                    long sequence = 0;
                    foreach (var pending in publication.Pending)
                    {
                        if (pending.OperationId == Guid.Empty || pending.ResourceId != state.ResourceId ||
                            pending.LifecycleGeneration != state.LifecycleGeneration || pending.Sequence <= sequence ||
                            pending.Sequence >= publication.NextSequence || pending.ContentVersion > state.ContentVersion)
                            throw new StorageCorruptionException("Invalid external publication receipt or pin.");
                        sequence = pending.Sequence;
                    }
                }
            }
            CheckNamespace(manifest.State);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException or ArgumentException or
            OverflowException or InvalidOperationException or NullReferenceException)
        { throw new StorageCorruptionException("Recovery state or graph is corrupt or unsupported.", ex); }
    }

    private static void CheckStateShape(DocumentState state)
    {
        if (state.FormatVersion != DocumentState.CurrentFormat || state.ResourceId == Guid.Empty || state.StateVersion < 0 ||
            state.ContentVersion == 0 || state.LifecycleGeneration < 1 || !CanonicalPath(state.Path) ||
            state.PathKey != StorageIds.PathKey(state.Path) || state.CreatedUtc.Kind != DateTimeKind.Utc || state.ModifiedUtc.Kind != DateTimeKind.Utc ||
            state.Security is null || state.Security.Grants is null || state.Security.Grants.Any(g => string.IsNullOrWhiteSpace(g.Key) ||
                (g.Value & ~(DocumentAccess.Read | DocumentAccess.Write)) != 0) ||
            state.Partitions.IsDefault || state.Editors.IsDefault || state.Receipts.IsDefault || state.Revisions.IsDefault ||
            state.RestoreReceipts.IsDefault || state.RetiredPathKeys.IsDefault || state.Coordination is null ||
            state.Coordination.SchemaOwners.IsDefault || state.Coordination.CoauthorClients.IsDefault ||
            state.Coordination.Generation < 0 || state.Partitions.Length != 3 ||
            !state.Partitions.Select(p => p.Kind).Order().SequenceEqual(new[] { 0, 1, 2 }) ||
            state.IsDeleted != state.DeletedFromStateVersion.HasValue || state.DeletedFromStateVersion >= state.StateVersion ||
            state.DeletedFromStateVersion < 0 || state.ReplacedBy is not null && !state.IsDeleted ||
            state.RetiredPathKeys.Distinct(StringComparer.Ordinal).Count() != state.RetiredPathKeys.Length ||
            state.RetiredPathKeys.Any(k => !CanonicalPath(k) || k != k.ToUpperInvariant()))
            throw new StorageCorruptionException("Invalid recovery document shape or lifecycle.");
        foreach (var editor in state.Editors)
            if (editor.ClientId == Guid.Empty || editor.Metadata is null || editor.Metadata.Any(p => p.Value.IsDefault) ||
                editor.ExpiresUtc.Kind != DateTimeKind.Utc || editor.LastSeenUtc.Kind != DateTimeKind.Utc || editor.JoinedUtc.Kind != DateTimeKind.Utc)
                throw new StorageCorruptionException("Invalid editor state.");
        foreach (var lease in state.Coordination.SchemaOwners.Concat(state.Coordination.Exclusive is null ? [] : new[] { state.Coordination.Exclusive }))
            if (string.IsNullOrWhiteSpace(lease.Id) || lease.ExpiresUtc.Kind != DateTimeKind.Utc)
                throw new StorageCorruptionException("Invalid lease state.");
        if (state.Coordination.HostLock is { } host && (string.IsNullOrWhiteSpace(host.Token) ||
            string.IsNullOrWhiteSpace(host.OwnerSubject) || host.ExpiresUtc.Kind != DateTimeKind.Utc))
            throw new StorageCorruptionException("Invalid host lease state.");
    }

    private static bool CanonicalPath(string path) => !string.IsNullOrWhiteSpace(path) && path.StartsWith('/') &&
        !path.Contains('\\') && !path.Any(char.IsControl) &&
        path.Split('/').Skip(1).All(s => s.Length > 0 && s is not "." and not "..");

    private async ValueTask ValidatePartitionsAsync(DocumentState owner, ContentHandle package,
        ImmutableArray<PartitionState> partitions, IContentStore content,
        Dictionary<(Guid Resource, int Partition, ExtendedId Id), GraphElementState> immutable, CancellationToken cancellationToken)
    {
        foreach (var partition in partitions)
        {
            if (partition.Identity is null || partition.Elements.IsDefault ||
                new[] { partition.Identity.StorageManifest, partition.Identity.CellManifest, partition.Identity.RevisionManifest,
                    partition.Identity.ObjectGroup, partition.Identity.ObjectDataBlob, partition.Identity.Object,
                    partition.Identity.Revision, partition.Identity.CellLong, partition.Identity.CellShort }.Any(id => id is null) ||
                partition.Elements.Select(e => e.Id).Distinct().Count() != partition.Elements.Length ||
                partition.StorageIndex is null && !partition.Elements.IsEmpty ||
                partition.Kind != 0 && partition.InlineContent.IsDefault)
                throw new StorageCorruptionException("Invalid recovery partition shape.");
            StorageLimits.Check("recovery partition elements", partition.Elements.Length, _storage.MaxGraphElements);
            StorageLimits.Check("recovery partition bytes", partition.Elements.Sum(e => e.Payload.Length), _storage.MaxGraphBytes);
            foreach (var element in partition.Elements)
            {
                if (element.Id is null || element.Serial is null || element.MappingSerials.IsDefault)
                    throw new StorageCorruptionException("Incomplete graph element metadata.");
                var key = (owner.ResourceId, partition.Kind, element.Id);
                if (immutable.TryGetValue(key, out var previous) && (previous.Type != element.Type ||
                    previous.Payload.Length != element.Payload.Length || previous.Payload.Sha256 != element.Payload.Sha256 ||
                    previous.Serial.Guid != Guid.Empty && element.Serial.Guid != Guid.Empty && previous.Serial != element.Serial))
                    throw new StorageCorruptionException("Immutable graph element identity was reused.");
                // A legacy null occurrence must not erase the known immutable serial watermark.
                immutable[key] = previous is not null && previous.Serial.Guid != Guid.Empty && element.Serial.Guid == Guid.Empty
                    ? element with { Serial = previous.Serial } : element;
                if (element.Type == (uint)DataElementType.StorageIndexDataElementData)
                {
                    var bytes = await content.ReadVerifiedAsync(element.Payload, _storage.MaxObjectBytes, cancellationToken);
                    if (!element.MappingSerials.SequenceEqual(StorageIndexMappingSerials.Read(bytes).Select(s => new SerialId(s.Guid, s.Value))))
                        throw new StorageCorruptionException("Persisted mapping serials disagree with graph bytes.");
                }
                else if (!element.MappingSerials.IsEmpty) throw new StorageCorruptionException("Mapping serials on a non-index element.");
            }
            if (partition.Kind != 0 && partition.StorageIndex is not null)
                _ = await DocumentPartition.RestoreGraphAsync(partition, content, _storage, cancellationToken);
        }
        var file = partitions.Single(p => p.Kind == 0);
        var state = owner with { Content = package, Partitions = partitions, Revisions = [], Receipts = [], RestoreReceipts = [], Publication = null };
        var graph = await DocumentPartition.RestoreFileGraphAsync(state, content, _storage, cancellationToken);
        string temporary = Path.Combine(Path.GetTempPath(), "cellbridge-recovered-package-" + Guid.NewGuid().ToString("N"));
        await using var reconstructed = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await graph.MaterializeToAsync(reconstructed, _storage.MaxDocumentBytes, cancellationToken);
        reconstructed.Position = 0;
        if (reconstructed.Length != package.Length || file.Content != package ||
            Convert.ToHexStringLower(await SHA256.HashDataAsync(reconstructed, cancellationToken)) != package.Sha256)
            throw new StorageCorruptionException("Recovered graph and materialized document disagree.");
        reconstructed.Position = 0;
        await ValidatePackageAsync(reconstructed, owner.Path, cancellationToken);
    }

    private async ValueTask ValidatePackageAsync(Stream source, string path, CancellationToken cancellationToken)
    {
        string main = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".docx" => "word/document.xml", ".xlsx" => "xl/workbook.xml", ".pptx" => "ppt/presentation.xml",
            _ => throw new NotSupportedException("Portable file recovery supports DOCX, XLSX and PPTX packages."),
        };
        CheckZipDirectory(source, portable: false);
        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.GetEntry("[Content_Types].xml") is null || zip.GetEntry(main) is null)
            throw new StorageCorruptionException("Recovered package lacks required Office members.");
        var buffer = new byte[65536]; long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StorageLimits.Check("expanded package bytes", checked(expanded + entry.Length), _storage.MaxDocumentBytes);
            await using var bytes = entry.Open(); long actual = 0; int read;
            while ((read = await bytes.ReadAsync(buffer, cancellationToken)) != 0)
            {
                actual += read; expanded = checked(expanded + read);
                StorageLimits.Check("expanded package bytes", expanded, _storage.MaxDocumentBytes);
            }
            if (actual != entry.Length) throw new StorageCorruptionException("Recovered package member length mismatch.");
        }
    }

    private static void CheckNamespace(ProviderSnapshot snapshot)
    {
        var states = snapshot.Snapshots.ToDictionary(s => (s.ResourceId, s.StateVersion));
        var heads = snapshot.Heads.ToDictionary(h => h.ResourceId, h => states[(h.ResourceId, h.StateVersion)]);
        foreach (var group in snapshot.Snapshots.GroupBy(s => s.ResourceId))
        {
            var ordered = group.OrderBy(s => s.StateVersion).ToArray();
            for (int i = 1; i < ordered.Length; i++)
            {
                var previous = ordered[i - 1]; var next = ordered[i];
                if (previous.LifecycleGeneration != next.LifecycleGeneration || previous.CreatedUtc != next.CreatedUtc ||
                    previous.ContentVersion > next.ContentVersion ||
                    previous.RetiredPathKeys.Any(k => !next.RetiredPathKeys.Contains(k)) ||
                    previous.PathKey != next.PathKey && !next.RetiredPathKeys.Contains(previous.PathKey) ||
                    previous.IsDeleted && (!next.IsDeleted || previous.DeletedFromStateVersion != next.DeletedFromStateVersion) ||
                    previous.ReplacedBy is not null && previous.ReplacedBy != next.ReplacedBy ||
                    previous.Revisions.Any(r => !next.Revisions.Any(n => n.RevisionNumber == r.RevisionNumber &&
                        JsonSerializer.Serialize(n) == JsonSerializer.Serialize(r))))
                    throw new StorageCorruptionException("Retained snapshots disagree on identity, lifecycle, history or permanent path reservations.");
            }
        }
        var parents = new Dictionary<Guid, Guid>();
        foreach (var state in heads.Values)
            if (state.ReplacedBy is { } replacement)
            {
                if (!heads.TryGetValue(replacement, out var next) || next.LifecycleGeneration != state.LifecycleGeneration + 1 ||
                    !parents.TryAdd(replacement, state.ResourceId))
                    throw new StorageCorruptionException("Incomplete or branching recovery lifecycle chain.");
            }
        bool Ancestor(Guid ancestor, Guid descendant)
        {
            var seen = new HashSet<Guid>();
            while (parents.TryGetValue(descendant, out var parent))
            {
                if (!seen.Add(descendant)) throw new StorageCorruptionException("Recovery lifecycle cycle.");
                if (parent == ancestor) return true;
                descendant = parent;
            }
            return false;
        }
        var claims = new Dictionary<string, List<DocumentState>>(StringComparer.Ordinal);
        foreach (var state in heads.Values)
            foreach (var key in state.RetiredPathKeys.Append(state.PathKey).Distinct(StringComparer.Ordinal))
            {
                if (!claims.TryGetValue(key, out var owners)) claims[key] = owners = [];
                foreach (var other in owners)
                    if (!(other.IsDeleted && other.PathKey == key && Ancestor(other.ResourceId, state.ResourceId)) &&
                        !(state.IsDeleted && state.PathKey == key && Ancestor(state.ResourceId, other.ResourceId)))
                        throw new StorageCorruptionException("Recovery namespace contains conflicting live, retired or reserved paths.");
                owners.Add(state);
            }
    }
}
