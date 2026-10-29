using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

/// <summary>Explicit offline legacy writer for migration/codec fixtures. Never used by ordinary repository tests.</summary>
internal static class LegacyStorageFixture
{
    internal static async Task<DocumentState> CreateAsync(IContentStore content, string path, byte[] bytes)
    {
        var document = new DocumentStore().Put(path, bytes);
        document.Security = DocumentSecurity.Create(TestActor.Value.Identity);
        return RevisionHistory.Initialize(await document.CaptureAsync(content));
    }

    internal static async Task<(DocumentState State, FsshttpbCellRequest Request, FsshttpbResponse Response)> SaveAsync(
        DocumentState current, IContentStore content, byte[] bytes)
    {
        var document = await StoredDocument.RestoreAsync(current, content);
        var request = SyntheticWorkload.Save(document.FilePartition.FileGraph, bytes, "changed-full",
            checked((int)current.ContentVersion), new Random(Random.Shared.Next()));
        var operation = request.SubRequests.Single();
        var put = Assert.IsType<PutChangesSubRequestData>(operation.Data);
        var graph = document.FilePartition.FileGraph.Merge(request.DataElementPackage!.DataElements,put.StorageIndex);
        Assert.Equal(bytes,graph.Materialize());
        document.CommitFileRevision(graph,bytes,checked(document.FilePartition.KnowledgeSequence + 1));
        var knowledge = new BinaryWriterEx();
        ClientKnowledge.FromElements(graph.Elements).Serialize(knowledge);
        var response = new FsshttpbResponse
        {
            SubResponses = { new() { RequestId = operation.RequestId,RequestType = RequestTypes.PutChanges,
                Data = new PutChangesSubResponseData { PutChangesResponse = new() { AppliedStorageIndexID = put.StorageIndex },
                    KnowledgeBytes = knowledge.ToArray() } } },
            DataElementPackage = new() { DataElements = [graph.Elements.Single(element => element.DataElementExtendedGuid.Equals(put.StorageIndex))] },
        };
        using var responseStream = new MemoryStream(response.ToByteArray());
        var responseHandle = await content.WriteAsync(responseStream);
        var identity = IndexedPartitionSavePreparer.Identify(current.LifecycleGeneration, 0, operation, request.DataElementPackage!);
        var receipts = current.Receipts.Add(new(identity.Operation.OperationKey, identity.Digest,
            document.ContentVersion, responseHandle, TestActor.Value.Identity.Subject));
        var next = await document.CaptureAsync(content, receipts: receipts);
        next = RevisionHistory.Append(current, next with { StateVersion = current.StateVersion + 1 },
            TestActor.Value.Identity, next.ModifiedUtc);
        return (next, request, response);
    }
}
