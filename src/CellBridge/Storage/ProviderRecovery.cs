using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CellBridge.Storage.Abstractions;

public sealed record RecoveryHead(Guid ResourceId, long StateVersion);
public sealed record RecoveryReceipt(Guid OperationId, string Fingerprint, int Documents, int Snapshots);
public sealed record ProviderSnapshot(ImmutableArray<NormalizedDocumentSnapshot> Snapshots, ImmutableArray<RecoveryReceipt> Receipts)
{
    [JsonIgnore]
    public ImmutableArray<RecoveryHead> Heads => Snapshots.Select(snapshot =>
        new RecoveryHead(snapshot.Current.Head.ResourceId, snapshot.Current.Head.StateVersion)).ToImmutableArray();
}

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
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
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

    /// <summary>Reads stored JSON text through a validated document.</summary>
    public static T Read<T>(string text)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(text);
            using var json = ParseRawJson(text);
            return json.RootElement.Deserialize<T>(Options) ?? throw new JsonException("Null recovery record.");
        }
        catch (Exception ex) when (ex is JsonException or EncoderFallbackException)
        { throw new StorageCorruptionException("Unsupported or invalid recovery JSON.", ex); }
    }

    private static JsonDocument ParseRawJson(string text)
    {
        try { return JsonDocument.Parse(text, new() { MaxDepth = 64, AllowDuplicateProperties = false }); }
        catch (InvalidOperationException ex) { throw new JsonException("Invalid recovery JSON text.", ex); }
    }

    public static T Read<T>(ReadOnlySpan<byte> bytes)
    {
        try
        {
            // Validate raw input before converters can skip ignored properties or subtrees.
            _ = StrictUtf8.GetCharCount(bytes);
            ValidateRawJson(bytes);
            return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new JsonException("Null recovery record.");
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        { throw new StorageCorruptionException("Unsupported or invalid recovery JSON.", ex); }
    }

    private static void ValidateRawJson(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 64, AllowDuplicateProperties = false });
        }
        catch (InvalidOperationException ex) { throw new JsonException("Invalid recovery JSON text.", ex); }
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
        if (snapshot.Snapshots.IsDefault || snapshot.Receipts.IsDefault)
            throw new StorageCorruptionException("Missing recovery collections.");
        if (snapshot.Snapshots.Select(document => document.Current.Head.ResourceId).Distinct().Count() != snapshot.Snapshots.Length)
            throw new StorageCorruptionException("Duplicate recovery document identity.");
        foreach (var document in snapshot.Snapshots)
        {
            NormalizedDocumentRecovery.Validate(document, limits);
            if (document.Current.Head.Security.AuthorizationPolicy is not null)
                throw new NotSupportedException("Portable recovery cannot back up the host's external permission store.");
            if (requireExpired && (document.Current.Editors.Any(editor => editor.ExpiresUtc > now) ||
                document.Current.Coordination.SchemaOwners.Any(lease => lease.ExpiresUtc > now) ||
                document.Current.Coordination.Exclusive is { } exclusive && exclusive.ExpiresUtc > now ||
                document.Current.Coordination.HostLock is { } host && host.ExpiresUtc > now))
                throw new RecoveryConflictException("Recovery requires expired editor sessions and leases. Preserve the archive and retry after expiry.");
        }
        if (snapshot.Receipts.Length > 1000 || snapshot.Receipts.Select(receipt => receipt.OperationId).Distinct().Count() != snapshot.Receipts.Length ||
            snapshot.Receipts.Any(receipt => receipt.OperationId == Guid.Empty || receipt.Fingerprint.Length != 64 ||
                receipt.Fingerprint.Any(value => !char.IsAsciiHexDigit(value)) || receipt.Documents < 0 || receipt.Snapshots < 0))
            throw new StorageCorruptionException("Invalid recovery receipt ledger.");
    }
}
