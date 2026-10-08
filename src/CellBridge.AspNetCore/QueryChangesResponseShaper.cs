using CellBridge.FssHttpB;
using CellBridge.Storage;

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

        // The protocol calls this field MaxDataElements, but its value is a
        // byte budget for the serialized data elements, not an element count.
        // A partial graph needs corresponding knowledge and continuation
        // state. We do not have that state yet, so fail explicitly instead of
        // returning a truncated graph while advertising complete knowledge.
        if (request.MaxDataElements is { } maximum
            && maximum > 0)
        {
            ulong used = 0;
            foreach (var element in package.DataElements)
            {
                // Keep the existing Current-profile admission budget, even when
                // the final response uses the smaller SharePoint framing.
                ulong size = (ulong)element.GetSerializedLength();
                if (size > maximum - used)
                {
                    response.DataElementPackage = null;
                    subResponse.Status = true;
                    subResponse.Error = new ResponseError(
                        ErrorType.Cell,
                        (ulong)CellErrorCode.RequestNotSupported,
                        "The requested QueryChanges byte budget requires continuation state that is not implemented.");
                    subResponse.Data = null;
                    return;
                }

                used += size;
            }
        }
    }

}
