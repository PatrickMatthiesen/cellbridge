using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;
using CellBridge.TestInfrastructure;

public sealed partial class InMemoryDocumentRepositoryTests
{
    [Fact]
    public async Task NormalizedSeederRemapsEveryDocumentAndPreservesIndependentGraphHistory()
    {
        var content = new InMemoryContentStore();
        var templateRepository = new InMemoryDocumentRepository(content);
        var templateProvider = new StorageProvider(templateRepository, content);
        var bytes = MinimalDocx.Create("shared immutable template");
        var template = (await new CellBridgeDocumentService(templateProvider).CreateAsync("/template.docx", bytes,
            new(Actor, CanCreate: true)))!;
        var snapshot = (await templateRepository.ExportDocumentAsync(template.ResourceId))!;
        var destination = new InMemoryDocumentRepository(content);
        var provider = new StorageProvider(destination, content);
        var requested = Enumerable.Range(0, 3).Select(index => (Path: "/seeded-" + index + ".docx",
            Owner: new SubjectIdentity("owner-" + index, "owner-" + index, "Owner " + index))).ToArray();
        var heads = await DocumentLoadSeeder.SeedAsync(provider, snapshot, requested);
        Assert.Equal(3, heads.Count);
        var inventory = await destination.CaptureRecoveryAsync();
        Assert.Equal(3, inventory.Snapshots.Length);
        Assert.Equal(3, Assert.Single(inventory.Receipts).Documents);
        foreach (var document in inventory.Snapshots)
        {
            var owner = requested.Single(value => value.Path == document.Current.Head.Path).Owner;
            Assert.Equal(owner.Subject, document.Current.Head.Security.Owner);
            Assert.Equal(owner, Assert.Single(document.ApplicationRevisions).Author);
            Assert.All(document.Elements, element => Assert.Equal(document.Current.Head.ResourceId, element.Scope.ResourceId));
            Assert.All(document.CurrentBindings, binding => Assert.Equal(document.Current.Head.ResourceId, binding.Key.Scope.ResourceId));
            Assert.Empty(document.Receipts);
            NormalizedDocumentRecovery.Validate(document, provider.Limits);
            await using var package = await provider.Packages.OpenReadAsync(document.Current.Head.Package);
            using var restored = new MemoryStream();
            await package.CopyToAsync(restored);
            Assert.Equal(bytes, restored.ToArray());
        }
        var selected = heads[0];
        var selectedActor = new CellBridgeActor(requested[0].Owner);
        var service = new CellBridgeDocumentService(provider);
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(selected.ResourceId, HostLockOperation.Acquire, "host", selectedActor)).Success);
        using var replacement = new MemoryStream(MinimalDocx.Create("independent update"));
        await service.SaveWholeFileAsync(selected.ResourceId, replacement, "independent", selectedActor, "host");
        foreach (var untouched in heads.Skip(1))
        {
            var current = (await destination.FindAsync(new DocumentIdentity.Resource(untouched.ResourceId)))!;
            Assert.Equal(0, current.StateVersion);
            Assert.Equal(1, current.Counters.Revisions);
            Assert.Equal(0, current.Counters.WholeFileSaveReceipts);
        }
        await Assert.ThrowsAsync<RecoveryConflictException>(async () => await DocumentLoadSeeder.SeedAsync(provider, snapshot, [("/extra.docx", Actor)]));
    }

    [Fact]
    public async Task TypedProviderConformanceChecksPassForMemory()
    {
        var content = new InMemoryContentStore();
        await ProviderConformance.VerifyAsync(new(new InMemoryDocumentRepository(content), content));
    }
}
