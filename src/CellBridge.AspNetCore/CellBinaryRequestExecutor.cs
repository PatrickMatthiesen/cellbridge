using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

/// <summary>Executes each binary operation in a SOAP Cell payload.</summary>
public static class CellBinaryRequestExecutor
{
    /// <summary>
    /// Builds one matching sub-response for each request. File PutChanges
    /// validates and commits its retained object graph. Explicit targets select
    /// a known partition; absent targets inherit the SOAP partition. Queries
    /// share the union of their independently selected payloads. EditorsTable
    /// queries require a response builder callback.
    /// </summary>
    public static FsshttpbResponse Execute(
        StoredDocument document,
        DocumentPartition partition,
        FsshttpbCellRequest request,
        DocumentAccess access,
        Func<ulong, FsshttpbResponse>? editorsTableQueryChanges = null,
        long maxResponseBytes = 512L * 1024 * 1024,
        Func<DocumentPartition, ulong, FsshttpbResponse>? partitionEditorsQueryChanges = null)
    {
        var response = new FsshttpbResponse();
        if (request.SubRequests.Count == 0)
        {
            response.Status = true;
            response.Error = new ResponseError(
                ErrorType.Protocol,
                (ulong)ProtocolErrorCode.RequestStreamSchemaError,
                "A cell request must contain at least one subrequest.");
            return response;
        }

        var queries = new QueryChangesResponseAssembler(response, maxResponseBytes);
        foreach (var subRequest in request.SubRequests)
        {
            if (subRequest.RequestType != RequestTypes.QueryAccess &&
                !access.HasFlag(subRequest.RequestType is RequestTypes.PutChanges or RequestTypes.AllocateExtendedGuidRange
                    ? DocumentAccess.Write : DocumentAccess.Read))
            {
                response.SubResponses.Add(CellBridge.AspNetCore.CellBridgeAuthorization.Denied(subRequest));
                continue;
            }
            if (!TryResolveTarget(subRequest, partition.Kind, out var kind))
            {
                response.SubResponses.Add(UnsupportedSubResponse(subRequest.RequestId, subRequest.RequestType));
                continue;
            }
            var selectedPartition = document.GetPartition(kind);
            if (subRequest.RequestType == RequestTypes.PutChanges)
            {
                response.SubResponses.Add(FilePartitionSaveHandler.Apply(document, selectedPartition, subRequest, request.DataElementPackage));
                continue;
            }
            if (subRequest.RequestType == RequestTypes.AllocateExtendedGuidRange)
            {
                response.SubResponses.Add(AllocateRange(subRequest));
                continue;
            }
            if (subRequest.RequestType == RequestTypes.QueryChanges)
            {
                Func<ulong, FsshttpbResponse>? editors = partitionEditorsQueryChanges is not null
                    ? id => partitionEditorsQueryChanges(selectedPartition, id)
                    : partition.Kind == DocumentPartitionKind.EditorsTable ? editorsTableQueryChanges : null;
                var queryChanges = QueryChanges(document, selectedPartition, subRequest, editors);
                queries.Append(queryChanges);
                continue;
            }

            response.SubResponses.Add(subRequest.RequestType == RequestTypes.QueryAccess
                ? QueryAccessSubResponse(subRequest.RequestId, access)
                : UnsupportedSubResponse(subRequest.RequestId, subRequest.RequestType));
        }

        return response;
    }

    internal static bool MatchesTarget(FsshttpbCellSubRequest request, DocumentPartitionKind kind) =>
        request.TargetPartitionId is not { } target || CellPartitionSelector.TryResolve(target, out var selected) && selected == kind;

    internal static bool TryResolveTarget(FsshttpbCellSubRequest request, DocumentPartitionKind fallback,
        out DocumentPartitionKind kind)
    {
        kind = fallback;
        return request.TargetPartitionId is not { } target || CellPartitionSelector.TryResolve(target, out kind);
    }

    private static FsshttpbSubResponse QueryAccessSubResponse(ulong requestId, DocumentAccess access) => new()
    {
        RequestId = requestId,
        RequestType = RequestTypes.QueryAccess,
        Data = new QueryAccessSubResponseData
        {
            ReadAccessError = access.HasFlag(DocumentAccess.Read) ? null : CellBridge.AspNetCore.CellBridgeAuthorization.AccessError(),
            WriteAccessError = access.HasFlag(DocumentAccess.Write) ? null : CellBridge.AspNetCore.CellBridgeAuthorization.AccessError(),
        },
    };

    private static FsshttpbSubResponse AllocateRange(FsshttpbCellSubRequest request)
    {
        if (request.Data is not AllocateExtendedGuidRangeSubRequestData data || data.RequestIdCount is 0 or > 100_000)
            return new FsshttpbSubResponse
            {
                RequestId = request.RequestId, RequestType = request.RequestType, Status = true,
                Error = new ResponseError(ErrorType.Protocol, (ulong)ProtocolErrorCode.RequestNotSupported,
                    "Allocation requires a count from 1 through 100000."),
            };
        // A new UUID namespace per request avoids a shared/restarted integer
        // counter. The integer zero is valid when the GUID component is non-null.
        ulong maximum = Math.Max(1000UL, data.RequestIdCount);
        return new FsshttpbSubResponse
        {
            RequestId = request.RequestId, RequestType = request.RequestType,
            Data = new AllocateExtendedGuidRangeSubResponseData
            {
                GuidComponent = Guid.NewGuid(), IntegerRangeMin = maximum - data.RequestIdCount,
                IntegerRangeMax = maximum,
            },
        };
    }

    private static FsshttpbResponse QueryChanges(
        StoredDocument document,
        DocumentPartition partition,
        FsshttpbCellSubRequest subRequest,
        Func<ulong, FsshttpbResponse>? editorsTableQueryChanges)
    {
        ulong requestId = subRequest.RequestId;
        var controls = subRequest.Data as QueryChangesSubRequestData;
        if (partition.Kind == DocumentPartitionKind.FileContents)
            return FileQuery(document, partition, requestId, controls);
        if (!FileQueryResponseBuilder.Supports(controls, partition.ProtocolIdentity.CellId))
            return FileQueryResponseBuilder.Unsupported(requestId);
        FsshttpbResponse response;
        if (partition.Kind == DocumentPartitionKind.EditorsTable)
        {
            response = editorsTableQueryChanges is not null
                ? editorsTableQueryChanges(requestId)
                : Unsupported(requestId, RequestTypes.QueryChanges);
        }
        else
        {
            // The application metadata stream remains an explicit placeholder.
            response = StorageManifestBuilder.BuildStorageIndexOnlyQueryChangesResponse(
                requestId,
                new ExGuid(1, partition.ProtocolIdentity.SerialGuid),
                partition.KnowledgeSequence,
                emitNullManifestMapping: true,
                includeCellKnowledge: true,
                waterlineCellStorage: partition.FssHttpBIdentity.CellId.ShortId);
        }
        if (response.DataElementPackage is null || response.SubResponses.FirstOrDefault()?.Data is not QueryChangesSubResponseData data)
            return response;
        var elements = response.DataElementPackage.DataElements;
        var selection = FileQueryResponseBuilder.Select(elements, data.StorageIndexExtendedGuid,
            partition.ProtocolIdentity.CellId, data.CellKnowledgeTo, controls);
        return FileQueryResponseBuilder.Build(requestId, selection,
            elements.Where(e => selection.PayloadIds.Contains(e.DataElementExtendedGuid)), controls);
    }

    private static FsshttpbResponse FileQuery(StoredDocument document, DocumentPartition partition, ulong requestId,
        QueryChangesSubRequestData? request)
    {
        lock (document)
        {
            var graph = partition.FileGraph;
            var indexes = graph.StorageIndexes;
            var currentIndex = indexes.Single(e => e.DataElementExtendedGuid.Equals(graph.StorageIndex));
            var manifestId = PartitionGraphSnapshot.ReadStorageManifestId(currentIndex);
            var manifest = graph.SelectElements(e => e.DataElementExtendedGuid.Equals(manifestId)).Single();
            var cells = PartitionGraphSnapshot.ReadFileCells(currentIndex, manifest);
            if (!FileQueryResponseBuilder.Supports(request, cells))
                return FileQueryResponseBuilder.Unsupported(requestId);
            var cell = FileQueryResponseBuilder.IsScoped(request) ? request!.CellId! : graph.FileCell;
            IReadOnlySet<ExGuid>? scope = null;
            if (FileQueryResponseBuilder.IsScoped(request) && cells.Count > 1)
            {
                try { scope = GenericPartitionGraphSnapshot.Create(graph.Elements, graph.StorageIndex).GetRequiredElements(cell).ToHashSet(); }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
                { return FileQueryResponseBuilder.Unsupported(requestId); }
            }
            var selected = FileQueryResponseBuilder.Select(graph.ElementMetadata, graph.StorageIndex,
                cell, partition.KnowledgeSequence, request, graph.MappingSerials, scope);
            return FileQueryResponseBuilder.Build(requestId, selected,
                graph.SelectElements(e => selected.PayloadIds.Contains(e.DataElementExtendedGuid)), request);
        }
    }

    private static FsshttpbResponse Unsupported(ulong requestId, RequestTypes requestType) => new()
    {
        SubResponses = { UnsupportedSubResponse(requestId, requestType) },
    };

    internal static FsshttpbSubResponse UnsupportedSubResponse(ulong requestId, RequestTypes requestType) => new()
    {
        RequestId = requestId,
        RequestType = requestType,
        Status = true,
        Error = new ResponseError(
            ErrorType.Protocol,
            (ulong)ProtocolErrorCode.RequestNotSupported,
            "The server does not apply this binary operation."),
    };
}
