using System.Security.Claims;
using CellBridge.Storage.Abstractions;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;

namespace CellBridge.AspNetCore;

public sealed record CellBridgeActor(SubjectIdentity Identity, bool CanCreate = false)
{
    // Only the engine can attach an admission. Public permission ceilings cannot create one.
    internal DocumentOperationAuthorization? Authorization { get; init; }
    /// <summary>Optional trusted-host ceiling for this request, restricted to one resource.</summary>
    public DocumentAccessLimit? AccessLimit { get; init; }
    public const string SubjectClaim = "cellbridge:subject";
    public const string CreateClaim = "cellbridge:create";
    public const string DisplayNameClaim = "cellbridge:display-name";
    public const string SipAddressClaim = "cellbridge:sip-address";
    public static CellBridgeActor? FromPrincipal(ClaimsPrincipal principal)
    {
        var identity = principal.Identities.FirstOrDefault(i => i.IsAuthenticated && i.HasClaim(c => c.Type == SubjectClaim));
        var subject = identity?.FindFirst(SubjectClaim)?.Value;
        if (string.IsNullOrWhiteSpace(subject)) return null;
        var login = identity!.Name ?? subject;
        var subjectIdentity = new SubjectIdentity(subject, login, identity.FindFirst(DisplayNameClaim)?.Value ?? login)
        {
            EmailAddress = identity.FindFirst(ClaimTypes.Email)?.Value,
            SipAddress = identity.FindFirst(SipAddressClaim)?.Value,
        };
        return new(subjectIdentity,
            identity.HasClaim(CreateClaim, "true"));
    }
}

/// <summary>Immutable request permission ceiling. It restricts, rather than replaces, the access evaluator.</summary>
public sealed class DocumentAccessLimit
{
    public Guid ResourceId { get; }
    public DocumentAccess Access { get; }

    public DocumentAccessLimit(Guid resourceId, DocumentAccess access)
    {
        if (resourceId == Guid.Empty) throw new ArgumentException("A resource identifier is required.", nameof(resourceId));
        if ((access & ~(DocumentAccess.Read | DocumentAccess.Write)) != 0)
            throw new ArgumentOutOfRangeException(nameof(access));
        ResourceId = resourceId;
        Access = access.HasFlag(DocumentAccess.Write) ? access | DocumentAccess.Read : access;
    }
}

public interface ICellBridgeAccessEvaluator
{
    DocumentAccess Evaluate(CellBridgeActor actor, DocumentAuthorizationView state);
}

public sealed class StoredDocumentAccessEvaluator : ICellBridgeAccessEvaluator
{
    public DocumentAccess Evaluate(CellBridgeActor actor, DocumentAuthorizationView state) => state.Security.AccessFor(actor.Identity.Subject);
}

public static class CellBridgeAuthorization
{
    public const ulong AccessDeniedHResult = 0x80070005;
    public static ResponseError AccessError() => new(ErrorType.HResult, AccessDeniedHResult, "Document access denied.");
    public static FsshttpbSubResponse Denied(FsshttpbCellSubRequest operation) => new()
    { RequestId = operation.RequestId, RequestType = operation.RequestType, Status = true, Error = AccessError() };

    public static DocumentAccess RequiredAccess(FssHttpSubRequest request)
    {
        var a = request.SubRequestDataAttributes;
        return request.Type switch
        {
            SubRequestType.WhoAmI or SubRequestType.ServerTime or SubRequestType.GetDocMetaInfo or SubRequestType.GetVersions
                or SubRequestType.LockStatus or SubRequestType.AmIAlone or SubRequestType.Properties => DocumentAccess.Read,
            SubRequestType.Versioning => a.GetValueOrDefault("VersioningRequestType") == "GetVersionList" ? DocumentAccess.Read : DocumentAccess.Write,
            SubRequestType.EditorsTable => a.GetValueOrDefault("EditorsTableRequestType") switch
            {
                "LeaveEditingSession" or "UpdateEditorMetadata" or "RemoveEditorMetadata" => DocumentAccess.Read,
                "JoinEditingSession" or "RefreshEditingSession" =>
                    CellSubRequestDataValidation.TryGetBoolean(a, "AsEditor", out bool asEditor) && !asEditor
                    ? DocumentAccess.Read : DocumentAccess.Write,
                _ => DocumentAccess.Write,
            },
            SubRequestType.Coauth => a.GetValueOrDefault("CoauthRequestType") is "ExitCoauthoring" or "GetCoauthoringStatus" or "CheckLockAvailability"
                ? DocumentAccess.Read : DocumentAccess.Write,
            SubRequestType.SchemaLock => a.GetValueOrDefault("SchemaLockRequestType") is "ReleaseLock" or "CheckLockAvailability"
                ? DocumentAccess.Read : DocumentAccess.Write,
            SubRequestType.ExclusiveLock => a.GetValueOrDefault("ExclusiveLockRequestType") is "ReleaseLock" or "CheckLockAvailability"
                ? DocumentAccess.Read : DocumentAccess.Write,
            _ => DocumentAccess.Write,
        };
    }
}
