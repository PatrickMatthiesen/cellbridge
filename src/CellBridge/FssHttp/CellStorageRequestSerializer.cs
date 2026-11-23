using System.Xml.Linq;

namespace CellBridge.FssHttp;

internal static class CellStorageRequestSerializer
{
    private static readonly XNamespace Protocol = CellStorageRequest.Namespace;
    private static readonly XName DataName = Protocol + "SubRequestData";
    private static readonly XName IncludeName = CellStorageRequestParser.XopIncludeNs + "Include";

    public static string Serialize(CellStorageRequest request, List<ReadOnlyMemory<byte>>? parts)
    {
        if (request.SoapNamespace is not (CellStorageRequest.Soap11Namespace or CellStorageRequest.Soap12Namespace))
            throw new InvalidDataException("SOAP envelope namespace is not supported.");
        if (request.Version > ushort.MaxValue || request.MinorVersion > ushort.MaxValue)
            throw new InvalidDataException("RequestVersion fields must be unsigned 16-bit integers.");
        if (request.Requests.Count == 0)
            throw new InvalidDataException("RequestCollection must contain at least one Request element.");

        XNamespace soap = request.SoapNamespace;
        var collection = new XElement(Protocol + "RequestCollection",
            new XAttribute("CorrelationId", request.CorrelationId.ToString("B")));
        var requestTokens = new HashSet<ulong>();
        foreach (var file in request.Requests)
        {
            if (string.IsNullOrEmpty(file.Url) || file.RequestToken > uint.MaxValue || !requestTokens.Add(file.RequestToken))
                throw new InvalidDataException("Request must specify a URL and a unique unsigned 32-bit RequestToken.");
            if (file.SubRequests.Count == 0)
                throw new InvalidDataException("Request must contain at least one SubRequest element.");

            var element = new XElement(Protocol + "Request",
                new XAttribute("Url", file.Url), new XAttribute("RequestToken", file.RequestToken));
            if (file.UseResourceId)
                element.SetAttributeValue("UseResourceID", true);
            element.SetAttributeValue("ResourceID", file.ResourceId);
            element.SetAttributeValue("UserAgent", file.UserAgent?.ToString("B"));
            foreach (var (name, value) in file.RequestAttributes)
            {
                if (!CellStorageRequestParser.IsModeledRequestAttribute(name))
                    element.SetAttributeValue(name, value);
            }

            var tokens = new HashSet<ulong>();
            foreach (var sub in file.SubRequests)
            {
                if (sub.SubRequestToken is not { } token || token > uint.MaxValue || !tokens.Add(token))
                    throw new InvalidDataException("SubRequest must specify a unique unsigned 32-bit SubRequestToken.");
                if (!Enum.IsDefined(sub.Type))
                    throw new InvalidDataException($"Unknown SubRequest Type '{sub.Type}'.");
                if ((sub.DependsOn is null) != (sub.DependencyType is null) || sub.DependsOn > uint.MaxValue ||
                    sub.DependencyType is { } dependency && !CellStorageRequestParser.DependencyTypes.Contains(dependency))
                    throw new InvalidDataException("DependsOn and a valid DependencyType must be specified together.");

                var subElement = new XElement(Protocol + "SubRequest",
                    new XAttribute("Type", sub.Type.ToString()), new XAttribute("SubRequestToken", token));
                subElement.SetAttributeValue("DependsOn", sub.DependsOn);
                subElement.SetAttributeValue("DependencyType", sub.DependencyType);
                subElement.Add(SerializeData(sub, parts));
                element.Add(subElement);
            }

            collection.Add(element);
        }

        var envelope = new XElement(soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soap", soap.NamespaceName),
            new XElement(soap + "Body",
                new XElement(Protocol + "RequestVersion",
                    new XAttribute("Version", request.Version), new XAttribute("MinorVersion", request.MinorVersion)),
                collection));
        if (parts?.Count > 0)
            envelope.SetAttributeValue(XNamespace.Xmlns + "xop", IncludeName.NamespaceName);
        return envelope.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement? SerializeData(FssHttpSubRequest sub, List<ReadOnlyMemory<byte>>? parts)
    {
        var binary = sub.SubRequestDataBinaryMemory;
        bool hasData = sub.SubRequestDataXml is not null || binary is not null ||
            sub.SubRequestDataAttributes.Count > 0;
        if (!CellStorageRequestParser.AllowsSubRequestData(sub.Type))
        {
            if (hasData)
                throw new InvalidDataException($"{sub.Type} must not contain SubRequestData.");
            return null;
        }
        if (!hasData && sub.Type == SubRequestType.Cell)
            return null;

        var data = sub.SubRequestDataXml is { } raw
            ? XElement.Parse(raw, LoadOptions.PreserveWhitespace)
            : new XElement(DataName);
        if (data.Name != DataName && data.Name != "SubRequestData")
            throw new InvalidDataException("SubRequestDataXml must contain a SubRequestData element.");
        data.Name = DataName;
        // The enclosing element supplies the protocol namespace.
        data.Attribute("xmlns")?.Remove();
        foreach (var (name, value) in sub.SubRequestDataAttributes)
        {
            // IncludeHref is reader state. Each MTOM message allocates its own Content-IDs.
            if (name != "IncludeHref")
                data.SetAttributeValue(name, value);
        }

        if (sub.Type != SubRequestType.Cell)
        {
            if (binary is not null)
                throw new InvalidDataException("Only a Cell subrequest can carry a binary payload.");
            return data;
        }

        if (binary is null)
        {
            if (data.Element(IncludeName) is not null || sub.SubRequestDataAttributes.ContainsKey("IncludeHref"))
                throw new InvalidDataException("Serializing an XOP request requires the referenced Cell payload.");
            if (data.HasElements || string.IsNullOrWhiteSpace(data.Value))
                throw new InvalidDataException("Cell SubRequestData must contain a binary payload.");
            binary = Convert.FromBase64String(data.Value);
        }

        if (binary.Value.IsEmpty)
            throw new InvalidDataException("Cell SubRequestData must contain a nonempty binary payload.");
        data.SetAttributeValue("BinaryDataSize", binary.Value.Length);
        var attributes = data.Attributes().Where(a => a.Name.Namespace == XNamespace.None)
            .ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.Ordinal);
        if (!CellSubRequestDataValidation.TryValidate(attributes, requireBinaryDataSize: true, out string message))
            throw new InvalidDataException(message);

        if (parts is null)
        {
            data.Value = Convert.ToBase64String(binary.Value.Span);
        }
        else
        {
            string id = MtomResponseMessage.ContentId(parts.Count + 1);
            parts.Add(binary.Value);
            data.ReplaceNodes(new XElement(IncludeName, new XAttribute("href", "cid:" + id)));
        }
        return data;
    }
}
