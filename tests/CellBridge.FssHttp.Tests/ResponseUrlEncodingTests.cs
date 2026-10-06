using System.Xml.Linq;
using CellBridge.FssHttp;

namespace CellBridge.FssHttp.Tests;

public sealed class ResponseUrlEncodingTests
{
    [Theory]
    [InlineData("https://localhost:7292/shared/Excel%20test.xlsx", "https://localhost:7292/shared/Excel%20test.xlsx")]
    [InlineData("https://localhost:7292/shared/Excel test.xlsx", "https://localhost:7292/shared/Excel%20test.xlsx")]
    [InlineData("https://localhost:7292/shared/Literal%2520.xlsx", "https://localhost:7292/shared/Literal%2520.xlsx")]
    [InlineData("https://localhost:7292/shared/a%23b%26c.xlsx", "https://localhost:7292/shared/a%23b%26c.xlsx")]
    public void EscapedResponseUrlIsFlaggedCorrectlyWithoutAnotherEscape(string input, string expected)
    {
        var response = new CellStorageResponse { WebUrl = "https://localhost:7292" };
        response.Responses.Add(new FssHttpResponse { Url = input, RequestToken = 1 });
        var element = XDocument.Parse(response.ToSoapEnvelope()).Descendants()
            .Single(e => e.Name.LocalName == "Response");
        Assert.Equal(expected, (string?)element.Attribute("Url"));
        Assert.Equal("true", (string?)element.Attribute("UrlIsEncoded"));
    }

    [Fact]
    public void ParserRetainsResourceIdentityFromExcelRelease()
    {
        var parsed = CellStorageRequestParser.Parse("""
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <RequestVersion xmlns="http://schemas.microsoft.com/sharepoint/soap/" Version="2" MinorVersion="2" />
                <RequestCollection xmlns="http://schemas.microsoft.com/sharepoint/soap/" CorrelationId="6B29FC40-CA47-1067-B31D-00DD010662DA">
                <Request Url="https://localhost:7292/shared/Excel%2520test.xlsx" UseResourceID="true"
                         ResourceID="643ac4721a2f444f821fbc42121f928d" RequestToken="1">
                  <SubRequest Type="ExclusiveLock" SubRequestToken="1">
                    <SubRequestData ExclusiveLockRequestType="ReleaseLock" ExclusiveLockID="{649EC616-2B78-4912-B9D9-A0A13D2065A7}" />
                  </SubRequest>
                </Request>
              </RequestCollection></s:Body>
            </s:Envelope>
            """);
        var request = Assert.Single(parsed.Requests);
        Assert.True(request.UseResourceId);
        Assert.Equal("643ac4721a2f444f821fbc42121f928d", request.ResourceId);
    }
}
