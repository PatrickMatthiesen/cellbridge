using System.Net;
using System.Net.Http.Json;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using Npgsql;

namespace CellBridge.Benchmarks.Tests;

[Collection("benchmark-database")]
public sealed class LoadSessionTests(BenchmarkEnvironment fixture)
{
    [Fact]
    public async Task PreissuedSessionsExceedTheLoginWindowAndKeepAccountOwnershipAndCsrfChecks()
    {
        var environment = fixture.Environment;
        // A password login for each session would exhaust the shared 20/minute
        // address limit. Ticket preparation does not measure login throughput.
        for (var session = 0; session < 25; session++)
        {
            using var client = await environment.CreateClientAsync(session % environment.UserCount);
            using var accepted = await client.GetAsync("/auth/complete");
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using var owner = await environment.CreateClientAsync(1);
        using var other = await environment.CreateClientAsync(2);
        var request = new { name = "load-session-" + Guid.NewGuid().ToString("N"), type = "docx" };
        var csrf = owner.DefaultRequestHeaders.GetValues("X-CellBridge-CSRF").Single();
        owner.DefaultRequestHeaders.Remove("X-CellBridge-CSRF");
        using (var rejected = await owner.PostAsJsonAsync("/api/documents", request))
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        owner.DefaultRequestHeaders.Add("X-CellBridge-CSRF", csrf);
        using var created = await owner.PostAsJsonAsync("/api/documents", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var path = created.Headers.Location!.OriginalString;
        await using var source = NpgsqlDataSource.Create(environment.DocumentConnectionString);
        var state = await ((IDocumentRepository)new PostgreSqlStateStore(source)).FindAsync(new DocumentIdentity.Path(StorageIds.PathKey(path)));
        Assert.NotNull(state);
        Assert.Equal(environment.GetUserIdentity(1).Subject, state.Security.Owner);
        using var ownDownload = await owner.GetAsync(path);
        using var deniedDownload = await other.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, ownDownload.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deniedDownload.StatusCode);
    }

    [Fact]
    public async Task TheExplicitPasswordLoginPathStillAuthenticatesSeededAccounts()
    {
        using var client = await fixture.Environment.CreateLoginClientAsync(0);
        using var accepted = await client.GetAsync("/auth/complete");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.True(client.DefaultRequestHeaders.Contains("X-CellBridge-CSRF"));
    }
}
