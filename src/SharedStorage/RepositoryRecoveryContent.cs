using System.Security.Cryptography;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.Shared;

// An import-local verified copy, not a runtime cache. Payloads stay on disk and
// every document still checks its own derived indexes and exact package bytes.
internal sealed class RecoveryContentSpool : IContentStore, IAsyncDisposable
{
    private readonly string directory;
    private readonly Dictionary<string, (ContentHandle Handle, string Path)> files = new(StringComparer.Ordinal);

    private RecoveryContentSpool()
    {
        directory = Path.Combine(Path.GetTempPath(), "cellbridge-recovery-" + Guid.NewGuid().ToString("N"));
        ConfidentialFileSystem.CreateDirectory(directory);
    }

    public bool Durable => false;
    public bool Shared => false;
    public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The recovery verification spool is read-only.");

    public static async Task<RecoveryContentSpool> CreateAsync(IContentStore source, IEnumerable<ContentHandle> handles,
        long maxObjectBytes, CancellationToken ct)
    {
        // Check conflicting aliases before creating files or opening source payloads.
        var unique = ContentStoreReader.UniqueHandles(handles);
        var spool = new RecoveryContentSpool();
        try
        {
            var buffer = new byte[65536];
            foreach (var handle in unique)
            {
                ct.ThrowIfCancellationRequested();
                StorageLimits.Check("object bytes", handle.Length, maxObjectBytes);
                var path = Path.Combine(spool.directory, spool.files.Count.ToString("D10"));
                await using (var output = ConfidentialFileSystem.CreateFile(path, FileAccess.Write))
                await using (var input = await source.OpenReadAsync(handle, ct))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    long length = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, ct)) != 0)
                    {
                        if (read > handle.Length - length)
                            throw new StorageCorruptionException("Recovery content exceeds its declared length.");
                        length += read;
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    if (length != handle.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != handle.Sha256)
                        throw new StorageCorruptionException("Recovery content failed immutable length or digest verification.");
                }
                spool.files.Add(handle.Key, (handle, path));
            }
            ct.ThrowIfCancellationRequested();
            return spool;
        }
        catch { await spool.DisposeAsync(); throw; }
    }

    public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!files.TryGetValue(handle.Key, out var file) || file.Handle != handle)
            throw new StorageCorruptionException("A recovery graph references an unverified physical object.");
        return ValueTask.FromResult<Stream>(new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public ValueTask DisposeAsync()
    {
        Directory.Delete(directory, recursive: true);
        files.Clear();
        return ValueTask.CompletedTask;
    }
}
