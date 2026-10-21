using System.Collections.Immutable;
using System.Text.Json.Serialization;
using CellBridge.Storage.Abstractions;

namespace CellBridge.DocumentLibrary;

public sealed record LibraryPermissionSnapshot(long Revision,
    ImmutableDictionary<string, DocumentAccess> Decisions);

public sealed record DeliveryFingerprint(Guid BindingId, string Destination, string ExpectedRevision,
    Guid OperationId, Guid ResourceId, long LifecycleGeneration, long Sequence, uint ContentVersion,
    string ContentKey, long Length, string Sha256)
{
    public static DeliveryFingerprint From(ExternalDeliveryRequest request) => new(
        request.BindingId,
        request.Destination,
        request.ExpectedRevision,
        request.Revision.OperationId,
        request.Revision.ResourceId,
        request.Revision.LifecycleGeneration,
        request.Revision.Sequence,
        request.Revision.ContentVersion,
        request.Revision.Content.Key,
        request.Revision.Content.Length,
        request.Revision.Content.Sha256);
}

public sealed record DeliveryReceipt(DeliveryFingerprint Fingerprint, string ResultingRevision,
    DateTimeOffset AppliedUtc);

public sealed record LibraryManifest(
    int FormatVersion,
    Guid ResourceId,
    Guid BindingId,
    string DestinationId,
    string FileName,
    string CellBridgePath,
    bool CellBridgeBound,
    string CurrentRevision,
    string CurrentFile,
    long Length,
    string Sha256,
    DateTimeOffset ModifiedUtc,
    long DesiredPermissionRevision,
    ImmutableDictionary<long, LibraryPermissionSnapshot> PermissionSnapshots,
    ImmutableDictionary<Guid, DeliveryReceipt> Receipts)
{
    public const int CurrentFormat = 1;

    // Legacy snapshots remain intact when authority moves to operation-time checks.
    public ImmutableDictionary<string, DocumentAccess>? CurrentPermissions { get; init; }

    // A staged interactive upload cannot be imported by recovery before Create succeeds.
    public bool PendingCreation { get; init; }

    [JsonIgnore]
    public ImmutableDictionary<string, DocumentAccess> Permissions =>
        CurrentPermissions ?? PermissionSnapshots[DesiredPermissionRevision].Decisions;
}
