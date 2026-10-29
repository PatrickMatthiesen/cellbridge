using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using CellBridge.TestInfrastructure;
using Npgsql;

namespace CellBridge.Benchmarks.Tests;

public sealed class LoadEnvironmentOptionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidUserCountsFailBeforeStartingResources(int count)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LoadTestEnvironment.StartAsync(userCount: count));
    }
}

[Collection("benchmark-database")]
public sealed class LoadIdentityTests(BenchmarkEnvironment fixture)
{
    [Fact]
    public async Task DistinctSignedInAccountsOwnSeparateDocumentsAndCannotReadOrSaveEachOthersFiles()
    {
        var environment = fixture.Environment;
        Assert.Equal(3, environment.UserCount);
        Assert.Equal("local:integration-writer", environment.GetUserIdentity(0).Subject);
        Assert.Equal("integration-writer-1", environment.GetUserIdentity(1).Login);
        Assert.Equal("integration-writer-2", environment.GetUserIdentity(2).Login);
        Assert.Throws<ArgumentOutOfRangeException>(() => environment.GetUserIdentity(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => environment.GetUserIdentity(3));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => environment.CreateClientAsync(3));

        using var first = await environment.CreateClientAsync();
        using var second = await environment.CreateClientAsync(1);
        using var third = await environment.CreateClientAsync(2);
        HttpClient[] clients = [first, second, third];
        var paths = new string[clients.Length];
        await using var source = NpgsqlDataSource.Create(environment.DocumentConnectionString);
        var provider = new StorageProvider(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));
        for (var index = 0; index < clients.Length; index++)
        {
            using var response = await clients[index].PostAsJsonAsync("/api/documents", new
            {
                name = "load-identity-" + Guid.NewGuid().ToString("N"), type = "docx",
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            paths[index] = response.Headers.Location!.OriginalString;
            var state = await provider.Documents.FindAsync(new DocumentIdentity.Path(StorageIds.PathKey(paths[index])));
            Assert.NotNull(state);
            Assert.Equal(environment.GetUserIdentity(index).Subject, state.Security.Owner);
            Assert.Equal(environment.GetUserIdentity(index).Subject, state.Security.CreatedBy!.Subject);
            using var ownDownload = await clients[index].GetAsync(paths[index]);
            Assert.Equal(HttpStatusCode.OK, ownDownload.StatusCode);
        }
        for (var index = 0; index < clients.Length; index++)
        {
            var listing = (await clients[index].GetFromJsonAsync<JsonObject[]>("/api/documents"))!;
            Assert.Contains(listing, document => document["path"]!.GetValue<string>() == paths[index]);
            for (var other = 0; other < clients.Length; other++)
            {
                if (other == index) continue;
                Assert.DoesNotContain(listing, document => document["path"]!.GetValue<string>() == paths[other]);
                using var deniedDownload = await clients[index].GetAsync(paths[other]);
                Assert.Equal(HttpStatusCode.NotFound, deniedDownload.StatusCode);
            }
        }

        var before = (await provider.Documents.FindAsync(new DocumentIdentity.Path(StorageIds.PathKey(paths[0]))))!;
        var restored = await BenchmarkTestDocuments.GraphAsync(new CellBridgeDocumentService(provider), before, new(environment.GetUserIdentity(0)));
        var updatedBytes = MinimalDocx.Create("A save from the document owner");
        var request = SyntheticWorkload.Save(restored, updatedBytes, "full", 0, new Random(42));
        var endpoint = new Uri(environment.HttpEndpoint, "/_vti_bin/cellstorage.svc");
        var fileUrl = new Uri(environment.HttpEndpoint, paths[0]).AbsoluteUri;
        var denied = await new CellStorageTransport(second, endpoint).SendCellAsync(fileUrl,
            request.ToByteArray(), null, true, request.UserAgentGuid);
        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
        Assert.Contains(XDocument.Parse(denied.Soap).Descendants().Attributes(), attribute =>
            attribute.Name.LocalName == "ErrorCode" && attribute.Value == "FileUnauthorizedAccess");
        var unchanged = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(before.ResourceId)))!;
        Assert.Equal(before.StateVersion, unchanged.StateVersion);
        Assert.Equal(before.Package, unchanged.Package);
        Assert.Equal(before.Counters.SaveReceipts, unchanged.Counters.SaveReceipts);

        // The same request must succeed for the owner, so denial is not caused by malformed save bytes.
        var accepted = await new CellStorageTransport(first, endpoint).SendCellAsync(fileUrl,
            request.ToByteArray(), null, true, request.UserAgentGuid);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.NotNull(accepted.Binary);
        var result = FsshttpbResponse.Deserialize(new BinaryReaderEx(accepted.Binary));
        Assert.False(result.Status);
        Assert.False(Assert.Single(result.SubResponses).Status);
        var changed = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(before.ResourceId)))!;
        Assert.True(changed.StateVersion > before.StateVersion);
        Assert.Equal(updatedBytes, await BenchmarkTestDocuments.PackageAsync(provider, changed.Package));
    }
}
