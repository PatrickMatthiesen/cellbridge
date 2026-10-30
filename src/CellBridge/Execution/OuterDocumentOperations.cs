using System.Globalization;
using System.Collections.Immutable;
using System.Xml.Linq;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

internal static class OuterDocumentOperations
{
    public static bool Handles(SubRequestType type) => type is SubRequestType.GetVersions or
        SubRequestType.Versioning or SubRequestType.FileOperation or SubRequestType.Properties;

    public static async Task ExecuteAsync(CellBridgeDocumentService service, Guid resourceId,
        FssHttpSubRequest request, FssHttpSubResponse response, string origin, CellBridgeActor actor,
        CancellationToken cancellationToken, OfficeDocumentLibrary? library = null)
    {
        var attributes = request.SubRequestDataAttributes;
        try
        {
            if (request.Type == SubRequestType.Versioning && attributes.GetValueOrDefault("VersioningRequestType") == "RestoreVersion")
            {
                if (!TryVersion(attributes.GetValueOrDefault("Version"), out var number, out var minor))
                { response.ErrorCode = "InvalidArgument"; return; }
                if (minor != 0) { response.ErrorCode = "VersionNotFound"; return; }
                var current = await service.Provider.Documents.FindAsync(new DocumentIdentity.Resource(resourceId), cancellationToken)
                    ?? throw new KeyNotFoundException();
                // SOAP tokens correlate messages; they are not durable retry identities.
                await service.RestoreRevisionAsync(resourceId, number, current.LatestRevisionNumber,
                    Guid.NewGuid().ToString("N"), actor, attributes, cancellationToken);
                return;
            }
            if (request.Type == SubRequestType.FileOperation)
            {
                if (attributes.GetValueOrDefault("FileOperation") != "Rename")
                { response.ErrorCode = attributes.ContainsKey("FileOperation") ? "RequestNotSupported" : "InvalidArgument"; return; }
                await service.RenameDocumentAsync(resourceId, attributes.GetValueOrDefault("NewFileName") ?? "", actor, attributes, cancellationToken);
                return;
            }
            var state = await service.GetDocumentMetadataAsync(resourceId, actor, cancellationToken);
                switch (request.Type)
                {
                    case SubRequestType.GetVersions:
                        if (library is null) { response.ErrorCode = "RequestNotSupported"; break; }
                        response.SubResponseXml = MetadataVersioningResponseBuilder.BuildGetVersions(await service.ListRevisionsAsync(resourceId, actor, cancellationToken), origin, library).SubResponseXml;
                        break;
                    case SubRequestType.Versioning:
                        if (attributes.GetValueOrDefault("VersioningRequestType") != "GetVersionList")
                        { response.ErrorCode = attributes.ContainsKey("VersioningRequestType") ? "RequestNotSupported" : "InvalidArgument"; break; }
                        var revisions = (await service.ListRevisionsAsync(resourceId, actor, cancellationToken)).ToArray();
                        var users = revisions.Select(r => r.Author).DistinctBy(u => u.Subject).ToArray();
                        var xml = new XElement("SubResponseData",
                            new XElement("UserTable", users.Select((u, index) => new XElement("User",
                                new XAttribute("UserId", index + 1), new XAttribute("UserLogin", u.Login), new XAttribute("UserName", u.DisplayName)))),
                            new XElement("Versions", revisions.Select((r, index) => new XElement("Version",
                                index == 0 ? new XAttribute("IsCurrent", "true") : null,
                                new XAttribute("Number", Version(r.RevisionNumber)),
                                new XAttribute("LastModifiedTime", r.CreatedUtc.ToFileTimeUtc()),
                                new XAttribute("UserId", Array.FindIndex(users, u => u.Subject == r.Author.Subject) + 1)))));
                        response.SubResponseDataXml = string.Concat(xml.Elements().Select(e => e.ToString(SaveOptions.DisableFormatting)));
                        break;
                    case SubRequestType.Properties:
                        Properties(state, request, response);
                        break;
                }

        }
        catch (UnauthorizedAccessException) { response.ErrorCode = "FileUnauthorizedAccess"; response.HResult = "2147942405"; }
        catch (KeyNotFoundException) { response.ErrorCode = request.Type == SubRequestType.Versioning ? "VersionNotFound" : "FileNotExistsOrCannotBeCreated"; }
        catch (ArgumentException) { response.ErrorCode = "InvalidArgument"; }
        catch (NotSupportedException) { response.ErrorCode = "RequestNotSupported"; }
        catch (DocumentOperationLockException ex) { response.ErrorCode = ex.ErrorCode; }
        catch (InvalidOperationException) { response.ErrorCode = "SubRequestFail"; }
    }

    public static string Version(ulong number) => number.ToString(CultureInfo.InvariantCulture) + ".0";
    public static string HistoricalUrl(Guid resourceId, long generation, ulong number, string origin) =>
        origin.TrimEnd('/') + "/_cellbridge/history/" + resourceId.ToString("D") + "/" +
        generation.ToString(CultureInfo.InvariantCulture) + "/" + number.ToString(CultureInfo.InvariantCulture);

    private static bool TryVersion(string? value, out ulong number, out ulong minor)
    {
        number = minor = 0;
        var parts = value?.Split('.');
        if (parts is not { Length: 2 } || parts.Any(p => p.Length == 0 || p.Any(c => c is < '0' or > '9'))) return false;
        _ = ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out number);
        minor = parts[1].Any(c => c != '0') ? 1UL : 0UL;
        return true;
    }

    private static void Properties(DocumentHead state, FssHttpSubRequest request, FssHttpSubResponse response)
    {
        var props = XElement.Parse(MetadataVersioningResponseBuilder.BuildGetDocMetaInfo(state).SubResponseDataXml!).Elements()
            .ToDictionary(e => (string)e.Attribute("Key")!, e => (string)e.Attribute("Value")!, StringComparer.Ordinal);
        XElement result;
        switch (request.SubRequestDataAttributes.GetValueOrDefault("Properties"))
        {
            case "PropertyEnumerate":
                result = new("PropertyIds", props.Keys.Select(id => new XElement("PropertyId", new XAttribute("id", id))));
                break;
            case "PropertyGet":
                if (request.SubRequestDataXml is null) { response.ErrorCode = "InvalidArgument"; return; }
                XElement input;
                try { input = XElement.Parse(request.SubRequestDataXml); }
                catch (System.Xml.XmlException) { response.ErrorCode = "InvalidArgument"; return; }
                var containers = input.Elements().Where(e => e.Name.LocalName == "PropertyIds").ToArray();
                if (containers.Length != 1) { response.ErrorCode = "InvalidArgument"; return; }
                var container = containers[0];
                var ids = container.Elements().ToArray();
                if (ids.Length > 256 || ids.Any(e => e.Name.LocalName != "PropertyId" || e.Attribute("id") is null))
                { response.ErrorCode = "InvalidArgument"; return; }
                result = new("PropertyValues", ids.Select(e => (string)e.Attribute("id")!).Where(props.ContainsKey)
                    .Select(id => new XElement("PropertyValue", new XAttribute("id", id), new XAttribute("value", props[id]))));
                break;
            default:
                response.ErrorCode = request.SubRequestDataAttributes.ContainsKey("Properties") ? "RequestNotSupported" : "InvalidArgument"; return;
        }
        response.SubResponseDataXml = result.ToString(SaveOptions.DisableFormatting);
    }
}

public sealed partial class CellBridgeDocumentService
{
    public async ValueTask<DocumentHead> RenameDocumentAsync(Guid resourceId, string newFileName, CellBridgeActor actor,
        IReadOnlyDictionary<string, string>? lockAttributes = null, CancellationToken cancellationToken = default)
    {
        if (!ValidPathSegment(newFileName) || ReservedPathSegment(newFileName))
            throw new ArgumentException("A valid filename without a relative path is required.", nameof(newFileName));
        return await ChangePathAsync(resourceId,
            current => current.Path[..(current.Path.LastIndexOf('/') + 1)] + newFileName,
            actor, lockAttributes, cancellationToken);
    }

    /// <summary>Atomically moves a resource to a decoded application-rooted path without changing its identity or content.</summary>
    /// <remarks>The input is not URL-decoded. A retry to the same exact path checks permissions and locks but does not change state.</remarks>
    public ValueTask<DocumentHead> MoveDocumentAsync(Guid resourceId, string newPath, CellBridgeActor actor,
        IReadOnlyDictionary<string, string>? lockAttributes = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(newPath) || newPath[0] != '/' || newPath == "/" ||
            newPath.Split('/').Skip(1).Any(segment => !ValidPathSegment(segment) || ReservedPathSegment(segment)))
            throw new ArgumentException("An application-rooted document path is required.", nameof(newPath));
        return ChangePathAsync(resourceId, _ => newPath, actor, lockAttributes, cancellationToken);
    }

    private static bool ReservedPathSegment(string segment) =>
        segment.Equals("_vti_bin", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("_vti_inf.html", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("_cellbridge", StringComparison.OrdinalIgnoreCase);

    private static bool ValidPathSegment(string segment) =>
        segment.Length is > 0 and <= 255 && segment is not ("." or "..") &&
        segment.IndexOfAny(['/', '\\', ':', '?', '*', '"', '<', '>', '|']) < 0 &&
        !segment.Any(char.IsControl);

    private async ValueTask<DocumentHead> ChangePathAsync(Guid resourceId, Func<DocumentHead, string> pathFor,
        CellBridgeActor actor, IReadOnlyDictionary<string, string>? lockAttributes, CancellationToken cancellationToken)
    {
        var current = await CurrentAsync(resourceId, cancellationToken);
        actor = await AuthorizeActorAsync(actor, current, DocumentAccess.Write, cancellationToken);
        Demand(actor, current, DocumentAccess.Write);
        var path = pathFor(current);
        var result = await provider.Documents.ChangePathAsync(new(resourceId, Admission(actor, current), path,
            StorageIds.PathKey(path), (lockAttributes ?? ImmutableDictionary<string, string>.Empty).ToImmutableDictionary(StringComparer.Ordinal)), cancellationToken);
        if (result.ErrorCode is { } error) throw new DocumentOperationLockException(error);
        return result.Current;
    }
}
