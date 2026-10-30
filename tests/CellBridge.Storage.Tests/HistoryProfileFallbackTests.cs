using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class HistoryProfileFallbackTests
{
    [Fact]
    public Task NumericVersionQueriesReadRetainedGraphsWithoutPublishing() => Check(StorageTestDocuments.Memory());

    [PostgreSqlFact]
    public async Task NumericVersionQueriesReadRetainedPostgreSqlGraphs()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await Check(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)));
    }

    private static async Task Check(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        byte[] original = MinimalDocx.Create("original historical text"), latest = MinimalDocx.Create("later saved marker");
        var initial = (await service.CreateAsync($"/history-query-{Guid.NewGuid():N}.docx", original, TestActor.Value))!;
        var head = await RepositoryTestPublications.SaveAsync(service, initial, latest);
        var reader = TestActor.Value with { AccessLimit = new(head.ResourceId, DocumentAccess.Read) };
        async Task<CellExecution> Query(QueryChangesVersion? version, CellBridgeActor? actor = null)
        {
            var query = new FsshttpbCellRequest { SubRequests = { new(RequestTypes.QueryChanges) { RequestId = 1,
                Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true, Version = version } } } };
            return await new CellBridgeDocumentService(provider).ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents,
                query, new Dictionary<string, string>(), actor ?? reader);
        }
        static byte[] Bytes(CellExecution result)
        {
            var sub = Assert.Single(result.Response.SubResponses);
            Assert.False(sub.Status);
            var graph = PartitionGraphSnapshot.Create(result.Response.DataElementPackage!.DataElements,
                Assert.IsType<QueryChangesSubResponseData>(sub.Data).StorageIndexExtendedGuid);
            return graph.Materialize();
        }
        Assert.Equal(original, Bytes(await Query(new(1, 0, []))));
        Assert.Equal(original, Bytes(await Query(new(null, null, [3, 0, 0]))));
        Assert.Equal(latest, Bytes(await Query(new(null, null, [5, 0, 0]))));
        Assert.Equal(latest, Bytes(await Query(new(2, 0, []))));
        Assert.Equal(latest, Bytes(await Query(new(0, 0, []))));
        Assert.Equal(latest, Bytes(await Query(null)));
        var missing = Assert.Single((await Query(new(99, 0, []))).Response.SubResponses);
        Assert.Equal((ulong)CellErrorCode.RevisionIDNotFound, missing.Error?.ErrorCode);
        foreach (var unsupported in new[] { new QueryChangesVersion(1, 1, []), new QueryChangesVersion(0, 0, [1]),
            new(null, null, [3, 0, 1]), new(null, null, [3, 0, 0, 0]), new(null, null, [0xff]), new(null, null, []) })
        {
            var result = await Query(unsupported);
            Assert.Equal((ulong)CellErrorCode.RequestNotSupported, Assert.Single(result.Response.SubResponses).Error?.ErrorCode);
            Assert.Null(result.Response.DataElementPackage);
        }
        var after = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(head.ResourceId)))!;
        Assert.Equal(head.LatestRevisionNumber, after.LatestRevisionNumber);
        Assert.Equal(head.Package, after.Package);
        Assert.Equal(head.StateVersion, after.StateVersion);
        var revoked = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(head.ResourceId,
            StorageTestDocuments.Admission(after), after.Security with { Owner = "another-owner" }, after.AuthorityGeneration));
        Assert.True(revoked.Changed);
        var denied = await Query(new(1, 0, []), TestActor.Value);
        Assert.True(Assert.Single(denied.Response.SubResponses).Status);
        Assert.Null(denied.Response.DataElementPackage);
        Assert.Throws<NotSupportedException>(() => new QueryChangesSubRequestData { Waterline = 1 }.Serialize(new()));
    }
}
