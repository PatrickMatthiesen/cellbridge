using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.DocumentLibrary;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.Extensions.Logging.Abstractions;

namespace CellBridge.DocumentLibrary.Tests;

public sealed class PermissionPolicyTests
{
    [Fact]
    public async Task DeserializedPermissionSnapshotCanBeInstalledAgain()
    {
        using var files = new TemporaryDirectory();
        await using var destination = new DocumentLibraryDestination(files.Path);
        var resourceId = Guid.NewGuid();
        await destination.CreateAsync(resourceId, Guid.NewGuid(), "example.docx", "/shared/example.docx",
            new MemoryStream(TestOfficeFile.Docx("baseline")),
            ImmutableDictionary<string, DocumentAccess>.Empty
                .Add(DocumentLibraryService.OwnerSubject, DocumentAccess.Write)
                .Add(DocumentLibraryService.ReaderSubject, DocumentAccess.Read));
        var policy = new DocumentLibraryPermissionPolicy(destination);
        await policy.LoadAsync();
        await policy.LoadAsync();
        var manifest = await destination.GetAsync(resourceId);
        policy.Install(manifest);
        Assert.NotNull(policy.BindNewDocument(resourceId));
    }

    [Fact]
    public async Task MissingStateForBoundManifestFailsClosed()
    {
        using var files = new TemporaryDirectory();
        await using var destination = new DocumentLibraryDestination(files.Path);
        var resourceId = Guid.NewGuid();
        await destination.CreateAsync(resourceId, Guid.NewGuid(), "example.docx", "/shared/example.docx",
            new MemoryStream(TestOfficeFile.Docx("baseline")),
            ImmutableDictionary<string, DocumentAccess>.Empty
                .Add(DocumentLibraryService.OwnerSubject, DocumentAccess.Write));
        await destination.MarkCellBridgeBoundAsync(resourceId);
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var policy = new DocumentLibraryPermissionPolicy(destination);
        var documents = new CellBridgeDocumentService(provider, authorizationPolicy: policy);
        var publisher = new ExternalRevisionPublisher(provider, destination);
        var options = new DocumentLibraryOptions();
        using var worker = new ExternalRevisionPublicationWorker(provider, publisher,
            new() { PollingInterval = options.PublicationInterval }, NullLogger<ExternalRevisionPublicationWorker>.Instance);
        var library = new DocumentLibraryService(documents, destination, policy, publisher, worker, options);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => library.InitializeAsync());
        Assert.Contains("coordinated recovery point", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnfinishedUnboundUploadCanRecoverItsVerifiedBaseline()
    {
        using var files = new TemporaryDirectory();
        await using var destination = new DocumentLibraryDestination(files.Path);
        var resourceId = Guid.NewGuid();
        var baseline = TestOfficeFile.Docx("baseline");
        await destination.CreateAsync(resourceId, Guid.NewGuid(), "example.docx", "/shared/example.docx",
            new MemoryStream(baseline), ImmutableDictionary<string, DocumentAccess>.Empty
                .Add(DocumentLibraryService.OwnerSubject, DocumentAccess.Write));
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var policy = new DocumentLibraryPermissionPolicy(destination);
        var documents = new CellBridgeDocumentService(provider, authorizationPolicy: policy);
        var publisher = new ExternalRevisionPublisher(provider, destination);
        var options = new DocumentLibraryOptions();
        using var worker = new ExternalRevisionPublicationWorker(provider, publisher,
            new() { PollingInterval = options.PublicationInterval }, NullLogger<ExternalRevisionPublicationWorker>.Instance);
        var library = new DocumentLibraryService(documents, destination, policy, publisher, worker, options);

        await library.InitializeAsync();
        var recovered = await provider.State.FindByResourceIdAsync(resourceId);
        Assert.NotNull(recovered?.Publication);
        Assert.True((await destination.GetAsync(resourceId)).CellBridgeBound);
    }
}
