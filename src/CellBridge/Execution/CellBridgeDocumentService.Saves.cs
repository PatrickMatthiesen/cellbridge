using CellBridge.Diagnostics;
using System.Collections.Immutable;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public sealed partial class CellBridgeDocumentService
{
    private async ValueTask<CellExecution> SaveAsync(DocumentHead initial, DocumentPartitionKind kind,
        FsshttpbCellSubRequest operation, DataElementPackage? package, IReadOnlyDictionary<string, string> attributes,
        CellBridgeActor actor, Func<DataElementPackage?, bool> canAppend, CancellationToken cancellationToken)
    {
        using var timing = CellBridgeMetrics.Measure(CellBridgePhase.SaveTotal);
        var id = initial.ResourceId;
        if (!Access(actor, initial).HasFlag(DocumentAccess.Write)) return Wrap(CellBridgeAuthorization.Denied(operation), initial);
        if (kind is not (DocumentPartitionKind.FileContents or DocumentPartitionKind.Metadata) ||
            operation.Data is not PutChangesSubRequestData put || package is null || put.StorageIndex.IsNull ||
            (put.Flags & ~0x79) != 0 || (put.AdditionalFlagsBits & 0x38) != 0 ||
            kind == DocumentPartitionKind.Metadata && CellSubRequestDataValidation.TryGetBoolean(attributes, "Coalesce", out var coalesce) && coalesce)
            return Wrap(Failure(operation.RequestId, CellErrorCode.RequestNotSupported, "Only file and application metadata uploads are supported."), initial);
        // The caller retains its request buffers. Own them before computing the retry identity.
        var owned = new DataElementPackage();
        foreach (var element in package.DataElements)
            owned.DataElements.Add(new(element.DataElementType,
                new(element.DataElementExtendedGuid.Value, element.DataElementExtendedGuid.Guid),
                new(element.SerialNumber.Guid, element.SerialNumber.Value)) { Data = element.Data?.ToArray() });
        SaveOperationIdentity identity;
        string digest;
        ImmutableArray<IndexedMappingKey> keys;
        SaveGraphReadRequest graphRequest;
        try
        {
            (identity, digest) = IndexedPartitionSavePreparer.Identify(initial.LifecycleGeneration, (int)kind, operation, owned);
            var proposed = owned.DataElements.FirstOrDefault(element => element.DataElementExtendedGuid.Equals(put.StorageIndex));
            var scope = new IndexedGraphScope(id, initial.LifecycleGeneration, (int)kind);
            keys = proposed is null ? [] : IndexedDocumentGraph.ReadMetadata(scope, proposed).Mappings.Select(mapping => mapping.Key).Distinct().ToImmutableArray();
            if (kind == DocumentPartitionKind.FileContents && proposed is not null)
                keys = keys.Add(new(IndexedMappingKind.Manifest)).Distinct().ToImmutableArray();
            graphRequest = new(
                owned.DataElements.Select(element => IndexedDocumentGraph.Capture(element.DataElementExtendedGuid)).Distinct().ToImmutableArray(),
                proposed?.Data is { } indexBytes
                    ? StorageIndexMappingSerials.Read(indexBytes).Select(serial => new SerialId(serial.Guid, serial.Value))
                        .Where(serial => serial.Guid != Guid.Empty || serial.Value != 0).Distinct().ToImmutableArray()
                    : []);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
        { return Wrap(Failure(operation.RequestId, CellErrorCode.InvalidObject, error.Message), initial); }

        CoordinationState? originalCoordination = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            SavePreparationRead preparation;
            using (CellBridgeMetrics.Measure(CellBridgePhase.CoherentPrepare))
                preparation = await provider.Documents.PrepareSaveAsync(id,
                    new((int)kind, identity, keys) { IncludeBindings = true, Graph = graphRequest }, cancellationToken) ?? throw MissingDocument();
            var head = preparation.Control.Head;
            actor = await AuthorizeActorAsync(actor, head, DocumentAccess.Write, cancellationToken);
            if (!Access(actor, head).HasFlag(DocumentAccess.Write)) return Wrap(CellBridgeAuthorization.Denied(operation), head);
            if (head.LifecycleGeneration != initial.LifecycleGeneration)
                return Wrap(Failure(operation.RequestId, CellErrorCode.CoherencyFailure, "The document lifecycle changed."), head);
            if (preparation.Receipt is not null)
                return await RepeatSaveAsync(id, identity, digest, operation.RequestId, actor, canAppend, cancellationToken);
            if (!MatchesEtag(attributes, head)) return new(new(), head, "CellRequestFail");
            // Retry may move a graph root, but it cannot replace the lease authority
            // observed when this binary operation was admitted.
            if (preparation.Control.Coordination.Generation != initial.CoordinationGeneration)
                return await LostLeaseAsync(head);
            var admission = Admission(actor, head);
            if (DocumentControlReducer.ValidateWrite(preparation.Control, admission, attributes) is { } writeError)
                return new(new(), head, writeError);
            var sanitized = DocumentControlReducer.ExpireForPublication(preparation.Control).Current.Coordination;
            if (originalCoordination is not null && CoordinationFencing.Capture(originalCoordination with { Generation = 0 },
                sanitized with { Generation = 0 }).Generation != 0) return await LostLeaseAsync(head);
            originalCoordination ??= sanitized;
            var coordination = preparation.Control.Coordination;
            var now = preparation.Control.UtcNow;
            if (attributes.ContainsKey("ExclusiveLockID") || attributes.ContainsKey("SchemaLockID") ||
                coordination.Exclusive is { } exclusive && exclusive.ExpiresUtc > now ||
                coordination.HostLock is { } host && host.ExpiresUtc > now ||
                coordination.SchemaOwners.Any(lease => lease.ExpiresUtc > now))
            {
                var beforeStaging = await CurrentAsync(id, cancellationToken);
                if (!Access(actor, beforeStaging).HasFlag(DocumentAccess.Write))
                    return Wrap(CellBridgeAuthorization.Denied(operation), beforeStaging);
                if (beforeStaging.CoordinationGeneration != initial.CoordinationGeneration)
                    return await LostLeaseAsync(beforeStaging);
            }
            using var stagingContent = new DocumentStagingContent(provider.Documents, provider.Content, admission, provider.Limits);
            IndexedPreparedSave prepared;
            try
            {
                prepared = await IndexedPartitionSavePreparer.PrepareAsync(preparation, operation, owned, admission,
                    attributes, provider.Documents, stagingContent,
                    provider.Limits, cancellationToken, canAppend: canAppend);
            }
            catch (StagingAdmissionChangedException) { continue; }
            if (prepared.ErrorCode is { } preparationError) return new(prepared.Response, head, preparationError);
            if (prepared.Publication is not { } publication) return new(prepared.Response, head);
            publication = publication with { Fence = publication.Fence with { PreparedCoordination = originalCoordination } };
            if (!canAppend(prepared.Response.DataElementPackage))
                return Wrap(Failure(operation.RequestId, CellErrorCode.RequestNotSupported, "The save response exceeds the combined response budget."), head);
            PublicationResult result;
            try { result = await stagingContent.PublishAsync(publication, cancellationToken); }
            catch (Exception error) when (error is StorageUnavailableException || error is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                var receipt = await provider.Documents.ReadReceiptAsync(id, identity, CancellationToken.None);
                if (receipt?.Receipt is null) throw;
                var recovered = await RepeatSaveAsync(id, identity, digest, operation.RequestId, actor, canAppend, CancellationToken.None);
                if (!recovered.HasAcceptedSave) throw;
                return recovered with { IsCancelled = error is OperationCanceledException };
            }
            if (result.Status == PublicationStatus.Reprepare) continue;
            if (result.Status == PublicationStatus.Replay)
                return await RepeatSaveAsync(id, identity, digest, operation.RequestId, actor, canAppend, cancellationToken);
            if (result.Status == PublicationStatus.ProtocolError)
            {
                if (result.ErrorCode is "AccessDenied" or "FileUnauthorizedAccess") return Wrap(CellBridgeAuthorization.Denied(operation), result.Current);
                if (result.ErrorCode == "CoherencyFailure")
                    return Wrap(Failure(operation.RequestId, CellErrorCode.CoherencyFailure, result.ErrorMessage ?? "An affected mapping changed."), result.Current);
                return new(new(), result.Current, result.ErrorCode ?? "CellRequestFail");
            }
            return new(prepared.Response, result.Current)
            {
                AcceptedSaves = kind == DocumentPartitionKind.FileContents
                    ? [new(id, result.Current.ContentVersion, result.Current.Package, identity.OperationKey, false)
                        { LifecycleGeneration = result.Current.LifecycleGeneration }] : [],
            };
        }
        return Wrap(Failure(operation.RequestId, CellErrorCode.CoherencyFailure, "The document changed while preparing the save."),
            await CurrentAsync(id, cancellationToken));

        async ValueTask<CellExecution> LostLeaseAsync(DocumentHead current)
        {
            // A concurrent copy of this exact operation may already have committed.
            var receipt = await provider.Documents.ReadReceiptAsync(id, identity, cancellationToken);
            return receipt?.Receipt is not null
                ? await RepeatSaveAsync(id, identity, digest, operation.RequestId, actor, canAppend, cancellationToken)
                : new(new(), current, "InvalidCoauthSession");
        }
    }

    private async ValueTask<CellExecution> RepeatSaveAsync(Guid id, SaveOperationIdentity identity, string digest,
        ulong requestId, CellBridgeActor actor, Func<DataElementPackage?, bool> canAppend, CancellationToken cancellationToken)
    {
        try { return await ReadAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var result = await ReadAsync(CancellationToken.None);
            if (!result.HasAcceptedSave) throw;
            return result with { IsCancelled = true };
        }

        async ValueTask<CellExecution> ReadAsync(CancellationToken ct)
        {
            var read = await provider.Documents.ReadReceiptAsync(id, identity, ct) ?? throw MissingDocument();
            if (!Access(actor, read.Current).HasFlag(DocumentAccess.Write) || read.Receipt?.OwnerSubject != actor.Identity.Subject)
                return Wrap(CellBridgeAuthorization.Denied(new(RequestTypes.PutChanges) { RequestId = requestId }), read.Current);
            var receipt = read.Receipt;
            if (receipt.Operation != identity || read.Current.LifecycleGeneration != identity.LifecycleGeneration ||
                identity.PartitionKind == 0 && receipt.ContentVersion != read.Current.ContentVersion)
                return Wrap(Failure(requestId, CellErrorCode.CoherencyFailure, "The accepted operation has been superseded."), read.Current);
            if (receipt.Digest != digest)
                return Wrap(Failure(requestId, CellErrorCode.InvalidObject, "The accepted operation key was reused with different data."), read.Current);
            if (identity.PartitionKind != 0)
            {
                var partition = await provider.Documents.ReadPartitionAsync(id, new(identity.PartitionKind), ct) ?? throw MissingDocument();
                if (partition.Partition.StorageIndex != receipt.AcceptedStorageIndex)
                    return Wrap(Failure(requestId, CellErrorCode.CoherencyFailure, "The accepted partition has been superseded."), partition.Current);
            }
            var bytes = await provider.Content.ReadVerifiedAsync(receipt.Response ?? throw new StorageCorruptionException("Missing accepted response."),
                provider.Limits.MaxObjectBytes, ct);
            FsshttpbResponse response;
            try
            {
                var reader = new BinaryReaderEx(bytes);
                response = FsshttpbResponse.Deserialize(reader);
                if (reader.Remaining != 0 || response.SubResponses.Count != 1 || response.SubResponses[0].Status ||
                    response.SubResponses[0].RequestType != RequestTypes.PutChanges) throw new InvalidDataException("Incomplete successful save response.");
            }
            catch (Exception error) when (error is InvalidDataException or EndOfStreamException or FormatException or ArgumentException or OverflowException)
            { throw new StorageCorruptionException("The accepted response is malformed.", error); }
            var current = await provider.Documents.ReadReceiptAsync(id, identity, ct) ?? throw MissingDocument();
            if (!Access(actor, current.Current).HasFlag(DocumentAccess.Write))
                return Wrap(CellBridgeAuthorization.Denied(new(RequestTypes.PutChanges) { RequestId = requestId }), current.Current);
            if (current.Receipt != receipt || current.Current.LifecycleGeneration != identity.LifecycleGeneration ||
                identity.PartitionKind == 0 && (current.Current.ContentVersion != read.Current.ContentVersion || current.Current.Package != read.Current.Package))
                return Wrap(Failure(requestId, CellErrorCode.CoherencyFailure, "The accepted operation changed during response preparation."), current.Current);
            if (identity.PartitionKind != 0)
            {
                var partition = await provider.Documents.ReadPartitionAsync(id, new(identity.PartitionKind), ct) ?? throw MissingDocument();
                if (!Access(actor, partition.Current).HasFlag(DocumentAccess.Write))
                    return Wrap(CellBridgeAuthorization.Denied(new(RequestTypes.PutChanges) { RequestId = requestId }), partition.Current);
                if (partition.Current.LifecycleGeneration != identity.LifecycleGeneration || partition.Partition.StorageIndex != receipt.AcceptedStorageIndex)
                    return Wrap(Failure(requestId, CellErrorCode.CoherencyFailure, "The accepted metadata operation changed during response preparation."), partition.Current);
                current = current with { Current = partition.Current };
            }
            if (!canAppend(response.DataElementPackage))
                return Wrap(Failure(requestId, CellErrorCode.RequestNotSupported, "The accepted response exceeds the combined response budget."), current.Current);
            response.SubResponses[0].RequestId = requestId;
            return new(response, current.Current)
            {
                AcceptedSaves = identity.PartitionKind == 0
                    ? [new(id, receipt.ContentVersion, current.Current.Package, identity.OperationKey, true)
                        { LifecycleGeneration = current.Current.LifecycleGeneration }] : [],
            };
        }
    }
}
