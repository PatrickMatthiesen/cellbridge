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
            // Validate the actual ResponseCollection, independent of SOAP wrapper
            // selection. The upstream Envelope schema lacks a tns import.
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
        var collection = source.Descendants(XName.Get("ResponseCollection", "http://schemas.microsoft.com/sharepoint/soap/")).Single();
        var errors = new List<string>();
        new XDocument(new XElement(collection)).Validate(Schemas.Value, (_, e) => errors.Add(e.Message));
        return errors;
    }
}
