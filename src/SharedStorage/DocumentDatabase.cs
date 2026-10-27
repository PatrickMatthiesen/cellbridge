using System.Text;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

// These contracts are internal implementation details compiled into each provider.
// Every operation owns its transaction and calls the required locking operations.
internal interface IDocumentLocking<TContext> where TContext : DbContext
{
    Task PublicationAsync(TContext db, bool exclusive, CancellationToken ct);
    Task NamespaceAsync(TContext db, CancellationToken ct);
    Task DocumentAsync(TContext db, Guid id, CancellationToken ct);
    Task ContentAsync(TContext db, string key, CancellationToken ct);
    Task<UsageRow> UsageAsync(TContext db, CancellationToken ct);
}

internal interface IDocumentDatabase<TContext> where TContext : DbContext
{
    TContext CreateContext();
    bool ReadClockAfterStatement { get; }
    IDocumentLocking<TContext> Locks { get; }
    Task<T> ReadAsync<T>(TContext db, Func<Task<T>> action, bool coherent, CancellationToken ct);
    IAsyncEnumerable<(DocumentRow Pointer, string? Json)> ReadNamespaceRowsAsync(TContext db, CancellationToken ct);
    Task ConfigureAsync(TContext db, CancellationToken ct);
    Task CheckConfigurationAsync(TContext db, CancellationToken ct);
    Task CheckRecoveryAsync(TContext db, CancellationToken ct);
    Task<DateTime> ClockAsync(TContext db, CancellationToken ct);
    bool IsDatabaseException(Exception exception);
    bool IsUniqueViolation(Exception exception);
    IQueryable<DocumentRow> AtPath(IQueryable<DocumentRow> rows, string key);
    void SetNamespaceFields(DocumentRow row, DocumentState state);
    void ValidatePointer(DocumentRow row, DocumentState state);
    void ValidateNamespaceFields(DocumentRow row, DocumentNamespaceState state);
    Task VerifyReservationsAsync(TContext db, IReadOnlyDictionary<Guid, DocumentNamespaceState> states, CancellationToken ct);
    Task WriteReservationsAsync(TContext db, DocumentState state, CancellationToken ct);
    Task<bool> InsertObjectAsync(TContext db, ContentHandle handle, CancellationToken ct);
    Task<bool> HasReservationsAsync(TContext db, CancellationToken ct);
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

internal static class DocumentBudget
{
    public static async Task AdjustAsync<TContext>(IDocumentDatabase<TContext> database, TContext db,
        long bytes, long documents, CancellationToken ct) where TContext : DbContext
    {
        Adjust(await database.Locks.UsageAsync(db, ct), bytes, documents);
    }

    public static void Adjust(UsageRow usage, long bytes, long documents)
    {
        var total = checked(usage.StoredBytes + bytes);
        var count = checked(usage.DocumentCount + documents);
        StorageLimits.Check("total stored bytes", total, bytes > 0 ? usage.MaxStoredBytes : long.MaxValue);
        StorageLimits.Check("document count", count, documents > 0 ? usage.MaxDocuments : long.MaxValue);
        usage.StoredBytes = total;
        usage.DocumentCount = count;
    }
}
