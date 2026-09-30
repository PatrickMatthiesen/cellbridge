using System.Xml.Linq;

namespace CellBridge.FssHttp;

/// <summary>
/// Parses MS-FSSHTTP cell storage service SOAP request envelopes.
/// [MS-FSSHTTP] section 2.2.2.1.
/// </summary>
public static class CellStorageRequestParser
{
    private static readonly XNamespace Tns = "http://schemas.microsoft.com/sharepoint/soap/";
    private static readonly XNamespace XopIncludeNs = "http://www.w3.org/2004/08/xop/include";

    /// <summary>
    /// Parses a SOAP envelope containing an ExecuteCellStorageRequest into a
    /// <see cref="CellStorageRequest"/>.
    /// </summary>
    public static CellStorageRequest Parse(string soapXml)
    {
        var doc = XDocument.Parse(soapXml);
        // Word has used both SOAP 1.1 and SOAP 1.2 envelopes. The protocol
        // payload is identical, so identify the envelope elements by local
        // name rather than rejecting the SOAP 1.2 namespace.
        var body = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Body")
            ?? throw new InvalidDataException("SOAP envelope is missing a Body element.");

        var execute = body.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "ExecuteCellStorageRequest");
        var protocolContainer = execute ?? body;

        var request = new CellStorageRequest();

        var requestVersion = protocolContainer.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "RequestVersion");
        if (requestVersion is not null)
        {
            request.Version = (uint?)requestVersion.Attribute("Version") ?? 2;
            request.MinorVersion = (uint?)requestVersion.Attribute("MinorVersion") ?? 0;
        }

        var requestCollection = protocolContainer.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "RequestCollection")
            ?? throw new InvalidDataException("ExecuteCellStorageRequest is missing RequestCollection.");

        request.UsesDirectBody = execute is null;

        if (Guid.TryParse((string?)requestCollection.Attribute("CorrelationId"), out var correlationId))
        {
            request.CorrelationId = correlationId;
        }

        foreach (var requestElement in requestCollection.Elements().Where(e => e.Name.LocalName == "Request"))
        {
            var fileRequest = new FssHttpRequest
            {
                Url = (string?)requestElement.Attribute("Url") ?? string.Empty,
                UseResourceId = string.Equals((string?)requestElement.Attribute("UseResourceID"), "true", StringComparison.OrdinalIgnoreCase),
                ResourceId = (string?)requestElement.Attribute("ResourceID"),
                RequestToken = (ulong?)requestElement.Attribute("RequestToken") ?? 0,
                UserAgent = Guid.TryParse((string?)requestElement.Attribute("UserAgent"), out var ua) ? ua : null,
            };

            foreach (var subRequestElement in requestElement.Elements().Where(e => e.Name.LocalName == "SubRequest"))
            {
                var subRequest = new FssHttpSubRequest
                {
                    Type = ParseSubRequestType((string?)subRequestElement.Attribute("Type")),
                    SubRequestToken = (ulong?)subRequestElement.Attribute("SubRequestToken"),
                    DependsOn = (ulong?)subRequestElement.Attribute("DependsOn"),
                    DependencyType = (string?)subRequestElement.Attribute("DependencyType"),
                };

                var subRequestData = subRequestElement.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "SubRequestData");
                if (subRequestData is not null)
                {
                    foreach (var attribute in subRequestData.Attributes())
                    {
                        subRequest.SubRequestDataAttributes[attribute.Name.LocalName] = attribute.Value;
                    }

                    // Capture any Include href (XOP MTOM reference) or inline content.
                    var include = subRequestData.Element(XopIncludeNs + "Include");
                    if (include is not null)
                    {
                        subRequest.SubRequestDataAttributes["IncludeHref"] =
                            (string?)include.Attribute("href") ?? string.Empty;
                    }

                    subRequest.SubRequestDataXml = subRequestData.ToString(SaveOptions.DisableFormatting);
                }

                fileRequest.SubRequests.Add(subRequest);
            }

            request.Requests.Add(fileRequest);
        }

        return request;
    }

    private static SubRequestType ParseSubRequestType(string? value) => value?.Trim() switch
    {
        "Cell" => SubRequestType.Cell,
        "Coauth" => SubRequestType.Coauth,
        "SchemaLock" => SubRequestType.SchemaLock,
        "WhoAmI" => SubRequestType.WhoAmI,
        "ServerTime" => SubRequestType.ServerTime,
        "ExclusiveLock" => SubRequestType.ExclusiveLock,
        "EditorsTable" => SubRequestType.EditorsTable,
        "GetDocMetaInfo" => SubRequestType.GetDocMetaInfo,
        "GetVersions" => SubRequestType.GetVersions,
        "FileOperation" => SubRequestType.FileOperation,
        "Versioning" => SubRequestType.Versioning,
        "AmIAlone" => SubRequestType.AmIAlone,
        "LockStatus" => SubRequestType.LockStatus,
        "Properties" => SubRequestType.Properties,
        _ => throw new InvalidDataException($"Unknown SubRequest Type '{value}'."),
    };
}
