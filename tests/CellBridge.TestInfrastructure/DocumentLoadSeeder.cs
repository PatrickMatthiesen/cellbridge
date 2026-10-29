using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.TestInfrastructure;

/// <summary>Untimed setup for an explicitly owned, empty disposable document namespace.</summary>
public static class DocumentLoadSeeder
{
    /// <summary>Clones validated protocol records and immutable content handles, then imports all owned documents atomically.</summary>
    public static async Task<IReadOnlyList<DocumentHead>> SeedAsync(StorageProvider provider,
        NormalizedDocumentSnapshot template, IReadOnlyList<(string Path, SubjectIdentity Owner)> documents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(documents);
        cancellationToken.ThrowIfCancellationRequested();
        NormalizedDocumentRecovery.Validate(template, provider.Limits);
        var head = template.Current.Head;
        if (head.IsDeleted || head.DeletedFromStateVersion is not null || head.ReplacedBy is not null || head.Delivery is not null ||
            head.Security.AuthorizationPolicy is not null || !template.Current.Editors.IsEmpty ||
            template.Current.Coordination != CoordinationState.Empty || head.ContentVersion == 0)
            throw new ArgumentException("The load template must have no sessions, leases, external binding, or retired lifecycle state.", nameof(template));
        var snapshots = ImmutableArray.CreateBuilder<NormalizedDocumentSnapshot>(documents.Count);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (requestedPath, owner) in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentException.ThrowIfNullOrWhiteSpace(owner.Subject);
            var path = DocumentStore.NormalizeUrl(requestedPath);
            if (path.Contains('\\') || path.Any(char.IsControl) ||
                path.Split('/').Skip(1).Any(segment => segment.Length == 0 || segment is "." or ".."))
                throw new ArgumentException("Load documents require canonical document paths.", nameof(documents));
            if (!paths.Add(StorageIds.PathKey(path))) throw new ArgumentException("Load document paths must be unique.", nameof(documents));
            snapshots.Add(Clone(template, path, owner));
        }
        var recovery = provider.Documents;
        await recovery.CheckRecoveryAsync(cancellationToken);
        var existing = await recovery.CaptureRecoveryAsync(cancellationToken);
        if (!existing.Snapshots.IsEmpty || !existing.Receipts.IsEmpty)
            throw new RecoveryConflictException("Document load seeding requires an empty disposable namespace and recovery ledger.");
        var snapshot = new ProviderSnapshot(snapshots.MoveToImmutable(), []);
        ProviderRecovery.CheckAdmission(snapshot, provider.Limits, DateTime.UtcNow);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(template, RecoveryJson.Options));
        foreach (var document in snapshot.Snapshots)
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { document.Current.Head.ResourceId, document.Current.Head.Path, document.Current.Head.Security }));
        var receipt = new RecoveryReceipt(Guid.NewGuid(), Convert.ToHexStringLower(hash.GetHashAndReset()),
            snapshot.Snapshots.Length, snapshot.Snapshots.Length);
        ProviderRecovery.CheckReceipt(await recovery.ImportRecoveryAsync(snapshot, receipt, cancellationToken), receipt);
        return snapshot.Snapshots.Select(document => document.Current.Head).ToArray();
    }

    private static NormalizedDocumentSnapshot Clone(NormalizedDocumentSnapshot template, string path, SubjectIdentity owner)
    {
        var resourceId = Guid.NewGuid();
        IndexedGraphScope Scope(IndexedGraphScope scope) => scope with { ResourceId = resourceId, LifecycleGeneration = 1 };
        IndexedBindingKey Key(IndexedBindingKey key) => key with { Scope = Scope(key.Scope) };
        IndexedObjectBinding Binding(IndexedObjectBinding binding) => binding with { Key = Key(binding.Key) };
        DocumentPackage Package(DocumentPackage package) => package with
        { ResourceId = resourceId, LifecycleGeneration = 1, RevisionNumber = 1, Root = package.Root with { Scope = Scope(package.Root.Scope) } };
        var package = Package(template.Current.Head.Package);
        var head = template.Current.Head with
        {
            ResourceId = resourceId, Path = path, PathKey = StorageIds.PathKey(path), StateVersion = 0, LifecycleGeneration = 1,
            AuthorityGeneration = 0, CoordinationGeneration = 0, Package = package, LatestRevisionNumber = 1, Security = DocumentSecurity.Create(owner),
            NextEditorExpiryUtc = null, ActiveEditors = 0, Counters = new()
            {
                GraphElements = template.Elements.Length, GraphBytes = template.Elements.Sum(element => element.Element.Payload.Length), Revisions = 1,
            },
        };
        var revision = new DocumentRevisionRecord(resourceId, 1, 1, head.ContentVersion, head.ModifiedUtc, owner, package,
            template.Current.Partitions.Where(partition => partition.Kind != 2).ToImmutableArray());
        var deltas = template.BindingDeltas.Select(delta =>
        {
            var changes = delta.Changes.Select(change => new IndexedBindingChange(Key(change.Key), change.Value is { } value ? Binding(value) : null)).ToImmutableArray();
            return delta with { Scope = Scope(delta.Scope), Changes = changes, EncodedBytes = JsonSerializer.SerializeToUtf8Bytes(changes).LongLength };
        }).ToImmutableArray();
        var content = ContentStoreReader.UniqueHandles(template.Elements.Select(element => element.Element.Payload))
            .Select(handle => new NormalizedContentReference(handle, 1)).ToImmutableArray();
        var result = template with
        {
            Current = template.Current with { Head = head, Coordination = CoordinationState.Empty, Editors = [] },
            Elements = template.Elements.Select(element => element with { Scope = Scope(element.Scope) }).ToImmutableArray(),
            Visibility = template.Visibility.Select(value => value with { Scope = Scope(value.Scope) }).ToImmutableArray(),
            GraphRevisions = template.GraphRevisions.Select(value => value with { Scope = Scope(value.Scope) }).ToImmutableArray(),
            Objects = template.Objects.Select(value => value with { Scope = Scope(value.Scope) }).ToImmutableArray(),
            Cells = template.Cells.Select(value => value with { Scope = Scope(value.Scope) }).ToImmutableArray(),
            StorageManifests = template.StorageManifests.Select(value => value with { Scope = Scope(value.Scope) }).ToImmutableArray(),
            MappingIntervals = template.MappingIntervals.Select(value => value with { Mapping = value.Mapping with { Scope = Scope(value.Mapping.Scope) } }).ToImmutableArray(),
            MappingRoots = template.MappingRoots.Select(value => value with { Scope = Scope(value.Scope) }).ToImmutableArray(),
            MappingClaims = template.MappingClaims.Select(value => value with { Scope = Scope(value.Scope) }).ToImmutableArray(),
            CurrentBindings = template.CurrentBindings.Select(Binding).ToImmutableArray(), BindingDeltas = deltas,
            Checkpoints = template.Checkpoints.Select(value => IndexedDocumentGraph.Checkpoint(Scope(value.Scope), value.Sequence, value.Bindings.Select(Binding))).ToImmutableArray(),
            ApplicationRevisions = [revision], Receipts = [], Deliveries = [], PathClaims = [head.PathKey], ContentReferences = content,
        };
        head = head with { Counters = head.Counters with { StoredBytes = NormalizedDocumentRecovery.StoredBytes(result) } };
        return result with { Current = result.Current with { Head = head } };
    }
}
