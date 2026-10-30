using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class RecoveryGraphMembershipBudgetTests
{
    [Fact]
    public async Task RecoveryUsesPerDocumentGraphBoundsWithoutAProviderWideAggregateQuota()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var first = (await service.CreateAsync("/first.docx", MinimalDocx.Create("first"), TestActor.Value))!;
        var second = (await service.CreateAsync("/second.docx", MinimalDocx.Create("second"), TestActor.Value))!;
        var snapshot = await provider.Documents.CaptureRecoveryAsync();
        var maximum = Math.Max(first.Counters.GraphElements, second.Counters.GraphElements);
        Assert.True(snapshot.Snapshots.Sum(document => document.Elements.Length) > maximum);
        ProviderRecovery.CheckAdmission(snapshot, new() { MaxGraphElements = maximum }, DateTime.UtcNow);
        Assert.Throws<StorageQuotaExceededException>(() => ProviderRecovery.CheckAdmission(snapshot,
            new() { MaxGraphElements = maximum - 1 }, DateTime.UtcNow));
    }

    [Fact]
    public async Task RecoveryChecksHistoricalPackageSizeEvenWhenTheCurrentPackageFits()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var initialBytes = MinimalDocx.Create(new string('a', 20000));
        var first = (await service.CreateAsync("/history.docx", initialBytes, TestActor.Value))!;
        var smaller = MinimalDocx.Create("small");
        Assert.True(initialBytes.Length > smaller.Length);
        var saved = await service.ExecuteAsync(first.ResourceId, DocumentPartitionKind.FileContents,
            await StorageTestDocuments.SaveRequestAsync(service, first, smaller), new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        var snapshot = await provider.Documents.CaptureRecoveryAsync();
        var limits = new StorageLimits { MaxDocumentBytes = initialBytes.Length - 1 };
        Assert.True(saved.State.Package.Length <= limits.MaxDocumentBytes);
        Assert.Throws<StorageQuotaExceededException>(() => ProviderRecovery.CheckAdmission(snapshot, limits, DateTime.UtcNow));
        ProviderRecovery.CheckAdmission(snapshot, limits with { MaxDocumentBytes = initialBytes.Length }, DateTime.UtcNow);
    }
}
