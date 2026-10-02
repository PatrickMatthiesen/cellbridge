using System.Collections.Immutable;
using System.Security.Cryptography;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Web;

namespace CellBridge.AspNetCore;

public sealed record CellExecution(FsshttpbResponse Response, DocumentState State, string? LockError = null);

/// <summary>Prepares file revisions outside coordination transactions and publishes them durably.</summary>
public sealed class CellBridgeDocumentService(StorageProvider provider)
{
    public StorageProvider Provider => provider;

    public ValueTask<DocumentState?> ResolveAsync(FssHttpRequest request, CancellationToken cancellationToken = default)
    {
        if (request.UseResourceId && request.ResourceId is not null)
            return Guid.TryParse(request.ResourceId, out var id)
                ? provider.State.FindByResourceIdAsync(id, cancellationToken)
                : ValueTask.FromResult<DocumentState?>(null);
        return string.IsNullOrWhiteSpace(request.Url) ? ValueTask.FromResult<DocumentState?>(null)
            : provider.State.FindByPathKeyAsync(StorageIds.PathKey(DocumentStore.NormalizeUrl(request.Url)), cancellationToken);
    }

    public async ValueTask<DocumentState?> CreateAsync(string escapedPath, byte[] bytes, CancellationToken cancellationToken = default)
    {
        var documents = new DocumentStore();
        var document = documents.Put(escapedPath, bytes);
        var state = await document.CaptureAsync(provider.Content, cancellationToken: cancellationToken);
        return await provider.State.TryCreateAsync(state, cancellationToken) ? state : null;
    }

    public async ValueTask<CellExecution> ExecuteAsync(Guid id, DocumentPartitionKind kind,
        FsshttpbCellRequest request, IReadOnlyDictionary<string, string> attributes,
        CancellationToken cancellationToken = default)
    {
        var response = new FsshttpbResponse();
        var state = await CurrentAsync(id, cancellationToken);
        if (request.SubRequests.Any(s => s.Data is PutChangesSubRequestData p && (p.Flags & 0x26) != 0))
        {
            foreach (var operation in request.SubRequests)
                response.SubResponses.Add(Failure(operation.RequestId, CellErrorCode.RequestNotSupported,
                    "Partial and multi-request uploads are unsupported; no operation was published.", operation.RequestType));
            return new(response, state);
        }
        if (request.SubRequests.Count == 0)
        {
            var emptyDocument = StoredDocument.RestoreMetadata(state, DateTime.UtcNow);
            return new(CellBinaryRequestExecutor.Execute(emptyDocument, emptyDocument.GetPartition(kind), request), state);
        }
        // Each operation has its own publication boundary. QueryChanges continues
        // to permit only one returned data package, matching the existing executor.
        bool queried = false;
        foreach (var operation in request.SubRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operation.RequestType == RequestTypes.PutChanges)
            {
                var saved = await SaveAsync(id, kind, operation, request.DataElementPackage, attributes, cancellationToken);
                response.SubResponses.AddRange(saved.Response.SubResponses);
                state = saved.State;
                if (saved.LockError is not null) return new(response, state, saved.LockError);
                continue;
            }
            state = await CurrentAsync(id, cancellationToken);
            if (operation.RequestType == RequestTypes.QueryChanges && queried)
            {
                response.SubResponses.Add(Failure(operation.RequestId, CellErrorCode.RequestNotSupported, "Repeated queries are not supported.", operation.RequestType));
                continue;
            }
            var observedNow = DateTime.UtcNow;
            if (kind == DocumentPartitionKind.EditorsTable && operation.RequestType == RequestTypes.QueryChanges)
                (state, observedNow) = await ExpireEditorsAsync(id, cancellationToken);
            var document = kind == DocumentPartitionKind.FileContents && operation.RequestType == RequestTypes.QueryChanges
                ? await StoredDocument.RestoreAsync(state, provider.Content, cancellationToken)
                : StoredDocument.RestoreMetadata(state, observedNow);
            var single = new FsshttpbCellRequest { DataElementPackage = request.DataElementPackage };
            single.SubRequests.Add(operation);
            var partition = document.GetPartition(kind);
            var result = CellBinaryRequestExecutor.Execute(document, partition, single,
                requestId => EditorsQuery(document, partition, requestId));
            response.SubResponses.AddRange(result.SubResponses);
            if (result.DataElementPackage is not null) response.DataElementPackage = result.DataElementPackage;
            if (operation.RequestType == RequestTypes.QueryChanges) queried = true;
        }
        // MS-FSSHTTP GetFileProps describes the updated version after the Cell
        // operations. Capture its metadata here; the SOAP adapter never rereads it.
        return new(response, state);
    }

    private async ValueTask<CellExecution> SaveAsync(Guid id, DocumentPartitionKind kind,
        FsshttpbCellSubRequest operation, DataElementPackage? package, IReadOnlyDictionary<string, string> attributes,
        CancellationToken cancellationToken)
    {
        if (kind != DocumentPartitionKind.FileContents || operation.Data is not PutChangesSubRequestData put || package is null)
            return await FailedAsync(id, operation.RequestId, CellErrorCode.RequestNotSupported, "Only complete file partition writes are supported.", cancellationToken);
        if ((put.Flags & ~0x59) != 0 || (put.AdditionalFlagsBits & 0x38) != 0)
            return await FailedAsync(id, operation.RequestId, CellErrorCode.RequestNotSupported, "This upload mode is unsupported.", cancellationToken);
        string key = $"{put.StorageIndex.Value}:{put.StorageIndex.Guid:D}";
        string digest;
        try { digest = Digest(put, package); }
        catch (InvalidDataException ex) { return await FailedAsync(id, operation.RequestId, CellErrorCode.InvalidObject, ex.Message, cancellationToken); }
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var before = await CurrentAsync(id, cancellationToken);
            if (before.Receipts.FirstOrDefault(r => r.OperationKey == key) is { } prior)
                return await RepeatAsync(before, prior, digest, operation.RequestId, cancellationToken);
            var document = await StoredDocument.RestoreAsync(before, provider.Content, cancellationToken);
            var result = FilePartitionSaveHandler.Apply(document, document.FilePartition, operation, package);
            if (result.Status) return Wrap(result, before);
            var candidate = await document.CaptureAsync(provider.Content, before.Coordination, before.Receipts, cancellationToken);
            var receiptResponse = new FsshttpbResponse();
            receiptResponse.SubResponses.Add(result);
            using var receiptStream = new MemoryStream(receiptResponse.ToByteArray());
            var responseHandle = await provider.Content.WriteAsync(receiptStream, cancellationToken);
            var receipt = new SaveReceipt(key, digest, candidate.ContentVersion, responseHandle);
            PublishResult published;
            try
            {
                published = await provider.State.TransitionAsync(id, (current, now) =>
                {
                    if (current.Receipts.FirstOrDefault(r => r.OperationKey == key) is { } accepted)
                        return new StateTransition<PublishResult>(null, new(current, accepted, null, false));
                    if (current.ContentVersion != before.ContentVersion || current.Content != before.Content)
                        return new StateTransition<PublishResult>(null, new(current, null, null, true));
                    var metadata = StoredDocument.RestoreMetadata(current, now);
                    var coordinator = FssHttpLockCoordinator.Restore(metadata, current.Coordination, now);
                    if (!coordinator.ExecuteCellWrite(attributes, () => true, out _, out var error, now))
                        return new StateTransition<PublishResult>(null, new(current, null, error, false));
                    var state = metadata.CaptureCoordination(current, coordinator.Capture()) with
                    {
                        Content = candidate.Content,
                        StateVersion = checked(current.StateVersion + 1),
                        ContentVersion = candidate.ContentVersion,
                        ModifiedUtc = candidate.ContentVersion == before.ContentVersion ? current.ModifiedUtc : now,
                        // Keep the authoritative editors partition, including updates
                        // made while file materialization/staging was in progress.
                        Partitions = candidate.Partitions.Select(p => p.Kind == 2
                            ? current.Partitions.Single(x => x.Kind == 2) with
                            { Knowledge = metadata.EditorsTablePartition.KnowledgeSequence,
                              InlineContent = metadata.EditorsTablePartition.Content.ToImmutableArray() }
                            : p.Kind == 1 && candidate.ContentVersion != before.ContentVersion ? p with
                            { InlineContent = System.Text.Encoding.UTF8.GetBytes(
                                $"<Metadata ContentVersion=\"{candidate.ContentVersion}\" Modified=\"{now.Ticks}\" />").ToImmutableArray() }
                            : p).ToImmutableArray(),
                        Coordination = coordinator.Capture() with { Generation = checked(current.Coordination.Generation + 1) },
                        Receipts = current.Receipts.Add(receipt),
                    };
                    return new StateTransition<PublishResult>(state, new(state, null, null, false));
                }, cancellationToken);
            }
            catch (StorageUnavailableException)
            {
                // A lost commit response is not a rollback. Resolve the durable
                // receipt before attempting to apply this operation again.
                var current = await CurrentAsync(id, CancellationToken.None);
                if (current.Receipts.FirstOrDefault(r => r.OperationKey == key) is not { } accepted) throw;
                return await RepeatAsync(current, accepted, digest, operation.RequestId, CancellationToken.None);
            }
            if (published.Receipt is { } duplicate)
                return await RepeatAsync(published.State, duplicate, digest, operation.RequestId, cancellationToken);
            if (published.LockError is not null) return new(new FsshttpbResponse(), published.State, published.LockError);
            if (published.Retry) continue;
            return Wrap(result, published.State);
        }
        return await FailedAsync(id, operation.RequestId, CellErrorCode.CoherencyFailure, "The document changed while preparing this save.", cancellationToken);
    }

    private async ValueTask<CellExecution> RepeatAsync(DocumentState state, SaveReceipt receipt, string digest,
        ulong requestId, CancellationToken cancellationToken)
    {
        if (receipt.Digest != digest)
            return Wrap(Failure(requestId, CellErrorCode.InvalidObject, "An accepted storage index was reused for a different operation."), state);
        if (state.ContentVersion != receipt.ContentVersion)
            return Wrap(Failure(requestId, CellErrorCode.CoherencyFailure, "The accepted operation has been superseded by a later revision."), state);
        await using var stream = await provider.Content.OpenReadAsync(receipt.Response, cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        var response = FsshttpbResponse.Deserialize(new BinaryReaderEx(buffer.ToArray()));
        response.SubResponses.Single().RequestId = requestId;
        return new(response, state);
    }

    private ValueTask<DocumentState> CurrentAsync(Guid id, CancellationToken cancellationToken) => GetCurrentAsync(id, cancellationToken);
    private ValueTask<(DocumentState State, DateTime Now)> ExpireEditorsAsync(Guid id, CancellationToken cancellationToken) =>
        provider.State.TransitionAsync(id, (current, now) =>
        {
            if (!current.Editors.Any(e => e.ExpiresUtc <= now))
                return new StateTransition<(DocumentState, DateTime)>(null, (current, now));
            var document = StoredDocument.RestoreMetadata(current, now);
            var next = document.CaptureCoordination(current, current.Coordination) with
            {
                StateVersion = checked(current.StateVersion + 1),
                Coordination = current.Coordination with { Generation = checked(current.Coordination.Generation + 1) },
            };
            return new StateTransition<(DocumentState, DateTime)>(next, (next, now));
        }, cancellationToken);
    private async ValueTask<DocumentState> GetCurrentAsync(Guid id, CancellationToken cancellationToken) =>
        await provider.State.FindByResourceIdAsync(id, cancellationToken) ?? throw new KeyNotFoundException("Document does not exist.");
    private async ValueTask<CellExecution> FailedAsync(Guid id, ulong requestId, CellErrorCode error, string message, CancellationToken cancellationToken) =>
        Wrap(Failure(requestId, error, message), await CurrentAsync(id, cancellationToken));
    private static CellExecution Wrap(FsshttpbSubResponse response, DocumentState state)
    {
        var result = new FsshttpbResponse(); result.SubResponses.Add(response); return new(result, state);
    }
    private static FsshttpbSubResponse Failure(ulong requestId, CellErrorCode error, string message, RequestTypes requestType = RequestTypes.PutChanges) => new()
    {
        RequestId = requestId, RequestType = requestType, Status = true,
        Error = new ResponseError(ErrorType.Cell, (ulong)error, message),
    };
    private static string Digest(PutChangesSubRequestData put, DataElementPackage package)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var writer = new BinaryWriterEx();
        put.StorageIndex.Serialize(writer); put.ExpectedStorageIndex.Serialize(writer);
        writer.WriteByte((byte)(put.Flags & 1));
        writer.WriteUInt16((ushort)(put.AdditionalFlagsBits & 7));
        hash.AppendData(writer.ToArray());
        // Normalize identical duplicate elements and wire order. Request IDs,
        // transport tokens and ignored reserved bits are not operation identity.
        foreach (var group in package.DataElements.GroupBy(e => e.DataElementExtendedGuid)
                     .OrderBy(g => g.Key.Guid).ThenBy(g => g.Key.Value))
        {
            var element = group.First();
            if (group.Any(e => e.DataElementType != element.DataElementType ||
                !e.SerialNumber.Equals(element.SerialNumber) || !(e.Data ?? []).SequenceEqual(element.Data ?? [])))
                throw new InvalidDataException("Conflicting duplicate data element identifiers.");
            writer = new BinaryWriterEx();
            element.DataElementExtendedGuid.Serialize(writer);
            writer.WriteUInt32((uint)element.DataElementType); element.SerialNumber.Serialize(writer);
            writer.WriteUInt64((ulong)(element.Data?.LongLength ?? 0));
            hash.AppendData(writer.ToArray());
            hash.AppendData(element.Data ?? []);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static FsshttpbResponse EditorsQuery(StoredDocument doc, DocumentPartition partition, ulong requestId) =>
        EditorsTablePartitionBuilder.BuildSharePointV13QueryChangesResponse(requestId,
            doc.Sessions.Select(s => new EditorsTableEditor(s.ClientId.ToString("D"), s.ExpiresUtc.Ticks,
                s.UserName, s.UserName, HasEditorPermission: s.AsEditor, Metadata: s.Metadata)), partition.FssHttpBIdentity.CellId,
            partition.FssHttpBIdentity.SerialGuid, partition.KnowledgeSequence);
    private sealed record PublishResult(DocumentState State, SaveReceipt? Receipt, string? LockError, bool Retry);
}
