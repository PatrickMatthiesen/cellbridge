using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;

namespace CellBridge.FssHttp.Tests;

public sealed class CellStorageCodecTests
{
    [Theory]
    [InlineData(CellStorageRequest.Soap11Namespace, false)]
    [InlineData(CellStorageRequest.Soap11Namespace, true)]
    [InlineData(CellStorageRequest.Soap12Namespace, false)]
    [InlineData(CellStorageRequest.Soap12Namespace, true)]
    public void RequestRoundTripsEveryTypeAndPreservesResourceIdentity(string soap, bool mtom)
    {
        byte[] binary = new FsshttpbCellRequest
        {
            SubRequests = { new(RequestTypes.QueryAccess) { RequestId = 7 } },
        }.ToByteArray();
        var request = new CellStorageRequest { CorrelationId = Guid.NewGuid(), MinorVersion = 2, SoapNamespace = soap };
        var file = new FssHttpRequest
        {
            Url = "https://example.test/shared/Book%20one.xlsx?a=1&b=2",
            RequestToken = 17,
            UserAgent = Guid.NewGuid(),
            UseResourceId = true,
            ResourceId = Guid.NewGuid().ToString("N"),
            RequestAttributes = { ["UserAgentClient"] = "msexcel", ["Build"] = "16.0.3942.3688" },
        };
        request.Requests.Add(file);
        foreach (var type in Enum.GetValues<SubRequestType>())
            file.SubRequests.Add(new FssHttpSubRequest { Type = type, SubRequestToken = (ulong)type + 41 });

        var cell = file.SubRequests[0];
        cell.DependsOn = file.SubRequests[1].SubRequestToken;
        cell.DependencyType = "OnExecute";
        cell.SubRequestDataBinary = binary;
        cell.SubRequestDataAttributes["SchemaLockID"] = Guid.NewGuid().ToString("B");
        cell.SubRequestDataAttributes["ClientID"] = Guid.NewGuid().ToString("B");
        var properties = file.SubRequests.Single(s => s.Type == SubRequestType.Properties);
        properties.SubRequestDataXml = """<SubRequestData PropertyRequestType="PropertyGet"><PropertyIds /></SubRequestData>""";
        properties.SubRequestDataAttributes["ClientID"] = "client & reader";
        file.SubRequests.Single(s => s.Type == SubRequestType.EditorsTable).SubRequestDataXml =
            """<SubRequestData EditorsTableRequestType="UpdateEditorMetadata" Key="Platform">MQ==</SubRequestData>""";

        var secondFile = new FssHttpRequest { Url = "https://example.test/other.xlsx", RequestToken = 18 };
        secondFile.SubRequests.Add(new FssHttpSubRequest
        {
            Type = SubRequestType.Cell,
            SubRequestToken = cell.SubRequestToken, // Reuse within a different file is valid.
            SubRequestDataBinary = binary,
        });
        request.Requests.Add(secondFile);

        IReadOnlyList<MtomPart>? parts = null;
        string xml;
        if (mtom)
        {
            var message = request.ToMtomMessage();
            parts = MtomMessageParser.ParseViews(message.Body, message.ContentType);
            Assert.Equal(3, parts.Count);
            xml = Encoding.UTF8.GetString(parts[0].ContentMemory.Span);
        }
        else
        {
            xml = request.ToSoapEnvelope();
        }

        var decoded = CellStorageRequestParser.Parse(xml);
        Assert.Equal(soap, decoded.SoapNamespace);
        Assert.Equal(request.CorrelationId, decoded.CorrelationId);
        Assert.Equal(request.MinorVersion, decoded.MinorVersion);
        Assert.Equal(2, decoded.Requests.Count);
        var decodedFile = decoded.Requests[0];
        Assert.Equal(file.Url, decodedFile.Url);
        Assert.Equal(file.RequestToken, decodedFile.RequestToken);
        Assert.Equal(file.UserAgent, decodedFile.UserAgent);
        Assert.Equal(file.ResourceId, decodedFile.ResourceId);
        Assert.True(decodedFile.UseResourceId);
        Assert.Equal(file.RequestAttributes, decodedFile.RequestAttributes);
        Assert.Equal(Enum.GetValues<SubRequestType>(), decodedFile.SubRequests.Select(s => s.Type));
        var decodedCell = decodedFile.SubRequests[0];
        Assert.Equal(cell.SubRequestToken, decodedCell.SubRequestToken);
        Assert.Equal(cell.DependsOn, decodedCell.DependsOn);
        Assert.Equal(cell.DependencyType, decodedCell.DependencyType);
        Assert.Equal(cell.SubRequestDataAttributes["SchemaLockID"], decodedCell.SubRequestDataAttributes["SchemaLockID"]);
        Assert.Equal(cell.SubRequestDataAttributes["ClientID"], decodedCell.SubRequestDataAttributes["ClientID"]);
        Assert.Equal(binary.Length.ToString(), decodedCell.SubRequestDataAttributes["BinaryDataSize"]);
        var payload = mtom
            ? Assert.Single(parts!, part => $"cid:{part.ContentId}" == decodedCell.SubRequestDataAttributes["IncludeHref"]).Content
            : Convert.FromBase64String(XElement.Parse(decodedCell.SubRequestDataXml!).Value);
        Assert.Equal(binary, payload);
        var decodedProperties = decodedFile.SubRequests.Single(s => s.Type == SubRequestType.Properties);
        Assert.Equal("PropertyGet", decodedProperties.SubRequestDataAttributes["PropertyRequestType"]);
        Assert.Equal("client & reader", decodedProperties.SubRequestDataAttributes["ClientID"]);
        Assert.Equal("PropertyIds", Assert.Single(XElement.Parse(decodedProperties.SubRequestDataXml!).Elements()).Name.LocalName);
        var decodedEditors = decodedFile.SubRequests.Single(s => s.Type == SubRequestType.EditorsTable);
        Assert.Equal("MQ==", XElement.Parse(decodedEditors.SubRequestDataXml!).Value);

        // A parsed request can be reframed. The server resolves the XOP references into binary memory.
        if (mtom)
        {
            foreach (var sub in decoded.Requests.SelectMany(r => r.SubRequests))
            {
                if (sub.SubRequestDataAttributes.TryGetValue("IncludeHref", out var href))
                    sub.SubRequestDataBinaryMemory = Assert.Single(parts!, part => $"cid:{part.ContentId}" == href).ContentMemory;
            }
            Assert.Equal(binary, Convert.FromBase64String(XElement.Parse(
                CellStorageRequestParser.Parse(decoded.ToSoapEnvelope()).Requests[0].SubRequests[0].SubRequestDataXml!).Value));
        }
        else
        {
            var message = decoded.ToMtomMessage();
            var reframed = MtomMessageParser.ParseViews(message.Body, message.ContentType);
            Assert.Equal(binary, reframed[1].Content);
        }
    }

    [Theory]
    [InlineData(CellStorageRequest.Soap11Namespace, false)]
    [InlineData(CellStorageRequest.Soap11Namespace, true)]
    [InlineData(CellStorageRequest.Soap12Namespace, false)]
    [InlineData(CellStorageRequest.Soap12Namespace, true)]
    public void ResponseRoundTripsTokensXmlAndBinaryErrors(string soap, bool mtom)
    {
        var request = new CellStorageRequest
        {
            Requests =
            {
                new FssHttpRequest { RequestToken = 12, SubRequests =
                {
                    new FssHttpSubRequest { SubRequestToken = 1, Type = SubRequestType.Cell },
                    new FssHttpSubRequest { SubRequestToken = 2, Type = SubRequestType.Properties },
                    new FssHttpSubRequest { SubRequestToken = 3, Type = SubRequestType.GetVersions },
                    new FssHttpSubRequest { SubRequestToken = 4, Type = SubRequestType.Cell },
                } },
                new FssHttpRequest { RequestToken = 13, SubRequests =
                {
                    new FssHttpSubRequest { SubRequestToken = 1, Type = SubRequestType.WhoAmI },
                } },
            },
        };
        byte[] binary = new FsshttpbResponse
        {
            Status = true,
            Error = new ResponseError(ErrorType.HResult, 0x80070005, "Access denied."),
        }.ToByteArray();
        var file = new FssHttpResponse
        {
            Url = "https://example.test/shared/Book%20one.xlsx",
            RequestToken = 12,
            ResourceId = Guid.NewGuid(),
            HealthScore = 2,
            IntervalOverride = 30,
            SubResponses =
            {
                new FssHttpSubResponse
                {
                    SubRequestToken = 2,
                    SubResponseDataAttributes = { ["Example"] = "attribute & value" },
                    SubResponseDataXml = "<PropertyValues><PropertyValue>url &amp; value</PropertyValue></PropertyValues>",
                },
                new FssHttpSubResponse
                {
                    SubRequestToken = 1, ErrorCode = "CellRequestFail", HResult = "2147942405", ErrorMessage = "outer & error",
                    ServerCorrelationId = "correlation", SubResponseDataBase64 = binary,
                    SubResponseDataAttributes = { ["Etag"] = "\"etag\"" },
                },
                new FssHttpSubResponse
                {
                    SubRequestToken = 3, SubResponseXml = "<GetVersionsResponse><GetVersionsResult /></GetVersionsResponse>",
                },
                new FssHttpSubResponse
                {
                    SubRequestToken = 4, SubResponseStreamInvalid = true, SubResponseDataBase64 = [1, 2, 3],
                },
            },
        };
        var response = new CellStorageResponse
        {
            SoapNamespace = soap,
            WebUrl = "https://example.test/shared%20site",
            WebUrlIsEncoded = true,
            MinorVersion = 2,
            Responses = { new FssHttpResponse
            {
                Url = "https://example.test/missing.xlsx", RequestToken = 13, ErrorCode = "FileNotFound",
                SubResponses = { new FssHttpSubResponse { SubRequestToken = 1, EmitEmptySubResponseData = true } },
            }, file },
        };

        CellStorageResponse decoded;
        if (mtom)
        {
            var message = response.ToMtomMessage();
            decoded = CellStorageResponseParser.Parse(message.Body, message.ContentType, request);
        }
        else
        {
            decoded = CellStorageResponseParser.Parse(response.ToSoapEnvelope(), request);
        }

        Assert.Equal(soap, decoded.SoapNamespace);
        Assert.Equal(response.WebUrl, decoded.WebUrl);
        Assert.True(decoded.WebUrlIsEncoded);
        Assert.Equal(response.MinorVersion, decoded.MinorVersion);
        Assert.Equal("FileNotFound", decoded.Responses[0].ErrorCode);
        var whoAmI = Assert.Single(decoded.Responses[0].SubResponses);
        Assert.Equal(SubRequestType.WhoAmI, whoAmI.Type);
        Assert.True(whoAmI.EmitEmptySubResponseData);
        var decodedFile = decoded.Responses[1];
        Assert.Equal(file.RequestToken, decodedFile.RequestToken);
        Assert.Equal(file.ResourceId, decodedFile.ResourceId);
        Assert.Equal(file.HealthScore, decodedFile.HealthScore);
        Assert.Equal(file.IntervalOverride, decodedFile.IntervalOverride);
        Assert.Equal(file.Url, decodedFile.Url);
        var properties = decodedFile.SubResponses[0];
        Assert.Equal(SubRequestType.Properties, properties.Type);
        Assert.Equal("attribute & value", properties.SubResponseDataAttributes["Example"]);
        Assert.Equal("url & value", XElement.Parse(properties.SubResponseDataXml!).Value);
        var cell = decodedFile.SubResponses[1];
        Assert.Equal(1UL, cell.SubRequestToken);
        Assert.Equal("CellRequestFail", cell.ErrorCode);
        Assert.Equal("2147942405", cell.HResult);
        Assert.Equal("outer & error", cell.ErrorMessage);
        Assert.Equal("correlation", cell.ServerCorrelationId);
        Assert.Equal("\"etag\"", cell.SubResponseDataAttributes["Etag"]);
        Assert.Equal(binary, cell.SubResponseDataBase64);
        Assert.Equal(ErrorType.HResult, cell.BinaryResponse!.Error!.Type);
        Assert.Equal(0x80070005UL, cell.BinaryResponse.Error.ErrorCode);
        Assert.Equal("Access denied.", cell.BinaryResponse.Error.ErrorMessage);
        Assert.Equal("GetVersionsResponse", XElement.Parse(decodedFile.SubResponses[2].SubResponseXml!).Name.LocalName);
        Assert.True(decodedFile.SubResponses[3].SubResponseStreamInvalid);
        Assert.Null(decodedFile.SubResponses[3].BinaryResponse);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("save-first")]
    [InlineData("save-second")]
    [InlineData("reopen")]
    public void ParsesCapturedSharePointResponse(string name)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json")));
        var source = fixture.RootElement.GetProperty("response");
        var body = Convert.FromBase64String(source.GetProperty("bodyBase64").GetString()!);
        var decoded = CellStorageResponseParser.Parse(body, source.GetProperty("contentType").GetString()!);
        Assert.Null(decoded.Fault);
        var subResponses = Assert.Single(decoded.Responses).SubResponses;
        Assert.Contains(subResponses, sub => sub.BinaryResponse is not null);
        if (name is "open" or "reopen")
        {
            var access = Assert.Single(subResponses, sub =>
                sub.BinaryResponse?.SubResponses.SingleOrDefault()?.Data is QueryAccessSubResponseData);
            var data = Assert.IsType<QueryAccessSubResponseData>(Assert.Single(access.BinaryResponse!.SubResponses).Data);
            Assert.NotNull(data.AdditionalAccessResponseBytes);
            Assert.Equal(access.SubResponseDataBase64, access.BinaryResponse.ToByteArray(FsshttpbSerializationProfile.SharePoint13_11));
        }
    }

    [Theory]
    [InlineData(CellStorageRequest.Soap11Namespace, "<faultcode>s:Server</faultcode><faultstring>Unavailable</faultstring><detail><retry /></detail>", "s:Server")]
    [InlineData(CellStorageRequest.Soap12Namespace, "<s:Code><s:Value>s:Receiver</s:Value></s:Code><s:Reason><s:Text xml:lang=\"en\">Unavailable</s:Text></s:Reason><s:Detail><retry /></s:Detail>", "s:Receiver")]
    public void ParsesAndReserializesSoapFault(string soap, string detail, string code)
    {
        var response = CellStorageResponseParser.Parse($"""<s:Envelope xmlns:s="{soap}"><s:Body><s:Fault>{detail}</s:Fault></s:Body></s:Envelope>""");
        Assert.Empty(response.Responses);
        Assert.Equal(code, response.Fault!.Code);
        Assert.Equal("Unavailable", response.Fault.Reason);
        Assert.Equal(soap, XElement.Parse(response.Fault.Xml).GetNamespaceOfPrefix("s")!.NamespaceName);
        Assert.Equal("retry", XElement.Parse(response.Fault.Xml).Descendants("retry").Single().Name.LocalName);
        var message = response.ToMtomMessage();
        var decoded = CellStorageResponseParser.Parse(message.Body, message.ContentType);
        Assert.Equal(code, decoded.Fault!.Code);
    }

    [Fact]
    public void MtomResolvesPercentEncodedContentIdUsingTheDeclaredRoot()
    {
        var binary = new FsshttpbResponse().ToBase64();
        string contentType = """multipart/related; boundary="boundary"; start="<root@example>"; type="application/xop+xml" """;
        var body = string.Join("\r\n", "--boundary", "Content-Type: application/octet-stream",
            "Content-ID: <cell@example>", "", Encoding.Latin1.GetString(Convert.FromBase64String(binary)),
            "--boundary", "Content-Type: application/xop+xml; charset=utf-8", "Content-ID: <root@example>", "",
            WrapSubResponseData("""<xop:Include xmlns:xop="http://www.w3.org/2004/08/xop/include" href="cid:cell%40example" />"""),
            "--boundary--", "");

        var response = CellStorageResponseParser.Parse(Encoding.Latin1.GetBytes(body), contentType);
        Assert.NotNull(Assert.Single(Assert.Single(response.Responses).SubResponses).BinaryResponse);
        Assert.Throws<InvalidDataException>(() => CellStorageResponseParser.Parse(Encoding.Latin1.GetBytes(
            body.Replace("cid:cell%40example", "cid:missing%40example", StringComparison.Ordinal)), contentType));
    }

    [Fact]
    public void UnsupportedResponseCharsetIsMalformedResponseData()
    {
        Assert.Throws<InvalidDataException>(() => CellStorageResponseParser.Parse(
            Encoding.UTF8.GetBytes(WrapSubResponseData("")), "text/xml; charset=invalid-charset"));
    }

    [Fact]
    public void ReadsVersionErrorWithoutResponseCollection()
    {
        var response = CellStorageResponseParser.Parse($"""
            <s:Envelope xmlns:s="{CellStorageRequest.Soap11Namespace}">
              <s:Body>
                <ResponseVersion xmlns="{CellStorageRequest.Namespace}" Version="2" MinorVersion="0"
                   ErrorCode="InvalidWebUrl" ErrorMessage="Site unavailable." />
              </s:Body>
            </s:Envelope>
            """);
        Assert.Equal("InvalidWebUrl", response.VersionErrorCode);
        Assert.Equal("Site unavailable.", response.VersionErrorMessage);
        Assert.Empty(response.Responses);
    }

    private static string WrapSubResponseData(string data) => $"""
        <s:Envelope xmlns:s="{CellStorageRequest.Soap11Namespace}">
          <s:Body>
            <ResponseVersion xmlns="{CellStorageRequest.Namespace}" Version="2" MinorVersion="0" />
            <ResponseCollection xmlns="{CellStorageRequest.Namespace}">
              <Response RequestToken="1"><SubResponse SubRequestToken="1"><SubResponseData>{data}</SubResponseData></SubResponse></Response>
            </ResponseCollection>
          </s:Body>
        </s:Envelope>
        """;
}
