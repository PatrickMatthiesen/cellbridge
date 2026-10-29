using CellBridge.Admin;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;

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
            var provider = TestStorage.Memory();
            var service = new CellBridgeDocumentService(provider);
            var owner = new SubjectIdentity("local:owner", "owner", "Owner");

            Assert.Equal((1, 0), await DocumentImports.ImportDirectoryAsync(service, directory, owner));
            var summary = Assert.Single(await provider.Documents.ListAsync(0, 10));
            Assert.Equal("/shared/" + fileName, summary.Path);
            var original = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(summary.ResourceId)))!;
            Assert.Equal(owner.Subject, original.Security.Owner);
            Assert.Equal("system:imports", original.Security.CreatedBy!.Subject);
            await using (var stream = await provider.Packages.OpenReadAsync(original.Package))
            {
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                Assert.Equal(content, copy.ToArray());
            }

            // A later saved revision must survive subsequent imports, including
            // a changed file on disk and a different requested import owner.
            var actor = new CellBridgeActor(owner);
            Assert.True((await new SharedDocumentLocks(service).ApplyAsync(summary.ResourceId, HostLockOperation.Acquire, "import-save", actor)).Success);
            var savedBytes = MinimalDocx.Create("Saved revision");
            using var replacement = new MemoryStream(savedBytes);
            await service.SaveWholeFileAsync(summary.ResourceId, replacement, "saved", actor, "import-save");
            var saved = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(summary.ResourceId)))!;
            Assert.Equal(savedBytes, await TestStorage.ReadPackageAsync(provider, saved.Package));
            Assert.Equal(2, saved.Counters.Revisions);
            await File.WriteAllBytesAsync(Path.Combine(directory, fileName), MinimalDocx.Create("Changed disk file"));
            Assert.Equal((0, 1), await DocumentImports.ImportDirectoryAsync(service, directory,
                new SubjectIdentity("local:other", "other", "Other")));
            Assert.Equal(saved, await provider.Documents.FindAsync(new DocumentIdentity.Resource(summary.ResourceId)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task MissingDirectoryFailsWithoutCreatingDocuments()
    {
        var provider = TestStorage.Memory();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => DocumentImports.ImportDirectoryAsync(
            new CellBridgeDocumentService(provider), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()),
            new SubjectIdentity("local:owner", "owner", "Owner")));
        Assert.Empty(await provider.Documents.ListAsync(0, 10));
    }
}
