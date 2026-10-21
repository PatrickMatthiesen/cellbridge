using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public sealed partial class CellBridgeDocumentService
{
    private static FsshttpbSubResponse QueryAccessSubResponse(ulong requestId, DocumentAccess access) => new()
    {
        RequestId = requestId,
        RequestType = RequestTypes.QueryAccess,
        Data = new QueryAccessSubResponseData
        {
            ReadAccessError = access.HasFlag(DocumentAccess.Read) ? null : CellBridgeAuthorization.AccessError(),
            WriteAccessError = access.HasFlag(DocumentAccess.Write) ? null : CellBridgeAuthorization.AccessError(),
        },
    };

    private static FsshttpbSubResponse AllocateRange(FsshttpbCellSubRequest request)
    {
        if (request.Data is not AllocateExtendedGuidRangeSubRequestData data || data.RequestIdCount is 0 or > 100_000)
            return new FsshttpbSubResponse
            {
                RequestId = request.RequestId, RequestType = request.RequestType, Status = true,
                Error = new ResponseError(ErrorType.Cell, (ulong)CellErrorCode.RequestNotSupported,
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

    private static FsshttpbSubResponse UnsupportedSubResponse(ulong requestId, RequestTypes requestType) => new()
    {
        RequestId = requestId,
        RequestType = requestType,
        Status = true,
        Error = new ResponseError(
            ErrorType.Cell,
            (ulong)(requestType is RequestTypes.QueryAccess or RequestTypes.QueryChanges or RequestTypes.PutChanges or RequestTypes.AllocateExtendedGuidRange
                ? CellErrorCode.RequestNotSupported : CellErrorCode.UnknownRequest),
            "The server does not apply this binary operation."),
    };

    private FsshttpbResponse GeneratedPartitionQuery(StoredDocument document, DocumentPartition partition,
        FsshttpbCellSubRequest operation, DocumentState state)
    {
        var controls = operation.Data as QueryChangesSubRequestData;
        if (!FileQueryResponseBuilder.Supports(controls, partition.ProtocolIdentity.CellId))
            return FileQueryResponseBuilder.Unsupported(operation.RequestId);
        // Uploaded metadata graphs and file graphs use the selective query handlers.
        // Only generated metadata and editor snapshots reach this handler.
        var response = partition.Kind == DocumentPartitionKind.EditorsTable
            ? EditorsQuery(document, partition, operation.RequestId, state)
            : StorageManifestBuilder.BuildStorageIndexOnlyQueryChangesResponse(operation.RequestId,
                new ExGuid(1, partition.ProtocolIdentity.SerialGuid), partition.KnowledgeSequence,
                emitNullManifestMapping: true, includeCellKnowledge: true,
                waterlineCellStorage: partition.FssHttpBIdentity.CellId.ShortId);
        if (response.DataElementPackage is null || response.SubResponses.FirstOrDefault()?.Data is not QueryChangesSubResponseData data)
            return response;
        var elements = response.DataElementPackage.DataElements;
        var selection = FileQueryResponseBuilder.Select(elements, data.StorageIndexExtendedGuid,
            partition.ProtocolIdentity.CellId, data.CellKnowledgeTo, controls);
        return FileQueryResponseBuilder.Build(operation.RequestId, selection,
            elements.Where(e => selection.PayloadIds.Contains(e.DataElementExtendedGuid)), controls);
    }
}
