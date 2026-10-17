using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public sealed record WholeFileSaveResult(DocumentRevision Revision, bool IsReplay);

/// <summary>A prepared whole-file write no longer matches the authoritative document or retry key.</summary>
public sealed class WholeFileSaveConflictException(string message) : InvalidOperationException(message);

public sealed partial class CellBridgeDocumentService
{
    /// <summary>
    /// Prepares and commits a complete Office package while preserving protocol graphs, shared locks,
    /// history and conditional external publication. Repeating the same key returns its accepted revision.
    /// A missing host lock is accepted only for an empty document without active protocol locks.
    /// </summary>
    public async ValueTask<WholeFileSaveResult> SaveWholeFileAsync(Guid resourceId, Stream content,
        string operationKey, CellBridgeActor actor, string? hostLockToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        if (operationKey.Length > 256) throw new ArgumentException("The save key exceeds 256 characters.", nameof(operationKey));
        if (hostLockToken is not null && (string.IsNullOrWhiteSpace(hostLockToken) || hostLockToken.Length > 1024))
            throw new ArgumentException("A host lock token must contain 1 to 1024 characters.", nameof(hostLockToken));

        // Capture authority before consuming host bytes. Preparing a file never refreshes its lease.
        var before = await provider.State.TransitionAsync(resourceId, (current, now) =>
        {
            Demand(actor, current, DocumentAccess.Write);
            if (Receipt(current) is null) DemandWholeFileLock(current, actor, hostLockToken, now);
            return new StateTransition<DocumentState>(null, current);
        }, cancellationToken);

        await using var staged = OpenSaveStagingStream();
        long bound = Math.Min(provider.Limits.MaxDocumentBytes, int.MaxValue);
        var buffer = new byte[65536];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken)) != 0)
        {
            length = checked(length + read);
            StorageLimits.Check("document bytes", length, bound);
            hash.AppendData(buffer, 0, read);
            await staged.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        string sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        string lockDigest = hostLockToken is null ? "none" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hostLockToken)));
        string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{length}:{sha256}:{lockDigest}"))));

        if (Receipt(before) is not null)
            return await provider.State.TransitionAsync(resourceId, (current, _) =>
            {
                Demand(actor, current, DocumentAccess.Write);
                DemandLifecycle(current);
                return new StateTransition<WholeFileSaveResult>(null, Replay(current) ??
                    throw new WholeFileSaveConflictException("The accepted whole-file save is no longer available."));
            }, cancellationToken);

        StorageLimits.Check("whole-file save receipts", before.WholeFileSaveReceipts.Length + 1L, provider.Limits.MaxWholeFileSaveReceipts);
        bool changed = before.Content.Length != length || before.Content.Sha256 != sha256;
        PartitionState? preparedFile = null;
        ContentHandle preparedContent = before.Content;
        if (changed)
        {
            staged.Position = 0;
            await FilePartitionSaveHandler.ValidateDocumentAsync(staged, before.Path, provider.Limits.MaxDocumentBytes, cancellationToken);
            staged.Position = 0;
            var bytes = new byte[checked((int)length)];
            await staged.ReadExactlyAsync(bytes, cancellationToken);
            var imported = await new DocumentStore().Put(before.Path, bytes).CaptureAsync(provider.Content,
                cancellationToken: cancellationToken);
            var currentFile = before.Partitions.Single(p => p.Kind == 0);
            var currentGraph = await DocumentPartition.RestoreGraphAsync(currentFile, provider.Content, provider.Limits, cancellationToken);
            var importedGraph = await DocumentPartition.RestoreGraphAsync(imported.Partitions.Single(p => p.Kind == 0),
                provider.Content, provider.Limits, cancellationToken);
            var publication = HistoricalGraphRestorer.Rebase(currentGraph, importedGraph, currentFile.Identity.SerialGuid,
                currentFile.Knowledge, new(MaxElements: provider.Limits.MaxGraphElements, MaxPayloadBytes: provider.Limits.MaxGraphBytes));
            preparedContent = imported.Content;
            preparedFile = await DocumentPartition.CaptureGraphAsync(currentFile, publication.Graph, publication.Knowledge,
                provider.Content, provider.Limits, cancellationToken);
            preparedFile = preparedFile with { Content = preparedContent, InlineContent = [] };
        }

        Guid publicationOperationId = Guid.NewGuid();
        try
        {
            return await provider.State.TransitionAsync(resourceId, (current, now) =>
            {
                Demand(actor, current, DocumentAccess.Write);
                DemandLifecycle(current);
                if (Replay(current) is { } accepted) return new StateTransition<WholeFileSaveResult>(null, accepted);
                if (current.StateVersion != before.StateVersion)
                    throw new WholeFileSaveConflictException("The document changed while preparing the whole-file save.");
                if (current.Coordination.Generation != before.Coordination.Generation)
                    throw new DocumentOperationLockException("InvalidCoauthSession");
                DemandWholeFileLock(current, actor, hostLockToken, now);
                var next = current;
                if (changed)
                {
                    uint version = checked(current.ContentVersion + 1);
                    next = current with
                    {
                        Content = preparedContent,
                        ContentVersion = version,
                        ModifiedUtc = now,
                        Security = current.Security with { ModifiedBy = actor.Identity },
                        Partitions = current.Partitions.Select(p => p.Kind == 0 ? preparedFile! :
                            p.Kind == 1 && p.StorageIndex is null && p.Elements.IsEmpty ? p with
                            {
                                Knowledge = checked(p.Knowledge + 1),
                                InlineContent = Encoding.UTF8.GetBytes(
                                    $"<Metadata ContentVersion=\"{version}\" Modified=\"{now.Ticks}\" />").ToImmutableArray(),
                            } : p).ToImmutableArray(),
                        Receipts = current.Receipts.Select(r => r.PartitionKind == 0 ? r with { Response = null } : r).ToImmutableArray(),
                    };
                    next = RevisionHistory.Append(current, next, actor.Identity, now);
                    next = ExternalPublication.Append(current, next, publicationOperationId, provider.Limits);
                }
                else next = RevisionHistory.Initialize(next);
                var revision = next.Revisions[^1];
                next = next with { WholeFileSaveReceipts = current.WholeFileSaveReceipts.Add(new(
                    current.LifecycleGeneration, actor.Identity.Subject, operationKey, digest, revision.RevisionNumber)) };
                provider.Limits.CheckDocument(next);
                return new StateTransition<WholeFileSaveResult>(next, new(revision, false));
            }, cancellationToken);
        }
        catch (StorageUnavailableException)
        {
            // The transaction may have committed. Resolve its durable receipt without repeating publication.
            var accepted = await provider.State.TransitionAsync(resourceId, (current, _) =>
            {
                Demand(actor, current, DocumentAccess.Write);
                DemandLifecycle(current);
                return new StateTransition<WholeFileSaveResult?>(null, Replay(current));
            }, CancellationToken.None);
            if (accepted is not null) return accepted;
            throw;
        }

        WholeFileSaveReceipt? Receipt(DocumentState state) => state.WholeFileSaveReceipts.FirstOrDefault(r =>
            r.LifecycleGeneration == state.LifecycleGeneration && r.OwnerSubject == actor.Identity.Subject && r.OperationKey == operationKey);

        WholeFileSaveResult? Replay(DocumentState state)
        {
            var receipt = Receipt(state);
            if (receipt is null) return null;
            if (!string.Equals(receipt.Digest, digest, StringComparison.Ordinal))
                throw new WholeFileSaveConflictException("The whole-file save key was reused with different content or lock authority.");
            return new(FindRevision(state, receipt.RevisionNumber), true);
        }

        void DemandLifecycle(DocumentState state)
        {
            if (state.LifecycleGeneration != before.LifecycleGeneration)
                throw new WholeFileSaveConflictException("The document lifecycle changed while preparing the whole-file save.");
        }
    }

    private static void DemandWholeFileLock(DocumentState state, CellBridgeActor actor, string? token, DateTime now)
    {
        var coordination = state.Coordination;
        if (coordination.Exclusive is { } exclusive && exclusive.ExpiresUtc > now ||
            coordination.SchemaOwners.Any(l => l.ExpiresUtc > now))
            throw new DocumentOperationLockException("FileAlreadyLockedOnServer");
        if (token is not null)
        {
            if (coordination.HostLock is not { } lease || lease.ExpiresUtc <= now ||
                !string.Equals(lease.Token, token, StringComparison.Ordinal) ||
                !string.Equals(lease.OwnerSubject, actor.Identity.Subject, StringComparison.Ordinal))
                throw new DocumentOperationLockException("InvalidCoauthSession");
        }
        else if (state.Content.Length != 0 || coordination.HostLock is { } host && host.ExpiresUtc > now)
            throw new DocumentOperationLockException("FileAlreadyLockedOnServer");
    }
}
