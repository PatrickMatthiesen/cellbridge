using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.FileSystem;
using CellBridge.Tests;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class ConfidentialFileSystemTests
{
    private const UnixFileMode FilePermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode DirectoryPermissions = FilePermissions | UnixFileMode.UserExecute;

    [Fact]
    public async Task NewFilesArePrivateBeforeWritingAndCannotOverwriteExistingFiles()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = NewRoot();
        try
        {
            ConfidentialFileSystem.CreateDirectory(root);
            var path = Path.Combine(root, "spool");
            await using (var file = ConfidentialFileSystem.CreateFile(path))
            {
                Assert.Equal(FilePermissions, File.GetUnixFileMode(path));
                await file.WriteAsync(new byte[] { 1, 2, 3 });
            }
            Assert.Throws<IOException>(() => ConfidentialFileSystem.CreateFile(path));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
            Assert.Equal(FilePermissions, File.GetUnixFileMode(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MissingParentsArePrivateAndExistingDirectoryPermissionsArePreserved()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(root);
            var existingMode = DirectoryPermissions | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
            File.SetUnixFileMode(root, existingMode);
            var nested = Path.Combine(root, "first", "second");
            ConfidentialFileSystem.CreateDirectory(nested);
            ConfidentialFileSystem.CreateDirectory(root);
            Assert.Equal(existingMode, File.GetUnixFileMode(root));
            Assert.Equal(DirectoryPermissions, File.GetUnixFileMode(Path.GetDirectoryName(nested)!));
            Assert.Equal(DirectoryPermissions, File.GetUnixFileMode(nested));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FileSystemPublicationKeepsStagingAndPublishedContentPrivate()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = NewRoot();
        try
        {
            var store = new FileSystemContentStore(Path.Combine(root, "content"), shared: true);
            using var input = new InspectingInput(() =>
            {
                if (OperatingSystem.IsWindows()) return;
                var staging = Assert.Single(Directory.GetFiles(Path.Combine(root, "content"), "*.tmp"));
                Assert.Equal(FilePermissions, File.GetUnixFileMode(staging));
            });
            var handle = await store.WriteAsync(input);
            Assert.True(store.Shared);
            Assert.Equal(DirectoryPermissions, File.GetUnixFileMode(root));
            Assert.Equal(DirectoryPermissions, File.GetUnixFileMode(Path.Combine(root, "content")));
            Assert.Equal(FilePermissions, File.GetUnixFileMode(Path.Combine(root, "content", handle.Key + ".blob")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ExportedRecoveryArchiveIsPrivate()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = NewRoot();
        try
        {
            ConfidentialFileSystem.CreateDirectory(root);
            var provider = PortabilityTests.Memory();
            await new CellBridgeDocumentService(provider).CreateAsync("/private.docx", MinimalDocx.Create(), TestActor.Value);
            var path = Path.Combine(root, "recovery.zip");
            await new PortableArchive().ExportAsync(provider, path, PortabilityTests.Context);
            Assert.Equal(FilePermissions, File.GetUnixFileMode(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "cellbridge-permissions-" + Guid.NewGuid().ToString("N"));

    private sealed class InspectingInput(Action inspect) : MemoryStream(new byte[] { 1, 2, 3 })
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            inspect();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
