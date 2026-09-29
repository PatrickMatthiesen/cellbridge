using OfficeCollabServer.FssHttpB;
using OfficeCollabServer.Storage;

namespace OfficeCollabServer.Web;

/// <summary>Executes each binary operation in a SOAP Cell payload.</summary>
public static class CellBinaryRequestExecutor
{
    /// <summary>
    /// Builds one matching sub-response for each request. File PutChanges
    /// validates and commits its retained object graph. The response model supports one
    /// QueryChanges data-element package per response. A repeated QueryChanges
    /// therefore receives RequestNotSupported until query filters can be
    /// applied independently. EditorsTable queries require the SOAP-specific
    /// response builder callback.
    /// </summary>
    public static FsshttpbResponse Execute(
        StoredDocument document,
        DocumentPartition partition,
        FsshttpbCellRequest request,
        Func<ulong, FsshttpbResponse>? editorsTableQueryChanges = null)
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

        bool queryChangesAlreadyHandled = false;
        foreach (var subRequest in request.SubRequests)
        {
            if (subRequest.RequestType == RequestTypes.PutChanges)
            {
                response.SubResponses.Add(FilePartitionSaveHandler.Apply(document, partition, subRequest, request.DataElementPackage));
                continue;
            }
            if (subRequest.RequestType == RequestTypes.QueryChanges && queryChangesAlreadyHandled)
            {
                response.SubResponses.Add(UnsupportedSubResponse(subRequest.RequestId, subRequest.RequestType));
                continue;
            }

            if (subRequest.RequestType == RequestTypes.QueryChanges)
            {
                queryChangesAlreadyHandled = true;
                var queryChanges = QueryChanges(document, partition, subRequest, editorsTableQueryChanges);
                response.SubResponses.AddRange(queryChanges.SubResponses);
                response.DataElementPackage = queryChanges.DataElementPackage;
                continue;
            }

            response.SubResponses.Add(subRequest.RequestType == RequestTypes.QueryAccess
                ? QueryAccessSubResponse(subRequest.RequestId)
                : UnsupportedSubResponse(subRequest.RequestId, subRequest.RequestType));
        }

        return response;
    }

    private static FsshttpbSubResponse QueryAccessSubResponse(ulong requestId) => new()
    {
        RequestId = requestId,
        RequestType = RequestTypes.QueryAccess,
        Data = new QueryAccessSubResponseData(),
    };

    private static FsshttpbResponse QueryChanges(
        StoredDocument document,
        DocumentPartition partition,
        FsshttpbCellSubRequest subRequest,
        Func<ulong, FsshttpbResponse>? editorsTableQueryChanges)
    {
        ulong requestId = subRequest.RequestId;
        if (partition.Kind == DocumentPartitionKind.EditorsTable)
        {
            return editorsTableQueryChanges is not null
                ? editorsTableQueryChanges(requestId)
                : Unsupported(requestId, RequestTypes.QueryChanges);
        }

        // Word's FileContents query asks for both the storage manifest and
        // cell changes. Returning only a StorageIndex makes the SOAP request
        // technically successful but leaves Word without the revision/object
        // graph and it falls back to a direct GET, losing the edit session.
        // Metadata has a different application stream shape, so retain its
        // validated StorageIndex response until that stream is modelled.
        FsshttpbResponse response = partition.Kind == DocumentPartitionKind.FileContents
            ? FileQuery(document, partition, requestId)
            : StorageManifestBuilder.BuildStorageIndexOnlyQueryChangesResponse(
                requestId,
                partition.FssHttpBIdentity.CellId.LongId,
                partition.KnowledgeSequence,
                emitNullManifestMapping: true,
                includeCellKnowledge: true,
                waterlineCellStorage: partition.FssHttpBIdentity.CellId.ShortId);

        QueryChangesResponseShaper.Apply(response, subRequest.Data as QueryChangesSubRequestData);
        return response;
    }

    private static FsshttpbResponse FileQuery(StoredDocument document, DocumentPartition partition, ulong requestId)
    {
        lock (document)
        {
            var graph = partition.FileGraph;
            var package = new DataElementPackage();
            package.DataElements.AddRange(graph.Elements);
            return new FsshttpbResponse
            {
                DataElementPackage = package,
                SubResponses = { new FsshttpbSubResponse
                {
                    RequestId = requestId,
                    RequestType = RequestTypes.QueryChanges,
                    Data = FilePartitionSaveHandler.CreateQueryData(partition, graph, partition.KnowledgeSequence),
                } },
            };
        }
    }

    private static FsshttpbResponse Unsupported(ulong requestId, RequestTypes requestType) => new()
    {
        SubResponses = { UnsupportedSubResponse(requestId, requestType) },
    };

    private static FsshttpbSubResponse UnsupportedSubResponse(ulong requestId, RequestTypes requestType) => new()
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
