using CellBridge.Storage;
using CellBridge.Web;

namespace CellBridge.FssHttp.Tests;

public class CellPartitionSelectorTests
{
    [Theory]
    [InlineData(null, DocumentPartitionKind.FileContents)]
    [InlineData("00000000-0000-0000-0000-000000000000", DocumentPartitionKind.FileContents)]
    [InlineData("383adc0b-e66e-4438-95e6-e39ef9720122", DocumentPartitionKind.Metadata)]
    [InlineData("7808f4dd-2385-49d6-b7ce-37aca5e43602", DocumentPartitionKind.EditorsTable)]
    public void SelectorWorksIndependentlyOfGetFileProps(string? partitionId, DocumentPartitionKind expected)
    {
        foreach (var props in new[] { false, true })
        {
            var attributes = new Dictionary<string, string>();
            if (partitionId != null) attributes["PartitionID"] = partitionId;
            if (props) attributes["GetFileProps"] = "true";
            Assert.True(CellPartitionSelector.TryResolve(attributes, out var actual));
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("11111111-1111-1111-1111-111111111111")]
    public void InvalidOrUnknownExplicitPartitionIsNotSilentlyRouted(string id)
    {
        Assert.False(CellPartitionSelector.TryResolve(new Dictionary<string, string>
        {
            ["PartitionID"] = id, ["GetFileProps"] = "true",
        }, out _));
    }
}
