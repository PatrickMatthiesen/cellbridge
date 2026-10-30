using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.Tests;

internal static class RepositoryTestPublications
{
    internal static async Task<DocumentHead> SaveAsync(CellBridgeDocumentService service, DocumentHead head, byte[] bytes,
        CellBridgeActor? actor = null)
    {
        actor ??= TestActor.Value;
        var request = await StorageTestDocuments.SaveRequestAsync(service, head, bytes, actor);
        var result = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), actor);
        Assert.False(result.IsCancelled);
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        return result.State;
    }

    internal static PreparedDocumentPublication Receipt(DocumentControlRead control, CellBridgeActor actor,
        string? hostToken = null, SaveOperationFamily family = SaveOperationFamily.WholeFile, string? operationKey = null) =>
        new(control.Head.ResourceId, TestStorage.Admit(control.Head, actor),
            new(control.Head.LifecycleGeneration, control.Head.AuthorityGeneration, control.Coordination.Generation,
                control.Head.StateVersion, control.Head.ContentVersion, []),
            new(new(control.Head.LifecycleGeneration, family, 0, operationKey ?? Guid.NewGuid().ToString("N")), "test-receipt-digest",
                actor.Identity.Subject, control.Head.ContentVersion, control.Head.LatestRevisionNumber, null, null), [])
        { HostLockToken = hostToken };
}
