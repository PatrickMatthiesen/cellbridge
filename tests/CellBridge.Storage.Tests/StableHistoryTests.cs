using CellBridge.Tests;

namespace CellBridge.Storage.Tests;

public sealed class StableHistoryTests
{
    [Theory]
    [InlineData("revoke")]
    [InlineData("delete")]
    [InlineData("recreate")]
    [InlineData("cancel")]
    public Task FullHistoryRechecksCurrentFences(string change) => StableHistoryScenario.FenceAsync(StorageTestDocuments.Memory(), change);

    [Fact]
    public Task AppendBetweenPagesDoesNotChangeFullHistory() => StableHistoryScenario.RunAsync(StorageTestDocuments.Memory());
}
