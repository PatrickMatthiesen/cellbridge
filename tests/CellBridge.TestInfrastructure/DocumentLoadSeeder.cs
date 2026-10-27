using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.TestInfrastructure;

/// <summary>Untimed load setup for an explicitly owned, empty disposable document namespace.</summary>
public static class DocumentLoadSeeder
{
    /// <summary>
    /// Publishes independent document identities in one recovery import. The template's immutable
    /// content must already exist in the destination content store; no development data is selected.
    /// </summary>
    public static async Task<IReadOnlyList<DocumentState>> SeedAsync(StorageProvider provider,
        DocumentState template, IReadOnlyList<(string Path, SubjectIdentity Owner)> documents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(documents);
        cancellationToken.ThrowIfCancellationRequested();
        if (provider.State is not IProviderRecoveryStore recovery)
            throw new NotSupportedException("Document load seeding requires provider recovery support.");
        if (template.IsDeleted || template.DeletedFromStateVersion is not null || template.ReplacedBy is not null ||
            template.Publication is not null || template.Security.AuthorizationPolicy is not null ||
            !template.Editors.IsEmpty || !template.Receipts.IsEmpty || !template.RestoreReceipts.IsEmpty ||
            !template.WholeFileSaveReceipts.IsEmpty || !template.RetiredPathKeys.IsEmpty ||
            template.Coordination != CoordinationState.Empty || template.ContentVersion == 0)
            throw new ArgumentException("The load template must have no sessions, locks, receipts, bindings or retired lifecycle state.", nameof(template));
        StorageLimits.Check("recovery documents", documents.Count, provider.Limits.MaxDocuments);

        var states = ImmutableArray.CreateBuilder<DocumentState>(documents.Count);
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
            var key = StorageIds.PathKey(path);
            if (!paths.Add(key)) throw new ArgumentException("Load document paths must be unique.", nameof(documents));
            var state = template with
            {
                ResourceId = Guid.NewGuid(), Path = path, PathKey = key, StateVersion = 0,
                LifecycleGeneration = 1, Security = DocumentSecurity.Create(owner), Revisions = [],
            };
            // Graph identities, knowledge and content remain immutable. History belongs to the new document and owner.
            states.Add(RevisionHistory.Initialize(state));
        }
        var snapshot = new ProviderSnapshot(states.Select(state => new RecoveryHead(state.ResourceId, state.StateVersion))
            .ToImmutableArray(), states.MoveToImmutable(), []);
        ProviderRecovery.CheckAdmission(snapshot, provider.Limits, DateTime.UtcNow);

        await recovery.CheckRecoveryAsync(cancellationToken);
        var existing = await recovery.CaptureRecoveryAsync(cancellationToken);
        if (!existing.Heads.IsEmpty || !existing.Snapshots.IsEmpty || !existing.Receipts.IsEmpty)
            throw new RecoveryConflictException("Document load seeding requires an empty disposable namespace and recovery ledger.");
        // Validate shared template content once, rather than rereading identical handles for every clone.
        var restored = await StoredDocument.RestoreAsync(template, provider.Content, cancellationToken: cancellationToken);
        if (!restored.FilePartition.FileGraph.AnalyzeRetention().Complete)
            throw new InvalidDataException("The load template graph has unresolved retention requirements.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(template, RecoveryJson.Options));
        foreach (var state in snapshot.Snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { state.ResourceId, state.Path, state.Security }));
        }
        var receipt = new RecoveryReceipt(Guid.NewGuid(), Convert.ToHexString(hash.GetHashAndReset()),
            snapshot.Heads.Length, snapshot.Snapshots.Length);
        ProviderRecovery.CheckAdmission(snapshot with { Receipts = [receipt] }, provider.Limits, DateTime.UtcNow);
        var accepted = await recovery.ImportRecoveryAsync(snapshot, receipt, cancellationToken);
        ProviderRecovery.CheckReceipt(accepted, receipt);
        return snapshot.Snapshots;
    }
}
