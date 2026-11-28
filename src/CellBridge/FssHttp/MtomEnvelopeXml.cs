using System.Text;
using System.Xml;

namespace CellBridge.FssHttp;

internal static class MtomEnvelopeXml
{
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        // Match XDocument.Parse. The generated envelope starts before any raw fragment.
        DtdProcessing = DtdProcessing.Parse,
        XmlResolver = null,
        MaxCharactersFromEntities = 10_000_000,
    };
    private static readonly XmlWriterSettings WriterSettings = new() { OmitXmlDeclaration = true };

    internal static string Normalize(string envelope)
    {
        using var source = new StringReader(envelope);
        using var reader = XmlReader.Create(source, ReaderSettings);
        var result = new StringBuilder(envelope.Length);
        var declarations = new List<(int Depth, string Prefix, string Namespace)>();

        using (var writer = XmlWriter.Create(result, WriterSettings))
        {
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        WriteStartElement(reader, writer, declarations);
                        if (!reader.IsEmptyElement)
                            break;
                        writer.WriteEndElement();
                        RemoveDeclarations(declarations, reader.Depth);
                        break;
                    case XmlNodeType.EndElement:
                        writer.WriteFullEndElement();
                        RemoveDeclarations(declarations, reader.Depth);
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.SignificantWhitespace:
                        writer.WriteString(reader.Value);
                        break;
                    case XmlNodeType.Whitespace:
                        writer.WriteWhitespace(reader.Value);
                        break;
                    case XmlNodeType.CDATA:
                        writer.WriteCData(reader.Value);
                        break;
                    case XmlNodeType.Comment:
                        writer.WriteComment(reader.Value);
                        break;
                    case XmlNodeType.ProcessingInstruction:
                        writer.WriteProcessingInstruction(reader.Name, reader.Value);
                        break;
                }
            }
        }

        return result.ToString();
    }

    private static void WriteStartElement(XmlReader reader, XmlWriter writer,
        List<(int Depth, string Prefix, string Namespace)> declarations)
    {
        int depth = reader.Depth;
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                if (reader.NamespaceURI == XmlnsNamespace)
                    declarations.Add((depth, reader.Prefix.Length == 0 ? "" : reader.LocalName, reader.Value));
            } while (reader.MoveToNextAttribute());
            reader.MoveToElement();
        }

        writer.WriteStartElement(GetPrefix(reader, declarations, allowDefault: true), reader.LocalName, reader.NamespaceURI);
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                writer.WriteAttributeString(GetPrefix(reader, declarations, allowDefault: false),
                    reader.LocalName, reader.NamespaceURI, reader.Value);
            } while (reader.MoveToNextAttribute());
            reader.MoveToElement();
        }
    }

    private static string GetPrefix(XmlReader reader,
        List<(int Depth, string Prefix, string Namespace)> declarations, bool allowDefault)
    {
        var ns = reader.NamespaceURI;
        // LINQ to XML chooses the last unshadowed declaration for a namespace.
        // The lexical prefix can differ when a fragment uses namespace aliases.
        switch (ns)
        {
            case "": return "";
            case XmlNamespace: return "xml";
            case XmlnsNamespace: return reader.Prefix;
        }
        for (int index = declarations.Count - 1; index >= 0; index--)
        {
            var declaration = declarations[index];
            if (declaration.Namespace == ns &&
                (allowDefault || declaration.Prefix.Length != 0) &&
                (declaration.Prefix == reader.Prefix || reader.LookupNamespace(declaration.Prefix) == ns))
                return declaration.Prefix;
        }
        return reader.Prefix;
    }

    private static void RemoveDeclarations(List<(int Depth, string Prefix, string Namespace)> declarations, int depth)
    {
        while (declarations.Count > 0 && declarations[^1].Depth == depth)
            declarations.RemoveAt(declarations.Count - 1);
    }
}
