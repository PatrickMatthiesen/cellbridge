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
    [InlineData("invalid", "Success", "InvalidRequestDependencyType")]
    public void ConditionsPreserveProtocolDependencyErrors(string type, string previous, string? expected)
    {
        var request = new FssHttpSubRequest { DependsOn = 1, DependencyType = type };
        Assert.Equal(expected, FssHttpDependencies.Error(request, [new() { SubRequestToken = 1, ErrorCode = previous }]));
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

        Assert.Equal("InvalidRequestDependencyType", FssHttpDependencies.Error(request, prior));
    }
}
