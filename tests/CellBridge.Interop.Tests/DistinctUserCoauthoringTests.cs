#nullable enable
using System.Text;
using System.Xml.Linq;
using System.Net.Http.Json;

namespace CellBridge.Interop.Tests;

public sealed class DistinctUserFactAttribute : FactAttribute
{
    public DistinctUserFactAttribute()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")))
            Skip = "This test needs the fixture-managed database and two disposable login accounts.";
    }
}

[Collection("WebDemo integration")]
public sealed class DistinctUserCoauthoringTests(WebDemoFixture fixture)
{
    [DistinctUserFact]
    public async Task TwoAuthenticatedUsersCannotHijackEachOthersCoauthorSession()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")!);
        using var firstHttp = LiveInteropHttp.Create(endpoint);
        using var secondHttp = fixture.CreateSecondUserHttpClient();
        var name = "distinct users " + Guid.NewGuid().ToString("N");
        using var created = await firstHttp.PostAsJsonAsync(new Uri(endpoint, "/api/documents"), new { name, type = "docx" });
        created.EnsureSuccessStatusCode();

        var path = "/shared/" + Uri.EscapeDataString(name + ".docx");
        await fixture.GrantSecondUserWriteAccessAsync(path);

        var schema = Guid.NewGuid().ToString("D");
        var firstClient = Guid.NewGuid();
        var secondClient = Guid.NewGuid();
        var firstJoin = Send(firstHttp, firstClient, "JoinCoauthoring");
        var secondJoin = Send(secondHttp, secondClient, "JoinCoauthoring");
        var joined = await Task.WhenAll(firstJoin, secondJoin);
        Assert.All(joined, result => Assert.Equal("Success", result));

        var control = await fixture.ReadControlAsync(path);
        Assert.Equal(2, control.Editors.Length);
        Assert.Equal(2, control.Coordination.SchemaOwners.Length);
        Assert.Equal(new[] { "local:integration-writer", "local:integration-writer-1" },
            control.Editors.Select(editor => editor.Owner!.Subject).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "local:integration-writer", "local:integration-writer-1" },
            control.Coordination.SchemaOwners.Select(lease => lease.OwnerSubject).Order(StringComparer.Ordinal).ToArray());

        Assert.Equal("FileUnauthorizedAccess", await Send(firstHttp, secondClient, "RefreshCoauthoring"));
        Assert.Equal("FileUnauthorizedAccess", await Send(secondHttp, firstClient, "RefreshCoauthoring"));
        Assert.Equal("Success", await Send(firstHttp, firstClient, "RefreshCoauthoring"));
        Assert.Equal("Success", await Send(secondHttp, secondClient, "RefreshCoauthoring"));
        Assert.Equal("Success", await Send(secondHttp, secondClient, "ExitCoauthoring"));
        var remaining = await fixture.ReadControlAsync(path);
        Assert.Equal("local:integration-writer", Assert.Single(remaining.Editors).Owner!.Subject);
        Assert.Equal("local:integration-writer", Assert.Single(remaining.Coordination.SchemaOwners).OwnerSubject);
        Assert.Equal("Success", await Send(firstHttp, firstClient, "ExitCoauthoring"));
        var departed = await fixture.ReadControlAsync(path);
        Assert.Empty(departed.Editors);
        Assert.Empty(departed.Coordination.SchemaOwners);

        async Task<string?> Send(HttpClient http, Guid client, string operation)
        {
            XNamespace ns = CellStorageClient.ProtocolNamespace;
            XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
            var xml = new XElement(soap + "Envelope", new XElement(soap + "Body",
                new XElement(ns + "RequestVersion", new XAttribute("Version", 2), new XAttribute("MinorVersion", 2)),
                new XElement(ns + "RequestCollection", new XAttribute("CorrelationId", Guid.NewGuid()),
                    new XElement(ns + "Request", new XAttribute("Url", new Uri(LiveInteropHttp.PublicOrigin(endpoint), path)),
                        new XAttribute("RequestToken", 1),
                        new XElement(ns + "SubRequest", new XAttribute("Type", "Coauth"),
                            new XAttribute("SubRequestToken", 1), new XElement(ns + "SubRequestData",
                                new XAttribute("CoauthRequestType", operation), new XAttribute("ClientID", client),
                                new XAttribute("SchemaLockID", schema), new XAttribute("Timeout", 3600)))))));
            using var response = await http.PostAsync(endpoint,
                new StringContent(xml.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml"));
            response.EnsureSuccessStatusCode();
            var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
            return (string?)document.Descendants(ns + "SubResponse").Single().Attribute("ErrorCode");
        }
    }
}
