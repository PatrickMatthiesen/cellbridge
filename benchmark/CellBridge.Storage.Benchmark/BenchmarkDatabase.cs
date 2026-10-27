using CellBridge.Storage.Abstractions;
using Npgsql;

// Aspire owns the database. Reset document data between measurements so repeat
// input bytes do not turn content writes into deduplication lookups.
internal sealed class BenchmarkDatabase(NpgsqlDataSource source) : IAsyncDisposable
{
    public NpgsqlDataSource Source { get; } = source;

    public static async Task<BenchmarkDatabase> OpenAsync(string connectionString, StorageLimits limits)
    {
        var source = NpgsqlDataSource.Create(connectionString);
        try
        {
            await using (var reset = source.CreateCommand("""
                TRUNCATE cellbridge_documents, cellbridge_states, cellbridge_read_heads,
                    cellbridge_revision_summaries, cellbridge_chunks, cellbridge_objects,
                    cellbridge_recovery_receipts;
                """))
                await reset.ExecuteNonQueryAsync();
            await using var quota = source.CreateCommand("""
                UPDATE cellbridge_usage SET stored_bytes=0, document_count=0,
                    max_stored_bytes=$1, max_documents=$2 WHERE singleton=true;
                """);
            quota.Parameters.AddWithValue(limits.MaxStoredBytes);
            quota.Parameters.AddWithValue(limits.MaxDocuments);
            await quota.ExecuteNonQueryAsync();
            return new(source);
        }
        catch
        {
            await source.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => Source.DisposeAsync();
}
