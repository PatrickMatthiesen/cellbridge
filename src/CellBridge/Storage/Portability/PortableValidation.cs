using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage;

public sealed partial class PortableArchive
{
    private async ValueTask ValidateStateAsync(PortableManifest manifest, IContentStore content, CancellationToken ct)
    {
        try
        {
            foreach (var snapshot in manifest.State.Snapshots)
            {
                ct.ThrowIfCancellationRequested();
                CheckStateShape(snapshot);
                NormalizedDocumentRecovery.Validate(snapshot, _storage);
                foreach (var reference in snapshot.ContentReferences)
                {
                    await using var bytes = await content.OpenReadAsync(reference.Handle, ct);
                    await CopyVerifiedAsync(bytes, Stream.Null, reference.Handle, ct);
                }
                var source = new NormalizedSnapshotGraphSource(snapshot, content, _storage);
                await ValidateIndexesAsync(snapshot, source, ct);
                // Check every logical publication, including intermediate protocol revisions.
                foreach (var root in snapshot.MappingRoots)
                {
                    ct.ThrowIfCancellationRequested();
                    var mappings = source.Mappings(root.Scope, root.Id);
                    var mappedManifest = mappings.SingleOrDefault(value => value.Key.Kind == IndexedMappingKind.Manifest && !NullTarget(value.Target));
                    if (mappedManifest is null)
                    {
                        if (root.Scope.TargetPartition != 1 || mappings.Any(value => !NullTarget(value.Target)) ||
                            source.Bindings(root.Scope,root.Sequence).Length != 0)
                            throw new StorageCorruptionException("A retained graph has no storage manifest.");
                        continue;
                    }
                    var storage = snapshot.StorageManifests.Single(value => value.Scope == root.Scope && value.ElementId == mappedManifest.Target);
                    foreach (var declared in storage.Roots)
                        if (!mappings.Any(value => value.Key.Kind == IndexedMappingKind.Cell && value.Key.Cell == declared.Cell && !NullTarget(value.Target)))
                            throw new StorageCorruptionException("An unmapped storage root cell was retained.");
                    var bindings = ImmutableArray.CreateBuilder<IndexedObjectBinding>();
                    foreach (var mapping in mappings.Where(value => value.Key.Kind == IndexedMappingKind.Cell && !NullTarget(value.Target)))
                    {
                        var cell = mapping.Key.Cell ?? throw new StorageCorruptionException("Missing cell identity.");
                        var metadata = snapshot.Cells.Single(value => value.Scope == root.Scope && value.ElementId == mapping.Target);
                        var resolved = root.Scope.TargetPartition == 0
                            ? await IndexedDocumentGraph.ResolveFileCellAsync(root.Scope, root.Id, cell, metadata.RevisionId, source,
                                new(MaxElements: _storage.MaxGraphElements, MaxPayloadBytes: _storage.MaxGraphBytes), ct)
                            : await IndexedDocumentGraph.ResolveCellAsync(root.Scope, root.Id, cell, metadata.RevisionId, source,
                                new(MaxElements: _storage.MaxGraphElements, MaxPayloadBytes: _storage.MaxGraphBytes), ct);
                        bindings.AddRange(resolved.Bindings);
                    }
                    Equal(IndexedDocumentGraph.Checkpoint(root.Scope, root.Sequence, bindings),
                        IndexedDocumentGraph.Checkpoint(root.Scope, root.Sequence, source.Bindings(root.Scope, root.Sequence)));
                }
                foreach (var revision in snapshot.ApplicationRevisions)
                {
                    if (revision.RevisionNumber == 0 || revision.ContentVersion == 0 || revision.Author is null ||
                        string.IsNullOrWhiteSpace(revision.Author.Subject) || revision.CreatedUtc.Kind != DateTimeKind.Utc ||
                        revision.Package.RevisionNumber != revision.RevisionNumber || revision.Partitions.Length != 2 ||
                        !revision.Partitions.Select(partition => partition.Kind).Order().SequenceEqual(new[] { 0, 1 }))
                        throw new StorageCorruptionException("Invalid immutable application revision.");
                    foreach (var partition in revision.Partitions)
                    {
                        var scope = new IndexedGraphScope(revision.ResourceId, revision.LifecycleGeneration, partition.Kind);
                        if (partition.StorageIndex is { } index && !snapshot.MappingRoots.Any(root => root.Scope == scope && root.Id == index &&
                            root.Sequence == partition.PublicationSequence && root.Serial == partition.StorageIndexSerial))
                            throw new StorageCorruptionException("An application revision selects another graph publication.");
                    }
                    await using var package = await source.PackageReader().OpenReadAsync(revision.Package, ct);
                    await ValidatePackageAsync(package, snapshot.Current.Head.Path, ct);
                }
                foreach (var receipt in snapshot.Receipts)
                {
                    if (string.IsNullOrWhiteSpace(receipt.Operation.OperationKey) || receipt.Operation.PartitionKind is < 0 or > 2 ||
                        !snapshot.ApplicationRevisions.Any(revision => revision.RevisionNumber == receipt.RevisionNumber && revision.ContentVersion == receipt.ContentVersion))
                        throw new StorageCorruptionException("A retained receipt has no matching application revision.");
                    if (receipt.Response is null) continue;
                    var reader = new BinaryReaderEx(await content.ReadVerifiedAsync(receipt.Response, _storage.MaxObjectBytes, ct));
                    var response = FsshttpbResponse.Deserialize(reader);
                    if (reader.Remaining != 0 || response.SubResponses.Count != 1 || response.SubResponses[0].Status ||
                        response.SubResponses[0].RequestType != RequestTypes.PutChanges)
                        throw new StorageCorruptionException("A retained protocol response is malformed.");
                }
                var binding = snapshot.Current.Head.Delivery;
                if (binding is not null && (binding.BindingId == Guid.Empty || string.IsNullOrWhiteSpace(binding.Destination) ||
                    string.IsNullOrWhiteSpace(binding.ExpectedRevision) || binding.NextSequence <= 0))
                    throw new StorageCorruptionException("Invalid external delivery binding.");
                foreach (var delivery in snapshot.Deliveries)
                    if (binding is null || delivery.OperationId == Guid.Empty || delivery.Sequence <= 0 || delivery.Sequence >= binding.NextSequence ||
                        !snapshot.ApplicationRevisions.Any(revision => revision.Package == delivery.Package && revision.ContentVersion == delivery.ContentVersion))
                        throw new StorageCorruptionException("A delivery has no matching immutable application revision or binding.");
            }
            CheckNamespace(manifest.State);
        }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or FormatException or ArgumentException or
            OverflowException or InvalidOperationException or NullReferenceException)
        { throw new StorageCorruptionException("Recovery state or graph is corrupt or unsupported.", error); }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(actual))
            throw new StorageCorruptionException("An indexed recovery record disagrees with its immutable graph bytes.");
    }

    private async ValueTask ValidateIndexesAsync(NormalizedDocumentSnapshot snapshot, NormalizedSnapshotGraphSource source, CancellationToken ct)
    {
        var revisions = snapshot.GraphRevisions.ToDictionary(value => (value.Scope, value.ElementId));
        var cells = snapshot.Cells.ToDictionary(value => (value.Scope, value.ElementId));
        var manifests = snapshot.StorageManifests.ToDictionary(value => (value.Scope, value.ElementId));
        var objects = snapshot.Objects.ToDictionary(value => (value.Scope, value.GroupElement, value.Ordinal));
        var elements = snapshot.Elements.ToDictionary(value => (value.Scope, value.Element.Id), value => value.Element);
        foreach (var item in snapshot.Elements)
        {
            var element = await source.ReadElementAsync(item.Scope, item.Element.Id, ct);
            if (element.DataElementType is not (DataElementType.StorageManifestDataElementData or DataElementType.CellManifestDataElementData or
                DataElementType.RevisionManifestDataElementData or DataElementType.ObjectGroupDataElementData or DataElementType.ObjectDataBLOBDataElementData) ||
                !item.Element.MappingSerials.IsEmpty)
                throw new StorageCorruptionException("Transient or unsupported canonical graph element.");
            var parsed = IndexedDocumentGraph.ReadMetadata(item.Scope, element);
            if (parsed.Revision is { } revision)
            {
                if (!revisions.Remove((item.Scope, revision.ElementId), out var actual)) throw new StorageCorruptionException("Missing revision index.");
                Equal(revision, actual);
                foreach (var group in revision.ObjectGroups)
                    if (!elements.TryGetValue((item.Scope, group), out var descriptor) || descriptor.Type != (uint)DataElementType.ObjectGroupDataElementData)
                        throw new StorageCorruptionException("A revision references a missing object group.");
            }
            if (parsed.CellManifest is { } cell)
            { if (!cells.Remove((item.Scope, cell.ElementId), out var actual)) throw new StorageCorruptionException("Missing cell index."); Equal(cell, actual); }
            if (parsed.StorageManifest is { } manifest)
            { if (!manifests.Remove((item.Scope, manifest.ElementId), out var actual)) throw new StorageCorruptionException("Missing manifest index."); Equal(manifest, actual); }
            foreach (var obj in parsed.Objects)
            {
                if (!objects.Remove((item.Scope, obj.GroupElement, obj.Ordinal), out var actual)) throw new StorageCorruptionException("Missing object index.");
                Equal(obj, actual);
                if (obj.BlobElement is { } blob && (!elements.TryGetValue((item.Scope, blob), out var descriptor) ||
                    descriptor.Type != (uint)DataElementType.ObjectDataBLOBDataElementData))
                    throw new StorageCorruptionException("An object references a missing BLOB.");
            }
        }
        if (revisions.Count + cells.Count + manifests.Count + objects.Count != 0)
            throw new StorageCorruptionException("Graph indexes are absent from physical graph bytes.");
        var claims = snapshot.MappingClaims.ToDictionary(value => (value.Scope, value.Serial));
        foreach (var interval in snapshot.MappingIntervals)
        {
            var value = interval.Mapping;
            var expected = value.Key.Kind switch
            {
                IndexedMappingKind.Manifest => DataElementType.StorageManifestDataElementData,
                IndexedMappingKind.Cell => DataElementType.CellManifestDataElementData,
                IndexedMappingKind.Revision => DataElementType.RevisionManifestDataElementData,
                _ => throw new StorageCorruptionException("Unknown mapping kind."),
            };
            if (!NullTarget(value.Target) && (!elements.TryGetValue((value.Scope, value.Target), out var descriptor) || descriptor.Type != (uint)expected ||
                value.Key.Kind == IndexedMappingKind.Cell && value.Key.Cell is null ||
                value.Key.Kind == IndexedMappingKind.Revision && !snapshot.GraphRevisions.Any(revision => revision.Scope == value.Scope &&
                    revision.ElementId == value.Target && revision.RevisionId == value.Key.Revision)))
                throw new StorageCorruptionException("A mapping disagrees with its immutable target.");
            if ((value.Serial.Guid != Guid.Empty || value.Serial.Value != 0) &&
                (!claims.TryGetValue((value.Scope, value.Serial), out var claim) || claim.Key != value.Key || claim.Target != value.Target))
                throw new StorageCorruptionException("A mapping has no matching immutable serial claim.");
        }
    }

    private static bool NullTarget(ExtendedId id) => id.Guid == Guid.Empty && id.Value == 0;

    private static void CheckStateShape(NormalizedDocumentSnapshot snapshot)
    {
        var head = snapshot.Current.Head;
        var coordination = snapshot.Current.Coordination;
        if (head.ResourceId == Guid.Empty || head.StateVersion < 0 || head.ContentVersion == 0 || head.LifecycleGeneration < 1 ||
            !CanonicalPath(head.Path) || head.PathKey != StorageIds.PathKey(head.Path) || head.CreatedUtc.Kind != DateTimeKind.Utc ||
            head.ModifiedUtc.Kind != DateTimeKind.Utc || head.Security.Grants is null || head.Security.Grants.Any(grant =>
                string.IsNullOrWhiteSpace(grant.Key) || (grant.Value & ~(DocumentAccess.Read | DocumentAccess.Write)) != 0) ||
            coordination.SchemaOwners.IsDefault || coordination.CoauthorClients.IsDefault || coordination.Generation < 0 ||
            !snapshot.Partitions.Select(partition => partition.Head.Kind).Order().SequenceEqual(new[] { 0, 1, 2 }) ||
            head.IsDeleted != head.DeletedFromStateVersion.HasValue || head.DeletedFromStateVersion >= head.StateVersion ||
            head.DeletedFromStateVersion < 0 || head.ReplacedBy is not null && !head.IsDeleted ||
            snapshot.PathClaims.Any(path => !CanonicalPath(path) || path != StorageIds.PathKey(path)))
            throw new StorageCorruptionException("Invalid recovery document shape or lifecycle.");
        foreach (var editor in snapshot.Current.Editors)
            if (editor.ClientId == Guid.Empty || editor.Metadata is null || editor.Metadata.Any(value => value.Value.IsDefault) ||
                editor.ExpiresUtc.Kind != DateTimeKind.Utc || editor.JoinedUtc.Kind != DateTimeKind.Utc || editor.LastSeenUtc.Kind != DateTimeKind.Utc)
                throw new StorageCorruptionException("Invalid editor state.");
        foreach (var lease in coordination.SchemaOwners.Concat(coordination.Exclusive is null ? [] : new[] { coordination.Exclusive }))
            if (string.IsNullOrWhiteSpace(lease.Id) || lease.ExpiresUtc.Kind != DateTimeKind.Utc) throw new StorageCorruptionException("Invalid lease state.");
        if (coordination.HostLock is { } host && (string.IsNullOrWhiteSpace(host.Token) || string.IsNullOrWhiteSpace(host.OwnerSubject) || host.ExpiresUtc.Kind != DateTimeKind.Utc))
            throw new StorageCorruptionException("Invalid host lease state.");
    }

    private static bool CanonicalPath(string path) => !string.IsNullOrWhiteSpace(path) && path.StartsWith('/') &&
        !path.Contains('\\') && !path.Any(char.IsControl) && path.Split('/').Skip(1).All(segment => segment.Length > 0 && segment is not "." and not "..");

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
        var documents = snapshot.Snapshots.ToDictionary(value => value.Current.Head.ResourceId);
        var parents = new Dictionary<Guid, Guid>();
        foreach (var document in documents.Values)
        {
            var head = document.Current.Head;
            if (head.ReplacedBy is { } replacement && (!head.IsDeleted || !documents.TryGetValue(replacement, out var next) ||
                next.Current.Head.LifecycleGeneration != head.LifecycleGeneration + 1 || !parents.TryAdd(replacement, head.ResourceId)))
                throw new StorageCorruptionException("Incomplete or branching recovery lifecycle chain.");
            if (head.ReplacedBy is null && !document.PathClaims.Contains(head.PathKey))
                throw new StorageCorruptionException("A current document has no filename claim.");
        }
        foreach (var id in documents.Keys)
        {
            var seen = new HashSet<Guid>(); var current = id;
            while (parents.TryGetValue(current, out var parent))
            { if (!seen.Add(current)) throw new StorageCorruptionException("Recovery lifecycle cycle."); current = parent; }
        }
        foreach (var document in documents.Values)
        {
            var original = document.Current.Head;
            var owner = document;
            while (!owner.PathClaims.Contains(original.PathKey) && owner.Current.Head.ReplacedBy is { } replacement)
                owner = documents[replacement];
            if (!owner.PathClaims.Contains(original.PathKey))
                throw new StorageCorruptionException("A recreated document lost its permanent filename claim.");
        }
        // Recreate transfers the current claim; retired ancestors retain their other reserved names.
        var claims = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents.Values)
            foreach (var path in document.PathClaims)
                if (!claims.Add(path)) throw new StorageCorruptionException("Recovery has conflicting filename claims.");
    }
}
