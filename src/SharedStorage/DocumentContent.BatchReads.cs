using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed partial class DocumentContent<TContext> : IContentBatchReader where TContext : DbContext
{
    public async ValueTask<IReadOnlyDictionary<string, byte[]>> ReadManyAsync(IReadOnlyCollection<ContentHandle> handles,
        CancellationToken cancellationToken = default)
    {
        var unique = DocumentContentBatchReader.ValidateHandles(handles, _maxObjectBytes, cancellationToken);
        foreach (var handle in unique)
        {
            if (handle.Key.Length != 64 || handle.Key.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                handle.Key != handle.Sha256)
                throw new StorageCorruptionException("Invalid content handle.");
        }
        if (unique.Length == 0) return new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            await using var context = CreateContext();
            await context.Database.OpenConnectionAsync(cancellationToken);
            try { return await DocumentContentBatchReader.ReadAsync(context, keys => database.ObjectsAtKeys(context, keys), unique, cancellationToken); }
            finally { await context.Database.CloseConnectionAsync(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (database.IsDatabaseException(ex))
        { throw new StorageUnavailableException("Content batch read failed.", ex); }
    }
}
