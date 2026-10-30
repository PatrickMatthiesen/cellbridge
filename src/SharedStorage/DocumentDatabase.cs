using System.Text;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

// These contracts are internal implementation details compiled into each provider.
// Physical object writes coordinate only their content key. Inventory is explicitly quiescent.
internal interface IDocumentLocking<TContext> where TContext : DbContext
{
    Task ContentAsync(TContext db, string key, CancellationToken ct);
}

internal interface IDocumentDatabase<TContext> where TContext : DbContext
{
    TContext CreateContext();
    IDocumentLocking<TContext> Locks { get; }
    Task ConfigureAsync(TContext db, CancellationToken ct);
    Task CheckConfigurationAsync(TContext db, CancellationToken ct);
    bool IsDatabaseException(Exception exception);
    void ValidateNamespaceFields(DocumentRow row, DocumentNamespaceState state);
    Task<bool> InsertObjectAsync(TContext db, ContentHandle handle, CancellationToken ct);
    Task<bool> TryInsertChunksAsync(TContext db, IReadOnlyList<ChunkRow> chunks, CancellationToken ct) =>
        Task.FromResult(false);
    IQueryable<ObjectRow> ObjectsAtKeys(TContext db, IReadOnlyList<string> keys);
    Task<IReadOnlySet<string>> CreateObjectsAsync(TContext db, IReadOnlyList<ContentHandle> handles, CancellationToken ct);
}

internal static class DocumentSql
{
    public static DateTime ClockUtc() => throw new NotSupportedException("Database translation only.");
    public static long ByteLength(string value) => throw new NotSupportedException("Database translation only.");
}

internal static class DocumentEncoding
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static byte[] Encode(string json) => Utf8.GetBytes(json);
    public static string Decode(byte[] data)
    {
        try { return Utf8.GetString(data); }
        catch (DecoderFallbackException ex) { throw new StorageCorruptionException("Snapshot JSON is not UTF-8.", ex); }
    }
}
