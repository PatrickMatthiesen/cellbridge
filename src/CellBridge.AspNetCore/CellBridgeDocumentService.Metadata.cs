using System.Collections.Immutable;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public sealed partial class CellBridgeDocumentService
{
    private async ValueTask<FsshttpbResponse> MetadataQueryAsync(DocumentState state,
        FsshttpbCellSubRequest operation, CancellationToken cancellationToken)
    {
        var partition = state.Partitions.Single(p => p.Kind == 1);
        var graph = await DocumentPartition.RestoreGraphAsync(partition, provider.Content, provider.Limits, cancellationToken);
        var controls = operation.Data as QueryChangesSubRequestData;
        if (!FileQueryResponseBuilder.Supports(controls, graph.Cells)) return FileQueryResponseBuilder.Unsupported(operation.RequestId);
        var cell = FileQueryResponseBuilder.IsScoped(controls) ? controls!.CellId!
            : graph.Roots.FirstOrDefault()?.Cell ?? graph.Cells.FirstOrDefault()
                ?? new CellId(StorageIds.Restore(partition.Identity.CellLong), StorageIds.Restore(partition.Identity.CellShort));
        var scope = FileQueryResponseBuilder.IsScoped(controls) ? graph.GetRequiredElements(cell).ToHashSet() : null;
        var elements = graph.Elements;
        var selection = FileQueryResponseBuilder.Select(elements, graph.StorageIndex, cell, partition.Knowledge,
            controls, graph.MappingSerials, scope, StorageIds.Restore(partition.Identity.CellShort));
        return FileQueryResponseBuilder.Build(operation.RequestId, selection,
            elements.Where(e => selection.PayloadIds.Contains(e.DataElementExtendedGuid)), controls);
    }

    private async ValueTask<CellExecution> SaveMetadataAsync(DocumentState initial, FsshttpbCellSubRequest operation,
        DataElementPackage? package, IReadOnlyDictionary<string, string> attributes, CellBridgeActor actor,
        Func<DataElementPackage?, bool> canAppend, CancellationToken cancellationToken)
    {
        if (operation.Data is not PutChangesSubRequestData put || package is null || put.StorageIndex.IsNull ||
            (put.Flags & ~0x79) != 0 || (put.AdditionalFlagsBits & 0x38) != 0 ||
            attributes.TryGetValue("Coalesce", out var coalesce) && coalesce is "true" or "1")
            return Wrap(Failure(operation.RequestId, CellErrorCode.RequestNotSupported,
                "Only complete, non-coalescing application metadata uploads are supported."), initial);
        var key = $"metadata:{put.StorageIndex.Value}:{put.StorageIndex.Guid:D}";
        string digest;
        try { digest = Digest(put, package); }
        catch (InvalidDataException ex) { return Wrap(Failure(operation.RequestId, CellErrorCode.InvalidObject, ex.Message), initial); }
        CoordinationState? preparedAuthority = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var before = await CurrentAsync(initial.ResourceId, cancellationToken);
            if (!Access(actor, before).HasFlag(DocumentAccess.Write)) return Wrap(CellBridgeAuthorization.Denied(operation), before);
            if (before.LifecycleGeneration != initial.LifecycleGeneration || before.Coordination.Generation != initial.Coordination.Generation)
                return Wrap(Failure(operation.RequestId, CellErrorCode.CoherencyFailure, "The document incarnation or lease epoch changed."), before);
            if (before.Receipts.FirstOrDefault(r => r.OperationKey == key && r.PartitionKind == 1) is not null)
                return await RepeatMetadataAsync(initial, key, digest, operation.RequestId, actor, canAppend, cancellationToken);
            if (preparedAuthority is null)
            {
                var authority = await provider.State.TransitionAsync<(DocumentState State, CoordinationState? Authority, string? Error)>(initial.ResourceId, (current, now) =>
                {
                    if (!Access(actor, current).HasFlag(DocumentAccess.Write))
                        return new StateTransition<(DocumentState State, CoordinationState? Authority, string? Error)>(null,
                            (current, null, "FileUnauthorizedAccess"));
                    if (current.LifecycleGeneration != initial.LifecycleGeneration || current.Coordination.Generation != initial.Coordination.Generation)
                        return new StateTransition<(DocumentState, CoordinationState?, string?)>(null, (current, null, "MetadataEpochChanged"));
                    var document = StoredDocument.RestoreMetadata(current, now);
                    var coordinator = FssHttpLockCoordinator.Restore(document, current.Coordination, now, actor.Identity);
                    if (!coordinator.ExecuteCellWrite(attributes, () => true, out _, out var error, now))
                        return new StateTransition<(DocumentState, CoordinationState?, string?)>(null, (current, null, error));
                    return new StateTransition<(DocumentState, CoordinationState?, string?)>(null,
                        (current, coordinator.Capture(), null));
                }, cancellationToken);
                before = authority.State;
                if (authority.Error == "FileUnauthorizedAccess") return Wrap(CellBridgeAuthorization.Denied(operation), before);
                if (authority.Error == "MetadataEpochChanged")
                    return Wrap(Failure(operation.RequestId, CellErrorCode.CoherencyFailure, "Metadata authority changed before preparation."), before);
                if (authority.Error is not null) return new(new(), before, authority.Error);
                preparedAuthority = authority.Authority!;
            }
            var source = before.Partitions.Single(p => p.Kind == 1);
            IReadOnlyCollection<DataElement> retained = source.StorageIndex is null ? []
                : (await DocumentPartition.RestoreGraphAsync(source, provider.Content, provider.Limits, cancellationToken)).Elements;
            var prepared = GenericPartitionSaveHandler.Prepare(source, retained, operation, package, provider.Limits);
            if (prepared.Graph is null) return new(prepared.Response, before);
            if (!canAppend(prepared.Response.DataElementPackage))
                return Wrap(Failure(operation.RequestId, CellErrorCode.RequestNotSupported,
                    "The mandatory save payload exceeds the response budget or conflicts with earlier results."), before);
            StorageLimits.Check("save receipts", before.Receipts.Length + 1L, provider.Limits.MaxSaveReceipts);
            var partition = await DocumentPartition.CaptureGraphAsync(source, prepared.Graph, prepared.KnowledgeSequence,
                provider.Content, provider.Limits, cancellationToken);
            var responseBytes = prepared.Response.ToByteArray();
            StorageLimits.Check("save receipt bytes", responseBytes.LongLength, provider.Limits.MaxObjectBytes);
            using var responseStream = new MemoryStream(responseBytes);
            var responseHandle = await provider.Content.WriteAsync(responseStream, cancellationToken);
            var receipt = new SaveReceipt(key, digest, before.ContentVersion, responseHandle, actor.Identity.Subject)
            { PartitionKind = 1, AcceptedStorageIndex = partition.StorageIndex, LifecycleGeneration = initial.LifecycleGeneration };
            PublishResult published;
            try
            {
                published = await provider.State.TransitionAsync(initial.ResourceId, (current, now) =>
                {
                    if (!Access(actor, current).HasFlag(DocumentAccess.Write))
                        return new StateTransition<PublishResult>(null, new(current, null, "FileUnauthorizedAccess", false));
                    if (current.LifecycleGeneration != initial.LifecycleGeneration || current.Coordination.Generation != initial.Coordination.Generation)
                        return new StateTransition<PublishResult>(null, new(current, null, "MetadataEpochChanged", false));
                    if (current.Receipts.FirstOrDefault(r => r.OperationKey == key && r.PartitionKind == 1) is { } prior)
                        return new StateTransition<PublishResult>(null, new(current, prior, null, false));
                    var currentPartition = current.Partitions.Single(p => p.Kind == 1);
                    if (currentPartition.StorageIndex != source.StorageIndex || currentPartition.Knowledge != source.Knowledge)
                        return new StateTransition<PublishResult>(null, new(current, null, null, true));
                    var document = StoredDocument.RestoreMetadata(current, now);
                    var coordinator = FssHttpLockCoordinator.Restore(document, current.Coordination, now, actor.Identity);
                    if (!coordinator.ExecuteCellWrite(attributes, () => true, out _, out var error, now))
                        return new StateTransition<PublishResult>(null, new(current, null, error, false));
                    if (CoordinationFencing.Capture(preparedAuthority, coordinator.Capture()).Generation != preparedAuthority.Generation)
                        return new StateTransition<PublishResult>(null, new(current, null, "MetadataEpochChanged", false));
                    var next = document.CaptureCoordination(current, CoordinationFencing.Capture(current.Coordination, coordinator.Capture())) with
                    {
                        StateVersion = checked(current.StateVersion + 1),
                        Partitions = current.Partitions.Select(p => p.Kind == 1 ? partition : p.Kind == 2
                            ? p with { Knowledge = document.EditorsTablePartition.KnowledgeSequence,
                                InlineContent = document.EditorsTablePartition.Content.ToImmutableArray() } : p).ToImmutableArray(),
                        Receipts = current.Receipts.Select(r => r.PartitionKind == 1 ? r with { Response = null } : r)
                            .Append(receipt).ToImmutableArray(),
                    };
                    next = RevisionHistory.Append(current, next, actor.Identity, now);
                    provider.Limits.CheckDocument(next);
                    return new StateTransition<PublishResult>(next, new(next, null, null, false));
                }, cancellationToken);
            }
            catch (StorageUnavailableException)
            {
                var current = await CurrentAsync(initial.ResourceId, CancellationToken.None);
                if (!current.Receipts.Any(r => r.OperationKey == key && r.PartitionKind == 1)) throw;
                return await RepeatMetadataAsync(initial, key, digest, operation.RequestId, actor, canAppend, CancellationToken.None);
            }
            if (published.Receipt is not null)
                return await RepeatMetadataAsync(initial, key, digest, operation.RequestId, actor, canAppend, cancellationToken);
            if (published.LockError == "FileUnauthorizedAccess") return Wrap(CellBridgeAuthorization.Denied(operation), published.State);
            if (published.LockError == "MetadataEpochChanged")
                return Wrap(Failure(operation.RequestId, CellErrorCode.CoherencyFailure, "The document incarnation or lease epoch changed."), published.State);
            if (published.LockError is not null) return new(new(), published.State, published.LockError);
            if (published.Retry) continue;
            return new(prepared.Response, published.State);
        }
        return Wrap(Failure(operation.RequestId, CellErrorCode.CoherencyFailure, "Metadata changed while preparing the save."),
            await CurrentAsync(initial.ResourceId, cancellationToken));
    }

    private async ValueTask<CellExecution> RepeatMetadataAsync(DocumentState initial, string key, string digest,
        ulong requestId, CellBridgeActor actor, Func<DataElementPackage?, bool> canAppend, CancellationToken cancellationToken)
    {
        var current = await provider.State.TransitionAsync(initial.ResourceId,
            (state, _) => new StateTransition<DocumentState>(null, state), cancellationToken);
        var receipt = current.Receipts.FirstOrDefault(r => r.OperationKey == key && r.PartitionKind == 1);
        if (!Access(actor, current).HasFlag(DocumentAccess.Write) || receipt?.OwnerSubject != actor.Identity.Subject)
            return Wrap(new() { RequestId = requestId, RequestType = RequestTypes.PutChanges, Status = true,
                Error = CellBridgeAuthorization.AccessError() }, current);
        if (current.LifecycleGeneration != initial.LifecycleGeneration || receipt.LifecycleGeneration != current.LifecycleGeneration ||
            current.Coordination.Generation != initial.Coordination.Generation ||
            receipt.AcceptedStorageIndex != current.Partitions.Single(p => p.Kind == 1).StorageIndex)
            return Wrap(Failure(requestId, CellErrorCode.CoherencyFailure, "The accepted metadata operation is no longer current."), current);
        if (receipt.Digest != digest)
            return Wrap(Failure(requestId, CellErrorCode.InvalidObject, "An accepted metadata index was reused for a different operation."), current);
        var bytes = await provider.Content.ReadVerifiedAsync(receipt.Response ?? throw new StorageCorruptionException("Missing current metadata receipt."),
            provider.Limits.MaxObjectBytes, cancellationToken);
        FsshttpbResponse response;
        try
        {
            var reader = new BinaryReaderEx(bytes);
            response = FsshttpbResponse.Deserialize(reader);
            if (reader.Remaining != 0 || response.SubResponses.Count != 1 || response.SubResponses[0].Status ||
                response.SubResponses[0].RequestType != RequestTypes.PutChanges)
                throw new StorageCorruptionException("Invalid metadata receipt response.");
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException or ArgumentException or OverflowException)
        {
            throw new StorageCorruptionException("Malformed metadata receipt response.", ex);
        }
        // Response preparation performs content I/O. Revalidate the same receipt
        // and authority afterward; a revocation or replacement must not become success.
        var authoritative = await provider.State.TransitionAsync(initial.ResourceId,
            (state, _) => new StateTransition<DocumentState>(null, state), cancellationToken);
        if (!Access(actor, authoritative).HasFlag(DocumentAccess.Write))
            return Wrap(new() { RequestId = requestId, RequestType = RequestTypes.PutChanges, Status = true,
                Error = CellBridgeAuthorization.AccessError() }, authoritative);
        if (authoritative.LifecycleGeneration != initial.LifecycleGeneration ||
            authoritative.Coordination.Generation != initial.Coordination.Generation ||
            authoritative.Partitions.Single(p => p.Kind == 1).StorageIndex != receipt.AcceptedStorageIndex ||
            authoritative.Receipts.FirstOrDefault(r => r.OperationKey == key && r.PartitionKind == 1) != receipt)
            return Wrap(Failure(requestId, CellErrorCode.CoherencyFailure, "The metadata retry became stale during response preparation."), authoritative);
        if (!canAppend(response.DataElementPackage))
            return Wrap(Failure(requestId, CellErrorCode.RequestNotSupported, "The mandatory retry payload cannot fit the response."), current);
        response.SubResponses[0].RequestId = requestId;
        return new(response, authoritative);
    }
}
