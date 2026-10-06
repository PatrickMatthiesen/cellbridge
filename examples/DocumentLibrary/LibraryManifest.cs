using System.Collections.Immutable;
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
}
