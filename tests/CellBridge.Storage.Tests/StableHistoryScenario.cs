using System.Collections.Immutable;
using System.Xml.Linq;
using CellBridge.FssHttp;
using CellBridge.Storage.InMemory;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage;
using CellBridge.Storage.Tests;
using CellBridge.TestFixtures;
using Xunit;

namespace CellBridge.Tests;

internal static class StableHistoryScenario
{
    private static readonly CellBridgeActor Owner = new(new("history:owner", "owner", "Owner"), CanCreate: true);
    private static readonly Guid DocumentLibraryId = Guid.Parse("c4aa42f0-0c5c-4b8d-a3cf-733ff51ed962");

    internal static async Task RunAsync(StorageProvider provider)
    {
        await BoundsAsync(provider);
        var writer = new CellBridgeDocumentService(provider);
        var head = await SeedAsync(writer);
        for (var mode = 0; mode < 3; mode++)
        {
            var count = 300 + mode;
            var proxy = new BetweenPages(provider.Documents, () => SaveAsync(writer, head.ResourceId));
            var reader = new CellBridgeDocumentService(new(proxy, provider.Content));
            IEnumerable<ulong> numbers;
            if (mode == 0)
                numbers = (await reader.ListRevisionsAsync(head.ResourceId, Owner)).Select(r => r.RevisionNumber);
            else
            {
                var sub = new FssHttpSubRequest { Type = mode == 1 ? SubRequestType.GetVersions : SubRequestType.Versioning, SubRequestToken = 1 };
                if (mode == 2) sub.SubRequestDataAttributes["VersioningRequestType"] = "GetVersionList";
                var processor = new CellBridgeRequestProcessor(reader,
                    options: mode == 1 ? new() { DocumentLibraryId = DocumentLibraryId } : null);
                var result = await processor.ExecuteAsync(new() { Requests = { new() { Url = "https://host.test/absent.docx",
                    ResourceId = head.ResourceId.ToString(), UseResourceId = true, RequestToken = 1, SubRequests = { sub } } } }, "https://host.test", Owner);
                var response = Assert.Single(Assert.Single(result.Response.Responses).SubResponses);
                Assert.Equal("Success", response.ErrorCode);
                var xml = XElement.Parse("<root>" + (mode == 1 ? response.SubResponseXml : response.SubResponseDataXml) + "</root>");
                numbers = xml.Descendants(mode == 1 ? "result" : "Version").Select(e =>
                    ulong.Parse(((string)e.Attribute(mode == 1 ? "version" : "Number")!).TrimStart('@').Split('.')[0]));
            }
            Assert.Equal(Enumerable.Range(1, count).Reverse().Select(i => (ulong)i), numbers);
            Assert.Equal((ulong)count + 1, (await provider.Documents.FindAsync(new DocumentIdentity.Resource(head.ResourceId)))!.LatestRevisionNumber);
            Assert.Equal(2, proxy.Reads);
        }
    }

    internal static async Task FenceAsync(StorageProvider provider, string change)
    {
        var writer = new CellBridgeDocumentService(provider);
        var head = await SeedAsync(writer);
        using var cancellation = new CancellationTokenSource();
        var proxy = new BetweenPages(provider.Documents, async () =>
        {
            var current = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(head.ResourceId)))!;
            if (change == "cancel") { cancellation.Cancel(); return; }
            if (change == "revoke")
            {
                Assert.True((await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(current.ResourceId,
                    new(Owner.Identity, current.ResourceId, current.LifecycleGeneration, current.Security.AuthorizationPolicy, DocumentAccess.Read | DocumentAccess.Write)
                    { AuthorityGeneration = current.AuthorityGeneration, StateVersion = current.StateVersion },
                    current.Security with { Owner = "revoked" }, current.AuthorityGeneration))).Changed);
                return;
            }
            Assert.True((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(current.ResourceId,
                current.LifecycleGeneration, current.StateVersion))).Applied);
            if (change == "recreate")
            {
                var retired = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(current.ResourceId, true)))!;
                var replacement = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), current.Path, MinimalDocx.Create(),
                    current.Security, provider.Content, provider.Limits, lifecycleGeneration: current.LifecycleGeneration + 1);
                Assert.True((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(current.ResourceId,
                    retired.LifecycleGeneration, retired.StateVersion, replacement))).Applied);
            }
        });
        var reader = new CellBridgeDocumentService(new(proxy, provider.Content));
        var listing = reader.ListRevisionsAsync(head.ResourceId, Owner, cancellation.Token).AsTask();
        if (change == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing);
        else if (change == "revoke") await Assert.ThrowsAsync<UnauthorizedAccessException>(() => listing);
        else await Assert.ThrowsAsync<KeyNotFoundException>(() => listing);
        Assert.Equal(2, proxy.Reads);
    }

    private static async Task<DocumentHead> SeedAsync(CellBridgeDocumentService service)
    {
        var head = (await service.CreateAsync("/history-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), Owner))!;
        for (var i = 1; i < 300; i++) await SaveAsync(service, head.ResourceId);
        return head;
    }

    private static async Task BoundsAsync(StorageProvider provider)
    {
        var memory = new InMemoryDocumentRepository(provider.Content);
        var service = new CellBridgeDocumentService(new(memory, provider.Content));
        var head = (await service.CreateAsync("/gaps-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), Owner))!;
        await SaveAsync(service, head.ResourceId);
        await SaveAsync(service, head.ResourceId);
        var snapshot = (await memory.ExportDocumentAsync(head.ResourceId))!;
        ulong[] numbers = [1, (ulong)long.MaxValue + 7, ulong.MaxValue];
        var revisions = snapshot.ApplicationRevisions.Select((r, i) => r with
            { RevisionNumber = numbers[i], Package = r.Package with { RevisionNumber = numbers[i] } }).ToImmutableArray();
        snapshot = snapshot with { ApplicationRevisions = revisions,
            Current = snapshot.Current with { Head = snapshot.Current.Head with { LatestRevisionNumber = ulong.MaxValue, Package = revisions[^1].Package } },
            Receipts = snapshot.Receipts.Select(r => r with { RevisionNumber = ulong.MaxValue }).ToImmutableArray() };
        snapshot = snapshot with { Current = snapshot.Current with { Head = snapshot.Current.Head with { Counters = snapshot.Current.Head.Counters with
            { StoredBytes = NormalizedDocumentRecovery.StoredBytes(snapshot) } } } };
        await provider.Documents.ImportRecoveryAsync(new([snapshot], []), new(Guid.NewGuid(), new string('a', 64), 1, 1));
        var page = (await provider.Documents.ReadRevisionsAsync(head.ResourceId, 0, 1, beforeRevision: ulong.MaxValue))!;
        Assert.Equal(numbers[1], Assert.Single(page.Revisions).RevisionNumber);
        Assert.Equal(1, page.NextOffset);
        var offset = (await provider.Documents.ReadRevisionsAsync(head.ResourceId, 1, 1, beforeRevision: ulong.MaxValue))!;
        Assert.Equal(1UL, Assert.Single(offset.Revisions).RevisionNumber);
        Assert.Null(offset.NextOffset);
        var gap = (await provider.Documents.ReadRevisionsAsync(head.ResourceId, 0, 256, beforeRevision: numbers[1] - 1))!;
        Assert.Equal(1UL, Assert.Single(gap.Revisions).RevisionNumber);
        Assert.Null(gap.NextOffset);
        Assert.Empty((await provider.Documents.ReadRevisionsAsync(head.ResourceId, 0, 256, beforeRevision: 0))!.Revisions);
        Assert.Empty((await provider.Documents.ReadRevisionsAsync(head.ResourceId, int.MaxValue, 256, beforeRevision: ulong.MaxValue))!.Revisions);
        Assert.Equal(numbers.Reverse(), (await new CellBridgeDocumentService(provider).ListRevisionsAsync(head.ResourceId, Owner)).Select(r => r.RevisionNumber));
    }

    private static async Task SaveAsync(CellBridgeDocumentService service, Guid id)
    {
        var index = new ExGuid(1, Guid.NewGuid());
        var request = new FsshttpbCellRequest { DataElementPackage = new() };
        request.DataElementPackage.DataElements.Add(new(DataElementType.StorageIndexDataElementData, index, SerialNumber.Null) { Data = [] });
        request.SubRequests.Add(new(RequestTypes.PutChanges) { RequestId = 1, Data = new PutChangesSubRequestData { StorageIndex = index } });
        var saved = await service.ExecuteAsync(id, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), Owner);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
    }

    private sealed class BetweenPages(IDocumentRepository inner, Func<Task> action) : RepositoryTestProxy(inner)
    {
        public int Reads { get; private set; }
        public override async ValueTask<DocumentRevisionPage?> ReadRevisionsAsync(Guid id, int offset, int limit, ulong? beforeRevision = null, CancellationToken ct = default)
        {
            if (++Reads == 2) await action();
            return await Inner.ReadRevisionsAsync(id, offset, limit, beforeRevision, ct);
        }
    }
}
