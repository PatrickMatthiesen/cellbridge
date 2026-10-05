using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CellBridge.Storage.Abstractions;

public sealed record RecoveryHead(Guid ResourceId, long StateVersion);
public sealed record RecoveryReceipt(Guid OperationId, string Fingerprint, int Documents, int Snapshots);
public sealed record ProviderSnapshot(ImmutableArray<RecoveryHead> Heads,
    ImmutableArray<DocumentState> Snapshots, ImmutableArray<RecoveryReceipt> Receipts);

/// <summary>Optional whole-provider recovery. Import publishes all heads and its receipt atomically into an empty namespace.</summary>
public interface IProviderRecoveryStore
{
    ValueTask CheckRecoveryAsync(CancellationToken cancellationToken = default);
    ValueTask<ProviderSnapshot> CaptureRecoveryAsync(CancellationToken cancellationToken = default);
    ValueTask<RecoveryReceipt?> FindRecoveryReceiptAsync(Guid operationId, CancellationToken cancellationToken = default);
    ValueTask<RecoveryReceipt> ImportRecoveryAsync(ProviderSnapshot snapshot, RecoveryReceipt receipt,
        CancellationToken cancellationToken = default);
}

public sealed class RecoveryConflictException(string message) : IOException(message);

/// <summary>Recovery rejects unknown fields rather than silently dropping newer persisted semantics.</summary>
public static class RecoveryJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            MaxDepth = 64,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static T Read<T>(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 64 });
            CheckDuplicates(json.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new JsonException("Null recovery record.");
        }
        catch (JsonException ex) { throw new StorageCorruptionException("Unsupported or invalid recovery JSON.", ex); }
    }

    private static void CheckDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate recovery property.");
                CheckDuplicates(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) CheckDuplicates(item);
    }
}

public static class ProviderRecovery
{
    public static void CheckReceipt(RecoveryReceipt actual, RecoveryReceipt expected)
    {
        if (actual != expected) throw new RecoveryConflictException("Recovery operation identity was reused with different input or context.");
    }

    public static void CheckAdmission(ProviderSnapshot snapshot, StorageLimits limits, DateTime now, bool requireExpired = true)
    {
        if (snapshot.Heads.IsDefault || snapshot.Snapshots.IsDefault || snapshot.Receipts.IsDefault)
            throw new StorageCorruptionException("Missing recovery collections.");
        StorageLimits.Check("recovery documents", snapshot.Heads.Length, limits.MaxDocuments);
        if (snapshot.Heads.Select(h => h.ResourceId).Distinct().Count() != snapshot.Heads.Length ||
            snapshot.Snapshots.Select(s => (s.ResourceId, s.StateVersion)).Distinct().Count() != snapshot.Snapshots.Length)
            throw new StorageCorruptionException("Duplicate recovery identity.");
        var heads = snapshot.Heads.ToDictionary(h => h.ResourceId);
        var groups = snapshot.Snapshots.GroupBy(s => s.ResourceId).ToDictionary(g => g.Key, g => g.ToArray());
        if (heads.Count != groups.Count) throw new StorageCorruptionException("Recovery heads and snapshots disagree.");
        foreach (var head in heads.Values)
        {
            if (!groups.TryGetValue(head.ResourceId, out var states) || head.StateVersion != states.Max(s => s.StateVersion))
                throw new StorageCorruptionException("Missing or stale recovery head.");
            StorageLimits.Check("retained state snapshots", states.Length, limits.MaxRetainedStateSnapshots);
            foreach (var state in states)
            {
                // Keep this guard effective when the parallel host-policy contract becomes a known codec field.
                // Recovery v1 cannot coordinate backups of that host-owned permission store.
                var security = JsonSerializer.SerializeToElement(state.Security);
                if (security.TryGetProperty("AuthorizationPolicy", out var binding) && binding.ValueKind != JsonValueKind.Null)
                    throw new NotSupportedException("Portable recovery of externally bound authorization state is unsupported.");
                limits.CheckDocument(state);
                foreach (var revision in state.Revisions)
                    limits.CheckDocument(state with { Content = revision.Content, Partitions = revision.Partitions,
                        Revisions = [], Receipts = [], RestoreReceipts = [], Publication = null });
            }
            var current = states.Single(s => s.StateVersion == head.StateVersion);
            if (requireExpired && (current.Editors.Any(e => e.ExpiresUtc > now) || current.Coordination.SchemaOwners.Any(l => l.ExpiresUtc > now) ||
                current.Coordination.Exclusive is { } exclusive && exclusive.ExpiresUtc > now ||
                current.Coordination.HostLock is { } host && host.ExpiresUtc > now))
                throw new RecoveryConflictException("Recovery requires expired editor sessions and leases. Preserve the archive and retry after expiry.");
        }
        if (snapshot.Receipts.Length > 1000 || snapshot.Receipts.Select(r => r.OperationId).Distinct().Count() != snapshot.Receipts.Length ||
            snapshot.Receipts.Any(r => r.OperationId == Guid.Empty || r.Fingerprint.Length != 64 ||
                r.Fingerprint.Any(c => !char.IsAsciiHexDigit(c)) || r.Documents < 0 || r.Snapshots < 0))
            throw new StorageCorruptionException("Invalid recovery receipt ledger.");
    }
}
