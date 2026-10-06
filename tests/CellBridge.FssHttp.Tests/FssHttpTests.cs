using CellBridge.FssHttp;
using Xunit;

namespace CellBridge.FssHttp.Tests;

public class CellStorageRequestParserTests
{
    private const string SampleQueryAccessEnvelope = """
        <?xml version="1.0" encoding="utf-8"?>
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
                       xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                       xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <soap:Body>
            <ExecuteCellStorageRequest xmlns="http://schemas.microsoft.com/sharepoint/soap/">
              <RequestVersion Version="2" MinorVersion="0" />
              <RequestCollection CorrelationId="6B29FC40-CA47-1067-B31D-00DD010662DA">
                <Request Url="https://example.com/shared/test.docx"
                         RequestToken="1"
                         UserAgent="E731B87E-DD45-44AA-AB80-0C75FBD1530E">
                  <SubRequest Type="Cell" SubRequestToken="0">
                    <SubRequestData ContentVersion="0" CoauthID="6B29FC40-CA47-1067-B31D-00DD010662DB" BinaryDataSize="1">AA==</SubRequestData>
                  </SubRequest>
                </Request>
              </RequestCollection>
            </ExecuteCellStorageRequest>
          </soap:Body>
        </soap:Envelope>
        """;

    [Fact]
    public void Parses_BasicEnvelope()
    {
        var request = CellStorageRequestParser.Parse(SampleQueryAccessEnvelope);

        Assert.Equal(2u, request.Version);
        Assert.Equal(0u, request.MinorVersion);
        Assert.Equal(Guid.Parse("6B29FC40-CA47-1067-B31D-00DD010662DA"), request.CorrelationId);

        var fileRequest = Assert.Single(request.Requests);
        Assert.Equal("https://example.com/shared/test.docx", fileRequest.Url);
        Assert.Equal(1UL, fileRequest.RequestToken);
        Assert.Equal(Guid.Parse("E731B87E-DD45-44AA-AB80-0C75FBD1530E"), fileRequest.UserAgent);

        var subRequest = Assert.Single(fileRequest.SubRequests);
        Assert.Equal(SubRequestType.Cell, subRequest.Type);
        Assert.Equal(0UL, subRequest.SubRequestToken);
        Assert.Equal("0", subRequest.SubRequestDataAttributes["ContentVersion"]);
        Assert.Equal("6B29FC40-CA47-1067-B31D-00DD010662DB", subRequest.SubRequestDataAttributes["CoauthID"]);
    }

    [Theory]
    [InlineData("Cell", SubRequestType.Cell)]
    [InlineData("Coauth", SubRequestType.Coauth)]
    [InlineData("SchemaLock", SubRequestType.SchemaLock)]
    [InlineData("WhoAmI", SubRequestType.WhoAmI)]
    [InlineData("ServerTime", SubRequestType.ServerTime)]
    [InlineData("ExclusiveLock", SubRequestType.ExclusiveLock)]
    [InlineData("EditorsTable", SubRequestType.EditorsTable)]
    [InlineData("GetDocMetaInfo", SubRequestType.GetDocMetaInfo)]
    [InlineData("GetVersions", SubRequestType.GetVersions)]
    [InlineData("FileOperation", SubRequestType.FileOperation)]
    [InlineData("Versioning", SubRequestType.Versioning)]
    [InlineData("AmIAlone", SubRequestType.AmIAlone)]
    [InlineData("LockStatus", SubRequestType.LockStatus)]
    [InlineData("Properties", SubRequestType.Properties)]
    public void Parses_AllSubRequestTypes(string typeName, SubRequestType expected)
    {
        string envelope = SampleQueryAccessEnvelope.Replace("Type=\"Cell\"", $"Type=\"{typeName}\"");
        if (expected is SubRequestType.WhoAmI or SubRequestType.ServerTime or SubRequestType.GetDocMetaInfo or
            SubRequestType.GetVersions or SubRequestType.LockStatus)
            envelope = envelope.Replace("<SubRequestData ContentVersion=\"0\" CoauthID=\"6B29FC40-CA47-1067-B31D-00DD010662DB\" BinaryDataSize=\"1\">AA==</SubRequestData>", string.Empty);
        else if (expected != SubRequestType.Cell)
            envelope = envelope.Replace("<SubRequestData ContentVersion=\"0\" CoauthID=\"6B29FC40-CA47-1067-B31D-00DD010662DB\" BinaryDataSize=\"1\">AA==</SubRequestData>", "<SubRequestData />");
        var request = CellStorageRequestParser.Parse(envelope);
        Assert.Equal(expected, request.Requests[0].SubRequests[0].Type);
    }

    [Theory]
    [InlineData("Version=\"2\"")]
    [InlineData("MinorVersion=\"0\"")]
    [InlineData("CorrelationId=\"6B29FC40-CA47-1067-B31D-00DD010662DA\"")]
    [InlineData("RequestToken=\"1\"")]
    [InlineData("SubRequestToken=\"0\"")]
    public void MissingRequiredScalar_Throws(string attribute)
    {
        Assert.Throws<InvalidDataException>(() => CellStorageRequestParser.Parse(
            SampleQueryAccessEnvelope.Replace(attribute, string.Empty)));
    }

    [Fact]
    public void UnsupportedButSyntacticallyValidVersion_ParsesForVersionErrorResponse()
    {
        var request = CellStorageRequestParser.Parse(SampleQueryAccessEnvelope.Replace("Version=\"2\"", "Version=\"3\""));

        Assert.Equal(3u, request.Version);
        Assert.Equal(0u, request.MinorVersion);
    }

    [Fact]
    public void ReservedMinorVersion_IsPreserved()
    {
        var request = CellStorageRequestParser.Parse(SampleQueryAccessEnvelope.Replace("MinorVersion=\"0\"", "MinorVersion=\"65535\""));

        Assert.Equal(65535u, request.MinorVersion);
    }

    [Fact]
    public void XmlSchemaScalarLexicalForms_AreAcceptedAndValuesArePreserved()
    {
        string envelope = SampleQueryAccessEnvelope
            .Replace("Version=\"2\"", "Version=\" +2 \"")
            .Replace("MinorVersion=\"0\"", "MinorVersion=\" -0 \"")
            .Replace("RequestToken=\"1\"", "RequestToken=\" +1 \" UseResourceID=\" 0 \"")
            .Replace("SubRequestToken=\"0\"", "SubRequestToken=\" -0 \"")
            .Replace("BinaryDataSize=\"1\"",
                "BinaryDataSize=\" +1 \" GetFileProps=\" 1 \" ExclusiveLockID=\"6B29FC40-CA47-1067-B31D-00DD010662DC\" Timeout=\" +60 \" LastModifiedTime=\" +0 \"");

        var request = CellStorageRequestParser.Parse(envelope);
        var fileRequest = Assert.Single(request.Requests);
        var subRequest = Assert.Single(fileRequest.SubRequests);

        Assert.Equal(2u, request.Version);
        Assert.Equal(0u, request.MinorVersion);
        Assert.Equal(1UL, fileRequest.RequestToken);
        Assert.False(fileRequest.UseResourceId);
        Assert.Equal(0UL, subRequest.SubRequestToken);
        Assert.Equal(" +1 ", subRequest.SubRequestDataAttributes["BinaryDataSize"]);
        Assert.True(CellSubRequestDataValidation.TryGetBoolean(
            subRequest.SubRequestDataAttributes, "GetFileProps", out bool getFileProps));
        Assert.True(getFileProps);
        Assert.True(CellSubRequestDataValidation.TryGetLastModifiedTime(
            subRequest.SubRequestDataAttributes, out DateTime lastModified));
        Assert.Equal(DateTime.FromFileTimeUtc(0), lastModified);
    }

    [Theory]
    [InlineData("BinaryDataSize=\"1\"", "BinaryDataSize=\"0\"")]
    [InlineData("BinaryDataSize=\"1\"", "BinaryDataSize=\"1\" GetFileProps=\"True\"")]
    [InlineData("BinaryDataSize=\"1\"", "BinaryDataSize=\"1\" ExclusiveLockID=\"bad\" Timeout=\"60\"")]
    [InlineData("BinaryDataSize=\"1\"", "BinaryDataSize=\"1\" ExclusiveLockID=\"6B29FC40-CA47-1067-B31D-00DD010662DC\" Timeout=\"59\"")]
    [InlineData("BinaryDataSize=\"1\"", "BinaryDataSize=\"1\" LastModifiedTime=\"-1\"")]
    public void MalformedCellScalars_AreRejectedBeforeExecution(string original, string replacement)
    {
        Assert.Throws<InvalidDataException>(() => CellStorageRequestParser.Parse(
            SampleQueryAccessEnvelope.Replace(original, replacement)));
    }

    [Fact]
    public void DuplicateRequestAndSubRequestTokens_Throw()
    {
        string duplicateRequest = SampleQueryAccessEnvelope.Replace("</RequestCollection>",
            SampleQueryAccessEnvelope[(SampleQueryAccessEnvelope.IndexOf("<Request Url=", StringComparison.Ordinal))..SampleQueryAccessEnvelope.IndexOf("</Request>", StringComparison.Ordinal)] + "</Request></RequestCollection>");
        Assert.Throws<InvalidDataException>(() => CellStorageRequestParser.Parse(duplicateRequest));

        string duplicateSubRequest = SampleQueryAccessEnvelope.Replace("</Request>",
            "<SubRequest Type=\"WhoAmI\" SubRequestToken=\"0\" /></Request>");
        Assert.Throws<InvalidDataException>(() => CellStorageRequestParser.Parse(duplicateSubRequest));
    }

    [Fact]
    public void UnknownSubRequestType_Throws()
    {
        string envelope = SampleQueryAccessEnvelope.Replace("Type=\"Cell\"", "Type=\"Bogus\"");
        Assert.Throws<InvalidDataException>(() => CellStorageRequestParser.Parse(envelope));
    }

    [Fact]
    public void MissingBody_Throws()
    {
        Assert.Throws<InvalidDataException>(() =>
            CellStorageRequestParser.Parse("<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"/>"));
    }
}

public class CellStorageResponseTests
{
    [Fact]
    public void ToSoapEnvelope_PreservesIntegerServerTime()
    {
        var response = new CellStorageResponse
        {
            Responses =
            {
                new FssHttpResponse
                {
                    SubResponses =
                    {
                        new FssHttpSubResponse
                        {
                            Type = SubRequestType.ServerTime,
                            SubResponseDataAttributes =
                            {
                                ["ServerTime"] = "638923392000000000",
                            },
                        },
                    },
                },
            },
        };

        string envelope = response.ToSoapEnvelope();

        Assert.Contains("ServerTime=\"638923392000000000\"", envelope);
        Assert.DoesNotContain("T00:00:00", envelope);
    }

    [Fact]
    public void ToSoapEnvelope_DoesNotAddNonstandardWhoAmIUserId()
    {
        var response = new CellStorageResponse
        {
            Responses =
            {
                new FssHttpResponse
                {
                    SubResponses =
                    {
                        new FssHttpSubResponse
                        {
                            Type = SubRequestType.WhoAmI,
                            SubResponseDataAttributes =
                            {
                                ["UserName"] = "officelab",
                                ["UserLogin"] = "officelab",
                            },
                        },
                    },
                },
            },
        };

        string envelope = response.ToSoapEnvelope();

        Assert.Contains("UserName=\"officelab\"", envelope);
        Assert.Contains("UserLogin=\"officelab\"", envelope);
        Assert.DoesNotContain("UserId=", envelope);
    }

    [Fact]
    public void ToSoapEnvelope_ProducesValidXml()
    {
        var response = new CellStorageResponse
        {
            WebUrl = "https://example.com/shared",
            Responses =
            {
                new FssHttpResponse
                {
                    Url = "https://example.com/shared/test.docx",
                    RequestToken = 1,
                    SubResponses =
                    {
                        new FssHttpSubResponse
                        {
                            Type = SubRequestType.Cell,
                            SubResponseDataBase64 = new byte[] { 0x01, 0x02 },
                        },
                    },
                },
            },
        };

        string envelope = response.ToSoapEnvelope();
        var doc = System.Xml.Linq.XDocument.Parse(envelope);

        var root = doc.Root;
        Assert.NotNull(root);
        Assert.Equal("Envelope", root!.Name.LocalName);

        Assert.Contains("ResponseCollection", envelope);
        Assert.Contains("RequestToken=\"1\"", envelope);
        Assert.Contains("WebUrl=\"https://example.com/shared\"", envelope);
    }

    [Fact]
    public void ToSoapEnvelope_EscapesUrl()
    {
        var response = new CellStorageResponse
        {
            WebUrl = "https://example.com/a&b",
            Responses =
            {
                new FssHttpResponse
                {
                    Url = "https://example.com/a&b/test.docx",
                },
            },
        };

        string envelope = response.ToSoapEnvelope();
        // Must remain parseable XML.
        System.Xml.Linq.XDocument.Parse(envelope);
        Assert.Contains("a&amp;b", envelope);
    }

    [Fact]
    public void ToSoapEnvelope_MatchesSharePointResponseAttributes()
    {
        var resourceId = Guid.Parse("596881b0-cdb7-4e3a-b5f2-42a58db5c60a");
        var response = new CellStorageResponse
        {
            Version = 2,
            MinorVersion = 3,
            UsesDirectBody = true,
            WebUrl = "https://example.com",
            Responses =
            {
                new FssHttpResponse
                {
                    Url = "https://example.com/shared/test.docx",
                    RequestToken = 1,
                    IntervalOverride = 0,
                    ResourceId = resourceId,
                    SubResponses =
                    {
                        new FssHttpSubResponse
                        {
                            Type = SubRequestType.Cell,
                            SubRequestToken = 8,
                            ErrorCode = "Success",
                            HResult = "0",
                            EmitEmptySubResponseData = true,
                        },
                    },
                },
            },
        };

        string envelope = response.ToSoapEnvelope();

        Assert.Contains("ResponseVersion xmlns=\"http://schemas.microsoft.com/sharepoint/soap/\" Version=\"2\" MinorVersion=\"3\"", envelope);
        Assert.Contains("WebUrl=\"https://example.com\"", envelope);
        Assert.Contains("IntervalOverride=\"0\"", envelope);
        Assert.Contains("ResourceID=\"596881b0cdb74e3ab5f242a58db5c60a\"", envelope);
        Assert.Contains("<SubResponse SubRequestToken=\"8\" ErrorCode=\"Success\" HResult=\"0\">", envelope);
        Assert.DoesNotContain("<SubResponse Type=", envelope);
    }

    [Fact]
    public void ToSoapEnvelope_EmitsRequiredResponseErrorMessageAndVersionErrorDetail()
    {
        var response = new CellStorageResponse
        {
            VersionErrorCode = "IncompatibleVersion",
            VersionErrorMessage = "Only version 2 is supported.",
            Responses = { new FssHttpResponse { ErrorCode = "InvalidArgument" } },
        };

        string envelope = response.ToSoapEnvelope();

        Assert.Contains("ErrorCode=\"IncompatibleVersion\" ErrorMessage=\"Only version 2 is supported.\"", envelope);
        Assert.Contains("ErrorCode=\"InvalidArgument\" ErrorMessage=\"InvalidArgument\"", envelope);
    }
}
