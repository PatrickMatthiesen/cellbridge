using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace CellBridge.Storage.Tests;

internal static class MicrosoftResponseSchema
{
    private static readonly Lazy<XmlSchemaSet> Schemas = new(() =>
    {
        var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MS-FSSHTTP-FSSHTTPB.wsdl"));
        var schemas = new XmlSchemaSet { XmlResolver = null };
        foreach (var element in source.Descendants(XName.Get("schema", XmlSchema.Namespace)))
        {
            // The upstream Envelope schema lacks a tns import. Validate its
            // documented Body shape separately and validate ResponseCollection below.
            if ((string?)element.Attribute("targetNamespace") == "http://schemas.xmlsoap.org/soap/envelope/") continue;
            using var reader = element.CreateReader();
            schemas.Add(XmlSchema.Read(reader, null)!);
        }
        schemas.Compile();
        return schemas;
    });

    public static void Validate(string soap)
        => Assert.Empty(Errors(soap));

    public static IReadOnlyList<string> Errors(string soap)
    {
        var source = XDocument.Parse(soap);
        var errors = new List<string>();
        XNamespace soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace soap12 = "http://www.w3.org/2003/05/soap-envelope";
        var body = source.Root?.Element(soap11 + "Body") ?? source.Root?.Element(soap12 + "Body");
        var expected = new[]
        {
            XName.Get("ResponseVersion", "http://schemas.microsoft.com/sharepoint/soap/"),
            XName.Get("ResponseCollection", "http://schemas.microsoft.com/sharepoint/soap/"),
        };
        if (body is null || !body.Elements().Select(element => element.Name).SequenceEqual(expected))
            errors.Add("SOAP Body must contain direct ResponseVersion and ResponseCollection children.");

        var collection = source.Descendants(XName.Get("ResponseCollection", "http://schemas.microsoft.com/sharepoint/soap/")).Single();
        new XDocument(new XElement(collection)).Validate(Schemas.Value, (_, e) => errors.Add(e.Message));
        return errors;
    }
}
