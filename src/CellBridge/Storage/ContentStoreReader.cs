using System.Security.Cryptography;

namespace CellBridge.Storage.Abstractions;

/// <summary>Reads an immutable content handle without trusting the provider's stream length.</summary>
public static class ContentStoreReader
{
    public static async ValueTask<byte[]> ReadVerifiedAsync(this IContentStore store, ContentHandle handle,
        long maxBytes = int.MaxValue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (handle.Length < 0 || handle.Length > Array.MaxLength)
            throw new StorageCorruptionException("Referenced content length cannot be represented by a byte array.");
        StorageLimits.Check("content read bytes", handle.Length, maxBytes);
        var bytes = GC.AllocateUninitializedArray<byte>((int)handle.Length);
        await using var source = await store.OpenReadAsync(handle, cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int position = 0;
        while (position < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = await source.ReadAsync(bytes.AsMemory(position, Math.Min(65536, bytes.Length - position)), cancellationToken);
            if (read == 0)
                throw new StorageCorruptionException("Referenced content is shorter than its declared length.");
            hash.AppendData(bytes, position, read);
            position += read;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (await source.ReadAsync(new byte[1], cancellationToken) != 0)
            throw new StorageCorruptionException("Referenced content exceeds its declared length.");
        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != handle.Sha256)
            throw new StorageCorruptionException("Referenced content failed SHA-256 verification.");
        cancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }

    /// <summary>Checks every alias before content is cached or deduplicated by key.</summary>
    public static IReadOnlyCollection<ContentHandle> UniqueHandles(IEnumerable<ContentHandle> handles)
    {
        var unique = new Dictionary<string, ContentHandle>(StringComparer.Ordinal);
        foreach (var handle in handles)
        {
            if (unique.TryGetValue(handle.Key, out var previous) && previous != handle)
                throw new StorageCorruptionException("Conflicting content handles share the same key.");
            unique[handle.Key] = handle;
        }
        return unique.Values;
    }
}
