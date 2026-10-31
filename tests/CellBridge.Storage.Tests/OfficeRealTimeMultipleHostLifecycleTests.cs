using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed partial class OfficeRealTimeMultipleHostTests
{
    [Fact]
    public async Task DeletingAndRecreatingDocumentFencesOldRtcGenerationAndAllowsFreshProtocolSession()
    {
        await using var fixture = await Fixture.StartAsync(backend);
        var file = await fixture.CreateDocumentAsync();
        Guid oldClient = await fixture.JoinCoauthoringAsync(file.Path);
        string oldTicket = await fixture.GetDocumentTicketAsync(fixture.First, file.Path);
        await using var oldPeer = await fixture.ConnectAsync(fixture.First, oldTicket, oldClient);

        var service = fixture.First.App.Services.GetRequiredService<CellBridgeDocumentService>();
        var provider = service.Provider;
        var current = (await provider.Documents.ReadControlAsync(file.ResourceId))!;
        Assert.True((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(
            current.Head.ResourceId, current.Head.LifecycleGeneration, current.Head.StateVersion))).Applied);

        await oldPeer.Closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.ConnectAsync(fixture.Second, oldTicket, oldClient));

        var retired = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(file.ResourceId, IncludeDeleted: true)))!;
        var replacementPackage = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), file.Path,
            MinimalDocx.Create("recreated RTC generation"), current.Head.Security, provider.Content, provider.Limits,
            lifecycleGeneration: retired.LifecycleGeneration + 1);
        Assert.True((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(
            retired.ResourceId, retired.LifecycleGeneration, retired.StateVersion, replacementPackage))).Applied);

        Guid freshClient = await StartFreshProtocolSessionAsync(fixture, file.Path, replacementPackage.Head.ResourceId);
        string freshTicket = await fixture.GetDocumentTicketAsync(fixture.Second, file.Path);
        Assert.NotEqual(oldTicket, freshTicket);
        await using var freshPeer = await fixture.ConnectAsync(fixture.Second, freshTicket, freshClient);
        var snapshot = await freshPeer.Connection.InvokeAsync<Dictionary<string, string>>("GetCompletePresenceState")
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(snapshot);
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.ConnectAsync(fixture.First, oldTicket, oldClient));
    }

    [Fact]
    public async Task ExpiredProtocolEditorLeaseClosesRtcSocketAndProtocolRefreshRenewsTheSameTicket()
    {
        await using var fixture = await Fixture.StartAsync(backend);
        var file = await fixture.CreateDocumentAsync();
        Guid client = Guid.NewGuid();
        Guid schema = Guid.NewGuid();
        await AcquireSchemaSessionAsync(fixture, file.Path, client, schema, "3600");
        string ticket = await fixture.GetDocumentTicketAsync(fixture.First, file.Path);
        await using var peer = await fixture.ConnectAsync(fixture.First, ticket, client);

        // Coauthor leases below 3600 seconds are normalized by the protocol
        // coordinator. Shorten only the persisted lease timestamps in this isolated
        // fixture, then let normal PostgreSQL reads and RTC authorization observe expiry.
        await ShortenPersistedLeaseAsync(fixture, file.ResourceId, client, TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromSeconds(3));
        await peer.Closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.ConnectAsync(fixture.Second, ticket, client));

        var expiredRefresh = await SendProtocolRequestAsync(fixture.First, file.Path,
            CoauthRefreshRequest(client, schema, "3600"));
        Assert.Equal("Success", SubResponseCode(expiredRefresh));

        // RefreshCoauthoring reacquires the expired schema owner. The still-valid RTC
        // ticket can connect again only after the actual protocol lease is renewed.
        await using var renewed = await fixture.ConnectAsync(fixture.Second, ticket, client);
        var state = await renewed.Connection.InvokeAsync<Dictionary<string, string>>("GetCompletePresenceState")
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(state);
        await renewed.Connection.InvokeAsync("SendUpdateNotification", 3,
            $$"""{"A":"{{client:D}}","id":1,"lease":"renewed"}""")
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task<Guid> StartFreshProtocolSessionAsync(Fixture fixture, string path, Guid resourceId)
    {
        Guid client = Guid.NewGuid();
        Guid exclusive = Guid.NewGuid();
        Guid schema = Guid.NewGuid();
        var exclusiveResult = await SendProtocolRequestAsync(fixture.First, path, new XElement(Protocol + "SubRequest",
            new XAttribute("Type", "ExclusiveLock"), new XAttribute("SubRequestToken", 1),
            new XElement(Protocol + "SubRequestData", new XAttribute("ExclusiveLockRequestType", "GetLock"),
                new XAttribute("ExclusiveLockID", exclusive), new XAttribute("ClientID", client), new XAttribute("Timeout", "3600"))), resourceId);
        Assert.Equal("Success", SubResponseCode(exclusiveResult));
        var conversionResult = await SendProtocolRequestAsync(fixture.First, path, new XElement(Protocol + "SubRequest",
            new XAttribute("Type", "ExclusiveLock"), new XAttribute("SubRequestToken", 1),
            new XElement(Protocol + "SubRequestData", new XAttribute("ExclusiveLockRequestType", "ConvertToSchemaJoinCoauth"),
                new XAttribute("ExclusiveLockID", exclusive), new XAttribute("SchemaLockID", schema),
                new XAttribute("ClientID", client), new XAttribute("Timeout", "3600"))), resourceId);
        Assert.Equal("Success", SubResponseCode(conversionResult));
        return client;
    }

    private static async Task AcquireSchemaSessionAsync(Fixture fixture, string path, Guid client, Guid schema, string timeout)
    {
        Guid exclusive = Guid.NewGuid();
        var exclusiveResult = await SendProtocolRequestAsync(fixture.First, path, new XElement(Protocol + "SubRequest",
            new XAttribute("Type", "ExclusiveLock"), new XAttribute("SubRequestToken", 1),
            new XElement(Protocol + "SubRequestData", new XAttribute("ExclusiveLockRequestType", "GetLock"),
                new XAttribute("ExclusiveLockID", exclusive), new XAttribute("ClientID", client), new XAttribute("Timeout", "3600"))));
        Assert.Equal("Success", SubResponseCode(exclusiveResult));
        var conversionResult = await SendProtocolRequestAsync(fixture.First, path, new XElement(Protocol + "SubRequest",
            new XAttribute("Type", "ExclusiveLock"), new XAttribute("SubRequestToken", 1),
            new XElement(Protocol + "SubRequestData", new XAttribute("ExclusiveLockRequestType", "ConvertToSchemaJoinCoauth"),
                new XAttribute("ExclusiveLockID", exclusive), new XAttribute("SchemaLockID", schema),
                new XAttribute("ClientID", client), new XAttribute("Timeout", timeout))));
        Assert.Equal("Success", SubResponseCode(conversionResult));
    }

    private static XElement CoauthRefreshRequest(Guid client, Guid schema, string timeout) =>
        new(Protocol + "SubRequest", new XAttribute("Type", "Coauth"), new XAttribute("SubRequestToken", 1),
            new XElement(Protocol + "SubRequestData", new XAttribute("CoauthRequestType", "RefreshCoauthoring"),
                new XAttribute("SchemaLockID", schema), new XAttribute("ClientID", client), new XAttribute("Timeout", timeout)));

    private static async Task ShortenPersistedLeaseAsync(Fixture fixture, Guid resourceId, Guid client, TimeSpan lifetime)
    {
        var provider = fixture.First.App.Services.GetRequiredService<CellBridgeDocumentService>().Provider;
        var control = (await provider.Documents.ReadControlAsync(resourceId))!;
        DateTime expiry = control.UtcNow.Add(lifetime);
        var editor = Assert.Single(control.Editors);
        Assert.Equal(client, editor.ClientId);
        Assert.Single(control.Coordination.SchemaOwners);
        var revisedCoordination = control.Coordination with
        {
            SchemaOwners = control.Coordination.SchemaOwners
                .Select(owner => owner with { ExpiresUtc = expiry }).ToImmutableArray()
        };

        var source = fixture.First.App.Services.GetRequiredService<NpgsqlDataSource>();
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var updateEditor = new NpgsqlCommand("""
            UPDATE cellbridge_repository_editors
            SET "ExpiresUtc" = $3, "Json" = $4
            WHERE "ResourceId" = $1 AND "ClientId" = $2
            """, connection, transaction))
        {
            updateEditor.Parameters.AddWithValue(resourceId);
            updateEditor.Parameters.AddWithValue(client);
            updateEditor.Parameters.AddWithValue(expiry);
            updateEditor.Parameters.AddWithValue(JsonSerializer.Serialize(editor with { ExpiresUtc = expiry }));
            Assert.Equal(1, await updateEditor.ExecuteNonQueryAsync());
        }
        await using (var updateCoordination = new NpgsqlCommand("""
            UPDATE cellbridge_repository_coordination
            SET "Json" = $2
            WHERE "ResourceId" = $1
            """, connection, transaction))
        {
            updateCoordination.Parameters.AddWithValue(resourceId);
            updateCoordination.Parameters.AddWithValue(JsonSerializer.Serialize(revisedCoordination));
            Assert.Equal(1, await updateCoordination.ExecuteNonQueryAsync());
        }
        await transaction.CommitAsync();
    }

    private static async Task<XDocument> SendProtocolRequestAsync(Host host, string path, XElement subRequest, Guid? resourceId = null)
    {
        XNamespace envelope = "http://schemas.xmlsoap.org/soap/envelope/";
        var protocolRequest = new XElement(Protocol + "Request", new XAttribute("Url", path), new XAttribute("RequestToken", 1));
        if (resourceId is { } id)
        {
            protocolRequest.Add(new XAttribute("UseResourceID", "true"));
            protocolRequest.Add(new XAttribute("ResourceID", id));
        }
        protocolRequest.Add(subRequest);
        var request = new XElement(envelope + "Envelope", new XElement(envelope + "Body",
            new XElement(Protocol + "RequestVersion", new XAttribute("Version", 2), new XAttribute("MinorVersion", 2)),
            new XElement(Protocol + "RequestCollection", new XAttribute("CorrelationId", Guid.NewGuid()),
                protocolRequest)));
        using var response = await host.Http.PostAsync("/_vti_bin/cellstorage.svc/CellStorageService",
            new StringContent(request.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return XDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static string? SubResponseCode(XDocument response) =>
        (string?)response.Descendants(Protocol + "SubResponse").SingleOrDefault()?.Attribute("ErrorCode");
}
