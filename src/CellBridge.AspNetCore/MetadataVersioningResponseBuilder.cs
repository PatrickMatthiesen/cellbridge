using System.Globalization;
using System.Xml.Linq;
using CellBridge.FssHttp;
using CellBridge.Storage;

namespace CellBridge.AspNetCore;

/// <summary>
/// Builds the two metadata responses that Word requests when the server
/// advertises FSSHTTP 1.1 or later.
///
/// The store deliberately has no SharePoint list identity or version-history
/// repository. These responses therefore expose only values derived from the
/// current in-memory document. In particular, GetVersions returns the current
/// version and does not invent historical versions or a list/settings URL.
/// </summary>
public static class MetadataVersioningResponseBuilder
{

    // These are the values observed repeatedly in the SharePoint capture for
    // Document.docx (20260925T084814Z-session-f088f59e). They describe the
    // FSSHTTP document stream and item level; they are not generated version
    // history or a SharePoint list identity.
    private const string CapturedStreamSchema = "66";
    private const string CapturedItemLevel = "1";

    /// <summary>
    /// Creates the XML DocProps payload used by GetDocMetaInfo.
    /// [MS-FSSHTTP] places DocProps inside SubResponseData.
    /// </summary>
    public static FssHttpSubResponse BuildGetDocMetaInfo(
        StoredDocument document,
        string? userName = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        string user = document.Security.ModifiedBy?.Login ?? userName ?? "unknown";
        var properties = new XElement("DocProps",
            Property("vti_filesize", document.ContentLength.ToString(CultureInfo.InvariantCulture)),
            Property("vti_contentversion", document.ContentVersion.ToString(CultureInfo.InvariantCulture)),
            Property("vti_docstoreversion", document.ContentVersion.ToString(CultureInfo.InvariantCulture)),
            Property("vti_modifiedby", user),
            Property("vti_author", document.Security.CreatedBy?.Login ?? userName ?? "unknown"),
            Property("vti_timecreated", FormatProtocolDate(document.CreatedUtc)),
            Property("vti_contentversionisdirty", "false"),
            Property("vti_level", CapturedItemLevel),
            Property("vti_streamschema", CapturedStreamSchema));

        return new FssHttpSubResponse
        {
            Type = SubRequestType.GetDocMetaInfo,
            ErrorCode = "Success",
            HResult = "0",
            SubResponseDataXml = properties.ToString(SaveOptions.DisableFormatting),
        };
    }

    /// <summary>
    /// Creates the nested GetVersions result used by SharePoint's SOAP
    /// response. The supplied URL is the public origin advertised to Word;
    /// the document path is taken from the store key.
    /// </summary>
    public static FssHttpSubResponse BuildGetVersions(
        StoredDocument document,
        string publicOrigin,
        string? userName = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicOrigin);

        string user = document.Security.ModifiedBy?.Login ?? userName ?? "unknown";
        string documentUrl = DocumentRequestResolver.CanonicalUrl(document, publicOrigin);
        string version = $"@{Math.Max(1, document.ContentVersion)}.0";
        string createdRaw = FormatProtocolDate(document.LastModifiedUtc);
        string created = document.LastModifiedUtc.ToString("M/d/yyyy h:mm tt", CultureInfo.InvariantCulture);

        var nested = new XElement("GetVersionsResponse",
            new XElement("GetVersionsResult",
                new XElement("results",
                    // The in-memory store keeps only the current document and
                    // has no historical-version repository. Reporting enabled
                    // here would claim history that the server cannot serve.
                    new XElement("versioning", new XAttribute("enabled", "0")),
                    new XElement("result",
                        new XAttribute("version", version),
                        new XAttribute("url", documentUrl),
                        new XAttribute("created", created),
                        new XAttribute("createdRaw", createdRaw),
                        new XAttribute("createdBy", user),
                        new XAttribute("createdByName", document.Security.ModifiedBy?.DisplayName ?? userName ?? "unknown"),
                        new XAttribute("size", document.ContentLength.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("comments", string.Empty)))));

        return new FssHttpSubResponse
        {
            Type = SubRequestType.GetVersions,
            ErrorCode = "Success",
            HResult = "0",
            SubResponseXml = nested.ToString(SaveOptions.DisableFormatting),
        };
    }

    private static XElement Property(string key, string value) =>
        new("Property", new XAttribute("Key", key), new XAttribute("Value", value));

    private static string FormatProtocolDate(DateTime value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

}
