using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public sealed record RestoreRevisionResult(DocumentRevision Revision, bool IsReplay);

public sealed partial class CellBridgeDocumentService
{
    public async ValueTask<IReadOnlyList<DocumentRevision>> ListRevisionsAsync(Guid resourceId, CellBridgeActor actor,
        CancellationToken cancellationToken = default)
    {
        var state = await CurrentAsync(resourceId, cancellationToken);
        Demand(actor, state, DocumentAccess.Read);
        return RevisionHistory.Initialize(state).Revisions.Reverse().ToArray();
    }

    public async ValueTask<DocumentRevision> GetRevisionAsync(Guid resourceId, ulong revisionNumber, CellBridgeActor actor,
        CancellationToken cancellationToken = default)
    {
        var state = await CurrentAsync(resourceId, cancellationToken);
        Demand(actor, state, DocumentAccess.Read);
        return FindRevision(state, revisionNumber);
    }

    /// <summary>Restores a revision with an explicit retry key and optimistic expected publication number.</summary>
    public async ValueTask<RestoreRevisionResult> RestoreRevisionAsync(Guid resourceId, ulong revisionNumber,
        ulong expectedRevision, string operationKey, CellBridgeActor actor,
        IReadOnlyDictionary<string, string>? lockAttributes = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        if (operationKey.Length > 256) throw new ArgumentException("The restore key exceeds 256 characters.", nameof(operationKey));
        var before = await provider.State.TransitionAsync(resourceId, (current, now) =>
        {
            Demand(actor, current, DocumentAccess.Write);
            if (current.RestoreReceipts.Any(r => r.LifecycleGeneration == current.LifecycleGeneration &&
                r.OwnerSubject == actor.Identity.Subject && r.OperationKey == operationKey))
                return new StateTransition<DocumentState>(null, current);
            var document = StoredDocument.RestoreMetadata(current, now);
            var coordinator = FssHttpLockCoordinator.Restore(document, current.Coordination, now, actor.Identity);
            if (!coordinator.ExecuteCellWrite(lockAttributes ?? ImmutableDictionary<string, string>.Empty,
                () => true, out _, out var error, now))
                throw new DocumentOperationLockException(error ?? "FileLockConflict");
            return new StateTransition<DocumentState>(null, current with
                { Coordination = CoordinationFencing.Capture(current.Coordination, coordinator.Capture()) });
        }, cancellationToken);
        string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{revisionNumber}:{expectedRevision}"))));
        if (Replay(before) is { } replay) return replay;
        if (RevisionHistory.Latest(before) != expectedRevision) throw new InvalidOperationException("The current revision changed.");
        var selected = FindRevision(before, revisionNumber);
        await VerifyHistoricalContentAsync(selected.Content, cancellationToken);
        var partitions = ImmutableArray.CreateBuilder<PartitionState>();
        var operationId = Guid.NewGuid();
        foreach (var historical in selected.Partitions)
        {
            var current = before.Partitions.Single(p => p.Kind == historical.Kind);
            if (historical.StorageIndex is null)
            {
                // Legacy metadata has no opaque graph. Preserve its explicit inline representation.
                if (current.StorageIndex is not null)
                    throw new NotSupportedException("Restoring legacy inline metadata over an opaque metadata graph is unsupported.");
                partitions.Add(historical with { Knowledge = checked(current.Knowledge + 1) });
                continue;
            }
            if (current.StorageIndex is null) throw new NotSupportedException("The current partition has no graph to rebase.");
            var oldGraph = await DocumentPartition.RestoreGraphAsync(historical, provider.Content, provider.Limits, cancellationToken);
            var currentGraph = await DocumentPartition.RestoreGraphAsync(current, provider.Content, provider.Limits, cancellationToken);
            var publication = HistoricalGraphRestorer.Rebase(currentGraph, oldGraph, current.Identity.SerialGuid,
                current.Knowledge, new(MaxElements: provider.Limits.MaxGraphElements, MaxPayloadBytes: provider.Limits.MaxGraphBytes));
            if (historical.Kind == 0)
            {
                var file = PartitionGraphSnapshot.Create(publication.Graph.Elements, publication.Graph.StorageIndex);
                await using var staged = OpenSaveStagingStream();
                await file.MaterializeToAsync(staged, provider.Limits.MaxDocumentBytes, cancellationToken);
                staged.Position = 0;
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(staged, cancellationToken));
                if (staged.Length != selected.Content.Length || hash != selected.Content.Sha256)
                    throw new StorageCorruptionException("Historical graph and recorded file content disagree.");
            }
            var captured = await DocumentPartition.CaptureGraphAsync(current, publication.Graph, publication.Knowledge,
                provider.Content, provider.Limits, cancellationToken);
            partitions.Add(captured with { Content = historical.Content, InlineContent = historical.InlineContent });
        }
        try
        {
            return await provider.State.TransitionAsync(resourceId, (current, now) =>
            {
                Demand(actor, current, DocumentAccess.Write);
                if (current.LifecycleGeneration != before.LifecycleGeneration)
                    throw new InvalidOperationException("The document lifecycle changed.");
                if (Replay(current) is { } accepted) return new StateTransition<RestoreRevisionResult>(null, accepted);
                if (RevisionHistory.Latest(current) != expectedRevision ||
                    current.Content != before.Content || current.ContentVersion != before.ContentVersion ||
                    before.Partitions.Where(p => p.Kind != 2).Any(p =>
                        current.Partitions.Single(x => x.Kind == p.Kind) is var latest &&
                        (latest.StorageIndex != p.StorageIndex || latest.Knowledge != p.Knowledge)))
                    throw new InvalidOperationException("The current revision changed while preparing restore.");
                var document = StoredDocument.RestoreMetadata(current, now);
                var coordinator = FssHttpLockCoordinator.Restore(document, current.Coordination, now, actor.Identity);
                if (!coordinator.ExecuteCellWrite(lockAttributes ?? ImmutableDictionary<string, string>.Empty,
                    () => true, out _, out var error, now))
                    throw new DocumentOperationLockException(error ?? "FileLockConflict");
                var authority = CoordinationFencing.Capture(current.Coordination, coordinator.Capture());
                if (authority.Generation != before.Coordination.Generation)
                    throw new DocumentOperationLockException("InvalidCoauthSession");
                var captured = document.CaptureCoordination(current, coordinator.Capture());
                var next = captured with
                {
                    Content = selected.Content, ContentVersion = checked(current.ContentVersion + 1), ModifiedUtc = now,
                    Security = current.Security with { ModifiedBy = actor.Identity },
                    Partitions = captured.Partitions.Select(p => p.Kind == 2 ? p : partitions.Single(x => x.Kind == p.Kind)).ToImmutableArray(),
                    Receipts = current.Receipts.Select(r => r with { Response = null }).ToImmutableArray(),
                    Coordination = authority,
                };
                next = RevisionHistory.Append(current, next, actor.Identity, now);
                next = ExternalPublication.Append(current, next, operationId, provider.Limits);
                next = next with { RestoreReceipts = current.RestoreReceipts.Add(new(current.LifecycleGeneration,
                    actor.Identity.Subject, operationKey, digest, RevisionHistory.Latest(next))) };
                provider.Limits.CheckDocument(next);
                return new StateTransition<RestoreRevisionResult>(next, new(next.Revisions[^1], false));
            }, cancellationToken);
        }
        catch (StorageUnavailableException)
        {
            var current = await CurrentAsync(resourceId, CancellationToken.None);
            Demand(actor, current, DocumentAccess.Write);
            if (current.LifecycleGeneration == before.LifecycleGeneration && Replay(current) is { } accepted) return accepted;
            throw;
        }

        RestoreRevisionResult? Replay(DocumentState state)
        {
            var receipt = state.RestoreReceipts.FirstOrDefault(r => r.LifecycleGeneration == before.LifecycleGeneration &&
                r.OwnerSubject == actor.Identity.Subject && r.OperationKey == operationKey);
            if (receipt is null) return null;
            if (receipt.Digest != digest) throw new InvalidOperationException("The restore key was reused with different arguments.");
            return new(FindRevision(state, receipt.RevisionNumber), true);
        }
    }

    private void Demand(CellBridgeActor actor, DocumentState state, DocumentAccess required)
    {
        if (!Access(actor, state).HasFlag(required)) throw new UnauthorizedAccessException("Document access denied.");
    }

    private async ValueTask VerifyHistoricalContentAsync(ContentHandle handle, CancellationToken cancellationToken)
    {
        StorageLimits.Check("historical document bytes", handle.Length, provider.Limits.MaxDocumentBytes);
        await using var source = await provider.Content.OpenReadAsync(handle, cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long length = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            length = checked(length + read);
            if (length > handle.Length) throw new StorageCorruptionException("Historical content exceeds its declared length.");
            hash.AppendData(buffer, 0, read);
        }
        if (length != handle.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != handle.Sha256)
            throw new StorageCorruptionException("Historical content failed integrity verification.");
    }

    private static DocumentRevision FindRevision(DocumentState state, ulong number) =>
        RevisionHistory.Initialize(state).Revisions.SingleOrDefault(r => r.ResourceId == state.ResourceId &&
            r.LifecycleGeneration == state.LifecycleGeneration && r.RevisionNumber == number)
        ?? throw new KeyNotFoundException("The requested version does not exist.");
}

public sealed class DocumentOperationLockException(string errorCode) : InvalidOperationException(errorCode)
{
    public string ErrorCode { get; } = errorCode;
}
