using System.Net;
using System.Text.Json;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.AspNetCore;

/// <summary>Maps the captured, read-only SharePoint REST projection of CSOM GetSharingInformation.</summary>
public static class NativeSharingEndpoint
{
    private const string Route = "/_api/web/GetFileByUrl(@v)/ListItemAllFields/GetSharingInformation";
    private const int MaximumEmptyBodyProbeBytes = 1;
    private enum SharingRole { None, View, Edit, Owner }

    /// <summary>
    /// Maps the native Office Share information read. Share mutations, email invitations and sharing links
    /// remain unavailable until their protocol adapters and backing services are implemented.
    /// </summary>
    public static RouteGroupBuilder MapNativeSharing(this RouteGroupBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost(Route, HandleGetSharingInformationAsync);
        return routes;
    }

    private static async Task<IResult> HandleGetSharingInformationAsync(HttpContext context,
        CellBridgeDocumentService service)
    {
        var actor = CellBridgeActor.FromPrincipal(context.User);
        if (actor is null) return Results.Unauthorized();

        var admission = context.RequestServices.GetRequiredService<ProtocolRequestAdmission>();
        var originalToken = context.RequestAborted;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(originalToken);
        deadline.CancelAfter(context.RequestServices.GetRequiredService<CellBridgeOptions>().RequestTimeout);
        ProtocolRequestAdmission.AdmissionResult admitted;
        try { admitted = await admission.AcquireAsync(actor.Identity.Subject, deadline.Token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !originalToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status408RequestTimeout);
        }
        using var lease = admitted.Lease;
        if (lease is null)
        {
            context.Response.Headers.RetryAfter = "1";
            return Results.StatusCode(admitted.RejectionStatus);
        }

        context.RequestAborted = deadline.Token;
        context.Response.Headers.CacheControl = "private, no-store";
        if (!SameOriginIfSupplied(context)) return Results.BadRequest();
        if (!await HasEmptyBodyAsync(context.Request, context.RequestAborted)) return Results.BadRequest();
        if (!TryGetDocumentPath(context, out var documentPath)) return Results.BadRequest();

        DocumentHead? metadata;
        try
        {
            metadata = await service.FindDocumentMetadataByPathKeyAsync(StorageIds.PathKey(documentPath), actor,
                context.RequestAborted);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.NotFound();
        }
        if (metadata is null) return Results.NotFound();

        DocumentSharingSnapshot? sharing;
        try
        {
            sharing = await service.GetSharingAsync(metadata.ResourceId, metadata.LifecycleGeneration, actor,
                context.RequestAborted);
        }
        catch (UnauthorizedAccessException)
        {
            // Do not reveal whether a reader's document has grants or an owner-managed sharing state.
            return Results.NotFound();
        }
        catch (NotSupportedException)
        {
            return Results.StatusCode(StatusCodes.Status501NotImplemented);
        }
        if (sharing is null) return Results.NotFound();

        var recipients = await ResolveRecipientsAsync(context.RequestServices, sharing, actor.Identity,
            context.RequestAborted);
        if (recipients is null) return Results.StatusCode(StatusCodes.Status501NotImplemented);

        var publicOrigin = CellBridgePublicOrigin.Resolve(context);
        if (publicOrigin is null || !Uri.TryCreate(publicOrigin, UriKind.Absolute, out var origin))
            return Results.BadRequest();
        var directUrl = BuildPublicDocumentUrl(origin, context.Request.PathBase.Value ?? "", documentPath);

        // MS-CSOMSPT 3.2.5.439.2.2.3 defines GetSharingInformation(ListItem, SharingInformationRequest)
        // -> SharingInformation (https://learn.microsoft.com/openspecs/sharepoint_protocols/ms-csomspt/e8604c72-262b-490f-933f-ece357258d63).
        // The captured Office REST request asks for permissionsInformation and pickerSettings and accepts
        // application/json;odata=verbose. The OData wrapper/field casing follows that captured REST profile;
        // the protocol spec does not define the REST JSON projection. Report only capabilities implemented here.
        var result = new
        {
            d = new
            {
                __metadata = new { type = "SP.Sharing.SharingInformation" },
                canAddExternalPrincipal = false,
                canAddInternalPrincipal = false,
                canSendEmail = false,
                canUseSimplifiedRoles = true,
                hasUniquePermissions = true,
                currentRole = 3, // SharingRole.Owner.
                requiresAccessApproval = false,
                hasPendingAccessRequests = false,
                sharedObjectType = 1, // SPSharedObjectType.File.
                directUrl,
                webUrl = origin.GetLeftPart(UriPartial.Authority) + (context.Request.PathBase.Value ?? ""),
                permissionsInformation = new
                {
                    hasInheritedLinks = false,
                    links = new { results = Array.Empty<object>() },
                    principals = new { results = recipients },
                },
                pickerSettings = new { },
            },
        };

        return Results.Json(result, contentType: "application/json;odata=verbose");
    }

    private static async ValueTask<object[]?> ResolveRecipientsAsync(IServiceProvider services,
        DocumentSharingSnapshot sharing, SubjectIdentity owner, CancellationToken cancellationToken)
    {
        var principals = new List<object>();
        var resolver = services.GetService<ICellBridgeSharingRecipientResolver>();
        foreach (var grant in sharing.Grants.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (grant.Key == sharing.OwnerSubject) continue;
            if (resolver is null) return null;
            var recipient = await resolver.ResolveAsync(grant.Key, cancellationToken);
            if (recipient is null || !StringComparer.Ordinal.Equals(recipient.Subject, grant.Key)) return null;
            principals.Add(Principal(recipient, (int)RoleFor(grant.Value)));
        }

        // The authenticated owner identity is already trusted by the host and does not need directory lookup.
        // A `null` numeric SharePoint ID is honest: CellBridge has no SharePoint user-info-list IDs.
        principals.Insert(0, Principal(new(owner.Subject, owner.Login, owner.DisplayName, owner.EmailAddress,
            IsActive: true), (int)SharingRole.Owner));
        return principals.ToArray();
    }

    private static object Principal(CellBridgeSharingRecipient recipient, int role)
    {
        var info = new Dictionary<string, object?>
        {
            ["directoryObjectId"] = null,
            ["email"] = recipient.EmailAddress ?? "",
            ["expiration"] = null,
            ["id"] = recipient.PrincipalId,
            ["jobTitle"] = null,
            ["loginName"] = recipient.Login,
            ["name"] = recipient.DisplayName,
            ["principalType"] = 1,
            ["userId"] = null,
            ["userPrincipalName"] = recipient.EmailAddress,
        };
        if (recipient.IsActive is { } active) info["isActive"] = active;
        if (recipient.IsExternal is { } external) info["isExternal"] = external;
        return new { principal = info, role };
    }

    private static SharingRole RoleFor(DocumentAccess access) => access switch
    {
        DocumentAccess.Read => SharingRole.View,
        DocumentAccess.Read | DocumentAccess.Write => SharingRole.Edit,
        _ => SharingRole.None,
    };

    private static bool TryGetDocumentPath(HttpContext context, out string path)
    {
        path = "";
        var aliases = context.Request.Query["@v"];
        if (aliases.Count != 1) return false;
        var alias = aliases[0];
        if (string.IsNullOrWhiteSpace(alias) || alias.Length < 3 || alias[0] != '\'' || alias[^1] != '\'')
            return false;
        var value = alias[1..^1];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var documentUrl) ||
            documentUrl.Scheme is not ("http" or "https") || documentUrl.UserInfo.Length != 0 ||
            documentUrl.Query.Length != 0 || documentUrl.Fragment.Length != 0 ||
            !Uri.TryCreate(CellBridgePublicOrigin.Resolve(context), UriKind.Absolute, out var expectedOrigin) ||
            !SameOrigin(documentUrl, expectedOrigin) ||
            !DocumentUrlPaths.TryDecode(value, out path) ||
            !DocumentUrlPaths.StripMount(ref path, context.Request.PathBase.Value ?? ""))
            return false;

        return path.StartsWith("/shared/", StringComparison.OrdinalIgnoreCase) &&
               path.Length > "/shared/".Length;
    }

    private static bool SameOriginIfSupplied(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("Origin", out var values)) return true;
        if (values.Count != 1 || !Uri.TryCreate(values[0], UriKind.Absolute, out var supplied) ||
            !Uri.TryCreate(CellBridgePublicOrigin.Resolve(context), UriKind.Absolute, out var expected)) return false;
        return SameOrigin(supplied, expected);
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;

    private static string BuildPublicDocumentUrl(Uri origin, string pathBase, string documentPath) =>
        new UriBuilder(origin) { Path = pathBase + documentPath }.Uri.AbsoluteUri;

    private static async ValueTask<bool> HasEmptyBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is > 0) return false;
        var probe = new byte[MaximumEmptyBodyProbeBytes];
        return await request.Body.ReadAsync(probe, cancellationToken) == 0;
    }
}
