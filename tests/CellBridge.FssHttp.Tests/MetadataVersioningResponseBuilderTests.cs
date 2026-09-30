using System.Xml.Linq;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Web;

public sealed class MetadataVersioningResponseBuilderTests
{
    [Fact]
    public void GetDocMetaInfo_UsesDerivedDocumentPropertiesInsideSubResponseData()
    {
        var store = new DocumentStore();
        var document = store.Put("/shared/test.docx", new byte[] { 1, 2, 3 });

        var subResponse = MetadataVersioningResponseBuilder.BuildGetDocMetaInfo(document, "test-user");
        var envelope = new CellStorageResponse
        {
            WebUrl = "https://example.test",
            Responses =
            {
                new FssHttpResponse
                {
                    Url = "https://example.test/shared/test.docx",
                    SubResponses = { subResponse },
                },
            },
        }.ToSoapEnvelope();

        var xml = XDocument.Parse(envelope);
        var data = xml.Descendants().Single(e => e.Name.LocalName == "SubResponseData");
        var props = data.Elements().Single(e => e.Name.LocalName == "DocProps");

        Assert.Equal("3", PropertyValue(props, "vti_filesize"));
        Assert.Equal("1", PropertyValue(props, "vti_contentversion"));
        Assert.Equal("test-user", PropertyValue(props, "vti_modifiedby"));
        Assert.Equal("false", PropertyValue(props, "vti_contentversionisdirty"));
    }

    [Fact]
    public void GetVersions_UsesNestedProtocolPayloadAndCurrentVersionOnly()
    {
        var store = new DocumentStore();
        var document = store.Put("/shared/test.docx", new byte[] { 1, 2, 3, 4 });
        document.SetContent(new byte[] { 5, 6 });

        var subResponse = MetadataVersioningResponseBuilder.BuildGetVersions(
            document, "https://example.test", "test-user");
        var envelope = new CellStorageResponse
        {
            WebUrl = "https://example.test",
            Responses =
            {
                new FssHttpResponse
                {
                    Url = "https://example.test/shared/test.docx",
                    SubResponses = { subResponse },
                },
            },
        }.ToSoapEnvelope();

        var xml = XDocument.Parse(envelope);
        var result = xml.Descendants().Single(e => e.Name.LocalName == "result");

        Assert.Equal("@2.0", (string?)result.Attribute("version"));
        Assert.Equal("https://example.test/shared/test.docx", (string?)result.Attribute("url"));
        Assert.Equal("2", (string?)result.Attribute("size"));
        Assert.Equal("test-user", (string?)result.Attribute("createdBy"));
        Assert.Equal("0", (string?)xml.Descendants().Single(e => e.Name.LocalName == "versioning").Attribute("enabled"));
        Assert.DoesNotContain(xml.Descendants(), e => e.Name.LocalName == "list");
        Assert.DoesNotContain(xml.Descendants(), e => e.Name.LocalName == "settings");
    }

    [Fact]
    public void GetVersions_EscapesDocumentUrlAndPropertyValues()
    {
        var store = new DocumentStore();
        var document = store.Put("/shared/a&b.docx", new byte[] { 1 });

        var subResponse = MetadataVersioningResponseBuilder.BuildGetVersions(
            document, "https://example.test/root", "a&b");
        var envelope = new CellStorageResponse
        {
            WebUrl = "https://example.test",
            Responses =
            {
                new FssHttpResponse { SubResponses = { subResponse } },
            },
        }.ToSoapEnvelope();

        var xml = XDocument.Parse(envelope);
        var result = xml.Descendants().Single(e => e.Name.LocalName == "result");
        Assert.Equal("https://example.test/root/shared/a&b.docx", (string?)result.Attribute("url"));
        Assert.Equal("a&b", (string?)result.Attribute("createdBy"));
    }

    private static string? PropertyValue(XElement props, string key) =>
        props.Elements().Single(e => (string?)e.Attribute("Key") == key).Attribute("Value")?.Value;
}
