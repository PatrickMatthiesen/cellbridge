using System.Xml;

namespace CellBridge.Xml;

/// <summary>Checks each node before exposing it to a consumer such as LINQ to XML.</summary>
/// <remarks>The caller owns the underlying reader.</remarks>
internal sealed class CheckedXmlReader(XmlReader reader, Action<XmlReader> check) : XmlReader
{
    public override bool Read()
    {
        if (!reader.Read())
            return false;
        check(reader);
        return true;
    }

    public override int AttributeCount => reader.AttributeCount;
    public override string BaseURI => reader.BaseURI;
    public override int Depth => reader.Depth;
    public override bool EOF => reader.EOF;
    public override bool IsEmptyElement => reader.IsEmptyElement;
    public override string LocalName => reader.LocalName;
    public override string Name => reader.Name;
    public override string NamespaceURI => reader.NamespaceURI;
    public override XmlNameTable NameTable => reader.NameTable;
    public override XmlNodeType NodeType => reader.NodeType;
    public override string Prefix => reader.Prefix;
    public override ReadState ReadState => reader.ReadState;
    public override string Value => reader.Value;

    public override string GetAttribute(int i) => reader.GetAttribute(i);
    public override string? GetAttribute(string name) => reader.GetAttribute(name);
    public override string? GetAttribute(string name, string? namespaceURI) => reader.GetAttribute(name, namespaceURI);
    public override string? LookupNamespace(string prefix) => reader.LookupNamespace(prefix);
    public override bool MoveToAttribute(string name) => reader.MoveToAttribute(name);
    public override bool MoveToAttribute(string name, string? ns) => reader.MoveToAttribute(name, ns);
    public override bool MoveToElement() => reader.MoveToElement();
    public override bool MoveToFirstAttribute() => reader.MoveToFirstAttribute();
    public override bool MoveToNextAttribute() => reader.MoveToNextAttribute();
    public override bool ReadAttributeValue() => reader.ReadAttributeValue();
    public override void ResolveEntity() => reader.ResolveEntity();
}
