using System.Collections.Immutable;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage;

/// <summary>Trusted configuration attestation, not a serialized external permission system.</summary>
public sealed record RecoveryAuthorizationContext(string Mode, int ContractVersion, string Domain)
{
    public static RecoveryAuthorizationContext StoredGrants(string subjectDomain) => new("stored-grants", 1, subjectDomain);
    internal void Validate()
    {
        if (Mode != "stored-grants" || ContractVersion != 1 || string.IsNullOrWhiteSpace(Domain))
            throw new NotSupportedException("Recovery supports stored-grants authorization contract 1 with an explicit subject domain.");
    }
}

/// <summary>Operator confirms this binding resolves to the same external system and durable delivery receipt ledger.</summary>
public sealed record RecoveryExternalContext(Guid BindingId, string Destination, string ReceiptDomain);
public sealed record RecoveryContext(RecoveryAuthorizationContext Authorization,
    ImmutableArray<RecoveryExternalContext> ExternalDestinations);

public sealed record PortableArchiveInfo(Guid ArchiveId, int Documents, int Snapshots, int Objects, string Sha256);
public sealed record PortableRecoveryLimits
{
    public long MaxArchiveBytes { get; init; } = 12L * 1024 * 1024 * 1024;
    public long MaxExpandedBytes { get; init; } = 12L * 1024 * 1024 * 1024;
    public long MaxManifestBytes { get; init; } = 128L * 1024 * 1024;
    public int MaxEntries { get; init; } = 1_000_001;
    public int MaxSnapshots { get; init; } = 640_000;
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxArchiveBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxExpandedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxManifestBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSnapshots);
    }
}

internal sealed record PortableManifest(int ArchiveVersion, Guid ArchiveId, RecoveryContext Context,
    ProviderSnapshot State, ImmutableArray<ContentHandle> Objects)
{
    public const int CurrentVersion = 1;
}
