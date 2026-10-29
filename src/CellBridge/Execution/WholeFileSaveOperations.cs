using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public sealed record WholeFileSaveResult(DocumentRevisionRecord Revision, bool IsReplay);
public sealed class WholeFileSaveConflictException(string message) : InvalidOperationException(message);

public sealed partial class CellBridgeDocumentService
{
    /// <summary>Stages a complete package as a canonical graph and atomically publishes its typed receipt and outbox.</summary>
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

        var before = await provider.Documents.ReadControlAsync(resourceId, cancellationToken) ?? throw MissingDocument();
        actor = await AuthorizeActorAsync(actor, before.Head, DocumentAccess.Write, cancellationToken, fresh: true);
        Demand(actor, before.Head, DocumentAccess.Write);
        var identity = new SaveOperationIdentity(before.Head.LifecycleGeneration, SaveOperationFamily.WholeFile, 0, operationKey);
        var prior = await provider.Documents.ReadReceiptAsync(resourceId, identity, cancellationToken) ?? throw MissingDocument();
        if (prior.Receipt is null && DocumentControlReducer.ValidateWholeFile(before, Admission(actor, before.Head), hostLockToken) is { } lockError)
            throw new DocumentOperationLockException(lockError);

        await using var staged = OpenSaveStagingStream();
        var buffer = new byte[65536];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken)) != 0)
        {
            length = checked(length + read);
            StorageLimits.Check("document bytes", length, Math.Min(provider.Limits.MaxDocumentBytes, Array.MaxLength));
            hash.AppendData(buffer, 0, read);
            await staged.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        var lockDigest = hostLockToken is null ? "none" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hostLockToken)));
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{length}:{sha256}:{lockDigest}"))));
        if (await ReplayAsync(cancellationToken) is { } replay) return replay;

        StorageLimits.Check("whole-file save receipts", before.Head.Counters.WholeFileSaveReceipts + 1L, provider.Limits.MaxWholeFileSaveReceipts);
        staged.Position = 0;
        await OfficePackageValidation.ValidateDocumentAsync(staged, before.Head.Path, provider.Limits.MaxDocumentBytes, cancellationToken);
        PreparedDocumentPublication publication;
        if (before.Head.Package.Length == length && before.Head.Package.Sha256 == sha256)
        {
            publication = new(resourceId, Admission(actor, before.Head),
                new(before.Head.LifecycleGeneration, before.Head.AuthorityGeneration, before.Coordination.Generation,
                    before.Head.StateVersion, before.Head.ContentVersion, []),
                new(identity, digest, actor.Identity.Subject, before.Head.ContentVersion, before.Head.LatestRevisionNumber, null, null), [])
            { HostLockToken = hostLockToken };
        }
        else
        {
            staged.Position = 0;
            var bytes = new byte[checked((int)length)];
            await staged.ReadExactlyAsync(bytes, cancellationToken);
            var file = before.Partitions.Single(p => p.Kind == 0);
            var cell = IndexedDocumentGraph.Restore(before.Head.Package.Root.Cell);
            ExGuid Fresh() => new(1, Guid.NewGuid());
            var graphIdentity = new StorageManifestBuilder.StableIdentity(Fresh(), Fresh(), Fresh(), Fresh(),
                Fresh(), Fresh(), Fresh(), cell, Guid.NewGuid());
            var imported = FileContentPartitionBuilder.BuildQueryChangesResponse(1, bytes, graphIdentity, file.Knowledge);
            var index = ((QueryChangesSubResponseData)imported.SubResponses[0].Data!).StorageIndexExtendedGuid;
            var package = imported.DataElementPackage!;
            var scope = new IndexedGraphScope(resourceId, before.Head.LifecycleGeneration, 0);
            var keys = IndexedDocumentGraph.ReadMetadata(scope,
                package.DataElements.Single(element => element.DataElementExtendedGuid.Equals(index))).Mappings.Select(m => m.Key).ToImmutableArray();
            var preparation = await provider.Documents.PrepareSaveAsync(resourceId,
                new(0, identity, keys) { IncludeBindings = true }, cancellationToken) ?? throw MissingDocument();
            if (preparation.Control.Head.StateVersion != before.Head.StateVersion)
            {
                Demand(actor, preparation.Control.Head, DocumentAccess.Write);
                throw new WholeFileSaveConflictException("The document changed while preparing the whole-file save.");
            }
            var operation = new FsshttpbCellSubRequest(RequestTypes.PutChanges)
            { RequestId = 1, Data = new PutChangesSubRequestData { StorageIndex = index } };
            var prepared = await IndexedPartitionSavePreparer.PrepareAsync(preparation, operation, package,
                Admission(actor, before.Head), ImmutableDictionary<string, string>.Empty,
                provider.Documents, provider.Content, provider.Limits, cancellationToken,
                new DocumentWriteContext(identity, digest, hostLockToken));
            if (prepared.ErrorCode is { } error)
            {
                if (error == "FileUnauthorizedAccess") throw new UnauthorizedAccessException("Write access denied.");
                throw new DocumentOperationLockException(error);
            }
            publication = prepared.Publication ?? throw new WholeFileSaveConflictException("The whole-file graph could not be prepared.");
            if (publication.Package is not { } descriptor || descriptor.Length != length || descriptor.Sha256 != sha256)
                throw new StorageCorruptionException("The prepared graph does not reproduce the supplied package.");
        }

        PublicationResult result;
        try { result = await provider.Documents.PublishAsync(publication, cancellationToken); }
        catch (Exception error) when (error is StorageUnavailableException || error is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            if (await ReplayAsync(CancellationToken.None) is { } accepted) return accepted;
            throw;
        }
        if (result.Status == PublicationStatus.Replay)
            return await ReplayAsync(cancellationToken) ?? throw new StorageCorruptionException("An accepted whole-file receipt is missing.");
        if (result.Status == PublicationStatus.Reprepare)
            throw new WholeFileSaveConflictException("The document changed while preparing the whole-file save.");
        if (result.Status == PublicationStatus.ProtocolError)
        {
            if (result.ErrorCode == "FileUnauthorizedAccess") throw new UnauthorizedAccessException("Write access denied.");
            if (result.ErrorCode is "CoherencyFailure" or "InvalidRequest") throw new WholeFileSaveConflictException(result.ErrorMessage ?? "The whole-file save conflicts with current state.");
            throw new DocumentOperationLockException(result.ErrorCode ?? "CellRequestFail");
        }
        var selected = await provider.Documents.ReadRevisionAsync(resourceId, result.Receipt!.RevisionNumber, cancellationToken) ?? throw MissingDocument();
        var revision = selected.Revision ?? throw new StorageCorruptionException("The accepted whole-file revision is missing.");
        actor = await AuthorizeActorAsync(actor, selected.Current, DocumentAccess.Write, cancellationToken);
        Demand(actor, selected.Current, DocumentAccess.Write);
        return new(revision, false);

        async ValueTask<WholeFileSaveResult?> ReplayAsync(CancellationToken ct)
        {
            var readReceipt = await provider.Documents.ReadReceiptAsync(resourceId, identity, ct) ?? throw MissingDocument();
            actor = await AuthorizeActorAsync(actor, readReceipt.Current, DocumentAccess.Write, ct);
            Demand(actor, readReceipt.Current, DocumentAccess.Write);
            if (readReceipt.Current.LifecycleGeneration != before.Head.LifecycleGeneration)
                throw new WholeFileSaveConflictException("The document lifecycle changed while preparing the whole-file save.");
            if (readReceipt.Receipt is not { } receipt) return null;
            if (receipt.OwnerSubject != actor.Identity.Subject || receipt.Digest != digest)
                throw new WholeFileSaveConflictException("The whole-file save key was reused by another actor or with different content or lock authority.");
            var selection = await provider.Documents.ReadRevisionAsync(resourceId, receipt.RevisionNumber, ct) ?? throw MissingDocument();
            var accepted = selection.Revision ?? throw new StorageCorruptionException("The accepted whole-file revision is missing.");
            var current = await provider.Documents.ReadReceiptAsync(resourceId, identity, ct) ?? throw MissingDocument();
            actor = await AuthorizeActorAsync(actor, current.Current, DocumentAccess.Write, ct);
            Demand(actor, current.Current, DocumentAccess.Write);
            if (current.Current.LifecycleGeneration != before.Head.LifecycleGeneration || current.Receipt != receipt)
                throw new WholeFileSaveConflictException("The accepted whole-file save changed during response preparation.");
            return new(accepted, true);
        }
    }
}
