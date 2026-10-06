using CellBridge.FssHttp;

namespace CellBridge.AspNetCore;

/// <summary>MS-FSSHTTP 2.2.5.2/2.2.5.3 dependency decisions before a dependent operation runs.</summary>
public static class FssHttpDependencies
{
    public static string? Error(FssHttpSubRequest request, IReadOnlyList<FssHttpSubResponse> prior)
    {
        if (request.DependsOn is null)
            return request.DependencyType is null ? null : "InvalidRequestDependencyType";
        if (request.DependencyType is null) return "InvalidRequestDependencyType";
        var matches = prior.Where(r => r.SubRequestToken == request.DependsOn).Take(2).ToArray();
        if (matches.Length > 1) return "InvalidRequestDependencyType";
        var previous = matches.SingleOrDefault();
        if (previous is null) return "DependentRequestNotExecuted";
        bool success = previous.ErrorCode == "Success";
        bool unsupported = previous.ErrorCode is "RequestNotSupported" or "NotSupported" or "SubRequestNotSupported";
        // MS-FSSHTTP 2.2.5.3 excludes only failed OnSuccess/OnFail/OnExecute
        // dependencies from OnExecute. An evaluated OnNotSupported fallback
        // must allow continuation, as in the Word open examples in 4.6.1/4.6.2.
        return request.DependencyType switch
        {
            "OnExecute" => previous.ErrorCode is "DependentOnlyOnSuccessRequestFailed" or
                "DependentOnlyOnFailRequestSucceeded" or "DependentRequestNotExecuted"
                ? "DependentRequestNotExecuted" : null,
            "OnSuccess" => success ? null : "DependentOnlyOnSuccessRequestFailed",
            "OnFail" => success ? "DependentOnlyOnFailRequestSucceeded" : null,
            "OnNotSupported" => unsupported ? null : "DependentOnlyOnNotSupportedRequestGetSupported",
            "OnSuccessOrNotSupported" => success || unsupported ? null : "DependentOnlyOnSuccessRequestFailed",
            _ => "InvalidRequestDependencyType",
        };
    }
}
