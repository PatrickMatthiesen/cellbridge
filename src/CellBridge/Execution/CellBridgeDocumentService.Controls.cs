using CellBridge.FssHttp;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public sealed partial class CellBridgeDocumentService
{
    internal async ValueTask<ControlResult> ExecuteControlAsync(Guid resourceId, FssHttpSubRequest request,
        CellBridgeActor actor, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var current = await provider.Documents.ReadControlAsync(resourceId, cancellationToken) ?? throw MissingDocument();
            actor = await AuthorizeActorAsync(actor, current.Head, CellBridgeAuthorization.RequiredAccess(request), cancellationToken);
            if (!Access(actor, current.Head).HasFlag(CellBridgeAuthorization.RequiredAccess(request)))
                return new(current, false) { ErrorCode = "FileUnauthorizedAccess" };
            var admission = Admission(actor, current.Head);
            var result = await provider.Documents.ExecuteControlAsync(
                new DocumentControlCommand.Office(resourceId, admission, request), cancellationToken);
            if (!result.Changed && result.Current.Head.StateVersion != admission.StateVersion) continue;
            return result;
        }
        var latest = await provider.Documents.ReadControlAsync(resourceId, cancellationToken) ?? throw MissingDocument();
        return new(latest, false) { ErrorCode = "SubRequestFail" };
    }
}
