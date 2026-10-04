using CellBridge.Admin;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Web;

namespace CellBridge.FssHttp.Tests;

public sealed class DocumentImportTests
{
    [Fact]
    public async Task ImportsFlatDirectoryWithExplicitOwnershipAndPreservesSavedRevision()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cellbridge-import-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var fileName = "Budget 100% #1.docx";
            var content = MinimalDocx.Create("Original import");
            await File.WriteAllBytesAsync(Path.Combine(directory, fileName), content);
            await File.WriteAllBytesAsync(Path.Combine(directory, "~$Budget.docx"), [0]);
            Directory.CreateDirectory(Path.Combine(directory, "nested"));
            await File.WriteAllBytesAsync(Path.Combine(directory, "nested", "hidden.docx"), [0]);
            var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
            var service = new CellBridgeDocumentService(provider);
            var owner = new SubjectIdentity("local:owner", "owner", "Owner");

            Assert.Equal((1, 0), await DocumentImports.ImportDirectoryAsync(service, directory, owner));
            var summary = Assert.Single(await provider.State.ListAsync(0, 10));
            Assert.Equal("/shared/" + fileName, summary.Path);
            var original = (await provider.State.FindByResourceIdAsync(summary.ResourceId))!;
            Assert.Equal(owner.Subject, original.Security.Owner);
            Assert.Equal("system:imports", original.Security.CreatedBy!.Subject);
            await using (var stream = await provider.Content.OpenReadAsync(original.Content))
            {
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                Assert.Equal(content, copy.ToArray());
            }

            // A later saved revision must survive subsequent imports, including
            // a changed file on disk and a different requested import owner.
            await provider.State.TransitionAsync(summary.ResourceId, (state, _) =>
                new StateTransition<bool>(state with { ContentVersion = state.ContentVersion + 1 }, true));
            var saved = (await provider.State.FindByResourceIdAsync(summary.ResourceId))!;
            await File.WriteAllBytesAsync(Path.Combine(directory, fileName), MinimalDocx.Create("Changed disk file"));
            Assert.Equal((0, 1), await DocumentImports.ImportDirectoryAsync(service, directory,
                new SubjectIdentity("local:other", "other", "Other")));
            Assert.Equal(saved, await provider.State.FindByResourceIdAsync(summary.ResourceId));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task MissingDirectoryFailsWithoutCreatingDocuments()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => DocumentImports.ImportDirectoryAsync(
            new CellBridgeDocumentService(provider), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()),
            new SubjectIdentity("local:owner", "owner", "Owner")));
        Assert.Empty(await provider.State.ListAsync(0, 10));
    }
}
