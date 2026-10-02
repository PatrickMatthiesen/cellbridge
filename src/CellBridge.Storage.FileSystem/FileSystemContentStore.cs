using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.FileSystem;

/// <summary>
/// Immutable content on a local Windows/Linux filesystem. Published objects are
/// never deleted. Shared must only be enabled for a backend whose flush and rename
/// guarantees have been verified by the operator; it does not enable file sharing.
/// </summary>
public sealed class FileSystemContentStore : IContentStore
{
    private readonly string _root;
    private readonly long _maxObjectBytes;
    public bool Durable => true;
    public bool Shared { get; }
    public FileSystemContentStore(string root, long maxObjectBytes = 512L * 1024 * 1024, bool shared = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxObjectBytes);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Durable filesystem publication is implemented for Windows and Linux.");
        _root = Path.GetFullPath(root);
        _maxObjectBytes = maxObjectBytes;
        Shared = shared;
        var missing = new Stack<string>();
        for (var path = _root; !Directory.Exists(path); path = Path.GetDirectoryName(path)!)
            missing.Push(path);
        while (missing.TryPop(out var path))
        {
            Directory.CreateDirectory(path);
            FlushDirectory(Path.GetDirectoryName(path)!);
        }
        FlushDirectory(_root);
    }
    public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
    {
        var temporary = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            long length = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[65536];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    length = checked(length + read);
                    if (length > _maxObjectBytes) throw new InvalidDataException("Content exceeds the configured storage object limit.");
                    hash.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            var handle = new ContentHandle(digest, length, digest);
            var destination = ObjectPath(digest);
            if (OperatingSystem.IsWindows())
            {
                if (!MoveFileEx(temporary, destination, 8) && !File.Exists(destination))
                    throw new IOException("Durable content publication failed.", Marshal.GetLastWin32Error());
            }
            else
            {
                try { File.Move(temporary, destination, overwrite: false); }
                catch (IOException) when (File.Exists(destination)) { }
                FlushDirectory(_root);
            }
            // Verify reused objects as well; existence alone is not proof of integrity.
            await using var verified = await OpenReadAsync(handle, cancellationToken);
            return handle;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
    {
        FileStream stream;
        try { stream = new FileStream(ObjectPath(handle.Key), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous); }
        catch (FileNotFoundException ex) { throw new StorageCorruptionException("Referenced content is missing.", ex); }
        try
        {
            if (stream.Length != handle.Length ||
                Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)) != handle.Sha256)
                throw new StorageCorruptionException("Referenced content does not match its integrity metadata.");
            stream.Position = 0;
            return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }
    private string ObjectPath(string key)
    {
        if (key.Length != 64 || key.Any(c => !char.IsAsciiHexDigit(c)) || key != key.ToLowerInvariant())
            throw new StorageCorruptionException("Invalid content key.");
        return Path.Combine(_root, key + ".blob");
    }
    private static void FlushDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        int descriptor = Open(path, 0x10000);
        if (descriptor < 0) throw new IOException("Cannot open the content directory for synchronization.");
        try { if (Fsync(descriptor) != 0) throw new IOException("Content directory synchronization failed."); }
        finally { Close(descriptor); }
    }
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)] private static extern int Fsync(int descriptor);
    [DllImport("libc", EntryPoint = "close")] private static extern int Close(int descriptor);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MoveFileEx(string existing, string target, uint flags);
}
