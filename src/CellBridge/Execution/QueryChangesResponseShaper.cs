using CellBridge.FssHttpB;

namespace CellBridge.AspNetCore;

/// <summary>
/// Applies the request-side QueryChanges controls that the current response
/// builders can represent without inventing a new manifest graph.
/// </summary>
internal static class QueryChangesResponseShaper
{
    public static void Apply(
        FsshttpbResponse response,
        QueryChangesSubRequestData? request)
    {
        if (request is null || response.SubResponses.Count == 0)
        {
            return;
        }

        var subResponse = response.SubResponses.FirstOrDefault(
            item => item.RequestType == RequestTypes.QueryChanges);
        if (subResponse?.Data is not QueryChangesSubResponseData)
        {
            return;
        }

        var package = response.DataElementPackage;
        if (package is null)
        {
            return;
        }

        if (!request.IncludeStorageManifest)
        {
            package.DataElements.RemoveAll(element =>
                element.DataElementType == DataElementType.StorageManifestDataElementData);
        }

        if (!request.IncludeCellChanges)
        {
            package.DataElements.RemoveAll(element =>
                element.DataElementType == DataElementType.CellManifestDataElementData);
        }

        // FileQueryResponseBuilder selected the complete-element page and its
        // knowledge before loading payloads. The last element may cross the
        // client's byte threshold. The assembler checks the admitted page and
        // the independent aggregate server limit; do not truncate this package.
    }

}
