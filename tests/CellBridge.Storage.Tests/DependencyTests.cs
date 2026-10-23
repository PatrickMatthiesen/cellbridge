using CellBridge.AspNetCore;
using CellBridge.FssHttp;

namespace CellBridge.Storage.Tests;

public class DependencyTests
{
    [Theory]
    [InlineData("OnExecute", "FileAlreadyLockedOnServer", null)]
    [InlineData("OnExecute", "DependentOnlyOnSuccessRequestFailed", "DependentRequestNotExecuted")]
    [InlineData("OnExecute", "DependentOnlyOnNotSupportedRequestGetSupported", null)]
    [InlineData("OnExecute", "InvalidRequestDependencyType", null)]
    [InlineData("OnSuccess", "Success", null)]
    [InlineData("OnSuccess", "CellRequestFail", "DependentOnlyOnSuccessRequestFailed")]
    [InlineData("OnFail", "Success", "DependentOnlyOnFailRequestSucceeded")]
    [InlineData("OnFail", "CellRequestFail", null)]
    [InlineData("OnNotSupported", "Success", "DependentOnlyOnNotSupportedRequestGetSupported")]
    [InlineData("OnNotSupported", "SubRequestNotSupported", null)]
    [InlineData("OnNotSupported", "NotSupported", null)]
    [InlineData("OnSuccessOrNotSupported", "SubRequestNotSupported", null)]
    [InlineData("OnSuccessOrNotSupported", "NotSupported", null)]
    [InlineData("OnSuccessOrNotSupported", "DependentOnlyOnNotSupportedRequestGetSupported", "DependentOnlyOnSuccessRequestFailed")]
    [InlineData("OnSuccessOrNotSupported", "FileAlreadyLockedOnServer", "DependentOnlyOnSuccessRequestFailed")]
    [InlineData("OnSuccessOrNotSupported", "FileUnauthorizedAccess", "DependentOnlyOnSuccessRequestFailed")]
    [InlineData("OnSuccessOrNotSupported", "DependentOnlyOnSuccessRequestFailed", "DependentOnlyOnSuccessRequestFailed")]
    [InlineData("OnSuccess", "DependentOnlyOnNotSupportedRequestGetSupported", "DependentOnlyOnSuccessRequestFailed")]
    [InlineData("invalid", "Success", "InvalidRequestDependencyType")]
    public void ConditionsPreserveProtocolDependencyErrors(string type, string previous, string? expected)
    {
        var request = new FssHttpSubRequest { DependsOn = 1, DependencyType = type };
        Assert.Equal(expected, FssHttpDependencies.Error(request, [new() { SubRequestToken = 1, ErrorCode = previous }], [request]));
    }

    [Fact]
    public void DuplicatePredecessorsReturnDependencyErrorWithoutThrowing()
    {
        var request = new FssHttpSubRequest { DependsOn = 1, DependencyType = "OnSuccess" };
        FssHttpSubResponse[] prior =
        [
            new() { SubRequestToken = 1, ErrorCode = "Success" },
            new() { SubRequestToken = 1, ErrorCode = "Success" },
        ];

        Assert.Equal("InvalidRequestDependencyType", FssHttpDependencies.Error(request, prior, [request]));
    }

    [Theory]
    [InlineData("Success", "OnSuccessOrNotSupported", null)]
    [InlineData("Success", "OnSuccess", "DependentOnlyOnSuccessRequestFailed")]
    [InlineData("FileAlreadyLockedOnServer", "OnSuccessOrNotSupported", "DependentOnlyOnSuccessRequestFailed")]
    [InlineData("FileUnauthorizedAccess", "OnSuccessOrNotSupported", "DependentOnlyOnSuccessRequestFailed")]
    [InlineData("InvalidCoauthSession", "OnSuccessOrNotSupported", "DependentOnlyOnSuccessRequestFailed")]
    [InlineData("NumberOfCoauthorsReachedMax", "OnSuccessOrNotSupported", "DependentOnlyOnSuccessRequestFailed")]
    [InlineData("FileAlreadyLockedOnServer", "OnExecute", null)]
    public void SkippedFallbackContinuesSaveOnlyAfterSuccessfulPredecessor(string ancestor, string condition, string? expected)
    {
        FssHttpSubRequest[] requests =
        [
            new() { SubRequestToken = 1, Type = SubRequestType.Coauth },
            new() { SubRequestToken = 5, Type = SubRequestType.SchemaLock, DependsOn = 1, DependencyType = "OnNotSupported" },
            new() { SubRequestToken = 6, Type = SubRequestType.Cell, DependsOn = 5, DependencyType = condition },
        ];
        FssHttpSubResponse[] prior =
        [
            new() { SubRequestToken = 1, ErrorCode = ancestor },
            new() { SubRequestToken = 5, ErrorCode = "DependentOnlyOnNotSupportedRequestGetSupported" },
        ];
        Assert.Equal(expected, FssHttpDependencies.Error(requests[2], prior, requests));
    }

    [Theory]
    [InlineData("Success", null)]
    [InlineData("RequestNotSupported", null)]
    [InlineData("FileAlreadyLockedOnServer", "DependentOnlyOnSuccessRequestFailed")]
    public void UnsupportedCoauthoringUsesActualFallbackOutcome(string fallback, string? expected)
    {
        FssHttpSubRequest[] requests =
        [
            new() { SubRequestToken = 1, Type = SubRequestType.Coauth },
            new() { SubRequestToken = 2, Type = SubRequestType.SchemaLock, DependsOn = 1, DependencyType = "OnNotSupported" },
            new() { SubRequestToken = 3, Type = SubRequestType.Cell, DependsOn = 2, DependencyType = "OnSuccessOrNotSupported" },
        ];
        FssHttpSubResponse[] prior =
        [
            new() { SubRequestToken = 1, ErrorCode = "RequestNotSupported" },
            new() { SubRequestToken = 2, ErrorCode = fallback },
        ];
        Assert.Null(FssHttpDependencies.Error(requests[1], prior.Take(1).ToArray(), requests));
        Assert.Equal(expected, FssHttpDependencies.Error(requests[2], prior, requests));
    }
}
