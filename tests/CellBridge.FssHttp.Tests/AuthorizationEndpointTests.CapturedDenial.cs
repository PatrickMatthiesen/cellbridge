using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.FssHttp.Tests;

public sealed partial class AuthorizationEndpointTests
{
    private static readonly XNamespace DenialXop = "http://www.w3.org/2004/08/xop/include";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SyntheticWordOpenBatchKeepsDenialsAtTheirOperationAfterSoapSerialization(bool mtom, bool externalPolicy)
    {
        await using var fixture = await Fixture.Start(externalPolicy: externalPolicy);
        var before = (await fixture.Provider.Documents.ReadControlAsync(fixture.State.ResourceId))!;
        var bytes = await TestStorage.ReadPackageAsync(fixture.Provider, before.Head.Package);
        using var client = fixture.CreateClient("other");
        var envelope = SyntheticDenialEnvelope(SyntheticDenialFile(fixture.State.ResourceId));

        var response = await SendSyntheticBatch(client, envelope, mtom);

        AssertNoGlobalBatchError(response.Xml);
        AssertSyntheticDenial(Assert.Single(response.Xml.Descendants(Protocol + "Response")), response);
        Assert.Equal(mtom ? 4 : 0, response.BinaryParts.Count);
        // Successful evaluation of QueryAccess reports denied capabilities; it does not grant them.
        using var download = await client.GetAsync("/shared/secret.bin");
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        var after = (await fixture.Provider.Documents.ReadControlAsync(fixture.State.ResourceId))!;
        Assert.Equal(before.Head, after.Head);
        Assert.Equal(before.Coordination, after.Coordination);
        Assert.Equal(before.Editors.ToArray(), after.Editors.ToArray());
        Assert.Equal(bytes, await TestStorage.ReadPackageAsync(fixture.Provider, after.Head.Package));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyntheticDeniedFileDoesNotDiscardAllowedFileResultsInTheSameSerializedBatch(bool mtom)
    {
        await using var fixture = await Fixture.Start();
        var allowed = (await fixture.Service.CreateAsync("/shared/allowed.bin", [9, 8, 7], TestActor.Value))!;
        await TestStorage.SetPermissionsAsync(fixture.Provider, allowed.ResourceId,
            allowed.Security with { Grants = allowed.Security.Grants.Add("tests:other", DocumentAccess.Read) });
        var deniedBefore = await fixture.Provider.Documents.FindAsync(new DocumentIdentity.Resource(fixture.State.ResourceId));
        var allowedBefore = await fixture.Provider.Documents.FindAsync(new DocumentIdentity.Resource(allowed.ResourceId));
        var allowedFile = new XElement(Protocol + "Request", new XAttribute("RequestToken", 2),
            new XAttribute("Url", "http://localhost/shared/allowed.bin"),
            new XAttribute("UseResourceID", "true"), new XAttribute("ResourceID", allowed.ResourceId.ToString("N")),
            SyntheticSubRequest(SubRequestType.WhoAmI, 1), SyntheticSubRequest(SubRequestType.ServerTime, 2));
        using var client = fixture.CreateClient("other");

        var response = await SendSyntheticBatch(client,
            SyntheticDenialEnvelope(SyntheticDenialFile(fixture.State.ResourceId), allowedFile), mtom);

        AssertNoGlobalBatchError(response.Xml);
        var files = response.Xml.Descendants(Protocol + "Response").ToArray();
        Assert.Equal(new[] { "1", "2" }, files.Select(file => (string?)file.Attribute("RequestToken")));
        AssertSyntheticDenial(files[0], response);
        Assert.Null(files[1].Attribute("ErrorCode"));
        Assert.Null(files[1].Attribute("ErrorMessage"));
        Assert.Equal(allowed.ResourceId.ToString("N"), (string?)files[1].Attribute("ResourceID"));
        var allowedResults = files[1].Elements(Protocol + "SubResponse").ToArray();
        Assert.Equal(new[] { "1", "2" }, allowedResults.Select(sub => (string?)sub.Attribute("SubRequestToken")));
        Assert.All(allowedResults, sub => Assert.Equal("Success", (string?)sub.Attribute("ErrorCode")));
        Assert.Equal("test-other", (string?)allowedResults[0].Element(Protocol + "SubResponseData")!.Attribute("UserLogin"));
        Assert.NotNull(allowedResults[1].Element(Protocol + "SubResponseData")!.Attribute("ServerTime"));
        Assert.Equal(deniedBefore, await fixture.Provider.Documents.FindAsync(new DocumentIdentity.Resource(fixture.State.ResourceId)));
        Assert.Equal(allowedBefore, await fixture.Provider.Documents.FindAsync(new DocumentIdentity.Resource(allowed.ResourceId)));
        using var download = await client.GetAsync("/shared/allowed.bin");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(new byte[] { 9, 8, 7 }, await download.Content.ReadAsByteArrayAsync());
    }

    private static XElement SyntheticDenialEnvelope(params XElement[] files)
    {
        XNamespace soap = CellStorageRequest.Soap11Namespace;
        return new XElement(soap + "Envelope", new XElement(soap + "Body",
            new XElement(Protocol + "RequestVersion", new XAttribute("Version", 2), new XAttribute("MinorVersion", 3)),
            new XElement(Protocol + "RequestCollection", new XAttribute("CorrelationId", Guid.NewGuid()), files)));
    }

    private static XElement SyntheticDenialFile(Guid resourceId)
    {
        // Synthetic identifiers and payloads retain only the observed open-batch structure.
        return new XElement(Protocol + "Request", new XAttribute("RequestToken", 1),
            new XAttribute("Url", "http://localhost/shared/supplied-only.bin"),
            new XAttribute("UseResourceID", "true"), new XAttribute("ResourceID", resourceId.ToString("N")),
            SyntheticSubRequest(SubRequestType.Coauth, 1, data: new XElement(Protocol + "SubRequestData",
                new XAttribute("CoauthRequestType", "JoinCoauthoring"), new XAttribute("ClientID", "11111111-1111-1111-1111-111111111111"),
                new XAttribute("SchemaLockID", "22222222-2222-2222-2222-222222222222"))),
            SyntheticSubRequest(SubRequestType.SchemaLock, 5, 1, "OnNotSupported",
                new XElement(Protocol + "SubRequestData", new XAttribute("SchemaLockRequestType", "GetLock"),
                    new XAttribute("SchemaLockID", "22222222-2222-2222-2222-222222222222"))),
            SyntheticCellRequest(8, StoredDocument.EditorsTablePartitionId),
            SyntheticCellRequest(7, StoredDocument.MetadataPartitionId),
            SyntheticCellRequest(6, Guid.Empty),
            SyntheticSubRequest(SubRequestType.EditorsTable, 4, 1, "OnSuccess",
                new XElement(Protocol + "SubRequestData", new XAttribute("EditorsTableRequestType", "JoinEditingSession"))),
            SyntheticSubRequest(SubRequestType.EditorsTable, 3, 1, "OnSuccess",
                new XElement(Protocol + "SubRequestData", new XAttribute("EditorsTableRequestType", "UpdateEditorMetadata"))),
            SyntheticSubRequest(SubRequestType.EditorsTable, 2, 1, "OnSuccess",
                new XElement(Protocol + "SubRequestData", new XAttribute("EditorsTableRequestType", "RefreshEditingSession"))),
            SyntheticSubRequest(SubRequestType.WhoAmI, 9), SyntheticSubRequest(SubRequestType.ServerTime, 10),
            SyntheticCellRequest(11, Guid.Empty, queryAccess: true));
    }

    private static XElement SyntheticCellRequest(uint token, Guid partition, bool queryAccess = false)
    {
        var cell = new FsshttpbCellRequest();
        cell.SubRequests.Add(new(queryAccess ? RequestTypes.QueryAccess : RequestTypes.QueryChanges)
        {
            RequestId = token,
            Data = queryAccess ? new QueryAccessSubRequestData() : new QueryChangesSubRequestData
                { IncludeStorageManifest = true, IncludeCellChanges = true },
        });
        var binary = cell.ToByteArray();
        return SyntheticSubRequest(SubRequestType.Cell, token, queryAccess ? null : 5, queryAccess ? null : "OnExecute",
            new XElement(Protocol + "SubRequestData", new XAttribute("PartitionID", partition.ToString("D")),
                new XAttribute("GetFileProps", "true"), new XAttribute("BinaryDataSize", binary.Length), Convert.ToBase64String(binary)));
    }

    private static XElement SyntheticSubRequest(SubRequestType type, uint token, uint? dependsOn = null,
        string? dependencyType = null, XElement? data = null) => new(Protocol + "SubRequest",
        new XAttribute("Type", type), new XAttribute("SubRequestToken", token),
        dependsOn is null ? null : new XAttribute("DependsOn", dependsOn),
        dependencyType is null ? null : new XAttribute("DependencyType", dependencyType), data);

    private static async Task<SyntheticBatchResponse> SendSyntheticBatch(HttpClient client, XElement envelope, bool mtom)
    {
        var requestParts = new List<(string Id, byte[] Bytes)>();
        if (mtom)
        {
            foreach (var file in envelope.Descendants(Protocol + "Request"))
            foreach (var sub in file.Elements(Protocol + "SubRequest").Where(sub => (string?)sub.Attribute("Type") == "Cell"))
            {
                var data = sub.Element(Protocol + "SubRequestData")!;
                string id = $"cell-{file.Attribute("RequestToken")!.Value}-{sub.Attribute("SubRequestToken")!.Value}";
                requestParts.Add((id, Convert.FromBase64String(data.Value)));
                data.ReplaceNodes(new XElement(DenialXop + "Include", new XAttribute("href", "cid:" + id)));
            }
        }
        var parsed = CellStorageRequestParser.Parse(envelope.ToString(SaveOptions.DisableFormatting));
        var deniedRequests = parsed.Requests[0].SubRequests;
        Assert.Equal(new ulong?[] { 1, 5, 8, 7, 6, 4, 3, 2, 9, 10, 11 }, deniedRequests.Select(sub => sub.SubRequestToken));
        Assert.Equal(new[] { SubRequestType.Coauth, SubRequestType.SchemaLock, SubRequestType.Cell, SubRequestType.Cell,
            SubRequestType.Cell, SubRequestType.EditorsTable, SubRequestType.EditorsTable, SubRequestType.EditorsTable,
            SubRequestType.WhoAmI, SubRequestType.ServerTime, SubRequestType.Cell }, deniedRequests.Select(sub => sub.Type));
        Assert.Equal(new ulong?[] { null, 1, 5, 5, 5, 1, 1, 1, null, null, null }, deniedRequests.Select(sub => sub.DependsOn));
        Assert.Equal(new string?[] { null, "OnNotSupported", "OnExecute", "OnExecute", "OnExecute", "OnSuccess", "OnSuccess",
            "OnSuccess", null, null, null }, deniedRequests.Select(sub => sub.DependencyType));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_vti_bin/cellstorage.svc");
        if (mtom)
        {
            using var body = new MemoryStream();
            body.Write(Encoding.UTF8.GetBytes("--synthetic\r\nContent-Type: application/xop+xml\r\nContent-ID: <root>\r\n\r\n" +
                envelope.ToString(SaveOptions.DisableFormatting) + "\r\n"));
            foreach (var part in requestParts)
            {
                body.Write(Encoding.ASCII.GetBytes($"--synthetic\r\nContent-Type: application/octet-stream\r\nContent-ID: <{part.Id}>\r\n\r\n"));
                body.Write(part.Bytes);
                body.Write(Encoding.ASCII.GetBytes("\r\n"));
            }
            body.Write(Encoding.ASCII.GetBytes("--synthetic--\r\n"));
            request.Content = new ByteArrayContent(body.ToArray());
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/related; boundary=synthetic; start=\"<root>\"");
            Assert.Equal(requestParts.Select(part => "cid:" + part.Id), deniedRequests.Where(sub => sub.Type == SubRequestType.Cell)
                .Select(sub => sub.SubRequestDataAttributes["IncludeHref"]));
        }
        else request.Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (!mtom)
        {
            Assert.Equal("text/xml", response.Content.Headers.ContentType?.MediaType);
            var xml = XDocument.Parse(Encoding.UTF8.GetString(bytes));
            Assert.Empty(xml.Descendants(DenialXop + "Include"));
            return new(xml, new Dictionary<string, byte[]>());
        }
        Assert.Equal("multipart/related", response.Content.Headers.ContentType?.MediaType);
        var parts = MtomMessageParser.Parse(bytes, response.Content.Headers.ContentType!.ToString());
        var mtomXml = XDocument.Parse(Encoding.UTF8.GetString(parts[0].ContentMemory.Span));
        var binaries = parts.Skip(1).ToDictionary(part => "cid:" + part.ContentId, part => part.ContentMemory.ToArray());
        var references = mtomXml.Descendants(DenialXop + "Include").Select(include => include.Attribute("href")!.Value).ToArray();
        Assert.Equal(binaries.Count, references.Length);
        Assert.Equal(references.Length, references.Distinct().Count());
        Assert.All(references, href => Assert.True(binaries.ContainsKey(href)));
        return new(mtomXml, binaries);
    }

    private static void AssertNoGlobalBatchError(XDocument xml)
    {
        var version = Assert.Single(xml.Descendants(Protocol + "ResponseVersion"));
        Assert.Null(version.Attribute("ErrorCode"));
        Assert.Null(version.Attribute("ErrorMessage"));
        Assert.DoesNotContain(xml.Descendants(), element => element.Name.LocalName == "Fault");
    }

    private static void AssertSyntheticDenial(XElement file, SyntheticBatchResponse response)
    {
        Assert.Null(file.Attribute("ErrorCode"));
        Assert.Null(file.Attribute("ErrorMessage"));
        Assert.Null(file.Attribute("ResourceID"));
        Assert.Equal("http://localhost/shared/supplied-only.bin", (string?)file.Attribute("Url"));
        var subs = file.Elements(Protocol + "SubResponse").ToArray();
        Assert.Equal(new[] { "1", "5", "8", "7", "6", "4", "3", "2", "9", "10", "11" },
            subs.Select(sub => (string?)sub.Attribute("SubRequestToken")));
        foreach (var sub in subs)
        {
            uint token = uint.Parse(sub.Attribute("SubRequestToken")!.Value);
            var data = sub.Element(Protocol + "SubResponseData");
            if (token is 6 or 7 or 8 or 11)
            {
                Assert.Equal("Success", (string?)sub.Attribute("ErrorCode"));
                Assert.Equal("0", (string?)sub.Attribute("HResult"));
                Assert.NotNull(data);
                Assert.Empty(data.Attributes());
                var include = data.Element(DenialXop + "Include");
                var binary = FsshttpbResponse.Deserialize(new BinaryReaderEx(include is null
                    ? Convert.FromBase64String(data.Value) : response.BinaryParts[include.Attribute("href")!.Value]));
                Assert.False(binary.Status);
                Assert.Null(binary.Error);
                Assert.Null(binary.DataElementPackage);
                var child = Assert.Single(binary.SubResponses);
                Assert.Equal((ulong)token, child.RequestId);
                Assert.Equal(token == 11 ? RequestTypes.QueryAccess : RequestTypes.QueryChanges, child.RequestType);
                Assert.Equal(token != 11, child.Status);
                if (token == 11)
                {
                    Assert.Null(child.Error);
                    var access = Assert.IsType<QueryAccessSubResponseData>(child.Data);
                    AssertDeniedHResult(access.ReadAccessError);
                    AssertDeniedHResult(access.WriteAccessError);
                }
                else
                {
                    Assert.Null(child.Data);
                    AssertDeniedHResult(child.Error);
                }
            }
            else
            {
                Assert.True(data is null || !data.HasAttributes && !data.HasElements && string.IsNullOrEmpty(data.Value));
                Assert.Equal(token == 5 ? "DependentOnlyOnNotSupportedRequestGetSupported"
                    : token is 2 or 3 or 4 ? "DependentOnlyOnSuccessRequestFailed" : "FileUnauthorizedAccess",
                    (string?)sub.Attribute("ErrorCode"));
                Assert.Equal(token is 1 or 9 or 10 ? "2147942405" : "2147500037", (string?)sub.Attribute("HResult"));
            }
        }
    }

    private static void AssertDeniedHResult(ResponseError? error)
    {
        Assert.NotNull(error);
        Assert.Equal(ErrorType.HResult, error.Type);
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, error.ErrorCode);
    }

    private sealed record SyntheticBatchResponse(XDocument Xml, IReadOnlyDictionary<string, byte[]> BinaryParts);
}
