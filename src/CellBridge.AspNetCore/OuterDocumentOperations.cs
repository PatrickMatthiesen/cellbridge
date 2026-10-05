using System.Globalization;
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
        CancellationToken cancellationToken)
    {
        var attributes = request.SubRequestDataAttributes;
        try
        {
            if (request.Type == SubRequestType.Versioning && attributes.GetValueOrDefault("VersioningRequestType") == "RestoreVersion")
            {
                if (!TryVersion(attributes.GetValueOrDefault("Version"), out var number, out var minor))
                { response.ErrorCode = "InvalidArgument"; return; }
                if (minor != 0) { response.ErrorCode = "VersionNotFound"; return; }
                var current = await service.Provider.State.FindByResourceIdAsync(resourceId, cancellationToken)
                    ?? throw new KeyNotFoundException();
                // SOAP tokens correlate messages; they are not durable retry identities.
                await service.RestoreRevisionAsync(resourceId, number, RevisionHistory.Latest(current),
                    Guid.NewGuid().ToString("N"), actor, attributes, cancellationToken);
                return;
            }
            if (request.Type == SubRequestType.FileOperation)
            {
                if (attributes.GetValueOrDefault("FileOperation") != "Rename")
                { response.ErrorCode = attributes.ContainsKey("FileOperation") ? "NotSupported" : "InvalidArgument"; return; }
                await service.RenameDocumentAsync(resourceId, attributes.GetValueOrDefault("NewFileName") ?? "", actor, attributes, cancellationToken);
                return;
            }
            await service.Provider.State.TransitionAsync(resourceId, (state, _) =>
            {
                if (!service.Access(actor, state).HasFlag(DocumentAccess.Read)) throw new UnauthorizedAccessException();
                switch (request.Type)
                {
                    case SubRequestType.GetVersions:
                        response.SubResponseXml = MetadataVersioningResponseBuilder.BuildGetVersions(state, origin).SubResponseXml;
                        break;
                    case SubRequestType.Versioning:
                        if (attributes.GetValueOrDefault("VersioningRequestType") != "GetVersionList")
                        { response.ErrorCode = attributes.ContainsKey("VersioningRequestType") ? "NotSupported" : "InvalidArgument"; break; }
                        var revisions = RevisionHistory.Initialize(state).Revisions.Reverse().ToArray();
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
                return new StateTransition<bool>(null, true);
            }, cancellationToken);
        }
        catch (UnauthorizedAccessException) { response.ErrorCode = "FileUnauthorizedAccess"; response.HResult = "2147942405"; }
        catch (KeyNotFoundException) { response.ErrorCode = request.Type == SubRequestType.Versioning ? "VersionNotFound" : "FileNotExistsOrCannotBeCreated"; }
        catch (ArgumentException) { response.ErrorCode = "InvalidArgument"; }
        catch (NotSupportedException) { response.ErrorCode = "NotSupported"; }
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

    private static void Properties(DocumentState state, FssHttpSubRequest request, FssHttpSubResponse response)
    {
        var props = XElement.Parse(MetadataVersioningResponseBuilder.BuildGetDocMetaInfo(
            StoredDocument.RestoreMetadata(state, DateTime.UtcNow)).SubResponseDataXml!).Elements()
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
                var container = input.Elements().SingleOrDefault(e => e.Name.LocalName == "PropertyIds");
                if (container is null) { response.ErrorCode = "InvalidArgument"; return; }
                var ids = container.Elements().ToArray();
                if (ids.Length > 256 || ids.Any(e => e.Name.LocalName != "PropertyId" || e.Attribute("id") is null))
                { response.ErrorCode = "InvalidArgument"; return; }
                result = new("PropertyValues", ids.Select(e => (string)e.Attribute("id")!).Where(props.ContainsKey)
                    .Select(id => new XElement("PropertyValue", new XAttribute("id", id), new XAttribute("value", props[id]))));
                break;
            default:
                response.ErrorCode = request.SubRequestDataAttributes.ContainsKey("Properties") ? "NotSupported" : "InvalidArgument"; return;
        }
        response.SubResponseDataXml = result.ToString(SaveOptions.DisableFormatting);
    }
}

public sealed partial class CellBridgeDocumentService
{
    public async ValueTask<DocumentState> RenameDocumentAsync(Guid resourceId, string newFileName, CellBridgeActor actor,
        IReadOnlyDictionary<string, string>? lockAttributes = null, CancellationToken cancellationToken = default)
    {
        if (newFileName.Length == 0 || newFileName.Length > 255 || newFileName is "." or ".." ||
            newFileName.IndexOfAny(['/', '\\', ':', '?', '*', '"', '<', '>', '|']) >= 0 || newFileName.Any(char.IsControl))
            throw new ArgumentException("A valid filename without a relative path is required.", nameof(newFileName));
        if (provider.State is not IAtomicDocumentRenameStore store) throw new NotSupportedException("This provider does not support atomic rename.");
        return await store.RenameAsync(resourceId, (current, now) =>
        {
            Demand(actor, current, DocumentAccess.Write);
            var document = StoredDocument.RestoreMetadata(current, now);
            var coordinator = FssHttpLockCoordinator.Restore(document, current.Coordination, now, actor.Identity);
            if (!coordinator.ExecuteCellWrite(lockAttributes ?? new Dictionary<string, string>(), () => true, out _, out var error, now))
                throw new DocumentOperationLockException(error ?? "FileLockConflict");
            var path = current.Path[..(current.Path.LastIndexOf('/') + 1)] + newFileName;
            if (path == current.Path) return new StateTransition<DocumentState>(null, current);
            var next = current with { Path = path, PathKey = StorageIds.PathKey(path),
                StateVersion = checked(current.StateVersion + 1) };
            return new StateTransition<DocumentState>(next, next);
        }, cancellationToken);
    }
}
