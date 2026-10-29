using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

internal sealed partial class DocumentLoadScenario
{
    private static FsshttpbCellRequest FullQuery()
    {
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 1,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } });
        return request;
    }

    private static async Task<(PartitionGraphSnapshot Graph, ClientKnowledge Knowledge)> ReadClientGraphAsync(
        CellBridgeDocumentService documents, DocumentHead head, CellBridgeActor owner, CancellationToken cancellationToken)
    {
        var result = await documents.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents,
            FullQuery(), new Dictionary<string, string>(), owner, cancellationToken);
        if (result.IsCancelled) throw new OperationCanceledException(cancellationToken);
        CheckResponse(result.Response);
        var query = result.Response.SubResponses.Single().Data as QueryChangesSubResponseData
            ?? throw new InvalidDataException("Query did not return graph metadata.");
        return (PartitionGraphSnapshot.Create(result.Response.DataElementPackage!.DataElements, query.StorageIndexExtendedGuid),
            ClientKnowledge.Deserialize(new(query.KnowledgeBytes!)));
    }

}
